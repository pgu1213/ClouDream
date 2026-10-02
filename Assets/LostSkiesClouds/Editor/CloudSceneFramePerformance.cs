using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>FHD Game 뷰의 GPU 마커와 프레임 API 값을 수집하고 두 값의 일관성을 별도로 검사합니다.</summary>
    public static class CloudSceneFramePerformance
    {
        [Serializable]
        public sealed class Measurement
        {
            public string view;
            public bool intervals;
            public bool spatial;
            public bool paletteFastPath;
            public bool paletteFastPathActive;
            public int temporalPhases;
            public bool temporalActive;
            public long temporalBytes;
            public string temporalStatus;
            public bool depthAwareUpsampling;
            public float resolutionScale;
            public float spatialCellSize;
            public long spatialBytes;
            public Vector3 densityPosition;
            public Quaternion rotation;
            public int cloudWidth;
            public int cloudHeight;
            public double[] gpuMilliseconds;
            public double[] cpuMilliseconds;
            public double gpuMedian;
            public double gpuP95;
            public double cpuMedian;
            public double cpuP95;
            public double[] rayMilliseconds;
            public double[] depthMilliseconds;
            public double[] compositeMilliseconds;
            public double rayMedian;
            public double rayP95;
            public double depthMedian;
            public double compositeMedian;
            public double[] totalCloudMilliseconds;
            public double[] spatialBuildMilliseconds;
            public double totalCloudMedian;
            public double totalCloudP95;
            public double spatialBuildMedian;
            public double spatialBuildP95;
            public bool frameGpuPassesDurationSanityCheck;
        }

        [Serializable]
        public sealed class Report
        {
            public bool running;
            public bool completed;
            public int activeCase;
            public bool spatialComparison;
            public bool reconstructionComparison;
            public bool reverseOrder;
            public bool comparisonPosesFixed;
            public bool paletteComparison;
            public bool temporalComparison;
            public string progress;
            public string device;
            public string shaderAsset;
            public string unityVersion;
            public string startedAtUtc;
            public string finishedAtUtc;
            public int width;
            public int height;
            public string limitations = "Raw FrameTimingManager Editor data plus separate live GPU markers. If frameGpuPassesDurationSanityCheck is false, the raw frame GPU value is smaller than the cloud dispatch and cannot certify total scene GPU time; passing this necessary check alone is also not proof of complete coverage. CPU values include Editor scheduling, not Player FPS. FHD Game view, 75% clouds, noon, wind zero; first-stage optimizations ON, shadow termination OFF. 20 warmup and 48 valid samples per source/case; sources are not frame-aligned. No performance extrapolation to GTX 1660.";
            public Measurement[] measurements;
            public string error;
        }

        public static Report LastReport = new Report();

        /// <summary>FHD Play 화면만 허용하고 카메라·조명·입력·설정은 검사 후 원래 값으로 복원합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Measure FHD Scene Frames")]
        public static void Start()
        {
            StartRun(false, false);
        }

        /// <summary>공간 조명 OFF/16/32/64m의 생성 비용을 포함한 실제 패스 GPU 시간을 비교합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Measure FHD Spatial Lighting")]
        public static void StartSpatial()
        {
            StartRun(true, false);
        }

        /// <summary>100/75/50% 선형 합성과 75/50% 깊이 합성의 전체 구름 GPU 비용을 비교합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Measure FHD Reconstruction")]
        public static void StartReconstruction()
        {
            StartRun(false, true);
        }

        /// <summary>조건 순서를 뒤집어 GPU 클록과 순차 측정 편차를 재확인합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Measure FHD Reconstruction Reverse")]
        public static void StartReconstructionReverse()
        {
            StartRun(false, true, true);
        }

        /// <summary>고정 시점마다 OFF/ON/ON/OFF로 팔레트 연산 생략 하나만 비교합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Measure FHD Palette Lighting")]
        public static void StartPalette()
        {
            StartRun(false, false, false, true);
        }

        /// <summary>역순 ON/OFF/OFF/ON 비교로 순차 실행 편차를 확인합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Measure FHD Palette Lighting Reverse")]
        public static void StartPaletteReverse()
        {
            StartRun(false, false, true, true);
        }

        /// <summary>OFF/2단계/4단계/OFF를 고정 시점에서 비교하며 재투영·저장 비용도 Cloud.Total에 포함합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Measure FHD Temporal Reprojection")]
        public static void StartTemporal()
        {
            StartRun(false, false, false, false, true);
        }

        /// <summary>2·4단계의 실행 순서를 바꿔 순차 측정 편차를 검사합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Measure FHD Temporal Reprojection Reverse")]
        public static void StartTemporalReverse()
        {
            StartRun(false, false, true, false, true);
        }

        /// <summary>개별 최적화 비교가 같은 Game 카메라 수집과 설정 복원을 사용합니다.</summary>
        private static async void StartRun(bool spatialComparison, bool reconstructionComparison, bool reverseOrder = false, bool paletteComparison = false, bool temporalComparison = false)
        {
            if (LastReport.running)
            {
                return;
            }

            Report report = new Report();
            report.spatialComparison = spatialComparison;
            report.reconstructionComparison = reconstructionComparison;
            report.reverseOrder = reverseOrder;
            report.paletteComparison = paletteComparison;
            report.temporalComparison = temporalComparison;
            if (temporalComparison)
            {
                report.limitations = "Fixed Game camera Cloud.Total GPU timestamps including reprojection and history store. 75% clouds, depth-aware ON; palette/interval/shadow/spatial experiments OFF. Noon, zero wind. OFF/2/4/OFF or OFF/4/2/OFF per view, 20 warmup + 48 valid samples per source. Static-view savings do not represent motion or reset frames; not Player FPS or GTX 1660 performance.";
            }
            if (paletteComparison)
            {
                report.limitations = "Fixed Game camera Cloud.Total GPU timestamps; 75% clouds, depth-aware reconstruction ON, spatial/interval/shadow options OFF. Noon palette, wind zero. ABBA or BAAB per view, 20 warmup and 48 samples per case/source. Sources are not frame-aligned. Editor GPU data is not Player FPS or GTX 1660 performance.";
            }
            if (reconstructionComparison)
            {
                report.limitations = "Live Game camera GPU markers; Cloud.Total includes depth copy, raymarch and reconstruction/composite. 100/75/50% bilinear and 75/50% depth-aware, spatial cache/intervals/shadow termination OFF, other optimizations ON. Noon, wind zero, 20 warmup and 48 samples per source; sources are not frame-aligned. FrameTimingManager is not valid total GPU time when smaller than Cloud.Total. RTX measurement cannot certify GTX 1660 FPS.";
            }
            LastReport = report;
            if (!Application.isPlaying || Screen.width != 1920 || Screen.height != 1080)
            {
                report.error = "Play mode with the Full HD Game view preset is required.";
                return;
            }

            CloudOptimizationPanel panel = UnityEngine.Object.FindFirstObjectByType<CloudOptimizationPanel>();
            CustomPassVolume volume = panel.GetComponent<CustomPassVolume>();
            LostSkiesCloudPass pass = null;
            foreach (CustomPass candidate in volume.customPasses)
            {
                pass = candidate as LostSkiesCloudPass;
                if (pass != null)
                {
                    break;
                }
            }

            Camera camera = Camera.main;
            Camera previousTargetCamera = volume.targetCamera;
            CloudSeaFlight flight = camera.GetComponent<CloudSeaFlight>();
            CloudTimeOfDayController clock = UnityEngine.Object.FindFirstObjectByType<CloudTimeOfDayController>();
            CloudTimeOfDayInput input = panel.GetComponent<CloudTimeOfDayInput>();
            Vector3 position = camera.transform.position;
            Quaternion rotation = camera.transform.rotation;
            float fieldOfView = camera.fieldOfView;
            float hour = clock.TimeOfDay;
            bool advance = clock.autoAdvance;
            bool flightEnabled = flight.enabled;
            bool inputEnabled = input.enabled;
            bool panelEnabled = panel.enabled;
            bool background = Application.runInBackground;
            float wind = pass.windSpeed;
            float scale = pass.resolutionScale;
            bool depthAware = pass.useDepthAwareUpsampling;
            bool intervals = pass.useRayIntervals;
            bool shadows = pass.useShadowTermination;
            bool paletteFastPath = pass.usePaletteLightingFastPath;
            bool temporal = pass.useTemporalReprojection;
            int temporalPhases = pass.temporalUpdatePhases;
            bool spatial = pass.useSpatialLightCache;
            float spatialCellSize = pass.spatialLightCellSize;
            bool cache = pass.useShapeCellCache;
            bool rejection = pass.useSupportRejection;
            bool compact = pass.useCompactTargets;
            bool profilerEnabled = UnityEditorInternal.ProfilerDriver.enabled;
            bool gpuEnabled = UnityEditorInternal.ProfilerDriver.profileGPU;
            Vector3 origin = Vector3.zero;
            if (pass.worldOrigin != null)
            {
                origin = pass.worldOrigin.SamplingOffset;
            }

            List<Measurement> measurements = new List<Measurement>();
            report.running = true;
            report.device = SystemInfo.graphicsDeviceName;
            report.shaderAsset = AssetDatabase.GetAssetPath(pass.raymarchShader);
            report.unityVersion = Application.unityVersion;
            report.startedAtUtc = DateTime.UtcNow.ToString("O");
            report.width = Screen.width;
            report.height = Screen.height;
            try
            {
                UnityEditorInternal.ProfilerDriver.profileGPU = true;
                UnityEditorInternal.ProfilerDriver.enabled = true;
                panel.enabled = false;
                flight.enabled = false;
                input.enabled = false;
                Application.runInBackground = true;
                clock.autoAdvance = false;
                clock.SetTime(12f);
                pass.windSpeed = 0f;
                pass.resolutionScale = 0.75f;
                pass.useDepthAwareUpsampling = false;
                pass.useShadowTermination = false;
                pass.usePaletteLightingFastPath = false;
                pass.useTemporalReprojection = false;
                pass.useSpatialLightCache = false;
                pass.useShapeCellCache = true;
                pass.useSupportRejection = true;
                pass.useCompactTargets = true;
                // Scene 뷰 카메라가 같은 GPU 마커에 섞이지 않도록 검사 중에만 Game 카메라를 지정합니다.
                volume.targetCamera = camera;
                camera.fieldOfView = 68f;
                // 경로 생성은 현재 카메라 위치에 의존하므로 고정 home에서 한 번만 계산합니다.
                Vector3 homePosition = new Vector3(1222, 3300, -6800);
                Quaternion homeRotation = Quaternion.Euler(10, 15, 0);
                camera.transform.SetPositionAndRotation(homePosition - origin, homeRotation);
                CloudStyleCapture.CameraPose sidePose = CloudStyleCapture.DescribePath("orbit").poses[26];
                CloudStyleCapture.CameraPose insidePose = CloudStyleCapture.DescribePath("traverse").poses[60];
                // 실제 장면 명령이 마커를 등록한 뒤 GPU 레코더를 연결합니다.
                await Task.Delay(500);
                int variants = 2;
                if (spatialComparison || reconstructionComparison || paletteComparison || temporalComparison)
                {
                    variants = 4;
                }

                if (reconstructionComparison)
                {
                    variants = 5;
                }

                for (int index = 0; index < variants * 3; index++)
                {
                    report.activeCase = index;
                    Measurement measurement = new Measurement();
                    measurement.view = "home";
                    measurement.densityPosition = homePosition;
                    measurement.rotation = homeRotation;
                    if (index / variants == 1)
                    {
                        measurement.view = "side";
                        measurement.densityPosition = sidePose.densityPosition;
                        measurement.rotation = sidePose.worldRotation;
                    }
                    else if (index / variants == 2)
                    {
                        measurement.view = "inside-candidate";
                        measurement.densityPosition = insidePose.densityPosition;
                        measurement.rotation = insidePose.worldRotation;
                    }

                    measurement.intervals = index % 2 == 1;
                    measurement.resolutionScale = 0.75f;
                    if (temporalComparison)
                    {
                        int variant = index % variants;
                        measurement.intervals = false;
                        measurement.depthAwareUpsampling = true;
                        pass.useDepthAwareUpsampling = true;
                        int phases = 0;
                        if (variant == 1)
                        {
                            phases = 2;
                        }
                        if (variant == 2)
                        {
                            phases = 4;
                        }
                        if (reverseOrder && phases > 0)
                        {
                            phases = 6 - phases;
                        }
                        measurement.temporalPhases = phases;
                        pass.temporalUpdatePhases = Mathf.Max(2, phases);
                        pass.useTemporalReprojection = phases > 0;
                    }
                    if (paletteComparison)
                    {
                        measurement.intervals = false;
                        measurement.paletteFastPath = index % variants == 1 || index % variants == 2;
                        if (reverseOrder)
                        {
                            measurement.paletteFastPath = !measurement.paletteFastPath;
                        }

                        measurement.depthAwareUpsampling = true;
                        pass.useDepthAwareUpsampling = true;
                        pass.usePaletteLightingFastPath = measurement.paletteFastPath;
                    }

                    if (reconstructionComparison)
                    {
                        measurement.intervals = false;
                        int variant = index % variants;
                        if (reverseOrder)
                        {
                            variant = variants - 1 - variant;
                        }

                        float[] scales = { 1f, 0.75f, 0.75f, 0.5f, 0.5f };
                        measurement.resolutionScale = scales[variant];
                        measurement.depthAwareUpsampling = variant == 2 || variant == 4;
                        pass.resolutionScale = measurement.resolutionScale;
                        pass.useDepthAwareUpsampling = measurement.depthAwareUpsampling;
                    }
                    if (spatialComparison)
                    {
                        measurement.intervals = false;
                        measurement.spatial = index % variants > 0;
                        measurement.spatialCellSize = 16f;
                        if (measurement.spatial)
                        {
                            measurement.spatialCellSize *= Mathf.Pow(2, index % variants - 1);
                        }

                        pass.useSpatialLightCache = measurement.spatial;
                        pass.spatialLightCellSize = measurement.spatialCellSize;
                    }

                    pass.useRayIntervals = measurement.intervals;
                    camera.transform.SetPositionAndRotation(measurement.densityPosition - origin, measurement.rotation);
                    await Collect(measurement);
                    if (Vector3.Distance(camera.transform.position + origin, measurement.densityPosition) > 0.01f
                        || Quaternion.Angle(camera.transform.rotation, measurement.rotation) > 0.01f)
                    {
                        throw new InvalidOperationException("Camera moved during GPU comparison.");
                    }

                    measurement.cloudWidth = pass.OutputWidth;
                    measurement.paletteFastPathActive = pass.PaletteLightingFastPathActive;
                    measurement.temporalActive = pass.TemporalActive;
                    measurement.temporalBytes = pass.TemporalBytes;
                    measurement.temporalStatus = pass.TemporalStatus;
                    if (temporalComparison && measurement.temporalPhases > 0 && !pass.TemporalActive)
                    {
                        throw new InvalidOperationException("Temporal path was not active.");
                    }
                    measurement.cloudHeight = pass.OutputHeight;
                    measurement.spatialBytes = pass.SpatialLightBytes;
                    measurements.Add(measurement);
                }

                report.comparisonPosesFixed = true;
                report.completed = true;
            }
            catch (Exception exception)
            {
                report.error = exception.ToString();
            }
            finally
            {
                // 검사 도중 Play를 종료해 장면 객체가 해제되어도 에디터 전역 설정은 먼저 복원합니다.
                Application.runInBackground = background;
                UnityEditorInternal.ProfilerDriver.enabled = profilerEnabled;
                UnityEditorInternal.ProfilerDriver.profileGPU = gpuEnabled;
                if (camera != null)
                {
                    camera.transform.SetPositionAndRotation(position, rotation);
                    camera.fieldOfView = fieldOfView;
                }

                if (clock != null)
                {
                    clock.SetTime(hour);
                    clock.autoAdvance = advance;
                }

                if (flight != null)
                {
                    flight.enabled = flightEnabled;
                }

                if (input != null)
                {
                    input.enabled = inputEnabled;
                }

                if (panel != null)
                {
                    panel.enabled = panelEnabled;
                }

                pass.windSpeed = wind;
                pass.resolutionScale = scale;
                pass.useDepthAwareUpsampling = depthAware;
                pass.useRayIntervals = intervals;
                pass.useShadowTermination = shadows;
                pass.usePaletteLightingFastPath = paletteFastPath;
                pass.useTemporalReprojection = temporal;
                pass.temporalUpdatePhases = temporalPhases;
                pass.useSpatialLightCache = spatial;
                pass.spatialLightCellSize = spatialCellSize;
                pass.useShapeCellCache = cache;
                pass.useSupportRejection = rejection;
                pass.useCompactTargets = compact;
                if (volume != null)
                {
                    volume.targetCamera = previousTargetCamera;
                }

                report.running = false;
                report.finishedAtUtc = DateTime.UtcNow.ToString("O");
                report.measurements = measurements.ToArray();
                Directory.CreateDirectory("Screenshots");
                string path = "Screenshots/RayOptimization-SceneFrames.json";
                if (spatialComparison)
                {
                    path = "Screenshots/SpatialLight-SceneFrames.json";
                }

                if (reconstructionComparison)
                {
                    path = "Screenshots/Reconstruction-SceneFrames.json";
                    if (reverseOrder)
                    {
                        path = "Screenshots/Reconstruction-SceneFrames-Reverse.json";
                    }
                }

                if (paletteComparison)
                {
                    path = "Screenshots/PaletteLighting-SceneFrames.json";
                    if (reverseOrder)
                    {
                        path = "Screenshots/PaletteLighting-SceneFrames-Reverse.json";
                    }
                }

                if (temporalComparison)
                {
                    path = "Screenshots/Temporal-SceneFrames.json";
                    if (reverseOrder)
                    {
                        path = "Screenshots/Temporal-SceneFrames-Reverse.json";
                    }
                }

                File.WriteAllText(path, JsonUtility.ToJson(report, true));
            }
        }

        /// <summary>GPU 시간이 유효한 서로 다른 프레임만 수집하고 전환 직후 20프레임은 제외합니다.</summary>
        private static async Task Collect(Measurement measurement)
        {
            List<double> gpu = new List<double>();
            List<double> cpu = new List<double>();
            FrameTiming[] timing = new FrameTiming[1];
            ulong previousTimestamp = 0;
            int warmup = 0;
            string[] names = { "Cloud.Raymarch", "Cloud.DepthCopy", "Cloud.Composite", "Cloud.Total", "Cloud.SpatialLightBuild" };
            int recorderCount = 4;
            if (measurement.spatial)
            {
                recorderCount = 5;
            }

            ProfilerRecorder[] recorders = new ProfilerRecorder[recorderCount];
            List<double>[] samples = new List<double>[recorderCount];
            int[] consumed = new int[recorderCount];
            int[] gpuWarmup = new int[recorderCount];
            try
            {
                for (int index = 0; index < recorders.Length; index++)
                {
                    samples[index] = new List<double>();
                    recorders[index] = ProfilerRecorder.StartNew(ProfilerCategory.Render, names[index], 4096,
                        ProfilerRecorderOptions.GpuRecorder | ProfilerRecorderOptions.SumAllSamplesInFrame);
                }

                for (int attempt = 0; attempt < 1500; attempt++)
                {
                    bool complete = gpu.Count >= 48;
                    foreach (List<double> sample in samples)
                    {
                        complete &= sample.Count >= 48;
                    }

                    if (complete)
                    {
                        break;
                    }

                    if (!Application.isPlaying)
                    {
                        throw new InvalidOperationException("Play mode stopped during measurement.");
                    }

                    FrameTimingManager.CaptureFrameTimings();
                    EditorApplication.QueuePlayerLoopUpdate();
                    await Task.Delay(20);
                    for (int index = 0; index < recorders.Length; index++)
                    {
                        Consume(recorders[index], samples[index], ref consumed[index], ref gpuWarmup[index]);
                    }

                    if (attempt % 25 == 0)
                    {
                        LastReport.progress = "Frame API " + gpu.Count + "; ray/depth/composite "
                            + samples[0].Count + "/" + samples[1].Count + "/" + samples[2].Count;
                    }

                    if (gpu.Count >= 48 || FrameTimingManager.GetLatestTimings(1, timing) == 0
                        || timing[0].frameStartTimestamp == previousTimestamp)
                    {
                        continue;
                    }

                    previousTimestamp = timing[0].frameStartTimestamp;
                    double value = timing[0].gpuFrameTime;
                    if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
                    {
                        continue;
                    }

                    warmup++;
                    if (warmup <= 20)
                    {
                        continue;
                    }

                    gpu.Add(value);
                    cpu.Add(timing[0].cpuFrameTime);
                }
            }
            finally
            {
                foreach (ProfilerRecorder recorder in recorders)
                {
                    recorder.Dispose();
                }
            }

            bool enoughSamples = gpu.Count == 48;
            foreach (List<double> sample in samples)
            {
                enoughSamples &= sample.Count == 48;
            }

            if (!enoughSamples)
            {
                throw new InvalidOperationException("48 valid frame/GPU-marker samples were not available.");
            }

            measurement.rayMilliseconds = samples[0].ToArray();
            measurement.depthMilliseconds = samples[1].ToArray();
            measurement.compositeMilliseconds = samples[2].ToArray();
            measurement.totalCloudMilliseconds = samples[3].ToArray();
            if (measurement.spatial)
            {
                measurement.spatialBuildMilliseconds = samples[4].ToArray();
            }

            foreach (List<double> sample in samples)
            {
                sample.Sort();
            }

            measurement.rayMedian = (samples[0][23] + samples[0][24]) * 0.5;
            measurement.rayP95 = samples[0][45];
            measurement.depthMedian = (samples[1][23] + samples[1][24]) * 0.5;
            measurement.compositeMedian = (samples[2][23] + samples[2][24]) * 0.5;
            measurement.totalCloudMedian = (samples[3][23] + samples[3][24]) * 0.5;
            measurement.totalCloudP95 = samples[3][45];
            if (measurement.spatial)
            {
                measurement.spatialBuildMedian = (samples[4][23] + samples[4][24]) * 0.5;
                measurement.spatialBuildP95 = samples[4][45];
            }

            measurement.gpuMilliseconds = gpu.ToArray();
            measurement.cpuMilliseconds = cpu.ToArray();
            gpu.Sort();
            cpu.Sort();
            measurement.gpuMedian = (gpu[23] + gpu[24]) * 0.5;
            measurement.gpuP95 = gpu[45];
            measurement.cpuMedian = (cpu[23] + cpu[24]) * 0.5;
            measurement.cpuP95 = cpu[45];
            measurement.frameGpuPassesDurationSanityCheck = measurement.gpuMedian >= measurement.totalCloudMedian;
        }

        /// <summary>같은 기록의 재사용과 여러 카메라가 섞인 프레임을 제외하여 단일 패스의 GPU 시간을 모읍니다.</summary>
        private static void Consume(ProfilerRecorder recorder, List<double> samples, ref int consumed, ref int warmup)
        {
            while (consumed < recorder.Count && samples.Count < 48)
            {
                ProfilerRecorderSample sample = recorder.GetSample(consumed);
                consumed++;
                if (sample.Count != 1 || sample.Value <= 0)
                {
                    continue;
                }

                warmup++;
                if (warmup > 20)
                {
                    samples.Add(sample.Value / 1000000d);
                }
            }
        }
    }
}
