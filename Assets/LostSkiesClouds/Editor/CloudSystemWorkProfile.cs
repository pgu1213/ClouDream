using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>밀도 호출의 용도를 구분하는 별도 GPU 작업량 계수입니다. 시간 측정에 사용하지 않습니다.</summary>
    public static class CloudSystemWorkProfile
    {
        [Serializable]
        public sealed class Sample
        {
            public int pose;
            public int pixels;
            public double iterations;
            public double filledSamples;
            public double entries;
            public double lightEvaluations;
            public double startDensity;
            public double primaryDensity;
            public double refinementDensity;
            public double normalDensity;
            public double shadowDensity;
            public double skyDensity;
            public double macroFields;
            public double normalStep;
            public int raysAtIterationCap;
        }

        [Serializable]
        public sealed class Report
        {
            public bool running;
            public bool passed;
            public string error;
            public string notes = "Instrumented GPU counts, NOT milliseconds. 18 CloudRoute-v1 poses, 320x184, no scene depth, Concept V2, optional quality reductions OFF. Fixed independent validation environment; not a count extrapolation to FHD. Each pixel stores 3 uint4 records without atomics.";
            public List<Sample> samples = new List<Sample>();
        }

        public static Report LastReport = new Report();

        private const string Root = "Assets/LostSkiesClouds/Runtime/";
        private const string Name = "CloudLoad-Work.compute";

        /// <summary>주 광선·표면·조명에서 호출한 밀도를 별도 계수에 기록하는 진단 자산을 만듭니다.</summary>
        public static void Prepare()
        {
            if (LastReport.running || CloudSystemLoadProfile.LastReport.running)
            {
                throw new InvalidOperationException("Cannot prepare shaders while profiling.");
            }

            string source = File.ReadAllText(Root + "CloudRaymarch.compute").Replace("\r\n", "\n");
            // GPU 계약: A는 반복/내부 표본/진입/조명, B는 시작/주 광선/탐색/법선,
            // C는 태양/하늘/MacroField/법선 간격을 기록합니다. 픽셀당 세 레코드를 소유합니다.
            source = Replace(source, "RWTexture2DArray<float4> _LightingOut;",
                "RWTexture2DArray<float4> _LightingOut;\nRWStructuredBuffer<uint4> _LoadWork;\nstatic uint4 _WorkA;\nstatic uint4 _WorkB;\nstatic uint4 _WorkC;\nstatic uint _WorkContext;");
            source = Replace(source, "float Density(float3 position, bool useDetail)\n{",
                "float Density(float3 position, bool useDetail)\n{\n"
                + "    if (_WorkContext == 0)\n    {\n        _WorkB.x++;\n    }\n"
                + "    else if (_WorkContext == 1)\n    {\n        _WorkB.y++;\n    }\n"
                + "    else if (_WorkContext == 2)\n    {\n        _WorkB.z++;\n    }\n"
                + "    else if (_WorkContext == 3)\n    {\n        _WorkB.w++;\n    }\n"
                + "    else if (_WorkContext == 4)\n    {\n        _WorkC.x++;\n    }\n"
                + "    else if (_WorkContext == 5)\n    {\n        _WorkC.y++;\n    }");
            source = Replace(source, "float3 EvaluateCloudLight(float3 position, float3 sun, float extinction, float phase, float sunCosine, float3 surfaceNormal)\n{",
                "float3 EvaluateCloudLight(float3 position, float3 sun, float extinction, float phase, float sunCosine, float3 surfaceNormal)\n{\n    _WorkA.w++;");
            source = Replace(source, "shadow += Density(position + sun * lightDistance, false) * lightStep;",
                "_WorkContext = 4;\n        shadow += Density(position + sun * lightDistance, false) * lightStep;");
            source = Replace(source, "skyDepth = Density(position + float3(0, 60, 0), false) * 60;",
                "_WorkContext = 5;\n        skyDepth = Density(position + float3(0, 60, 0), false) * 60;");
            source = Replace(source, "float3 CloudBoundaryNormal(float3 position)\n{",
                "float3 CloudBoundaryNormal(float3 position)\n{\n    _WorkContext = 3;");
            source = Replace(source, "float2 uv = (id.xy + 0.5) * _Size.zw;",
                "_WorkA = 0;\n    _WorkB = 0;\n    _WorkC = 0;\n    _WorkContext = 0;\n    uint workIndex = (id.y * (uint)_Size.x + id.x) * 3;\n    _LoadWork[workIndex] = 0;\n    _LoadWork[workIndex + 1] = 0;\n    _LoadWork[workIndex + 2] = 0;\n    float2 uv = (id.xy + 0.5) * _Size.zw;");
            source = Replace(source, "{\n        step = min(end - distance", "{\n        _WorkA.x++;\n        step = min(end - distance");
            source = Replace(source, "float density = Density(position, true);", "_WorkContext = 1;\n        float density = Density(position, true);");
            source = Replace(source, "float low = previousDistance;", "_WorkA.z++;\n            _WorkContext = 2;\n            float low = previousDistance;");
            source = Replace(source, "if (density > 0.002)\n        {", "if (density > 0.002)\n        {\n            _WorkA.y++;");
            source = Replace(source, "_LightingOut[id] = float4(radiance, representativeDepth);",
                "_LoadWork[workIndex] = _WorkA;\n    _LoadWork[workIndex + 1] = _WorkB;\n    _LoadWork[workIndex + 2] = _WorkC;\n    _LightingOut[id] = float4(radiance, representativeDepth);");
            string concept = File.ReadAllText(Root + "CloudConceptShapes.hlsl").Replace("\r\n", "\n");
            concept = Replace(concept, "float ConceptMacroField(float3 position)\n{", "float ConceptMacroField(float3 position)\n{\n    _WorkC.z++;");
            concept = Replace(concept, "float ConceptNormalStep(float3 position)\n{", "float ConceptNormalStep(float3 position)\n{\n    _WorkC.w++;");
            source = Replace(source, "#include \"CloudConceptShapes.hlsl\"", concept);
            Directory.CreateDirectory("Benchmarks/SystemLoadSources");
            File.WriteAllText("Benchmarks/SystemLoadSources/work.compute.txt", source);
            File.WriteAllText(Root + Name, source);
            AssetDatabase.ImportAsset(Root + Name);
        }

        /// <summary>원본 코드 구조가 바뀌면 계수를 잘못 기록하지 않고 즉시 실패합니다.</summary>
        private static string Replace(string source, string before, string after)
        {
            if (!source.Contains(before))
            {
                throw new InvalidOperationException("Work profile source anchor changed: " + before);
            }
            return source.Replace(before, after);
        }

        /// <summary>기존 검증 환경과 동일한 18개 자세에서 동기 읽기 비용을 시간 측정과 분리합니다.</summary>
        public static async void Start()
        {
            if (LastReport.running || CloudSystemLoadProfile.LastReport.running)
            {
                throw new InvalidOperationException("Run counts separately from timings.");
            }
            Report report = new Report();
            report.running = true;
            LastReport = report;
            GameObject host = new GameObject("Cloud system work counters");
            host.hideFlags = HideFlags.HideAndDontSave;
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.fieldOfView = 68;
            camera.aspect = 16f / 9f;
            camera.farClipPlane = 100000;
            CloudSkyProfile sky = UnityEngine.Object.Instantiate(CloudRayOptimizationValidation.Load<CloudSkyProfile>("Presets/Sky-ConceptV2.asset"));
            CloudStyleProfile style = UnityEngine.Object.Instantiate(CloudRayOptimizationValidation.Load<CloudStyleProfile>("Presets/Style-ConceptV2.asset"));
            try
            {
                using (LostSkiesCloudRenderer renderer = new LostSkiesCloudRenderer(
                    AssetDatabase.LoadAssetAtPath<ComputeShader>(Root + Name),
                    CloudRayOptimizationValidation.Load<ComputeShader>("SourceShaders/CloudGenerator.asset"),
                    CloudRayOptimizationValidation.Load<TextAsset>("Presets/normal.json")))
                using (ComputeBuffer buffer = new ComputeBuffer(320 * 184 * 3, 16))
                {
                    renderer.includeOcean = true;
                    renderer.oceanProfile = CloudRayOptimizationValidation.Load<CloudOceanProfile>("Presets/ContinuousOcean.asset");
                    renderer.skyProfile = sky;
                    renderer.styleProfile = style;
                    renderer.settings.coverageIntensity = 0.165f;
                    renderer.settings.density = 160000;
                    renderer.useCompactTargets = true;
                    renderer.Resize(320, 184);
                    ComputeShader shader = (ComputeShader)typeof(LostSkiesCloudRenderer).GetField("renderer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(renderer);
                    uint[] data = new uint[320 * 184 * 12];
                    for (int pose = 0; pose < 18; pose++)
                    {
                        CloudRayOptimizationValidation.Configure(0, camera, renderer, sky, style);
                        Vector3 position;
                        Quaternion rotation;
                        CloudBenchmark.GetPose(pose * 30, 180, new Vector3(5137.099f, 6241.348f, -5190.923f), out position, out rotation);
                        camera.transform.SetPositionAndRotation(position, rotation);
                        using (CommandBuffer commands = new CommandBuffer())
                        {
                            commands.SetComputeBufferParam(shader, shader.FindKernel("Raymarch"), "_LoadWork", buffer);
                            renderer.Render(commands, camera, Vector3.up, Color.white);
                            Graphics.ExecuteCommandBuffer(commands);
                        }
                        buffer.GetData(data);
                        Sample sample = new Sample();
                        sample.pose = pose;
                        sample.pixels = 320 * 184;
                        for (int index = 0; index < data.Length; index += 12)
                        {
                            sample.iterations += data[index];
                            sample.filledSamples += data[index + 1];
                            sample.entries += data[index + 2];
                            sample.lightEvaluations += data[index + 3];
                            sample.startDensity += data[index + 4];
                            sample.primaryDensity += data[index + 5];
                            sample.refinementDensity += data[index + 6];
                            sample.normalDensity += data[index + 7];
                            sample.shadowDensity += data[index + 8];
                            sample.skyDensity += data[index + 9];
                            sample.macroFields += data[index + 10];
                            sample.normalStep += data[index + 11];
                            if (data[index] == 1024)
                            {
                                sample.raysAtIterationCap++;
                            }
                        }
                        if (sample.primaryDensity != sample.iterations || sample.refinementDensity != sample.entries * 6
                            || sample.normalDensity != sample.entries * 6 || sample.shadowDensity != sample.lightEvaluations * 7
                            || sample.skyDensity != sample.lightEvaluations * 3 || sample.macroFields != sample.entries * 6
                            || sample.normalStep != sample.entries)
                        {
                            throw new InvalidOperationException("Counter contract failed at pose " + pose);
                        }
                        report.samples.Add(sample);
                        await Task.Delay(1);
                    }
                }
                report.passed = true;
            }
            catch (Exception exception)
            {
                report.error = exception.ToString();
                Debug.LogException(exception);
            }
            finally
            {
                report.running = false;
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(sky);
                UnityEngine.Object.DestroyImmediate(style);
                File.WriteAllText("Benchmarks/SystemLoad-WorkCounts.json", JsonUtility.ToJson(report, true));
            }
        }

        /// <summary>계수 종료 후 임시 GPU 자산만 제거합니다.</summary>
        public static void Cleanup()
        {
            if (!LastReport.running)
            {
                AssetDatabase.DeleteAsset(Root + Name);
            }
        }
    }
}
