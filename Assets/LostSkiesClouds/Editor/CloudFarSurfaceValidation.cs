using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>표면 사용 픽셀과 volume 복귀 픽셀을 구분하여 실제 GPU 출력과 자원 수명을 검사합니다.</summary>
    public static class CloudFarSurfaceValidation
    {
        [Serializable]
        public sealed class Sample
        {
            public string scenario;
            public int pixels;
            public int acceptedPixels;
            public int fallbackPixels;
            public int coveredPixels;
            public uint[] counters;
            public long bytes;
            public int builds;
            public double meanRgb;
            public float p95Rgb;
            public float maximumRgb;
            public float maximumTransmission;
            public float maximumThresholdHit;
            public float maximumWeightedDepth;
            public int maximumWeightedDepthHalfSteps;
            public int weightedDepthPixelsBeyondOneHalfStep;
            public float maximumMomentMean;
            public float maximumMomentVariance;
            public float maximumVarianceRoundoffBound;
            public float maximumFallbackChannelError;
            public float maximumFallbackMomentError;
            public bool finite;
            public bool countersConsistent;
            public bool fallbackExact;
            public bool fallbackNumericalBudgetPassed;
            public float[] maximumFallbackLightingAbsoluteError = new float[4];
            public int[] maximumFallbackLightingHalfSteps = new int[4];
            public int[] fallbackLightingPixelsBeyondOneHalfStep = new int[4];
            public int[] fallbackLightingInvalidHalfPixels = new int[4];
            public int fallbackTransmissionMismatchPixels;
            public int fallbackThresholdHitMismatchPixels;
            public float maximumFallbackTransmissionError;
            public float maximumFallbackThresholdHitError;
            public float maximumFallbackMomentMeanError;
            public float maximumFallbackMomentMeanBudget;
            public float maximumFallbackMomentVarianceError;
            public float maximumFallbackMomentVarianceBudget;
            public int fallbackMomentMeanBeyondBudgetPixels;
            public int fallbackMomentVarianceBeyondBudgetPixels;
            public int fallbackMomentInvalidPixels;
            public bool expectedFallbackOnly;
            public bool depthContractPassed;
            public bool imageBudgetPassed;
            public bool passed;
        }

        [Serializable]
        public sealed class Report
        {
            public bool running;
            public bool completed;

            public bool noiseLibraryReady;

            public int referenceNoisePreparationsAfterFirstDraw;

            public int candidateNoisePreparationsAfterFirstDraw;

            public int referenceNoiseGenerations;

            public int candidateNoiseGenerations;

            public string noiseSource;
            public bool passed;
            public bool contractsPassed;
            public bool contractsNumericalBudgetPassed;
            public bool passedNumericalBudget;
            public string fallbackPrecisionPolicy = "Strict fallbackExact/contractsPassed/passed are unchanged. "
                + "A separate numerical result measures every fallback pixel. Each RGB and lighting-alpha channel must round-trip "
                + "through a finite float16 encoding and differ by at most one adjacent value (signed ordering for RGB; alpha nonnegative). "
                + "T and threshold hit must remain exactly equal. Mean error <=16 float32 ULP(reference mean), "
                + "variance error <=16 ULP(reference mean squared), evaluated per pixel. No global metre/RGB tolerance is substituted. "
                + "The largest absolute differences, encoding-step counts, out-of-budget pixel counts and local-budget maxima are recorded. "
                + "This extra diagnostic budget does not turn a strict failure into a strict pass.";
            public bool imageBudgetPassed;
            public bool emittedNonzero;
            public bool acceptedNonzero;
            public bool thinRejectionObserved;
            public bool weatherRestoredExact;
            public bool windRestoredExact;
            public bool originRestoredExact;
            public bool stableAllocation;
            public bool cameraIsolation;
            public bool offReleasesAll;
            public bool unsupportedReleasesAll;
            public bool resizeReleasesOld;
            public bool disposeReleasesAll;
            public string progress;
            public string error;
            public string notes = "Diagnostic GPU reads, not performance timings. Exact fallback means every color/T/depth/moment channel is bit-value equal. "
                + "Surface color budget: mean RGB <=0.005 and P95 <=0.02. Successful mean-depth rounding bound is 16 float32 ULP; "
                + "variance cancellation bound is 16 ULP of mean^2. Successful lighting alpha must round-trip through float16 and differ "
                + "by at most one adjacent finite nonnegative float16 encoding per pixel, not a fixed metre threshold. "
                + "T must be exactly zero for both successful outputs after the existing T<=.008 opaque stop; threshold hit remains exact. "
                + "Finite outputs and real non-null RGFloat moment buffers are required. Strict differences are always recorded. "
                + "Temporal is cleared before each compared frame to expose actual RGFloat depth moments. "
                + "Weather/wind tests set interruption/reversal input sequences; the full weather controller transition test remains separate. "
                + "Any zero accepted/emitted result fails completion even if every fallback image is exact.";
            public List<Sample> samples = new List<Sample>();
        }

        private sealed class Capture
        {
            public CloudRayOptimizationValidation.Frame frame;
            public Color[] moments;
            public Color[] usage;
            public uint[] counters;
        }

        public static Report LastReport = new Report();

        /// <summary>같은 정확 밀도에서 성공·fallback·입력 갱신·GPU 자원 수명을 실제 렌더로 비교합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Validate Far Surface Prototype")]
        public static async void Start()
        {
            if (LastReport.running)
            {
                return;
            }

            Report report = new Report();
            report.running = true;
            report.contractsPassed = true;
            report.contractsNumericalBudgetPassed = true;
            report.imageBudgetPassed = true;
            LastReport = report;
            GameObject host = new GameObject("Cloud far surface validation");
            host.hideFlags = HideFlags.HideAndDontSave;
            GameObject secondHost = new GameObject("Cloud far surface second camera");
            secondHost.hideFlags = HideFlags.HideAndDontSave;
            Camera camera = CreateCamera(host);
            Camera secondCamera = CreateCamera(secondHost);
            CloudSkyProfile sky = UnityEngine.Object.Instantiate(CloudRayOptimizationValidation.Load<CloudSkyProfile>("Presets/Sky-ConceptV2.asset"));
            CloudStyleProfile style = UnityEngine.Object.Instantiate(CloudRayOptimizationValidation.Load<CloudStyleProfile>("Presets/Style-ConceptV2.asset"));
            LostSkiesCloudRenderer reference = null;
            LostSkiesCloudRenderer candidate = null;
            try
            {
                CloudNoiseLibrary.LoadAll();
                report.noiseLibraryReady = true;
                reference = CreateRenderer(sky, style);
                candidate = CreateRenderer(sky, style);
                candidate.useFarSurface = true;
                candidate.useFarSurfaceDiagnostics = true;
                ConfigureFar(camera, reference, candidate, sky, style);
                Compare(report, "far-ocean-success-candidate", reference, candidate, camera, false);
                report.referenceNoisePreparationsAfterFirstDraw = reference.NoisePreparationCount;
                report.candidateNoisePreparationsAfterFirstDraw = candidate.NoisePreparationCount;
                report.referenceNoiseGenerations = reference.NoiseGenerationCount;
                report.candidateNoiseGenerations = candidate.NoiseGenerationCount;
                report.noiseSource = candidate.NoiseSource;
                Require(report.referenceNoisePreparationsAfterFirstDraw == 6
                    && report.candidateNoisePreparationsAfterFirstDraw == 6
                    && report.referenceNoiseGenerations == 0 && report.candidateNoiseGenerations == 0,
                    "Far-surface validation did not use exactly six preserved noise resources per renderer.");
                Debug.Log("Cloud far-surface validation noise: reference preparations="
                    + report.referenceNoisePreparationsAfterFirstDraw + ", candidate preparations="
                    + report.candidateNoisePreparationsAfterFirstDraw + ", generator dispatches=0, source=" + report.noiseSource);
                long stableBytes = candidate.FarSurfaceBytes;
                RenderTexture stableUsage = candidate.FarSurfaceUsage;
                int previousBuilds = candidate.FarSurfaceBuilds;
                Compare(report, "far-ocean-static-repeat", reference, candidate, camera, false);
                report.stableAllocation = candidate.FarSurfaceBytes == stableBytes
                    && candidate.FarSurfaceUsage == stableUsage && candidate.FarSurfaceBuilds > previousBuilds;

                for (int pose = 0; pose < 3; pose++)
                {
                    camera.transform.position = new Vector3(6000 + pose * 6500, 17000 + pose * 1000, -6000 - pose * 4000);
                    Compare(report, "far-ocean-success-search-" + pose, reference, candidate, camera, false);
                    await Task.Delay(1);
                }

                ConfigureFar(camera, reference, candidate, sky, style);
                camera.transform.position = new Vector3(6000, 7000, -6000);
                SetDepth(reference, camera, 8000);
                SetDepth(candidate, camera, 8000);
                Compare(report, "near-clipped-exact", reference, candidate, camera, true);
                camera.transform.position = new Vector3(6000, 0, -6000);
                SetDepth(reference, camera, 1500);
                SetDepth(candidate, camera, 1500);
                Compare(report, "inside-clipped-exact", reference, candidate, camera, true);

                ConfigureFar(camera, reference, candidate, sky, style);
                reference.includeOcean = false;
                candidate.includeOcean = false;
                sky.altitude = new Vector2(-60000, -60000);
                sky.occupiedCells = 1;
                camera.fieldOfView = 68;
                camera.transform.SetPositionAndRotation(new Vector3(2000, 7200, -9000), Quaternion.Euler(0, 24, 0));
                Compare(report, "thin-ribbons-exact", reference, candidate, camera, true);
                reference.includeOcean = true;
                candidate.includeOcean = true;
                camera.transform.SetPositionAndRotation(new Vector3(2000, 22000, -9000), Quaternion.Euler(45, 24, 0));
                Sample layers = Compare(report, "thin-front-and-far-ocean", reference, candidate, camera, false);
                report.thinRejectionObserved = layers.counters[3] > 0 || layers.counters[15] > 0;

                ConfigureFar(camera, reference, candidate, sky, style);
                SetDepth(reference, camera, 10000);
                SetDepth(candidate, camera, 10000);
                Compare(report, "foreground-blocks-proxy-exact", reference, candidate, camera, true);

                ConfigureFar(camera, reference, candidate, sky, style);
                Capture restoredStart = Draw(candidate, camera);
                float[] coverage = { 0.165f, 0.3f, 0.22f, 0.1f, 0.165f };
                float[] densities = { 160000, 240000, 190000, 120000, 160000 };
                for (int state = 0; state < coverage.Length; state++)
                {
                    reference.settings.coverageIntensity = coverage[state];
                    candidate.settings.coverageIntensity = coverage[state];
                    reference.settings.density = densities[state];
                    candidate.settings.density = densities[state];
                    Compare(report, "weather-interrupt-reverse-input-" + state, reference, candidate, camera, false);
                    await Task.Delay(1);
                }

                report.weatherRestoredExact = MaximumDifference(restoredStart, Draw(candidate, camera)) == 0;
                Vector3 initialWind = candidate.settings.baseOffset;
                float[] wind = { 0, 0.001f, 0.002f, 0.001f, -0.001f, 0 };
                for (int state = 0; state < wind.Length; state++)
                {
                    reference.settings.baseOffset = initialWind + new Vector3(wind[state], 0, wind[state] * 0.4f);
                    candidate.settings.baseOffset = reference.settings.baseOffset;
                    Compare(report, "wind-input-reversal-" + state, reference, candidate, camera, false);
                    await Task.Delay(1);
                }

                report.windRestoredExact = MaximumDifference(restoredStart, Draw(candidate, camera)) == 0;
                Vector3 oldPosition = camera.transform.position;
                Vector3 shift = new Vector3(180000, 0, -190000);
                reference.worldOriginOffset = shift;
                candidate.worldOriginOffset = shift;
                camera.transform.position -= shift;
                Compare(report, "origin-shift-same-density-space", reference, candidate, camera, false);
                Capture shifted = Draw(candidate, camera);
                reference.worldOriginOffset = Vector3.zero;
                candidate.worldOriginOffset = Vector3.zero;
                camera.transform.position = oldPosition;
                Capture originRestored = Draw(candidate, camera);
                report.originRestoredExact = MaximumDifference(restoredStart, shifted) == 0
                    && MaximumDifference(restoredStart, originRestored) == 0;

                CloudLightingState light = reference.skyLighting;
                light.sunElevation = 2;
                light.cloudPaletteBlend = 0.5f;
                reference.skyLighting = light;
                candidate.skyLighting = light;
                Compare(report, "sunset-mixed-palette", reference, candidate, camera, false);
                light.cloudPaletteBlend = 1;
                light.sunLux = 0;
                light.moonLux = 0.1f;
                light.cloudLightColor = new Color(0.08f, 0.16f, 0.4f);
                reference.skyLighting = light;
                candidate.skyLighting = light;
                Compare(report, "night-palette", reference, candidate, camera, false);

                ConfigureFar(camera, reference, candidate, sky, style);
                RenderTexture oldUsage = candidate.FarSurfaceUsage;
                reference.Resize(160, 96);
                candidate.Resize(160, 96);
                Compare(report, "resize-small", reference, candidate, camera, false);
                report.resizeReleasesOld = oldUsage == null || !oldUsage.IsCreated();
                reference.Resize(320, 192);
                candidate.Resize(320, 192);
                Compare(report, "resize-restored", reference, candidate, camera, false);

                int firstId = camera.GetEntityId().GetHashCode();
                int secondId = secondCamera.GetEntityId().GetHashCode();
                candidate.Resize(320, 192, firstId);
                Capture firstCamera = Draw(candidate, camera);
                RenderTexture firstUsage = candidate.FarSurfaceUsage;
                secondCamera.transform.SetPositionAndRotation(camera.transform.position + new Vector3(8000, 0, 0), camera.transform.rotation);
                secondCamera.fieldOfView = camera.fieldOfView;
                candidate.Resize(320, 192, secondId);
                Draw(candidate, secondCamera);
                RenderTexture secondUsage = candidate.FarSurfaceUsage;
                candidate.Resize(320, 192, firstId);
                Capture firstAgain = Draw(candidate, camera);
                report.cameraIsolation = firstUsage != secondUsage && candidate.FarSurfaceUsage == firstUsage
                    && MaximumDifference(firstCamera, firstAgain) == 0;

                // 두 카메라의 표면이 실제 살아 있는 상태에서 전역 비호환 옵션을 켭니다.
                Require(firstUsage != null && secondUsage != null && firstUsage.IsCreated() && secondUsage.IsCreated(),
                    "Multi-camera surface resources were not live before the unsupported-combination test.");
                candidate.useAdaptiveIntegration = true;
                Draw(candidate, camera);
                report.unsupportedReleasesAll = candidate.FarSurfaceBytes == 0;
                candidate.useAdaptiveIntegration = false;
                candidate.Resize(320, 192, firstId);
                Draw(candidate, camera);
                RenderTexture reenabledFirst = candidate.FarSurfaceUsage;
                candidate.Resize(320, 192, secondId);
                Draw(candidate, secondCamera);
                Require(reenabledFirst != null && reenabledFirst.IsCreated()
                    && candidate.FarSurfaceUsage != null && candidate.FarSurfaceUsage != reenabledFirst,
                    "Two camera surfaces did not reallocate after unsupported-combination cleanup.");
                candidate.Resize(320, 192, firstId);

                candidate.useFarSurface = false;
                Draw(candidate, camera);
                report.offReleasesAll = candidate.FarSurfaceBytes == 0;
                candidate.useFarSurface = true;
                candidate.useNormalFieldCache = true;
                Draw(candidate, camera);
                Require(candidate.FarSurfaceUsage == null, "Normal-field combination did not fall back.");
                candidate.useNormalFieldCache = false;
                candidate.useAdaptiveIntegration = true;
                Draw(candidate, camera);
                Require(candidate.FarSurfaceUsage == null, "Adaptive combination did not fall back.");
                candidate.useAdaptiveIntegration = false;
                style.method = CloudStyleProfile.ShapeMethod.SoftNoise;
                Draw(candidate, camera);
                Require(candidate.FarSurfaceUsage == null, "Unsupported shape profile did not fall back.");
                style.method = CloudStyleProfile.ShapeMethod.ConceptV2;
                Draw(candidate, camera);
                Require(candidate.FarSurfaceBytes > 0, "Re-enabled surface did not allocate.");
                candidate.Dispose();
                report.disposeReleasesAll = candidate.FarSurfaceBytes == 0;
                report.contractsPassed &= report.emittedNonzero && report.acceptedNonzero && report.thinRejectionObserved
                    && report.weatherRestoredExact && report.windRestoredExact && report.originRestoredExact
                    && report.stableAllocation && report.cameraIsolation && report.offReleasesAll && report.unsupportedReleasesAll
                    && report.resizeReleasesOld && report.disposeReleasesAll;
                report.passed = report.contractsPassed && report.imageBudgetPassed;
                report.contractsNumericalBudgetPassed &= report.emittedNonzero && report.acceptedNonzero && report.thinRejectionObserved
                    && report.weatherRestoredExact && report.windRestoredExact && report.originRestoredExact
                    && report.stableAllocation && report.cameraIsolation && report.offReleasesAll && report.unsupportedReleasesAll
                    && report.resizeReleasesOld && report.disposeReleasesAll;
                report.passedNumericalBudget = report.contractsNumericalBudgetPassed && report.imageBudgetPassed;
            }
            catch (Exception exception)
            {
                report.passed = false;
                report.contractsPassed = false;
                report.contractsNumericalBudgetPassed = false;
                report.passedNumericalBudget = false;
                report.error = exception.ToString();
                Debug.LogException(exception);
            }
            finally
            {
                if (reference != null)
                {
                    reference.Dispose();
                }

                if (candidate != null)
                {
                    candidate.Dispose();
                }

                UnityEngine.Object.DestroyImmediate(sky);
                UnityEngine.Object.DestroyImmediate(style);
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(secondHost);
                report.running = false;
                report.completed = true;
                Directory.CreateDirectory("Docs/Validation");
                File.WriteAllText("Docs/Validation/Redesign-FarSurface.json", JsonUtility.ToJson(report, true));
            }
        }

        /// <summary>독립적인 비활성 원근 카메라를 만듭니다.</summary>
        private static Camera CreateCamera(GameObject host)
        {
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.aspect = 16f / 9f;
            camera.farClipPlane = 100000;
            return camera;
        }

        /// <summary>정확 밀도 경로와 첫 프레임 depth moments를 지원하는 렌더러를 만듭니다.</summary>
        private static LostSkiesCloudRenderer CreateRenderer(CloudSkyProfile sky, CloudStyleProfile style)
        {
            LostSkiesCloudRenderer renderer = new LostSkiesCloudRenderer(
                CloudRayOptimizationValidation.Load<ComputeShader>("Runtime/CloudRaymarch.compute"),
                null,
                CloudRayOptimizationValidation.Load<TextAsset>("Presets/normal.json"),
                CloudRayOptimizationValidation.Load<ComputeShader>("Runtime/CloudTemporal.compute"));
            renderer.oceanProfile = CloudRayOptimizationValidation.Load<CloudOceanProfile>("Presets/ContinuousOcean.asset");
            renderer.skyProfile = sky;
            renderer.styleProfile = style;
            renderer.useCompactTargets = true;
            renderer.useShapeCellCache = true;
            renderer.temporalUpdatePhases = 4;
            renderer.Resize(320, 192);
            return renderer;
        }

        /// <summary>기본 성공 후보를 현재 ConceptV2의 넓은 원경 운해 상면으로 고정합니다.</summary>
        private static void ConfigureFar(Camera camera, LostSkiesCloudRenderer reference,
            LostSkiesCloudRenderer candidate, CloudSkyProfile sky, CloudStyleProfile style)
        {
            CloudRayOptimizationValidation.Configure(0, camera, reference, sky, style);
            CloudRayOptimizationValidation.Configure(0, camera, candidate, sky, style);
            sky.occupiedCells = 0;
            camera.fieldOfView = 40;
            camera.transform.SetPositionAndRotation(new Vector3(6000, 17000, -6000), Quaternion.Euler(90, 0, 0));
            reference.includeOcean = true;
            candidate.includeOcean = true;
            reference.settings.density = 160000;
            candidate.settings.density = 160000;
            reference.settings.coverageIntensity = 0.165f;
            candidate.settings.coverageIntensity = 0.165f;
            reference.settings.baseOffset = Vector3.zero;
            candidate.settings.baseOffset = Vector3.zero;
            SetDepth(reference, camera, 0);
            SetDepth(candidate, camera, 0);
        }

        /// <summary>이력 재사용 없이 첫 temporal 광선의 실제 mean/variance와 surface 사용 mask를 읽습니다.</summary>
        private static Capture Draw(LostSkiesCloudRenderer renderer, Camera camera)
        {
            renderer.useTemporalReprojection = false;
            CloudRayOptimizationValidation.Draw(renderer, camera);
            renderer.useTemporalReprojection = true;
            Capture capture = new Capture();
            capture.frame = CloudRayOptimizationValidation.Draw(renderer, camera);
            capture.moments = Read(renderer.TemporalDepthMoments, capture.frame.lighting.Length);
            capture.usage = Read(renderer.FarSurfaceUsage, capture.frame.lighting.Length);
            capture.counters = renderer.ReadFarSurfaceDiagnostics();
            return capture;
        }

        /// <summary>압축 출력과 RGFloat 진단 버퍼를 공통 float 채널 배열로 읽습니다.</summary>
        private static Color[] Read(RenderTexture texture, int pixels)
        {
            if (texture == null)
            {
                return new Color[pixels];
            }

            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBAFloat);
            request.WaitForCompletion();
            Require(!request.hasError, "Far surface GPU readback failed.");
            return request.GetData<Color>().ToArray();
        }

        /// <summary>승인과 fallback mask별 색·깊이 계약을 비교하고 모든 실패 수치를 보존합니다.</summary>
        private static Sample Compare(Report report, string name, LostSkiesCloudRenderer reference,
            LostSkiesCloudRenderer candidate, Camera camera, bool fallbackOnly)
        {
            report.progress = name;
            Capture expected = Draw(reference, camera);
            Capture actual = Draw(candidate, camera);
            Require(reference.TemporalActive && candidate.TemporalActive
                && reference.TemporalDepthMoments != null && candidate.TemporalDepthMoments != null,
                "Compared depth moments must come from real first-frame temporal GPU targets, not missing-texture zero arrays.");
            Sample sample = new Sample();
            sample.scenario = name;
            sample.pixels = actual.frame.lighting.Length;
            sample.counters = actual.counters;
            sample.bytes = candidate.FarSurfaceBytes;
            sample.builds = candidate.FarSurfaceBuilds;
            sample.expectedFallbackOnly = fallbackOnly;
            sample.finite = true;
            sample.depthContractPassed = true;
            sample.fallbackNumericalBudgetPassed = true;
            float[] rgb = new float[sample.pixels];
            for (int index = 0; index < sample.pixels; index++)
            {
                Color a = actual.frame.lighting[index];
                Color e = expected.frame.lighting[index];
                Color at = actual.frame.transmission[index];
                Color et = expected.frame.transmission[index];
                Color am = actual.moments[index];
                Color em = expected.moments[index];
                sample.finite &= Finite(a) && Finite(at) && Finite(am) && Finite(e) && Finite(et) && Finite(em)
                    && am.r >= 0 && am.g >= 0 && em.r >= 0 && em.g >= 0;
                float error = Mathf.Max(Mathf.Abs(a.r - e.r), Mathf.Abs(a.g - e.g), Mathf.Abs(a.b - e.b));
                rgb[index] = error;
                sample.meanRgb += error / sample.pixels;
                sample.maximumRgb = Mathf.Max(sample.maximumRgb, error);
                sample.maximumTransmission = Mathf.Max(sample.maximumTransmission, Mathf.Abs(at.r - et.r));
                sample.maximumThresholdHit = Mathf.Max(sample.maximumThresholdHit, Mathf.Abs(at.g - et.g));
                sample.maximumWeightedDepth = Mathf.Max(sample.maximumWeightedDepth, Mathf.Abs(a.a - e.a));
                sample.maximumMomentMean = Mathf.Max(sample.maximumMomentMean, Mathf.Abs(am.r - em.r));
                sample.maximumMomentVariance = Mathf.Max(sample.maximumMomentVariance, Mathf.Abs(am.g - em.g));
                float varianceBound = 16 * FloatUlp(em.r * em.r);
                sample.maximumVarianceRoundoffBound = Mathf.Max(sample.maximumVarianceRoundoffBound, varianceBound);
                if (et.r < 0.9f)
                {
                    sample.coveredPixels++;
                }

                if (actual.usage[index].r > 0.5f)
                {
                    sample.acceptedPixels++;
                    int halfSteps;
                    bool storedDepthPassed = WithinOneHalfStep(e.a, a.a, out halfSteps);
                    sample.maximumWeightedDepthHalfSteps = Mathf.Max(sample.maximumWeightedDepthHalfSteps, halfSteps);
                    if (!storedDepthPassed)
                    {
                        sample.weightedDepthPixelsBeyondOneHalfStep++;
                    }

                    sample.depthContractPassed &= storedDepthPassed && at.r == 0 && et.r == 0 && at.g == et.g
                        && Mathf.Abs(am.r - em.r) <= 16 * FloatUlp(em.r)
                        && Mathf.Abs(am.g - em.g) <= varianceBound;
                }
                else
                {
                    sample.fallbackPixels++;
                    float channels = Mathf.Max(MaximumChannel(a - e), MaximumChannel(at - et));
                    sample.maximumFallbackChannelError = Mathf.Max(sample.maximumFallbackChannelError, channels);
                    sample.maximumFallbackMomentError = Mathf.Max(sample.maximumFallbackMomentError, MaximumChannel(am - em));
                    RecordFallbackPrecision(sample, e, a, et, at, em, am);
                }
            }

            Array.Sort(rgb);
            sample.p95Rgb = rgb[Mathf.FloorToInt((rgb.Length - 1) * 0.95f)];
            sample.fallbackExact = sample.maximumFallbackChannelError == 0 && sample.maximumFallbackMomentError == 0;
            sample.imageBudgetPassed = sample.meanRgb <= 0.005 && sample.p95Rgb <= 0.02f;
            uint buildOutcomes = 0;
            for (int index = 1; index <= 5; index++)
            {
                buildOutcomes += sample.counters[index];
            }

            uint queryFailures = 0;
            for (int index = 11; index <= 16; index++)
            {
                queryFailures += sample.counters[index];
            }

            sample.countersConsistent = sample.counters.Length == 20 && sample.counters[0] == buildOutcomes
                && sample.counters[9] == sample.counters[10] + queryFailures
                && sample.counters[19] == queryFailures && sample.counters[10] == sample.acceptedPixels;
            sample.passed = sample.finite && sample.countersConsistent && sample.fallbackExact
                && sample.depthContractPassed && sample.imageBudgetPassed;
            if (fallbackOnly)
            {
                sample.passed &= sample.acceptedPixels == 0;
                if (name != "foreground-blocks-proxy-exact")
                {
                    sample.passed &= sample.coveredPixels > 0;
                }
            }

            report.emittedNonzero |= sample.counters[1] > 0;
            report.acceptedNonzero |= sample.acceptedPixels > 0;
            report.contractsPassed &= sample.finite && sample.countersConsistent && sample.fallbackExact
                && sample.depthContractPassed && (!fallbackOnly || sample.acceptedPixels == 0);
            report.contractsNumericalBudgetPassed &= sample.finite && sample.countersConsistent && sample.fallbackNumericalBudgetPassed
                && sample.depthContractPassed && (!fallbackOnly || sample.acceptedPixels == 0);
            if (fallbackOnly && name != "foreground-blocks-proxy-exact" && sample.coveredPixels == 0)
            {
                report.contractsPassed = false;
                report.contractsNumericalBudgetPassed = false;
            }

            report.imageBudgetPassed &= sample.imageBudgetPassed;
            report.samples.Add(sample);
            return sample;
        }

        /// <summary>fallback의 각 저장 채널과 실제 모멘트를 픽셀별 표현 간격으로 별도 계측합니다.</summary>
        private static void RecordFallbackPrecision(Sample sample, Color expectedLight, Color actualLight,
            Color expectedTransmission, Color actualTransmission, Color expectedMoments, Color actualMoments)
        {
            for (int channel = 0; channel < 4; channel++)
            {
                float expected = expectedLight[channel];
                float actual = actualLight[channel];
                int steps;
                bool valid = HalfEncodingDistance(expected, actual, out steps);
                if (channel == 3 && (expected < 0 || actual < 0))
                {
                    valid = false;
                }

                sample.maximumFallbackLightingAbsoluteError[channel] = Mathf.Max(
                    sample.maximumFallbackLightingAbsoluteError[channel], Mathf.Abs(actual - expected));
                sample.maximumFallbackLightingHalfSteps[channel] = Mathf.Max(
                    sample.maximumFallbackLightingHalfSteps[channel], steps);
                if (!valid)
                {
                    sample.fallbackLightingInvalidHalfPixels[channel]++;
                }

                bool channelPassed = valid && steps <= 1;
                if (!channelPassed)
                {
                    sample.fallbackLightingPixelsBeyondOneHalfStep[channel]++;
                }

                sample.fallbackNumericalBudgetPassed &= channelPassed;
            }

            float transmissionError = Mathf.Abs(actualTransmission.r - expectedTransmission.r);
            float hitError = Mathf.Abs(actualTransmission.g - expectedTransmission.g);
            sample.maximumFallbackTransmissionError = Mathf.Max(sample.maximumFallbackTransmissionError, transmissionError);
            sample.maximumFallbackThresholdHitError = Mathf.Max(sample.maximumFallbackThresholdHitError, hitError);
            if (actualTransmission.r != expectedTransmission.r)
            {
                sample.fallbackTransmissionMismatchPixels++;
            }

            if (actualTransmission.g != expectedTransmission.g)
            {
                sample.fallbackThresholdHitMismatchPixels++;
            }

            sample.fallbackNumericalBudgetPassed &= Finite(expectedTransmission) && Finite(actualTransmission)
                && actualTransmission.r == expectedTransmission.r && actualTransmission.g == expectedTransmission.g;
            bool validMoments = Finite(expectedMoments) && Finite(actualMoments)
                && expectedMoments.r >= 0 && expectedMoments.g >= 0 && actualMoments.r >= 0 && actualMoments.g >= 0;
            if (!validMoments)
            {
                sample.fallbackMomentInvalidPixels++;
                sample.fallbackNumericalBudgetPassed = false;
                return;
            }

            float meanError = Mathf.Abs(actualMoments.r - expectedMoments.r);
            float varianceError = Mathf.Abs(actualMoments.g - expectedMoments.g);
            float meanBudget = 16 * FloatUlp(expectedMoments.r);
            float varianceBudget = 16 * FloatUlp(expectedMoments.r * expectedMoments.r);
            sample.maximumFallbackMomentMeanError = Mathf.Max(sample.maximumFallbackMomentMeanError, meanError);
            sample.maximumFallbackMomentMeanBudget = Mathf.Max(sample.maximumFallbackMomentMeanBudget, meanBudget);
            sample.maximumFallbackMomentVarianceError = Mathf.Max(sample.maximumFallbackMomentVarianceError, varianceError);
            sample.maximumFallbackMomentVarianceBudget = Mathf.Max(sample.maximumFallbackMomentVarianceBudget, varianceBudget);
            if (meanError > meanBudget)
            {
                sample.fallbackMomentMeanBeyondBudgetPixels++;
            }

            if (varianceError > varianceBudget)
            {
                sample.fallbackMomentVarianceBeyondBudgetPixels++;
            }

            sample.fallbackNumericalBudgetPassed &= meanError <= meanBudget && varianceError <= varianceBudget;
        }

        /// <summary>음수 RGB와 부호가 다른 0도 포함하여 실제 인접 float16 값 사이 거리를 구합니다.</summary>
        private static bool HalfEncodingDistance(float expected, float actual, out int steps)
        {
            steps = int.MaxValue;
            if (!float.IsFinite(expected) || !float.IsFinite(actual))
            {
                return false;
            }

            ushort expectedBits = Mathf.FloatToHalf(expected);
            ushort actualBits = Mathf.FloatToHalf(actual);
            if (Mathf.HalfToFloat(expectedBits) != expected || Mathf.HalfToFloat(actualBits) != actual)
            {
                return false;
            }

            int expectedOrdered = 32768 + expectedBits;
            if ((expectedBits & 32768) != 0)
            {
                expectedOrdered = 32768 - (expectedBits & 32767);
            }

            int actualOrdered = 32768 + actualBits;
            if ((actualBits & 32768) != 0)
            {
                actualOrdered = 32768 - (actualBits & 32767);
            }

            steps = Mathf.Abs(actualOrdered - expectedOrdered);
            return true;
        }

        /// <summary>원점·날씨·바람 복원 시 출력 전체가 정확히 돌아왔는지 비교합니다.</summary>
        private static float MaximumDifference(Capture a, Capture b)
        {
            float result = 0;
            Require(a.frame.lighting.Length == b.frame.lighting.Length, "Restoration resolution changed.");
            for (int index = 0; index < a.frame.lighting.Length; index++)
            {
                result = Mathf.Max(result, MaximumChannel(a.frame.lighting[index] - b.frame.lighting[index]));
                result = Mathf.Max(result, MaximumChannel(a.frame.transmission[index] - b.frame.transmission[index]));
                result = Mathf.Max(result, MaximumChannel(a.moments[index] - b.moments[index]));
                result = Mathf.Max(result, Mathf.Abs(a.usage[index].r - b.usage[index].r));
            }

            return result;
        }

        /// <summary>출력의 네 채널 중 가장 큰 절대 차이를 반환합니다.</summary>
        private static float MaximumChannel(Color value)
        {
            return Mathf.Max(Mathf.Abs(value.r), Mathf.Abs(value.g), Mathf.Abs(value.b), Mathf.Abs(value.a));
        }

        /// <summary>모든 GPU 출력 채널의 NaN/Infinity를 거절합니다.</summary>
        private static bool Finite(Color value)
        {
            for (int index = 0; index < 4; index++)
            {
                if (float.IsNaN(value[index]) || float.IsInfinity(value[index]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>저장된 대표 깊이를 픽셀별 인접 float16 인코딩으로 비교하며 전역 미터 허용치를 쓰지 않습니다.</summary>
        private static bool WithinOneHalfStep(float expected, float actual, out int steps)
        {
            steps = int.MaxValue;
            if (!float.IsFinite(expected) || !float.IsFinite(actual) || expected < 0 || actual < 0)
            {
                return false;
            }

            ushort expectedBits = Mathf.FloatToHalf(expected);
            ushort actualBits = Mathf.FloatToHalf(actual);
            if (Mathf.HalfToFloat(expectedBits) != expected || Mathf.HalfToFloat(actualBits) != actual)
            {
                return false;
            }

            steps = Mathf.Abs((int)actualBits - expectedBits);
            return steps <= 1;
        }

        /// <summary>양수 float32의 인접 표현 간격을 깊이 모멘트 반올림 범위에 사용합니다.</summary>
        private static float FloatUlp(float value)
        {
            value = Mathf.Abs(value);
            int bits = BitConverter.SingleToInt32Bits(value);
            return BitConverter.Int32BitsToSingle(bits + 1) - value;
        }

        /// <summary>현재 카메라 깊이 입력에 일정한 불투명 전경 평면을 기록합니다.</summary>
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

        /// <summary>필수 실행 조건이 누락되면 통과로 포장하지 않고 보고서를 실패시킵니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
