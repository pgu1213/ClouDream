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
    /// <summary>별도 계수 셰이더로 주 광선의 루프/밀도 조회/빈 표본/교차 조회를 셉니다. GPU 시간 측정과 분리합니다.</summary>
    public static class CloudEmptySpaceWorkProfile
    {
        [Serializable]
        public sealed class Sample
        {
            public int pose;
            public string variant;
            public int pixels;
            public double iterations;
            public double primaryDensityQueries;
            public double skippedSamples;
            public double intervalQueries;
        }

        [Serializable]
        public sealed class Report
        {
            public bool running;
            public bool passed;
            public bool frozenBaseline;
            public string error;
            public string notes = "320x184; 18 evenly spaced CloudRoute-v1 poses; no scene depth. Counts, not timings. "
                + "Primary density excludes start, bisection, normal and shadow queries. Instrumentation changes GPU execution cost.";
            public List<Sample> samples = new List<Sample>();
        }

        public static Report LastReport = new Report();
        private const string Root = "Assets/LostSkiesClouds/Runtime/";

        /// <summary>생산 코드의 연산을 유지하며 계수 저장만 추가한 임시 셰이더를 생성합니다.</summary>
        public static void Prepare()
        {
            string baseline = "CloudEmptyBaseline.compute";
            if (!File.Exists(Root + baseline))
            {
                baseline = "CloudRaymarch.compute";
            }
            Instrument(baseline, "CloudEmptyWorkLegacy.compute");
            Instrument("CloudRaymarch.compute", "CloudEmptyWorkImproved.compute");
        }

        /// <summary>표본 단위 카운터는 원자 연산 없이 각 픽셀의 독립 uint4 레코드에 기록합니다.</summary>
        private static void Instrument(string source, string destination)
        {
            string text = File.ReadAllText(Root + source).Replace("\r\n", "\n");
            text = text.Replace("RWTexture2DArray<float4> _LightingOut;",
                "RWTexture2DArray<float4> _LightingOut;\nRWStructuredBuffer<uint4> _CloudWork;");
            text = text.Replace("_LightingOut[id] = 0;", "_CloudWork[id.y * (uint)_Size.x + id.x] = 0;\n        _LightingOut[id] = 0;");
            text = text.Replace("float step = clamp(12 + start", "uint4 work = 0;\n    float step = clamp(12 + start");
            text = text.Replace("{\n        step = min(end - distance", "{\n        work.x++;\n        step = min(end - distance");
            text = text.Replace("nextDensityDistance = CloudNextDensityDistance", "work.w++;\n                nextDensityDistance = CloudNextDensityDistance");
            text = text.Replace("previousDistance = distance;\n                previousDensity = 0;",
                "work.z++;\n                previousDistance = distance;\n                previousDensity = 0;");
            text = text.Replace("float density = Density(position, true);", "work.y++;\n        float density = Density(position, true);");
            text = text.Replace("_LightingOut[id] = float4(radiance, representativeDepth);",
                "_CloudWork[id.y * (uint)_Size.x + id.x] = work;\n    _LightingOut[id] = float4(radiance, representativeDepth);");
            if (!text.Contains("work.x++") || !text.Contains("work.z++"))
            {
                throw new InvalidOperationException("Trace loop changed; update instrumentation before using it.");
            }
            File.WriteAllText(Root + destination, text);
            AssetDatabase.ImportAsset(Root + destination);
        }

        /// <summary>공통 벤치마크 경로에서 OFF/기존/개선의 실제 실행량을 읽어 JSON으로 보관합니다.</summary>
        public static async void Start()
        {
            if (LastReport.running)
            {
                return;
            }
            Report report = new Report();
            report.running = true;
            report.frozenBaseline = File.Exists(Root + "CloudEmptyBaseline.compute");
            LastReport = report;
            GameObject host = new GameObject("Empty-space work counters");
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
                for (int variant = 0; variant < 3; variant++)
                {
                    string shaderName = "CloudEmptyWorkLegacy.compute";
                    string label = "off";
                    if (variant == 1)
                    {
                        label = "current-reference";
                        if (report.frozenBaseline)
                        {
                            label = "legacy";
                        }
                    }
                    if (variant == 2)
                    {
                        shaderName = "CloudEmptyWorkImproved.compute";
                        label = "improved";
                    }
                    using (LostSkiesCloudRenderer renderer = new LostSkiesCloudRenderer(
                        AssetDatabase.LoadAssetAtPath<ComputeShader>(Root + shaderName),
                        CloudRayOptimizationValidation.Load<ComputeShader>("SourceShaders/CloudGenerator.asset"),
                        CloudRayOptimizationValidation.Load<TextAsset>("Presets/normal.json")))
                    using (ComputeBuffer buffer = new ComputeBuffer(320 * 184, 16))
                    {
                        renderer.includeOcean = true;
                        renderer.oceanProfile = CloudRayOptimizationValidation.Load<CloudOceanProfile>("Presets/ContinuousOcean.asset");
                        renderer.skyProfile = sky;
                        renderer.styleProfile = style;
                        renderer.settings.coverageIntensity = 0.165f;
                        renderer.settings.density = 160000;
                        renderer.useCompactTargets = true;
                        renderer.useRayIntervals = variant > 0;
                        renderer.Resize(320, 184);
                        ComputeShader shader = (ComputeShader)typeof(LostSkiesCloudRenderer)
                            .GetField("renderer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(renderer);
                        uint[] counts = new uint[320 * 184 * 4];
                        for (int pose = 0; pose < 18; pose++)
                        {
                            CloudRayOptimizationValidation.Configure(0, camera, renderer, sky, style);
                            Vector3 position;
                            Quaternion rotation;
                            CloudBenchmark.GetPose(pose * 30, 180, new Vector3(5137.099f, 6241.348f, -5190.923f), out position, out rotation);
                            camera.transform.SetPositionAndRotation(position, rotation);
                            using (CommandBuffer commands = new CommandBuffer())
                            {
                                commands.SetComputeBufferParam(shader, shader.FindKernel("Raymarch"), "_CloudWork", buffer);
                                renderer.Render(commands, camera, Vector3.up, Color.white);
                                Graphics.ExecuteCommandBuffer(commands);
                            }
                            buffer.GetData(counts);
                            Sample sample = new Sample();
                            sample.pose = pose;
                            sample.variant = label;
                            sample.pixels = 320 * 184;
                            for (int index = 0; index < counts.Length; index += 4)
                            {
                                sample.iterations += counts[index];
                                sample.primaryDensityQueries += counts[index + 1];
                                sample.skippedSamples += counts[index + 2];
                                sample.intervalQueries += counts[index + 3];
                            }
                            if (sample.iterations != sample.primaryDensityQueries + sample.skippedSamples)
                            {
                                throw new InvalidOperationException("Counter conservation failed.");
                            }
                            report.samples.Add(sample);
                            await Task.Delay(1);
                        }
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
                Directory.CreateDirectory("Benchmarks");
                File.WriteAllText("Benchmarks/EmptySpace-WorkCounts.json", JsonUtility.ToJson(report, true));
            }
        }
    }
}
