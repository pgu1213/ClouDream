using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>거리 옵션의 OFF 회귀, 분리 동작, 극단값 및 시간 이력 무효화를 실제 GPU 출력으로 검사합니다.</summary>
    public static class CloudDistanceQualityValidation
    {
        [Serializable]
        public sealed class Sample
        {
            public int scenario;
            public string variant;
            public CloudRayOptimizationValidation.Comparison difference;
            public double meanTransmission;
            public double p95Rgb;
        }

        [Serializable]
        public sealed class Report
        {
            public bool running;
            public bool passed;
            public string error;
            public int temporalResetChecks;
            public bool frozenBaseline;
            public List<Sample> samples = new List<Sample>();
        }

        public static Report LastReport = new Report();

        /// <summary>에디터 응답을 유지하며 readback 대기는 성능 수치로 사용하지 않습니다.</summary>
        public static async void Start()
        {
            if (LastReport.running)
            {
                return;
            }
            Report report = new Report();
            report.running = true;
            LastReport = report;
            GameObject host = new GameObject("Distance quality GPU validation");
            host.hideFlags = HideFlags.HideAndDontSave;
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.aspect = 16f / 9f;
            camera.fieldOfView = 68;
            camera.farClipPlane = 100000;
            CloudSkyProfile sky = UnityEngine.Object.Instantiate(CloudRayOptimizationValidation.Load<CloudSkyProfile>("Presets/Sky-ConceptV2.asset"));
            CloudStyleProfile style = UnityEngine.Object.Instantiate(CloudRayOptimizationValidation.Load<CloudStyleProfile>("Presets/Style-ConceptV2.asset"));
            try
            {
                ComputeShader shader = CloudRayOptimizationValidation.Load<ComputeShader>("Runtime/CloudRaymarch.compute");
                ComputeShader baseline = CloudRayOptimizationValidation.Load<ComputeShader>("Runtime/CloudDistanceBaseline.compute");
                report.frozenBaseline = baseline != null;
                if (baseline == null)
                {
                    baseline = shader;
                }
                using (LostSkiesCloudRenderer reference = Create(baseline, sky, style))
                using (LostSkiesCloudRenderer candidate = Create(shader, sky, style))
                {
                    for (int scenario = 0; scenario < 12; scenario++)
                    {
                        CloudRayOptimizationValidation.Configure(scenario, camera, reference, sky, style);
                        CloudRayOptimizationValidation.Configure(scenario, camera, candidate, sky, style);
                        candidate.useTemporalReprojection = false;
                        CloudRayOptimizationValidation.Frame expected = CloudRayOptimizationValidation.Draw(reference, camera);
                        string[] variants = { "off", "density", "lighting", "both", "unit-scales", "all-near", "maximum" };
                        for (int variant = 0; variant < variants.Length; variant++)
                        {
                            CloudDistanceQualitySettings quality = CloudDistanceQualitySettings.Default;
                            quality.density = variant == 1 || variant >= 3;
                            quality.lighting = variant >= 2;
                            if (variant == 4)
                            {
                                quality.densityStepScale = 1;
                                quality.lightingSpacingScale = 1;
                            }
                            if (variant == 5)
                            {
                                quality.densityNear = 41999;
                                quality.densityFar = 42000;
                                quality.lightingNear = 41999;
                                quality.lightingFar = 42000;
                            }
                            if (variant == 6)
                            {
                                quality.densityStepScale = 2;
                                quality.lightingSpacingScale = 3;
                                quality.densityNear = 0;
                                quality.lightingNear = 0;
                                quality.densityFar = 1;
                                quality.lightingFar = 1;
                            }
                            candidate.distanceQuality = quality;
                            Sample sample = Compare(expected, CloudRayOptimizationValidation.Draw(candidate, camera));
                            sample.scenario = scenario;
                            sample.variant = variants[variant];
                            report.samples.Add(sample);
                            if (variant == 0 || variant == 4 || variant == 5)
                            {
                                Require(sample.difference.maximumRgbError <= 0.001f && sample.difference.maximumTransmissionError <= 0.0001f,
                                    "Reference/near quality changed: " + scenario + "/" + variant);
                            }
                            if (variant == 2)
                            {
                                Require(sample.difference.maximumTransmissionError == 0 && sample.difference.maximumDepthError == 0,
                                    "Lighting changed density integration.");
                            }
                            await Task.Delay(1);
                        }
                    }
                    // 비교기는 새 옵션과 모든 수치 변경이 부분 갱신 이력을 즉시 버리는지도 확인합니다.
                    CloudRayOptimizationValidation.Configure(0, camera, reference, sky, style);
                    CloudRayOptimizationValidation.Configure(0, camera, candidate, sky, style);
                    candidate.distanceQuality = CloudDistanceQualitySettings.Default;
                    candidate.useTemporalReprojection = true;
                    CloudRayOptimizationValidation.Draw(candidate, camera);
                    for (int change = 0; change < 8; change++)
                    {
                        CloudRayOptimizationValidation.Draw(candidate, camera);
                        int resets = candidate.TemporalResets;
                        CloudDistanceQualitySettings quality = candidate.distanceQuality;
                        if (change == 0)
                        {
                            quality.density = true;
                        }
                        if (change == 1)
                        {
                            quality.lighting = true;
                        }
                        if (change == 2)
                        {
                            quality.densityNear += 100;
                        }
                        if (change == 3)
                        {
                            quality.densityFar += 100;
                        }
                        if (change == 4)
                        {
                            quality.densityStepScale += 0.1f;
                        }
                        if (change == 5)
                        {
                            quality.lightingNear += 100;
                        }
                        if (change == 6)
                        {
                            quality.lightingFar += 100;
                        }
                        if (change == 7)
                        {
                            quality.lightingSpacingScale += 0.1f;
                        }
                        candidate.distanceQuality = quality;
                        CloudRayOptimizationValidation.Draw(candidate, camera);
                        Require(candidate.TemporalActive && candidate.TemporalResets > resets, "Distance change did not reset history.");
                        report.temporalResetChecks++;
                        await Task.Delay(1);
                    }
                    style.method = CloudStyleProfile.ShapeMethod.SculptedLobes;
                    candidate.useTemporalReprojection = false;
                    reference.distanceQuality = CloudDistanceQualitySettings.Default;
                    Sample fallback = Compare(CloudRayOptimizationValidation.Draw(reference, camera), CloudRayOptimizationValidation.Draw(candidate, camera));
                    fallback.variant = "unsupported-style-fallback";
                    report.samples.Add(fallback);
                    Require(fallback.difference.maximumRgbError < 0.001f, "Unsupported shape used distance quality.");
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
                File.WriteAllText("Benchmarks/DistanceQuality-Validation.json", JsonUtility.ToJson(report, true));
            }
        }

        /// <summary>비교 대상은 독립 GPU 자원을 소유하고 같은 프로필과 입력을 사용합니다.</summary>
        private static LostSkiesCloudRenderer Create(ComputeShader shader, CloudSkyProfile sky, CloudStyleProfile style)
        {
            LostSkiesCloudRenderer renderer = new LostSkiesCloudRenderer(shader,
                CloudRayOptimizationValidation.Load<ComputeShader>("SourceShaders/CloudGenerator.asset"),
                CloudRayOptimizationValidation.Load<TextAsset>("Presets/normal.json"),
                CloudRayOptimizationValidation.Load<ComputeShader>("Runtime/CloudTemporal.compute"));
            renderer.includeOcean = true;
            renderer.oceanProfile = CloudRayOptimizationValidation.Load<CloudOceanProfile>("Presets/ContinuousOcean.asset");
            renderer.skyProfile = sky;
            renderer.styleProfile = style;
            renderer.useCompactTargets = true;
            renderer.settings.coverageIntensity = 0.165f;
            renderer.settings.density = 160000;
            renderer.Resize(320, 184);
            return renderer;
        }

        /// <summary>모든 픽셀의 유한값을 검사하고 평균 투과율 오차와 색 오차 p95를 기록합니다.</summary>
        private static Sample Compare(CloudRayOptimizationValidation.Frame expected, CloudRayOptimizationValidation.Frame actual)
        {
            Sample sample = new Sample();
            sample.difference = CloudRayOptimizationValidation.Compare(expected, actual);
            List<double> colors = new List<double>(expected.lighting.Length);
            for (int index = 0; index < expected.lighting.Length; index++)
            {
                Color delta = expected.lighting[index] - actual.lighting[index];
                colors.Add(Math.Max(Math.Abs(delta.r), Math.Max(Math.Abs(delta.g), Math.Abs(delta.b))));
                sample.meanTransmission += Math.Abs(expected.transmission[index].r - actual.transmission[index].r);
            }
            sample.meanTransmission /= expected.lighting.Length;
            colors.Sort();
            sample.p95Rgb = CloudBenchmarkReport.Percentile(colors, 0.95);
            return sample;
        }

        /// <summary>실패 조건과 그때까지의 결과를 보고서에 보존합니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
