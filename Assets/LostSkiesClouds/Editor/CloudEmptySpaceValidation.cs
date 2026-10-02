using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>이전 셰이더와 새 빈 공간 탐색의 실제 GPU 색/투과율/깊이 및 이력 계약을 검증합니다.</summary>
    public static class CloudEmptySpaceValidation
    {
        [Serializable]
        public sealed class Report
        {
            public bool running;
            public bool passed;
            public bool frozenBaseline;
            public int temporalChecks;
            public string error;
            public List<CloudRayOptimizationValidation.Comparison> samples = new List<CloudRayOptimizationValidation.Comparison>();
        }

        public static Report LastReport = new Report();

        /// <summary>12개 극단 시점과 이동 벤치마크 표본을 이전 구현 및 OFF와 교차 비교합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Validate Improved Empty Space")]
        public static async void Start()
        {
            if (LastReport.running)
            {
                return;
            }

            Report report = new Report();
            LastReport = report;
            report.running = true;
            GameObject host = new GameObject("Empty space GPU validation");
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
                ComputeShader baseline = CloudRayOptimizationValidation.Load<ComputeShader>("Runtime/CloudEmptyBaseline.compute");
                report.frozenBaseline = baseline != null;
                if (baseline == null)
                {
                    baseline = shader;
                }

                using (LostSkiesCloudRenderer reference = Create(baseline, sky, style))
                using (LostSkiesCloudRenderer candidate = Create(shader, sky, style))
                {
                    for (int scenario = 0; scenario < 30; scenario++)
                    {
                        int configuration = scenario;
                        if (scenario >= 12)
                        {
                            configuration = 0;
                        }
                        CloudRayOptimizationValidation.Configure(configuration, camera, reference, sky, style);
                        CloudRayOptimizationValidation.Configure(configuration, camera, candidate, sky, style);
                        if (scenario >= 12)
                        {
                            Vector3 position;
                            Quaternion rotation;
                            CloudBenchmark.GetPose((scenario - 12) * 30, 180,
                                new Vector3(5137.099f, 6241.348f, -5190.923f), out position, out rotation);
                            camera.transform.SetPositionAndRotation(position, rotation);
                        }

                        reference.useRayIntervals = false;
                        candidate.useRayIntervals = false;
                        reference.distanceQuality = CloudDistanceQualitySettings.Default;
                        candidate.distanceQuality = CloudDistanceQualitySettings.Default;
                        CloudRayOptimizationValidation.Frame expected = CloudRayOptimizationValidation.Draw(reference, camera);
                        Check(report, expected, CloudRayOptimizationValidation.Draw(candidate, camera), scenario, "off-regression");
                        candidate.useRayIntervals = true;
                        Check(report, expected, CloudRayOptimizationValidation.Draw(candidate, camera), scenario, "improved-vs-off");
                        reference.useRayIntervals = true;
                        Check(report, CloudRayOptimizationValidation.Draw(reference, camera),
                            CloudRayOptimizationValidation.Draw(candidate, camera), scenario, "improved-vs-legacy");
                        CloudDistanceQualitySettings quality = CloudDistanceQualitySettings.Default;
                        quality.density = true;
                        quality.lighting = true;
                        reference.distanceQuality = quality;
                        candidate.distanceQuality = quality;
                        Check(report, CloudRayOptimizationValidation.Draw(reference, camera),
                            CloudRayOptimizationValidation.Draw(candidate, camera), scenario, "distance-quality-combination");
                        await Task.Delay(1);
                    }

                    CloudRayOptimizationValidation.Configure(0, camera, candidate, sky, style);
                    candidate.useTemporalReprojection = true;
                    candidate.useRayIntervals = false;
                    CloudRayOptimizationValidation.Draw(candidate, camera);
                    for (int change = 0; change < 4; change++)
                    {
                        int resets = candidate.TemporalResets;
                        candidate.useRayIntervals = !candidate.useRayIntervals;
                        CloudRayOptimizationValidation.Draw(candidate, camera);
                        if (!candidate.TemporalActive || candidate.TemporalResets <= resets)
                        {
                            throw new InvalidOperationException("Skipping toggle did not invalidate temporal history.");
                        }
                        report.temporalChecks++;
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
                Directory.CreateDirectory("Benchmarks");
                File.WriteAllText("Benchmarks/EmptySpace-Validation.json", JsonUtility.ToJson(report, true));
            }
        }

        /// <summary>별도 GPU 자원으로 같은 프로필과 정오 조명을 렌더링합니다.</summary>
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

        /// <summary>공통 비교기로 유한값을 검사하고 기존 색/투과율/깊이 허용 오차를 적용합니다.</summary>
        private static void Check(Report report, CloudRayOptimizationValidation.Frame expected,
            CloudRayOptimizationValidation.Frame actual, int scenario, string variant)
        {
            CloudRayOptimizationValidation.Comparison sample = CloudRayOptimizationValidation.Compare(expected, actual);
            sample.scenario = scenario.ToString();
            sample.variant = variant;
            report.samples.Add(sample);
            if (sample.maximumRgbError > 0.001f || sample.maximumTransmissionError > 0.0001f || sample.maximumDepthError > 0.1f)
            {
                throw new InvalidOperationException("Empty-space output changed: " + scenario + "/" + variant);
            }
        }
    }
}
