using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>독립 전체 적분과 비교하여 이력 수명·이동 오차·가림 전환·카메라 격리를 검사합니다.</summary>
    public static class CloudTemporalValidation
    {
        [Serializable]
        public sealed class Sample
        {
            public string scenario;
            public int phases;
            public bool compact;
            public double meanRgb;
            public float maximumRgb;
            public float p95Rgb;
            public double meanTransmission;
            public float maximumTransmission;
            public float maximumDepthMoment;
            public float maximumFirstHit;
            public float tracedFraction;
            public float maximumAge;
            public string historyStatus;
        }

        [Serializable]
        public sealed class Report
        {
            public bool running;
            public bool completed;
            public bool passed;
            public string progress;
            public string error;
            public bool stableAllocations;
            public bool releasedOnDisable;
            public bool isolatedCameras;
            public List<Sample> samples = new List<Sample>();
        }

        public static Report LastReport = new Report();

        /// <summary>Editor 응답을 유지하며 GPU readback은 정확도 검사에만 사용합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Validate Temporal Reprojection")]
        public static async void Start()
        {
            if (LastReport.running)
            {
                return;
            }
            Report report = new Report();
            LastReport = report;
            report.running = true;
            GameObject host = new GameObject("Temporal cloud validation");
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
                ComputeShader baseline = CloudRayOptimizationValidation.Load<ComputeShader>("Runtime/CloudTemporalBaseline.compute");
                if (baseline == null)
                {
                    baseline = CloudRayOptimizationValidation.Load<ComputeShader>("Runtime/CloudRaymarch.compute");
                }

                using (LostSkiesCloudRenderer reference = Create(baseline, sky, style))
                using (LostSkiesCloudRenderer candidate = Create(CloudRayOptimizationValidation.Load<ComputeShader>("Runtime/CloudRaymarch.compute"), sky, style))
                {
                    for (int format = 0; format < 2; format++)
                    {
                        reference.useCompactTargets = format == 1;
                        candidate.useCompactTargets = format == 1;
                        reference.Resize(320, 184);
                        candidate.Resize(320, 184);
                        for (int phases = 2; phases <= 4; phases += 2)
                        {
                            CloudRayOptimizationValidation.Configure(0, camera, reference, sky, style);
                            CloudRayOptimizationValidation.Configure(0, camera, candidate, sky, style);
                            candidate.temporalUpdatePhases = phases;
                            candidate.useTemporalReprojection = false;
                            Compare(report, "off", phases, reference, candidate, camera, true);
                            candidate.useTemporalReprojection = true;
                            int allocations = -1;
                            for (int frame = 0; frame < 9; frame++)
                            {
                                report.progress = "Static " + format + "/" + phases + "/" + frame;
                                Sample sample = Compare(report, "static-" + frame, phases, reference, candidate, camera, true);
                                if (frame >= 5)
                                {
                                    Require(sample.tracedFraction < 0.76f, "No static history reuse.");
                                    Require(allocations == candidate.TemporalAllocations, "Stable history reallocated.");
                                }

                                allocations = candidate.TemporalAllocations;
                                await Task.Delay(1);
                            }

                            for (int frame = 0; frame < 12; frame++)
                            {
                                report.progress = "Motion " + format + "/" + phases + "/" + frame;
                                camera.transform.position += new Vector3(2f, 0.2f, 0);
                                camera.transform.rotation = Quaternion.Euler(10, 15 + (frame + 1) * 0.12f, 0);
                                Sample sample = Compare(report, "motion-" + frame, phases, reference, candidate, camera, false);
                                Require(sample.meanRgb < 0.01 && sample.p95Rgb < 0.04 && sample.meanTransmission < 0.01,
                                    "Motion exceeded the declared image error budget.");
                                await Task.Delay(1);
                            }

                            // 작은 연속 바람은 강체 이동으로 가정하지 않고 제한된 표본 수명으로 추적합니다.
                            for (int frame = 0; frame < 8; frame++)
                            {
                                reference.settings.baseOffset += Vector3.right * 0.00001f;
                                candidate.settings.baseOffset = reference.settings.baseOffset;
                                Sample sample = Compare(report, "slow-wind-" + frame, phases, reference, candidate, camera, false);
                                Require(sample.meanRgb < 0.01 && sample.p95Rgb < 0.04 && sample.meanTransmission < 0.01,
                                    "Wind exceeded the declared image error budget.");
                                await Task.Delay(1);
                            }

                            // 부분 갱신 상태에서 전경이 나타나고 사라지면 같은 프레임에 다시 적분해야 합니다.
                            SetDepth(reference, camera, 500);
                            SetDepth(candidate, camera, 500);
                            Compare(report, "foreground-appears", phases, reference, candidate, camera, true);
                            SetDepth(reference, camera, 0);
                            SetDepth(candidate, camera, 0);
                            Compare(report, "foreground-disappears", phases, reference, candidate, camera, true);

                            camera.transform.position += new Vector3(700, 0, 0);
                            CheckReset(report, "camera-cut", phases, reference, candidate, camera);
                            camera.fieldOfView += 1;
                            CheckReset(report, "projection", phases, reference, candidate, camera);
                            reference.worldOriginOffset = candidate.worldOriginOffset = new Vector3(50000, 0, 0);
                            camera.transform.position -= new Vector3(50000, 0, 0);
                            CheckReset(report, "origin", phases, reference, candidate, camera);
                            reference.settings.baseOffset += Vector3.right * 0.01f;
                            candidate.settings.baseOffset = reference.settings.baseOffset;
                            CheckReset(report, "wind-jump", phases, reference, candidate, camera);
                            reference.skyLighting = candidate.skyLighting = CloudLightingState.MoonlitNight();
                            CheckReset(report, "lighting", phases, reference, candidate, camera);
                            reference.skyDensityMultiplier = candidate.skyDensityMultiplier = 0;
                            CheckReset(report, "weather-density", phases, reference, candidate, camera);
                            reference.skyDensityMultiplier = candidate.skyDensityMultiplier = 1;
                            CheckReset(report, "weather-return", phases, reference, candidate, camera);
                            style.fineBillowDisplacement += 1;
                            CheckReset(report, "profile-value", phases, reference, candidate, camera);
                            candidate.temporalUpdatePhases = 6 - phases;
                            CheckReset(report, "phase-switch", 6 - phases, reference, candidate, camera);
                            await Task.Delay(600);
                            CheckReset(report, "frame-gap", 6 - phases, reference, candidate, camera);

                            // 다시 첫 카메라로 돌아가도 두 번째 카메라의 위치·나이가 섞이지 않아야 합니다.
                            reference.Resize(320, 184, 92);
                            candidate.Resize(320, 184, 92);
                            camera.transform.position += new Vector3(0, 50, 0);
                            CheckReset(report, "second-camera", 6 - phases, reference, candidate, camera);
                            reference.Resize(320, 184, 0);
                            candidate.Resize(320, 184, 0);
                            camera.transform.position -= new Vector3(0, 50, 0);
                            Compare(report, "first-camera-return", 6 - phases, reference, candidate, camera, true);
                            report.isolatedCameras = true;
                            reference.Resize(256, 144);
                            candidate.Resize(256, 144);
                            CheckReset(report, "resize", 6 - phases, reference, candidate, camera);
                            candidate.useTemporalReprojection = false;
                            Compare(report, "disabled", phases, reference, candidate, camera, true);
                            Require(candidate.TemporalBytes == 0, "OFF retained history memory.");
                            report.releasedOnDisable = true;
                            reference.Resize(320, 184);
                            candidate.Resize(320, 184);
                        }
                    }

                    report.stableAllocations = true;
                    report.passed = true;
                }
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
                Directory.CreateDirectory("Screenshots");
                File.WriteAllText("Screenshots/Temporal-Validation.json", JsonUtility.ToJson(report, true));
            }
        }

        /// <summary>원래 프리셋과 같은 입력을 사용하는 독립 렌더러를 만듭니다.</summary>
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
            renderer.settings.coverageIntensity = 0.165f;
            renderer.settings.density = 160000f;
            return renderer;
        }

        /// <summary>이력 리셋이 새 광선 전체 적분과 같은 출력을 만드는지 검사합니다.</summary>
        private static void CheckReset(Report report, string scenario, int phases, LostSkiesCloudRenderer reference,
            LostSkiesCloudRenderer candidate, Camera camera)
        {
            Sample sample = Compare(report, scenario, phases, reference, candidate, camera, true);
            Require(sample.tracedFraction == 1, "Expected full reset: " + scenario);
        }

        /// <summary>알려진 전경 깊이를 역 Z로 변환하여 두 렌더러에 같은 값으로 설정합니다.</summary>
        private static void SetDepth(LostSkiesCloudRenderer renderer, Camera camera, float distance)
        {
            float raw = 0;
            if (distance > 0)
            {
                float z = (camera.farClipPlane / camera.nearClipPlane - 1f) / camera.farClipPlane;
                raw = (1f / distance - 1f / camera.farClipPlane) / z;
            }

            using (CommandBuffer commands = new CommandBuffer())
            {
                commands.SetRenderTarget(renderer.DepthTarget, 0, CubemapFace.Unknown, 0);
                commands.ClearRenderTarget(false, true, new Color(raw, 0, 0, 0));
                Graphics.ExecuteCommandBuffer(commands);
            }
        }

        /// <summary>RGB·투과율·실제 표본 나이를 기록하고 정지 조건에서는 원 출력과의 일치도 검사합니다.</summary>
        private static Sample Compare(Report report, string scenario, int phases, LostSkiesCloudRenderer reference,
            LostSkiesCloudRenderer candidate, Camera camera, bool exact)
        {
            CloudRayOptimizationValidation.Frame expected = CloudRayOptimizationValidation.Draw(reference, camera);
            CloudRayOptimizationValidation.Frame actual = CloudRayOptimizationValidation.Draw(candidate, camera);
            CloudRayOptimizationValidation.Comparison comparison = CloudRayOptimizationValidation.Compare(expected, actual);
            Sample sample = new Sample();
            sample.scenario = scenario;
            sample.phases = phases;
            sample.compact = candidate.CompactTargetsActive;
            sample.meanRgb = comparison.meanRgbError;
            sample.maximumRgb = comparison.maximumRgbError;
            sample.maximumTransmission = comparison.maximumTransmissionError;
            sample.maximumDepthMoment = comparison.maximumDepthError;
            sample.historyStatus = candidate.TemporalStatus;
            float[] errors = new float[actual.lighting.Length];
            for (int index = 0; index < errors.Length; index++)
            {
                Color delta = actual.lighting[index] - expected.lighting[index];
                errors[index] = Mathf.Max(Mathf.Abs(delta.r), Mathf.Abs(delta.g), Mathf.Abs(delta.b));
                sample.meanTransmission += Mathf.Abs(actual.transmission[index].r - expected.transmission[index].r) / errors.Length;
                float expectedHit = expected.transmission[index].a;
                float actualHit = actual.transmission[index].a;
                if (candidate.CompactTargetsActive)
                {
                    expectedHit = expected.transmission[index].g;
                    actualHit = actual.transmission[index].g;
                }

                Require(!float.IsNaN(actualHit) && !float.IsInfinity(actualHit), "Non-finite first cloud hit.");
                sample.maximumFirstHit = Mathf.Max(sample.maximumFirstHit, Mathf.Abs(expectedHit - actualHit));
            }

            Array.Sort(errors);
            sample.p95Rgb = errors[Mathf.FloorToInt((errors.Length - 1) * 0.95f)];
            sample.tracedFraction = 1;
            if (candidate.TemporalState != null)
            {
                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(candidate.TemporalState, 0);
                request.WaitForCompletion();
                Require(!request.hasError, "Temporal state readback failed.");
                var states = request.GetData<Vector2>();
                int traced = 0;
                foreach (Vector2 state in states)
                {
                    Require(!float.IsNaN(state.y) && state.y >= 0 && state.y < phases, "Invalid history age.");
                    sample.maximumAge = Mathf.Max(sample.maximumAge, state.y);
                    if (state.y == 0)
                    {
                        traced++;
                    }
                }

                sample.tracedFraction = traced / (float)states.Length;
            }

            report.samples.Add(sample);
            if (scenario == "motion-7" && candidate.CompactTargetsActive && phases == 4)
            {
                SaveImage(expected, candidate.Width, candidate.Height, "Screenshots/Temporal-Motion-Reference.png");
                SaveImage(actual, candidate.Width, candidate.Height, "Screenshots/Temporal-Motion-Reprojected.png");
            }

            if (exact)
            {
                Require(sample.maximumRgb <= 0.001f && sample.maximumTransmission == 0
                    && sample.maximumDepthMoment == 0 && sample.maximumFirstHit == 0,
                    "Exact condition changed: " + scenario);
            }

            return sample;
        }

        /// <summary>동일한 단색 배경에 구름 RGB와 투과율을 합성한 검사 이미지를 저장합니다.</summary>
        private static void SaveImage(CloudRayOptimizationValidation.Frame frame, int width, int height, string path)
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
                Directory.CreateDirectory("Screenshots");
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(image);
            }
        }

        /// <summary>실패 원인을 검증 보고서에 남깁니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
