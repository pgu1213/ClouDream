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
        public sealed class DepthHalfDifference
        {
            public int x;
            public int y;
            public float reference;
            public float actual;
            public float absoluteDifference;
            public int halfSteps;
            public int referenceHalfBits;
            public int actualHalfBits;
        }

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
            public int maximumDepthHalfSteps;
            public int changedDepthPixels;
            public int depthPixelsBeyondOneHalfStep;
            public DepthHalfDifference worstDepthHalfDifference;
            public List<DepthHalfDifference> exactDepthDifferences = new List<DepthHalfDifference>();
            public float maximumFirstHit;
            public float tracedFraction;
            public float maximumAge;
            public string historyStatus;
            public bool compactTiles;
            public float maximumDepthVariance;
            public uint[] rejectionCounts;
            public Vector3 windBaseOffset;
            public float deformationNoisePeriod;
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
            public bool compactDispatchParity;
            public bool boundedLightingReuse;
            public bool abruptLightingPreservedShape;
            public bool finiteDepthMoments;
            public bool boundedWindReuse;
            public bool cumulativeWindRejections;
            public bool windReversalReuse;
            public string windPrecisionPolicy = "baseOffset is normalized texture input, not metres. "
                + "History compares current XZ input with every candidate frame using0.13*billowPeriod "
                + "metres per input unit. Maximum equivalent noise-coordinate displacement is0.5m; "
                + "changing wind caps age at2. No rigid cloud translation is applied. "
                + "This input bound is validated against actual full-render RGB and transmission errors.";
            public string depthPrecisionPolicy = "Lighting alpha is ARGBHalf. Exact cases allow at most one adjacent "
                + "finite nonnegative half encoding per pixel, not a fixed metre tolerance. RGB, transmission and "
                + "first-hit checks retain their original limits. Per-pixel half bits are reported for the worst "
                + "difference and first 32 exact-case differences; all pixels contribute to violation counts.";
            public List<Sample> samples = new List<Sample>();
        }

        public static Report LastReport = new Report();

        public static Report LastWindReport = new Report();

        /// <summary>전체 기존 검증을 반복하지 않고 두 출력 형식의 4분할 바람·누적·왕복 변경만 빠르게 검사합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Validate Bounded Temporal Wind")]
        public static async void StartWindValidation()
        {
            if (LastReport.running || LastWindReport.running)
            {
                return;
            }

            Report report = new Report();
            LastWindReport = report;
            report.running = true;
            GameObject host = new GameObject("Temporal wind validation");
            host.hideFlags = HideFlags.HideAndDontSave;
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.aspect = 16f / 9f;
            camera.fieldOfView = 68f;
            camera.farClipPlane = 100000f;
            CloudSkyProfile sky = UnityEngine.Object.Instantiate(CloudRayOptimizationValidation.Load<CloudSkyProfile>("Presets/Sky-ConceptV2.asset"));
            CloudStyleProfile style = UnityEngine.Object.Instantiate(CloudRayOptimizationValidation.Load<CloudStyleProfile>("Presets/Style-ConceptV2.asset"));
            try
            {
                ComputeShader shader = CloudRayOptimizationValidation.Load<ComputeShader>("Runtime/CloudRaymarch.compute");
                for (int format = 0; format < 2; format++)
                {
                    using (LostSkiesCloudRenderer reference = Create(shader, sky, style))
                    using (LostSkiesCloudRenderer candidate = Create(shader, sky, style))
                    {
                        reference.useCompactTargets = format == 1;
                        candidate.useCompactTargets = format == 1;
                        reference.Resize(320, 184);
                        candidate.Resize(320, 184);
                        CloudRayOptimizationValidation.Configure(0, camera, reference, sky, style);
                        CloudRayOptimizationValidation.Configure(0, camera, candidate, sky, style);
                        candidate.useTemporalReprojection = true;
                        candidate.useTemporalTileCompaction = true;
                        candidate.useTemporalDiagnostics = true;
                        candidate.temporalUpdatePhases = 4;
                        report.progress = "Wind format " + format;
                        Compare(report, "wind-seed-0", 4, reference, candidate, camera, true);
                        Compare(report, "wind-seed-1", 4, reference, candidate, camera, true);
                        await ValidateWindHistory(report, 4, reference, candidate, camera, style);
                        reference.settings.baseOffset += Vector3.right * 0.01f;
                        candidate.settings.baseOffset = reference.settings.baseOffset;
                        CheckReset(report, "wind-large-jump", 4, reference, candidate, camera);
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
                Directory.CreateDirectory("Screenshots");
                File.WriteAllText("Screenshots/Temporal-Wind-Validation.json", JsonUtility.ToJson(report, true));
            }
        }

        /// <summary>Editor 응답을 유지하며 GPU readback은 정확도 검사에만 사용합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Validate Temporal Reprojection")]
        public static async void Start()
        {
            if (LastReport.running || LastWindReport.running)
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
                ValidateLightingDescriptor();
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
                            candidate.useTemporalTileCompaction = true;
                            candidate.useTemporalDiagnostics = true;
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

                            // 같은 픽셀 유효성 상태에서 직접/간접 실행만 바꿔 색·깊이·투과율이 동일한지 검사합니다.
                            await ValidateCompactParity(report, sky, style, camera, phases, format == 1);

                            // 카메라 원점은 고정하고 빠르게 회전하여 화면 이탈과 새 윤곽의 재추적을 검사합니다.
                            for (int frame = 0; frame < 5; frame++)
                            {
                                camera.transform.rotation *= Quaternion.Euler(0, 3.5f, 0);
                                Sample sample = Compare(report, "rapid-rotation-" + frame, phases, reference, candidate, camera, false);
                                Require(sample.meanRgb < 0.015 && sample.p95Rgb < 0.06 && sample.meanTransmission < 0.015,
                                    "Rapid rotation exceeded the image error budget.");
                                await Task.Delay(1);
                            }

                            // 컷 기준보다 작은 횡이동으로 구름 뒤에서 드러나는 윤곽과 빈 광선의 보수적 거절을 검사합니다.
                            for (int frame = 0; frame < 4; frame++)
                            {
                                camera.transform.position += camera.transform.right * 24f;
                                Sample sample = Compare(report, "silhouette-disocclusion-" + frame, phases, reference, candidate, camera, false);
                                Require(sample.meanRgb < 0.015 && sample.p95Rgb < 0.06 && sample.meanTransmission < 0.015,
                                    "Translated silhouette disocclusion exceeded the image error budget.");
                                await Task.Delay(1);
                            }

                            CloudLightingState originalLighting = reference.skyLighting;
                            reference.skyLighting = candidate.skyLighting = CloudLightingState.ConceptDay();
                            Compare(report, "lighting-seed", phases, reference, candidate, camera, true);
                            int shapeResetsBeforeLighting = candidate.TemporalResets;
                            bool reusedChangingLight = false;
                            for (int frame = 0; frame < 8; frame++)
                            {
                                CloudLightingState changing = reference.skyLighting;
                                changing.cloudMidColor.r -= 0.0001f;
                                changing.cloudLightColor.g -= 0.0001f;
                                reference.skyLighting = candidate.skyLighting = changing;
                                Sample sample = Compare(report, "slow-lighting-" + frame, phases, reference, candidate, camera, false);
                                Require(sample.meanRgb < 0.01 && sample.p95Rgb < 0.04,
                                    "Bounded lighting history exceeded the image error budget.");
                                Require(candidate.TemporalResets == shapeResetsBeforeLighting,
                                    "Slow lighting discarded geometry history.");
                                if (sample.tracedFraction < 0.99f)
                                {
                                    reusedChangingLight = true;
                                }

                                await Task.Delay(1);
                            }

                            await ValidateWindHistory(report, phases, reference, candidate, camera, style);

                            Require(reusedChangingLight, "Slow lighting never reused history.");
                            report.boundedLightingReuse = true;
                            reference.skyLighting = candidate.skyLighting = CloudLightingState.MoonlitNight();
                            Sample abruptLight = Compare(report, "abrupt-lighting", phases, reference, candidate, camera, true);
                            Require(abruptLight.tracedFraction == 1f && candidate.TemporalResets == shapeResetsBeforeLighting,
                                "Abrupt lighting reused old RGB or discarded geometry validity.");
                            report.abruptLightingPreservedShape = true;
                            reference.skyLighting = candidate.skyLighting = originalLighting;
                            Compare(report, "lighting-restore", phases, reference, candidate, camera, true);

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

        /// <summary>RGB가 검어도 팔레트 경로가 사용하는 광원·주변광 스칼라 변화는 놓치지 않는지 검사합니다.</summary>
        private static void ValidateLightingDescriptor()
        {
            CloudLightingState sky = CloudLightingState.ConceptDay();
            sky.ambientSkyColor = Color.black;
            sky.ambientHorizonColor = Color.black;
            CloudTemporalLightingState initial = CloudTemporalLightingState.Capture(Vector3.up, Color.black, 1f, sky);
            Require(initial.Difference(initial) == 0, "Identical lighting acquired a nonzero history delta.");
            CloudTemporalLightingState direct = CloudTemporalLightingState.Capture(Vector3.up, Color.black, 0.5f, sky);
            Require(direct.Difference(initial) > CloudTemporalLightingState.MaximumHistoryInputChange,
                "Black direct RGB hid a palette intensity change.");
            sky.ambientIntensity *= 0.5f;
            CloudTemporalLightingState ambient = CloudTemporalLightingState.Capture(Vector3.up, Color.black, 1f, sky);
            Require(ambient.Difference(initial) > CloudTemporalLightingState.MaximumHistoryInputChange,
                "Black ambient RGB hid a palette intensity change.");
        }

        /// <summary>실제 wind5 비행과 한계에 가까운 누적·왕복 노이즈 변형을 전체 적분과 비교합니다.</summary>
        private static async Task ValidateWindHistory(Report report, int phases, LostSkiesCloudRenderer reference,
            LostSkiesCloudRenderer candidate, Camera camera, CloudStyleProfile style)
        {
            float width = reference.settings.geometryXExtent.y - reference.settings.geometryXExtent.x;
            float nominalStep = 5f / 30f * reference.settings.baseTile / width;
            bool nominalReuse = false;
            int resetCount = candidate.TemporalResets;
            for (int frame = 0; frame < 8; frame++)
            {
                reference.settings.baseOffset += Vector3.right * nominalStep;
                candidate.settings.baseOffset = reference.settings.baseOffset;
                camera.transform.position += camera.transform.forward * 2f;
                camera.transform.rotation *= Quaternion.Euler(0, 0.12f, 0);
                Sample sample = Compare(report, "wind5-flight-" + frame, phases, reference, candidate, camera, false);
                RequireWindBudget(sample);
                Require(candidate.TemporalResets == resetCount, "Normal wind5 incorrectly reset all geometry history.");
                Require(sample.maximumAge <= 2, "Changing wind kept an older deformation history.");
                nominalReuse |= sample.tracedFraction < 0.99f;
                await Task.Delay(1);
            }

            Require(nominalReuse, "Normal wind5 flight never reused history.");
            report.boundedWindReuse = true;
            float noiseScale = 0.13f * Mathf.Max(2000f, style.conceptBillowPeriod);
            float stressStep = 0.35f / noiseScale;
            bool cumulativeRejected = false;
            for (int frame = 0; frame < 6; frame++)
            {
                reference.settings.baseOffset += Vector3.right * stressStep;
                candidate.settings.baseOffset = reference.settings.baseOffset;
                Sample sample = Compare(report, "wind-cumulative-" + frame, phases, reference, candidate, camera, false);
                RequireWindBudget(sample);
                Require(candidate.TemporalResets == resetCount, "A bounded per-frame wind change reset all geometry history.");
                if (frame >= 2)
                {
                    Require(sample.maximumAge <= 1, "Cumulative deformation exceeded the declared0.5m noise-coordinate limit.");
                }

                cumulativeRejected |= sample.rejectionCounts[(int)CloudTemporalRejection.DeformationChanged] > 0;
                await Task.Delay(1);
            }

            if (phases == 4)
            {
                Require(cumulativeRejected, "Cumulative wind deformation did not record its rejection reason.");
                report.cumulativeWindRejections = true;
            }

            Vector3 anchor = reference.settings.baseOffset;
            bool reusedSecondAge = false;
            for (int frame = 0; frame < 8; frame++)
            {
                Vector3 wind = anchor;
                if (frame % 2 == 1)
                {
                    wind += Vector3.right * stressStep;
                }

                reference.settings.baseOffset = wind;
                candidate.settings.baseOffset = wind;
                Sample sample = Compare(report, "wind-reversal-" + frame, phases, reference, candidate, camera, false);
                RequireWindBudget(sample);
                Require(candidate.TemporalResets == resetCount, "Bounded reversing wind reset all geometry history.");
                if (frame >= 2)
                {
                    reusedSecondAge |= sample.maximumAge == 2;
                }

                await Task.Delay(1);
            }

            if (phases == 4)
            {
                Require(reusedSecondAge, "Wind reversal was accumulated as path length instead of candidate-to-current displacement.");
                report.windReversalReuse = true;
            }
        }

        /// <summary>노이즈 입력 변화 한계가 실제 색·투과율의 승인된 이동 검사 오차 안에 있는지 판정합니다.</summary>
        private static void RequireWindBudget(Sample sample)
        {
            Require(sample.meanRgb < 0.01 && sample.p95Rgb < 0.04 && sample.meanTransmission < 0.01,
                "Wind deformation exceeded the declared RGB/transmission image error budget: " + sample.scenario);
        }

        /// <summary>서로 독립된 이력에서 타일 목록 실행과 전체 화면 실행의 출력이 매 프레임 일치하는지 검사합니다.</summary>
        private static async Task ValidateCompactParity(Report report, CloudSkyProfile sky, CloudStyleProfile style,
            Camera camera, int phases, bool compact)
        {
            ComputeShader shader = CloudRayOptimizationValidation.Load<ComputeShader>("Runtime/CloudRaymarch.compute");
            using (LostSkiesCloudRenderer direct = Create(shader, sky, style))
            using (LostSkiesCloudRenderer indirect = Create(shader, sky, style))
            {
                direct.useCompactTargets = compact;
                indirect.useCompactTargets = compact;
                direct.Resize(160, 96);
                indirect.Resize(160, 96);
                direct.useTemporalReprojection = true;
                indirect.useTemporalReprojection = true;
                direct.temporalUpdatePhases = phases;
                indirect.temporalUpdatePhases = phases;
                indirect.useTemporalTileCompaction = true;
                for (int frame = 0; frame < 6; frame++)
                {
                    CloudRayOptimizationValidation.Frame expected = CloudRayOptimizationValidation.Draw(direct, camera);
                    CloudRayOptimizationValidation.Frame actual = CloudRayOptimizationValidation.Draw(indirect, camera);
                    CloudRayOptimizationValidation.Comparison comparison = CloudRayOptimizationValidation.Compare(expected, actual);
                    Require(comparison.maximumRgbError == 0 && comparison.maximumTransmissionError == 0
                        && comparison.maximumDepthError == 0, "Compacted tile dispatch changed a pixel.");
                    await Task.Delay(1);
                }
            }

            report.compactDispatchParity = true;
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
            sample.compactTiles = candidate.useTemporalTileCompaction;
            sample.windBaseOffset = candidate.settings.baseOffset;
            if (candidate.styleProfile.TryGetOceanShapeFieldParameters(out Vector4 shapeParameters, out Vector4 detailParameters))
            {
                sample.deformationNoisePeriod = detailParameters.x;
            }
            float[] errors = new float[actual.lighting.Length];
            for (int index = 0; index < errors.Length; index++)
            {
                Color delta = actual.lighting[index] - expected.lighting[index];
                errors[index] = Mathf.Max(Mathf.Abs(delta.r), Mathf.Abs(delta.g), Mathf.Abs(delta.b));
                RecordDepthPrecision(sample, expected.lighting[index].a, actual.lighting[index].a,
                    index, candidate.Width, exact);
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

            if (candidate.TemporalDepthMoments != null)
            {
                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(candidate.TemporalDepthMoments, 0);
                request.WaitForCompletion();
                Require(!request.hasError, "Temporal depth moments readback failed.");
                foreach (Vector2 moment in request.GetData<Vector2>())
                {
                    Require(!float.IsNaN(moment.x) && !float.IsInfinity(moment.x) && moment.x >= 0
                        && !float.IsNaN(moment.y) && !float.IsInfinity(moment.y) && moment.y >= 0,
                        "Non-finite or negative temporal depth moment.");
                    sample.maximumDepthVariance = Mathf.Max(sample.maximumDepthVariance, moment.y);
                }

                report.finiteDepthMoments = true;
                sample.rejectionCounts = candidate.ReadTemporalDiagnostics();
                uint total = 0;
                foreach (uint count in sample.rejectionCounts)
                {
                    total += count;
                }

                Require(total == candidate.Width * candidate.Height, "Temporal rejection counters do not partition all pixels.");
                float diagnosticTraced = 1f - sample.rejectionCounts[(int)CloudTemporalRejection.Reused] / (float)total;
                Require(Mathf.Abs(diagnosticTraced - sample.tracedFraction) < 0.00001f,
                    "Temporal diagnostics disagree with actual pixel history ages.");
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
                    && sample.depthPixelsBeyondOneHalfStep == 0 && sample.maximumFirstHit == 0,
                    "Exact condition changed: " + scenario);
            }

            return sample;
        }

        /// <summary>서로 다른 커널의 half 반올림 한 단위와 실제 깊이 계약 오차를 모든 픽셀에서 구분합니다.</summary>
        private static void RecordDepthPrecision(Sample sample, float expected, float actual,
            int index, int width, bool exact)
        {
            Require(!float.IsNaN(expected) && !float.IsInfinity(expected) && expected >= 0
                && !float.IsNaN(actual) && !float.IsInfinity(actual) && actual >= 0,
                "Non-finite or negative weighted cloud depth.");
            if (expected == actual)
            {
                return;
            }

            ushort expectedBits = Mathf.FloatToHalf(expected);
            ushort actualBits = Mathf.FloatToHalf(actual);
            Require(Mathf.HalfToFloat(expectedBits) == expected && Mathf.HalfToFloat(actualBits) == actual,
                "Weighted-depth readback did not preserve its declared half format.");
            int steps = Mathf.Abs((int)actualBits - expectedBits);
            float absoluteDifference = Mathf.Abs(expected - actual);
            sample.changedDepthPixels++;
            if (steps > 1)
            {
                sample.depthPixelsBeyondOneHalfStep++;
            }

            bool worst = steps > sample.maximumDepthHalfSteps;
            if (steps == sample.maximumDepthHalfSteps && sample.worstDepthHalfDifference != null
                && absoluteDifference > sample.worstDepthHalfDifference.absoluteDifference)
            {
                worst = true;
            }

            DepthHalfDifference difference = null;
            if (worst || (exact && sample.exactDepthDifferences.Count < 32))
            {
                difference = new DepthHalfDifference();
                difference.x = index % width;
                difference.y = index / width;
                difference.reference = expected;
                difference.actual = actual;
                difference.absoluteDifference = absoluteDifference;
                difference.halfSteps = steps;
                difference.referenceHalfBits = expectedBits;
                difference.actualHalfBits = actualBits;
            }

            if (worst)
            {
                sample.maximumDepthHalfSteps = steps;
                sample.worstDepthHalfDifference = difference;
            }

            if (exact && sample.exactDepthDifferences.Count < 32)
            {
                sample.exactDepthDifferences.Add(difference);
            }
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
