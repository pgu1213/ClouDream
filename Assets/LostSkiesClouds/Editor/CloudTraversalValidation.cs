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
    /// <summary>실제 거리 도약과 원경 내부 적분을 독립 비교하고 얇은 구름·근접 가림·작업량을 검증합니다.</summary>
    public static class CloudTraversalValidation
    {
        [Serializable]
        public sealed class Sample
        {
            public string scenario;
            public string variant;
            public double meanRgb;
            public float p95Rgb;
            public float maximumRgb;
            public double meanTransmission;
            public float p95Transmission;
            public float maximumTransmission;
            public float maximumDepthMoment;
            public float maximumFirstHit;
            public int referenceCoveredPixels;
            public int missingCoveredPixels;
            public bool finite;
            public bool imageBudgetPassed;
            public bool coveragePassed;
            public bool nearInvariantPassed;
            public bool firstHitInvariantPassed;
            public bool passed;
        }

        [Serializable]
        public sealed class Report
        {
            public bool running;
            public bool completed;
            public bool passed;
            public string progress;
            public string error;
            public string notes = "320x184, fixed camera poses, no temporal or field cache. "
                + "Macro traversal preserves the original floating-point sample lattice and last empty sample with scalar distance advancement; "
                + "main integration and occupancy queries jump across proven-empty spans. Actual output errors are measured independently. "
                + "Image budgets: mean RGB <=0.02, P95 <=0.06, mean transmission <=0.015, P95 <=0.06. "
                + "Coverage requires >=99.5% of reference opacity>0.1 pixels to retain opacity>=0.005. "
                + "Adaptive alone must preserve first-hit distances; clipped near-only rays must be exact.";
            public List<Sample> samples = new List<Sample>();
        }

        [Serializable]
        public sealed class WorkSample
        {
            public int pose;
            public string variant;
            public int pixels;
            public long iterations;
            public long densityQueries;
            public long emptyJumps;
            public long filledSamples;
        }

        [Serializable]
        public sealed class WorkReport
        {
            public bool running;
            public bool completed;
            public bool passed;
            public string error;
            public string notes = "Instrumented GPU operation counts, not timings. 18 fixed CloudRoute-v1 poses, "
                + "160x96, no scene depth, field/temporal caches OFF. Iterations count the main ray loop and exclude "
                + "cheap scalar distance-advancement iterations. Primary density excludes normal, shadow and bisection queries.";
            public List<WorkSample> samples = new List<WorkSample>();
        }

        public static Report LastReport = new Report();

        public static WorkReport LastWorkReport = new WorkReport();

        private const string ShaderRoot = "Assets/LostSkiesClouds/Runtime/";

        private const string WorkShaderName = "CloudTraversalWork.compute";

        /// <summary>동일한 입력에서 기준·거리 도약·내부 적분·결합을 비교하고 모든 조건의 실패도 함께 기록합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Validate Macro Traversal And Integration")]
        public static async void Start()
        {
            if (LastReport.running || LastWorkReport.running)
            {
                return;
            }

            Report report = new Report();
            LastReport = report;
            report.running = true;
            GameObject host = new GameObject("Cloud traversal validation");
            host.hideFlags = HideFlags.HideAndDontSave;
            Camera camera = CreateCamera(host);
            CloudSkyProfile sky = UnityEngine.Object.Instantiate(CloudRayOptimizationValidation.Load<CloudSkyProfile>("Presets/Sky-ConceptV2.asset"));
            CloudStyleProfile style = UnityEngine.Object.Instantiate(CloudRayOptimizationValidation.Load<CloudStyleProfile>("Presets/Style-ConceptV2.asset"));
            try
            {
                using (LostSkiesCloudRenderer renderer = CreateRenderer(
                    CloudRayOptimizationValidation.Load<ComputeShader>("Runtime/CloudRaymarch.compute"), sky, style, 320, 184))
                {
                    string[] names = { "home", "horizon", "above-down", "inside", "negative-cell", "axis-parallel",
                        "max-displacement", "origin-offset", "sky-disabled", "low-sun", "mixed-light", "bright-rim",
                        "thin-ribbons", "thin-ribbons-max-displacement", "near-only-inside" };
                    report.passed = true;
                    for (int index = 0; index < names.Length; index++)
                    {
                        Configure(index, camera, renderer, sky, style);
                        ConfigureVariant(renderer, 0);
                        CloudRayOptimizationValidation.Frame reference = CloudRayOptimizationValidation.Draw(renderer, camera);
                        for (int variant = 1; variant <= 3; variant++)
                        {
                            report.progress = names[index] + "/" + VariantName(variant);
                            ConfigureVariant(renderer, variant);
                            CloudRayOptimizationValidation.Frame actual = CloudRayOptimizationValidation.Draw(renderer, camera);
                            Sample sample = Compare(reference, actual, names[index], variant, index == 14);
                            report.samples.Add(sample);
                            report.passed &= sample.passed;
                            if (variant == 3 && (index == 0 || index == 3 || index == 6 || index == 12 || index == 13))
                            {
                                SaveImage(reference, renderer.Width, renderer.Height, names[index] + "-Reference");
                                SaveImage(actual, renderer.Width, renderer.Height, names[index] + "-Combined");
                            }

                            await Task.Delay(1);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                report.passed = false;
                report.error = exception.ToString();
                Debug.LogException(exception);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(sky);
                UnityEngine.Object.DestroyImmediate(style);
                report.running = false;
                report.completed = true;
                Directory.CreateDirectory("Screenshots/Traversal");
                File.WriteAllText("Screenshots/Traversal/Validation.json", JsonUtility.ToJson(report, true));
            }
        }

        /// <summary>기존 검증 자세에 얇은 띠만 보이는 조건과 3km 안쪽 가림 구간을 추가합니다.</summary>
        private static void Configure(int index, Camera camera, LostSkiesCloudRenderer renderer,
            CloudSkyProfile sky, CloudStyleProfile style)
        {
            int original = index;
            if (index >= 12)
            {
                original = 0;
            }

            CloudRayOptimizationValidation.Configure(original, camera, renderer, sky, style);
            renderer.includeOcean = true;
            SetDepth(renderer, camera, 0);
            if (index == 12 || index == 13)
            {
                // 상층 몸체만 최대 광선 거리 밖으로 옮깁니다. 같은 시드의 띠 생성식과 밀도는 그대로 둡니다.
                renderer.includeOcean = false;
                sky.altitude = new Vector2(-60000, -60000);
                sky.occupiedCells = 1f;
                camera.transform.SetPositionAndRotation(new Vector3(2000, 7200, -9000), Quaternion.Euler(0, 24, 0));
                if (index == 13)
                {
                    style.viewBillowDisplacement = 600f;
                    style.fineBillowDisplacement = 160f;
                }
            }

            if (index == 14)
            {
                CloudRayOptimizationValidation.Configure(3, camera, renderer, sky, style);
                // FOV 68도와 16:9 모서리 광선도 실제 적분 거리가 3km를 넘지 않게 합니다.
                SetDepth(renderer, camera, 1500f);
            }
        }

        /// <summary>다른 캐시·해상도 옵션을 고정한 상태에서 거리 도약과 내부 적분만 선택합니다.</summary>
        private static void ConfigureVariant(LostSkiesCloudRenderer renderer, int variant)
        {
            renderer.useRayIntervals = false;
            renderer.useMacroTraversal = variant == 1 || variant == 3;
            renderer.useAdaptiveIntegration = variant == 2 || variant == 3;
        }

        /// <summary>보고서와 이미지에서 사용하는 비교 옵션의 이름을 반환합니다.</summary>
        private static string VariantName(int variant)
        {
            if (variant == 1)
            {
                return "macro-traversal";
            }

            if (variant == 2)
            {
                return "adaptive-integration";
            }

            if (variant == 3)
            {
                return "combined";
            }

            return "baseline";
        }

        /// <summary>GPU readback과 성능 측정을 섞지 않는 동일한 렌더러를 생성합니다.</summary>
        private static LostSkiesCloudRenderer CreateRenderer(ComputeShader shader, CloudSkyProfile sky,
            CloudStyleProfile style, int width, int height)
        {
            LostSkiesCloudRenderer renderer = new LostSkiesCloudRenderer(shader,
                CloudRayOptimizationValidation.Load<ComputeShader>("SourceShaders/CloudGenerator.asset"),
                CloudRayOptimizationValidation.Load<TextAsset>("Presets/normal.json"));
            renderer.includeOcean = true;
            renderer.oceanProfile = CloudRayOptimizationValidation.Load<CloudOceanProfile>("Presets/ContinuousOcean.asset");
            renderer.skyProfile = sky;
            renderer.styleProfile = style;
            renderer.useCompactTargets = true;
            renderer.settings.coverageIntensity = 0.165f;
            renderer.settings.density = 160000f;
            renderer.Resize(width, height);
            return renderer;
        }

        /// <summary>검증과 작업량 측정이 공유하는 비활성 원근 카메라를 만듭니다.</summary>
        private static Camera CreateCamera(GameObject host)
        {
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.fieldOfView = 68f;
            camera.aspect = 16f / 9f;
            camera.farClipPlane = 100000f;
            return camera;
        }

        /// <summary>GPU 출력의 실제 RGB·투과율 오차와 구름 누락 수를 집계하고 사전 선언한 기준을 적용합니다.</summary>
        private static Sample Compare(CloudRayOptimizationValidation.Frame reference,
            CloudRayOptimizationValidation.Frame actual, string scenario, int variant, bool nearOnly)
        {
            Sample sample = new Sample();
            sample.scenario = scenario;
            sample.variant = VariantName(variant);
            sample.finite = true;
            float[] rgb = new float[reference.lighting.Length];
            float[] transmission = new float[rgb.Length];
            for (int index = 0; index < rgb.Length; index++)
            {
                Color expected = reference.lighting[index];
                Color result = actual.lighting[index];
                Color expectedT = reference.transmission[index];
                Color resultT = actual.transmission[index];
                sample.finite &= IsFinite(expected) && IsFinite(result) && IsFinite(expectedT) && IsFinite(resultT);
                rgb[index] = Mathf.Max(Mathf.Abs(expected.r - result.r), Mathf.Abs(expected.g - result.g));
                rgb[index] = Mathf.Max(rgb[index], Mathf.Abs(expected.b - result.b));
                transmission[index] = Mathf.Abs(expectedT.r - resultT.r);
                sample.meanRgb += rgb[index] / rgb.Length;
                sample.meanTransmission += transmission[index] / rgb.Length;
                sample.maximumRgb = Mathf.Max(sample.maximumRgb, rgb[index]);
                sample.maximumTransmission = Mathf.Max(sample.maximumTransmission, transmission[index]);
                sample.maximumDepthMoment = Mathf.Max(sample.maximumDepthMoment, Mathf.Abs(expected.a - result.a));
                sample.maximumFirstHit = Mathf.Max(sample.maximumFirstHit, Mathf.Abs(expectedT.g - resultT.g));
                if (expectedT.r < 0.9f)
                {
                    sample.referenceCoveredPixels++;
                    if (resultT.r > 0.995f)
                    {
                        sample.missingCoveredPixels++;
                    }
                }
            }

            Array.Sort(rgb);
            Array.Sort(transmission);
            int percentile = Mathf.FloorToInt((rgb.Length - 1) * 0.95f);
            sample.p95Rgb = rgb[percentile];
            sample.p95Transmission = transmission[percentile];
            sample.imageBudgetPassed = sample.meanRgb <= 0.02 && sample.p95Rgb <= 0.06f
                && sample.meanTransmission <= 0.015 && sample.p95Transmission <= 0.06f;
            sample.coveragePassed = sample.missingCoveredPixels <= Mathf.Max(2, sample.referenceCoveredPixels * 0.005f);
            if (scenario.StartsWith("thin-ribbons", StringComparison.Ordinal) && sample.referenceCoveredPixels == 0)
            {
                sample.coveragePassed = false;
            }
            sample.nearInvariantPassed = true;
            sample.firstHitInvariantPassed = true;
            if (variant == 2)
            {
                sample.firstHitInvariantPassed = sample.maximumFirstHit == 0;
                if (nearOnly)
                {
                    sample.nearInvariantPassed = sample.referenceCoveredPixels > 0 && sample.maximumRgb == 0
                        && sample.maximumTransmission == 0 && sample.maximumDepthMoment == 0;
                }
            }

            sample.passed = sample.finite && sample.imageBudgetPassed && sample.coveragePassed
                && sample.nearInvariantPassed && sample.firstHitInvariantPassed;
            return sample;
        }

        /// <summary>모든 저장 채널이 유한한 값인지 검사합니다.</summary>
        private static bool IsFinite(Color value)
        {
            for (int channel = 0; channel < 4; channel++)
            {
                if (float.IsNaN(value[channel]) || float.IsInfinity(value[channel]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>알려진 장면 가림 거리를 실제 렌더러의 역 Z 입력으로 설정합니다.</summary>
        private static void SetDepth(LostSkiesCloudRenderer renderer, Camera camera, float eyeDistance)
        {
            float raw = 0;
            if (eyeDistance > 0)
            {
                float z = (camera.farClipPlane / camera.nearClipPlane - 1f) / camera.farClipPlane;
                raw = (1f / eyeDistance - 1f / camera.farClipPlane) / z;
            }

            using (CommandBuffer commands = new CommandBuffer())
            {
                commands.SetRenderTarget(renderer.DepthTarget, 0, CubemapFace.Unknown, 0);
                commands.ClearRenderTarget(false, true, new Color(raw, 0, 0, 0));
                Graphics.ExecuteCommandBuffer(commands);
            }
        }

        /// <summary>동일한 선형 배경을 합성한 참조·결합 이미지를 저장합니다.</summary>
        private static void SaveImage(CloudRayOptimizationValidation.Frame frame, int width, int height, string name)
        {
            Texture2D image = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                Color[] pixels = new Color[width * height];
                Color background = new Color(0.2f, 0.4f, 0.7f, 0);
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int index = y * width + x;
                        Color color = frame.lighting[index] + background * frame.transmission[index].r;
                        color.a = 1;
                        pixels[(height - 1 - y) * width + x] = color.gamma;
                    }
                }

                image.SetPixels(pixels);
                image.Apply();
                Directory.CreateDirectory("Screenshots/Traversal");
                File.WriteAllBytes("Screenshots/Traversal/" + name + ".png", image.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(image);
            }
        }

        /// <summary>명시적 진단 준비 요청에서만 생산용 셰이더에 독립 픽셀의 반복·조회 계수를 추가합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Prepare Traversal Work Counters")]
        public static void PrepareWorkShader()
        {
            if (LastReport.running || LastWorkReport.running)
            {
                throw new InvalidOperationException("Cannot prepare counters while validation is running.");
            }

            string source = File.ReadAllText(ShaderRoot + "CloudRaymarch.compute").Replace("\r\n", "\n");
            source = ReplaceOnce(source, "RWTexture2DArray<float4> _LightingOut;",
                "RWTexture2DArray<float4> _LightingOut;\nRWStructuredBuffer<uint4> _CloudTraversalWork;");
            source = ReplaceOnce(source, "float2 uv = (id.xy + 0.5) * _Size.zw;",
                "uint4 work = 0;\n    uint workIndex = id.y * (uint)_Size.x + id.x;\n"
                + "    _CloudTraversalWork[workIndex] = 0;\n    float2 uv = (id.xy + 0.5) * _Size.zw;");
            source = ReplaceOnce(source, "{\n        step = min(end - distance", "{\n        work.x++;\n        step = min(end - distance");
            source = ReplaceOnce(source, "previousDistance = distance;\n                previousDensity = 0;",
                "work.z++;\n                previousDistance = distance;\n                previousDensity = 0;");
            source = ReplaceOnce(source, "float density = Density(position, true);",
                "work.y++;\n        float density = Density(position, true);");
            source = ReplaceOnce(source, "if (density > 0.002)\n        {", "if (density > 0.002)\n        {\n            work.w++;");
            source = ReplaceOnce(source, "_LightingOut[id] = float4(radiance, representativeDepth);",
                "_CloudTraversalWork[workIndex] = work;\n    _LightingOut[id] = float4(radiance, representativeDepth);");
            File.WriteAllText(ShaderRoot + WorkShaderName, source);
            AssetDatabase.ImportAsset(ShaderRoot + WorkShaderName);
        }

        /// <summary>생산 소스가 바뀌면 부정확한 계수 대신 명시적으로 준비 작업을 실패시킵니다.</summary>
        private static string ReplaceOnce(string source, string before, string after)
        {
            int first = source.IndexOf(before, StringComparison.Ordinal);
            if (first < 0 || source.IndexOf(before, first + before.Length, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException("Traversal work counter anchor changed: " + before);
            }

            return source.Replace(before, after);
        }

        /// <summary>별도 계수 셰이더로 같은 비행 경로의 루프·밀도 호출 감소를 읽으며 시간 단위로 환산하지 않습니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Run Traversal Work Counters")]
        public static async void StartWorkload()
        {
            if (LastReport.running || LastWorkReport.running)
            {
                return;
            }

            WorkReport report = new WorkReport();
            LastWorkReport = report;
            report.running = true;
            GameObject host = new GameObject("Cloud traversal work counters");
            host.hideFlags = HideFlags.HideAndDontSave;
            Camera camera = CreateCamera(host);
            CloudSkyProfile sky = UnityEngine.Object.Instantiate(CloudRayOptimizationValidation.Load<CloudSkyProfile>("Presets/Sky-ConceptV2.asset"));
            CloudStyleProfile style = UnityEngine.Object.Instantiate(CloudRayOptimizationValidation.Load<CloudStyleProfile>("Presets/Style-ConceptV2.asset"));
            try
            {
                ComputeShader instrumented = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderRoot + WorkShaderName);
                if (instrumented == null)
                {
                    throw new InvalidOperationException("Run PrepareWorkShader through Unity before workload measurement.");
                }

                using (LostSkiesCloudRenderer renderer = CreateRenderer(instrumented, sky, style, 160, 96))
                using (ComputeBuffer buffer = new ComputeBuffer(160 * 96, sizeof(uint) * 4))
                {
                    ComputeShader shader = (ComputeShader)typeof(LostSkiesCloudRenderer)
                        .GetField("renderer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(renderer);
                    uint[] counts = new uint[160 * 96 * 4];
                    for (int pose = 0; pose < 18; pose++)
                    {
                        CloudRayOptimizationValidation.Configure(0, camera, renderer, sky, style);
                        CloudBenchmark.GetPose(pose * 30, 180, new Vector3(5137.099f, 6241.348f, -5190.923f),
                            out Vector3 position, out Quaternion rotation);
                        camera.transform.SetPositionAndRotation(position, rotation);
                        for (int variant = 0; variant <= 3; variant++)
                        {
                            ConfigureVariant(renderer, variant);
                            using (CommandBuffer commands = new CommandBuffer())
                            {
                                commands.SetComputeBufferParam(shader, shader.FindKernel("Raymarch"), "_CloudTraversalWork", buffer);
                                renderer.Render(commands, camera, Vector3.up, Color.white);
                                Graphics.ExecuteCommandBuffer(commands);
                            }

                            buffer.GetData(counts);
                            WorkSample sample = new WorkSample();
                            sample.pose = pose;
                            sample.variant = VariantName(variant);
                            sample.pixels = 160 * 96;
                            for (int index = 0; index < counts.Length; index += 4)
                            {
                                sample.iterations += counts[index];
                                sample.densityQueries += counts[index + 1];
                                sample.emptyJumps += counts[index + 2];
                                sample.filledSamples += counts[index + 3];
                            }

                            if (sample.iterations != sample.densityQueries + sample.emptyJumps
                                || sample.filledSamples > sample.densityQueries)
                            {
                                throw new InvalidOperationException("Traversal counter conservation failed.");
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
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(sky);
                UnityEngine.Object.DestroyImmediate(style);
                report.running = false;
                report.completed = true;
                Directory.CreateDirectory("Benchmarks");
                File.WriteAllText("Benchmarks/Traversal-WorkCounts.json", JsonUtility.ToJson(report, true));
            }
        }
    }
}
