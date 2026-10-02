using System;
using System.Collections.Generic;
using System.IO;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies
{
    /// <summary>F9에서 반복 가능한 궤도/관통/운해 이동을 실행하고 원시 계측 결과를 저장합니다.</summary>
    [DefaultExecutionOrder(10000)]
    [RequireComponent(typeof(CustomPassVolume))]
    public sealed class CloudBenchmark : MonoBehaviour
    {
        [Range(60, 600)] public int framesPerLeg = 180;
        public Vector3 routeCenter = new Vector3(5137.099f, 6241.348f, -5190.923f);
        public bool showWindow;
        public enum DistanceMode
        {
            Density,
            Lighting,
            Both
        }

        public DistanceMode comparisonMode = DistanceMode.Both;

        public bool IsRunning { get; private set; }
        public string Status { get; private set; } = "Ready";
        public string LastPath { get; private set; }
        public CloudBenchmarkReport LastReport { get; private set; }
        public readonly List<string> ComparisonPaths = new List<string>();
        public readonly List<CloudBenchmarkReport.Summary> ComparisonSummaries = new List<CloudBenchmarkReport.Summary>();

        private int comparisonIndex = -1;
        private CloudDistanceQualitySettings comparisonOriginal;
        private string comparisonId;
        private DistanceMode runComparisonMode;
        private bool comparingEmptySpace;
        private bool comparisonOriginalIntervals;
        private ComputeShader comparisonOriginalShader;
        private ComputeShader comparisonReferenceShader;
        private string comparisonName = "distance";
        public string ComparisonAssessment { get; private set; }

        private LostSkiesCloudPass pass;
        private CustomPassVolume volume;
        private Camera cameraTarget;
        private CloudOptimizationPanel panel;
        private CloudSeaFlight flight;
        private CloudTimeOfDayInput timeInput;
        private CloudBenchmarkHardware hardware;
        private ProfilerRecorder[] gpuRecorders;
        private readonly int[] gpuConsumed = new int[4];
        private readonly FrameTiming[] timingBuffer = new FrameTiming[1];
        private ProfilerRecorder gcRecorder;
        private ProfilerRecorder drawRecorder;
        private ProfilerRecorder triangleRecorder;
        private ulong lastTiming;
        private int stage;
        private int tick;
        private int totalFrames;
        private int runFramesPerLeg;
        private Vector3 runCenter;
        private double previousWall;
        private double startedWall;
        private double lastHardware;
        private int previousCollections;
        private Vector3 posePosition;
        private Quaternion poseRotation;
        private bool saved;
        private Vector2 scroll;

        // 벤치마크가 임시 소유한 상태만 저장/복원하며 에셋이나 품질 선택은 변경하지 않습니다.
        private Vector3 savedPosition;
        private Quaternion savedRotation;
        private float savedFov;
        private Camera savedTarget;
        private bool savedPanelEnabled;
        private bool savedFlightEnabled;
        private bool savedTimeInputEnabled;
        private bool savedBackground;
        private float savedTimeScale;
        private int savedVSync;
        private int savedFrameRate;
        private CursorLockMode savedCursor;
        private bool savedCursorVisible;
#if UNITY_EDITOR
        private bool savedProfiler;
        private bool savedGpuProfiler;
#endif

        /// <summary>같은 볼륨의 구름 패스와 조작 UI를 찾습니다.</summary>
        private void Awake()
        {
            volume = GetComponent<CustomPassVolume>();
            panel = GetComponent<CloudOptimizationPanel>();
            timeInput = GetComponent<CloudTimeOfDayInput>();
            foreach (CustomPass candidate in volume.customPasses)
            {
                pass = candidate as LostSkiesCloudPass;
                if (pass != null)
                {
                    break;
                }
            }
        }

        /// <summary>현재 거리 배율로 OFF/ON/ON/OFF를 실행합니다. 나머지 렌더 설정과 정지된 시각은 같게 유지합니다.</summary>
        public void StartDistanceComparison()
        {
            if (IsRunning || !Application.isPlaying || pass == null || Camera.main == null)
            {
                return;
            }
            if (!pass.DistanceQualitySupported)
            {
                Status = "Distance quality requires Concept V2";
                return;
            }
            comparisonOriginal = pass.distanceQuality;
            comparingEmptySpace = false;
            comparisonName = "distance";
            runComparisonMode = comparisonMode;
            comparisonIndex = 0;
            comparisonId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            ComparisonPaths.Clear();
            ComparisonSummaries.Clear();
            ComparisonAssessment = "Running OFF / ON / ON / OFF";
            StartComparisonCase();
        }

        /// <summary>빈 공간 옵션만 OFF/ON/ON/OFF로 비교합니다. 선택적 기준 셰이더는 개발 시 이전 구현 비교용입니다.</summary>
        public void StartEmptySpaceComparison(ComputeShader referenceShader = null)
        {
            if (IsRunning || !Application.isPlaying || pass == null || Camera.main == null)
            {
                return;
            }
            if (!pass.DistanceQualitySupported)
            {
                Status = "Empty-space skipping requires Concept V2";
                return;
            }

            comparingEmptySpace = true;
            comparisonName = "empty-space";
            comparisonOriginalIntervals = pass.useRayIntervals;
            comparisonOriginalShader = pass.raymarchShader;
            comparisonReferenceShader = referenceShader;
            if (referenceShader != null)
            {
                comparisonName = "empty-space-legacy";
            }

            comparisonIndex = 0;
            comparisonId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            ComparisonPaths.Clear();
            ComparisonSummaries.Clear();
            ComparisonAssessment = "Running baseline / improved / improved / baseline";
            StartComparisonCase();
        }

        /// <summary>선택한 한 기능만 바꾸고 같은 카메라 경로와 계측 처리를 재사용합니다.</summary>
        private void StartComparisonCase()
        {
            bool enabled = comparisonIndex == 1 || comparisonIndex == 2;
            if (comparingEmptySpace)
            {
                pass.useRayIntervals = enabled;
                pass.raymarchShader = comparisonOriginalShader;
                string mode = "off";
                if (enabled)
                {
                    mode = "on";
                }
                else if (comparisonReferenceShader != null)
                {
                    pass.raymarchShader = comparisonReferenceShader;
                    pass.useRayIntervals = true;
                    mode = "legacy";
                }

                StartBenchmark(comparisonName + "-" + comparisonId + "-" + comparisonIndex + "-" + mode);
                return;
            }

            pass.distanceQuality.density = enabled && runComparisonMode != DistanceMode.Lighting;
            pass.distanceQuality.lighting = enabled && runComparisonMode != DistanceMode.Density;
            string label = "distance-" + comparisonId + "-" + comparisonIndex + "-off";
            if (enabled)
            {
                label = "distance-" + comparisonId + "-" + comparisonIndex + "-on";
            }
            StartBenchmark(label + "-" + runComparisonMode);
        }

        /// <summary>측정 시작은 Play 중에만 허용하며 변경할 전역/입력 상태를 먼저 보관합니다.</summary>
        public void StartBenchmark(string label = "current")
        {
            if (IsRunning || !Application.isPlaying || pass == null || Camera.main == null)
            {
                return;
            }
            try
            {
                cameraTarget = Camera.main;
                flight = cameraTarget.GetComponent<CloudSeaFlight>();
                savedPosition = cameraTarget.transform.position;
                savedRotation = cameraTarget.transform.rotation;
                savedFov = cameraTarget.fieldOfView;
                savedTarget = volume.targetCamera;
                savedBackground = Application.runInBackground;
                savedTimeScale = Time.timeScale;
                savedVSync = QualitySettings.vSyncCount;
                savedFrameRate = Application.targetFrameRate;
                savedCursor = Cursor.lockState;
                savedCursorVisible = Cursor.visible;
                if (panel != null)
                {
                    savedPanelEnabled = panel.enabled;
                    panel.enabled = false;
                }
                // 패널의 입력 반환이 끝난 뒤 저장해야 패널을 열고 시작해도 비행 상태가 복원됩니다.
                if (flight != null)
                {
                    savedFlightEnabled = flight.enabled;
                }
                if (timeInput != null)
                {
                    savedTimeInputEnabled = timeInput.enabled;
                }
#if UNITY_EDITOR
                savedProfiler = UnityEditorInternal.ProfilerDriver.enabled;
                savedGpuProfiler = UnityEditorInternal.ProfilerDriver.profileGPU;
#endif
                saved = true;
                IsRunning = true;
                if (flight != null)
                {
                    flight.enabled = false;
                }
                if (timeInput != null)
                {
                    timeInput.enabled = false;
                }
                volume.targetCamera = cameraTarget;
                cameraTarget.fieldOfView = 68f;
                Time.timeScale = 0f;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                Application.runInBackground = true;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
#if UNITY_EDITOR
                UnityEditorInternal.ProfilerDriver.profileGPU = true;
                UnityEditorInternal.ProfilerDriver.enabled = true;
#endif
                runFramesPerLeg = Mathf.Clamp(framesPerLeg, 60, 600);
                runCenter = routeCenter;
                totalFrames = runFramesPerLeg * 3;
                LastReport = new CloudBenchmarkReport();
                LastReport.label = label;
                LastReport.startedUtc = DateTime.UtcNow.ToString("O");
                LastReport.status = "running";
                LastReport.gpu = SystemInfo.graphicsDeviceName;
                LastReport.cpu = SystemInfo.processorType;
                LastReport.operatingSystem = SystemInfo.operatingSystem;
                LastReport.unityVersion = Application.unityVersion;
                LastReport.graphicsApi = SystemInfo.graphicsDeviceType.ToString();
                LastReport.logicalCores = SystemInfo.processorCount;
                LastReport.systemMemoryMiB = SystemInfo.systemMemorySize;
                LastReport.gpuMemoryMiB = SystemInfo.graphicsMemorySize;
                LastReport.scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
#if UNITY_EDITOR
                LastReport.shaderDependencyHash = UnityEditor.AssetDatabase.GetAssetDependencyHash(UnityEditor.AssetDatabase.GetAssetPath(pass.raymarchShader)).ToString();
#endif
                LastReport.editor = Application.isEditor;
                LastReport.developmentBuild = Debug.isDebugBuild;
                LastReport.width = Screen.width;
                LastReport.height = Screen.height;
                LastReport.framesPerLeg = runFramesPerLeg;
                LastReport.routeCenter = runCenter;
                LastReport.fieldOfView = cameraTarget.fieldOfView;
                LastReport.frozenGameTime = Time.time;
                LastReport.originalVSync = savedVSync;
                LastReport.originalTargetFrameRate = savedFrameRate;
                LastReport.passSettings = JsonUtility.ToJson(pass);
                LastReport.styleSettings = JsonUtility.ToJson(pass.styleProfile);
                LastReport.skySettings = JsonUtility.ToJson(pass.skyProfile);
                LastReport.oceanSettings = JsonUtility.ToJson(pass.oceanProfile);
                CloudTimeOfDayController clock = GetComponent<CloudTimeOfDayController>();
                if (clock != null)
                {
                    LastReport.hour = clock.TimeOfDay;
                }
                LastReport.frames.Capacity = totalFrames;
                LastReport.timings.Capacity = totalFrames + 64;
                LastReport.hardware.Capacity = 1024;
                LastReport.cloudGpuMs.Capacity = totalFrames;
                LastReport.rayGpuMs.Capacity = totalFrames;
                LastReport.depthGpuMs.Capacity = totalFrames;
                LastReport.compositeGpuMs.Capacity = totalFrames;
                stage = 0;
                tick = 0;
                ApplyPose(0);
                Status = "Warming complete route";
            }
            catch (Exception exception)
            {
                Finish("failed", exception.ToString());
            }
        }

        /// <summary>F9/Escape와 계측 상태 전환을 처리하고 다른 입력 업데이트 뒤 카메라를 고정합니다.</summary>
        private void LateUpdate()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.f9Key.wasPressedThisFrame)
                {
                    showWindow = !showWindow;
                    if (showWindow && panel != null)
                    {
                        panel.showPanel = false;
                    }
                }
                if (keyboard.escapeKey.wasPressedThisFrame && IsRunning)
                {
                    Cancel();
                }
            }
            if (panel != null)
            {
                panel.SetExternalModal(showWindow && !IsRunning);
            }
            if (!IsRunning)
            {
                return;
            }
            try
            {
                if (Screen.width != LastReport.width || Screen.height != LastReport.height)
                {
                    throw new InvalidOperationException("Resolution changed during benchmark; result rejected.");
                }
                if (stage == 0)
                {
                    tick++;
                    if (tick < totalFrames)
                    {
                        ApplyPose(tick);
                    }
                    else
                    {
                        ApplyPose(0);
                    }
                    if (tick >= totalFrames + 32)
                    {
                        BeginMeasurement();
                    }
                }
                else if (stage == 1)
                {
                    RecordFrame();
                    tick++;
                    if (tick >= totalFrames)
                    {
                        stage = 2;
                        tick = 0;
                        Status = "Draining delayed GPU timings";
                    }
                    else
                    {
                        ApplyPose(tick);
                    }
                }
                else
                {
                    CollectTimings();
                    tick++;
                    if (tick >= 32)
                    {
                        Finish("completed", null);
                    }
                }
            }
            catch (Exception exception)
            {
                Finish("failed", exception.ToString());
            }
        }

        /// <summary>동일 좌표 경로를 FPS와 무관하게 실행합니다. 밀도장 좌표를 사용해 원점 이동을 보정합니다.</summary>
        public static void GetPose(int index, int countPerLeg, Vector3 center, out Vector3 position, out Quaternion rotation)
        {
            int leg = index / countPerLeg;
            float t = (index % countPerLeg) / (float)(countPerLeg - 1);
            if (leg == 0)
            {
                float angle = t * Mathf.PI * 2f;
                position = center + new Vector3(Mathf.Sin(angle) * 9200, 1000 + Mathf.Sin(angle) * 900, -Mathf.Cos(angle) * 9200);
                rotation = Quaternion.LookRotation(center - position, Vector3.up);
            }
            else if (leg == 1)
            {
                position = center + new Vector3(0, Mathf.Sin(t * Mathf.PI * 2) * 300, Mathf.Lerp(-6800, 6800, t));
                rotation = Quaternion.Euler(Mathf.Lerp(-12, 12, t), Mathf.Lerp(-15, 15, t), 0);
            }
            else
            {
                position = Vector3.Lerp(new Vector3(1222, 3300, -6800), new Vector3(6222, 3300, 4200), t);
                rotation = Quaternion.Euler(15 + Mathf.Sin(t * Mathf.PI) * 18, Mathf.Lerp(15, 100, t), 0);
            }
        }

        /// <summary>현재 월드 원점에 맞게 미리 정한 밀도장 카메라 자세를 적용합니다.</summary>
        private void ApplyPose(int index)
        {
            GetPose(index, runFramesPerLeg, runCenter, out posePosition, out poseRotation);
            Vector3 origin = Vector3.zero;
            if (pass.worldOrigin != null)
            {
                origin = pass.worldOrigin.SamplingOffset;
            }
            cameraTarget.transform.SetPositionAndRotation(posePosition - origin, poseRotation);
        }

        /// <summary>전체 경로 워밍업 뒤 메모리를 선할당하고 지원되는 프로파일러 스트림을 엽니다.</summary>
        private void BeginMeasurement()
        {
            string[] names = { "Cloud.Total", "Cloud.Raymarch", "Cloud.DepthCopy", "Cloud.Composite" };
            gpuRecorders = new ProfilerRecorder[4];
            for (int index = 0; index < names.Length; index++)
            {
                gpuConsumed[index] = 0;
                gpuRecorders[index] = ProfilerRecorder.StartNew(ProfilerCategory.Render, names[index], totalFrames + 128,
                    ProfilerRecorderOptions.GpuRecorder | ProfilerRecorderOptions.SumAllSamplesInFrame);
            }
            gcRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            drawRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count", 1);
            triangleRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count", 1);
            hardware = new CloudBenchmarkHardware(Time.realtimeSinceStartupAsDouble);
            LastReport.gpuUtilizationSource = hardware.GpuStatus;
            LastReport.cloudWidth = pass.OutputWidth;
            LastReport.cloudHeight = pass.OutputHeight;
            previousCollections = GC.CollectionCount(0);
            lastTiming = 0;
            stage = 1;
            tick = 0;
            startedWall = Time.realtimeSinceStartupAsDouble;
            previousWall = startedWall;
            lastHardware = startedWall;
            Status = "Measuring 3 moving views";
            FrameTimingManager.CaptureFrameTimings();
        }

        /// <summary>직전 자세의 실제 프레임 간격과 GC 및 버퍼 변화를 기록합니다.</summary>
        private void RecordFrame()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            CloudBenchmarkReport.Frame frame = new CloudBenchmarkReport.Frame();
            frame.index = tick;
            frame.leg = tick / runFramesPerLeg;
            frame.densityPosition = posePosition;
            frame.rotation = poseRotation;
            frame.wallMs = (now - previousWall) * 1000;
            frame.gcAllocatedBytes = Counter(gcRecorder);
            frame.drawCalls = (int)Counter(drawRecorder);
            frame.triangles = (int)Counter(triangleRecorder);
            int collections = GC.CollectionCount(0);
            frame.gcCollections = collections - previousCollections;
            previousCollections = collections;
            frame.activeCloudBytes = pass.GetActiveTargetBytes();
            frame.temporalBytes = pass.TemporalBytes;
            frame.spatialBytes = pass.SpatialLightBytes;
            frame.targetAllocations = pass.TargetAllocations;
            frame.shapeBuilds = pass.ShapeCacheBuilds;
            frame.temporalResets = pass.TemporalResets;
            LastReport.frames.Add(frame);
            previousWall = now;
            CollectTimings();
            if (now - lastHardware >= 1 || tick == totalFrames - 1)
            {
                CloudBenchmarkHardware.Sample sample = hardware.Read(now);
                sample.seconds -= startedWall;
                LastReport.hardware.Add(sample);
                lastHardware = now;
            }
        }

        /// <summary>지연 GPU 값을 중복 없이 읽고 여러 카메라의 합산값은 제외합니다. 자세와 별도 스트림입니다.</summary>
        private void CollectTimings()
        {
            for (int index = 0; index < gpuRecorders.Length; index++)
            {
                List<double> destination = LastReport.cloudGpuMs;
                if (index == 1)
                {
                    destination = LastReport.rayGpuMs;
                }
                if (index == 2)
                {
                    destination = LastReport.depthGpuMs;
                }
                if (index == 3)
                {
                    destination = LastReport.compositeGpuMs;
                }
                ProfilerRecorder recorder = gpuRecorders[index];
                if (!recorder.Valid)
                {
                    continue;
                }
                while (gpuConsumed[index] < recorder.Count && destination.Count < totalFrames)
                {
                    ProfilerRecorderSample sample = recorder.GetSample(gpuConsumed[index]);
                    gpuConsumed[index]++;
                    if (sample.Count == 1 && sample.Value > 0)
                    {
                        destination.Add(sample.Value / 1000000d);
                    }
                }
            }
            FrameTimingManager.CaptureFrameTimings();
            if (LastReport.timings.Count < totalFrames && FrameTimingManager.GetLatestTimings(1, timingBuffer) > 0
                && timingBuffer[0].frameStartTimestamp != lastTiming)
            {
                FrameTiming source = timingBuffer[0];
                lastTiming = source.frameStartTimestamp;
                CloudBenchmarkReport.Timing timing = new CloudBenchmarkReport.Timing();
                timing.timestamp = lastTiming;
                timing.cpuFrameMs = source.cpuFrameTime;
                timing.mainThreadMs = source.cpuMainThreadFrameTime;
                timing.renderThreadMs = source.cpuRenderThreadFrameTime;
                timing.presentWaitMs = source.cpuMainThreadPresentWaitTime;
                timing.gpuMs = source.gpuFrameTime;
                LastReport.timings.Add(timing);
            }
        }

        /// <summary>미지원 카운터는 0 할당/0 draw call로 오인하지 않게 -1로 기록합니다.</summary>
        private static long Counter(ProfilerRecorder recorder)
        {
            if (!recorder.Valid || recorder.Count == 0)
            {
                return -1;
            }
            return recorder.LastValue;
        }

        /// <summary>취소 결과도 저장하되 완료된 측정과 명확하게 구분합니다.</summary>
        public void Cancel()
        {
            if (IsRunning)
            {
                Finish("cancelled", null);
            }
        }

        /// <summary>모든 종료 경로에서 먼저 실행 상태를 복원하고 그 뒤 결과 파일을 기록합니다.</summary>
        private void Finish(string state, string error)
        {
            IsRunning = false;
            Restore();
            Status = state;
            if (LastReport == null)
            {
                return;
            }
            LastReport.status = state;
            LastReport.error = error;
            LastReport.Summarize();
            try
            {
                string directory = Path.Combine(Application.persistentDataPath, "CloudBenchmarks");
#if UNITY_EDITOR
                directory = Path.GetFullPath("Benchmarks");
#endif
                LastPath = LastReport.Save(directory);
                Debug.Log("Cloud benchmark " + state + ": " + LastPath);
            }
            catch (Exception exception)
            {
                Status = "Report save failed: " + exception.Message;
                Debug.LogException(exception);
                state = "failed";
            }
            if (comparisonIndex >= 0)
            {
                ComparisonPaths.Add(LastPath);
                ComparisonSummaries.Add(LastReport.summary);
                if (state == "completed" && comparisonIndex < 3)
                {
                    comparisonIndex++;
                    StartComparisonCase();
                }
                else
                {
                    if (comparingEmptySpace)
                    {
                        pass.useRayIntervals = comparisonOriginalIntervals;
                        pass.raymarchShader = comparisonOriginalShader;
                    }
                    else
                    {
                        pass.distanceQuality = comparisonOriginal;
                    }
                    comparisonIndex = -1;
                    AssessComparison(state);
                }
            }
        }

        /// <summary>큰 반복 변동 또는 반복 오차 안의 작은 효과를 개선으로 확정하지 않습니다.</summary>
        private void AssessComparison(string state)
        {
            if (state != "completed" || ComparisonSummaries.Count != 4)
            {
                ComparisonAssessment = "Comparison incomplete: " + state;
                return;
            }
            foreach (CloudBenchmarkReport.Summary summary in ComparisonSummaries)
            {
                if (summary.cloudGpuSamples < summary.frames)
                {
                    ComparisonAssessment = "INCONCLUSIVE: incomplete GPU timing coverage";
                    return;
                }
            }
            double off = (ComparisonSummaries[0].cloudGpuMeanMs + ComparisonSummaries[3].cloudGpuMeanMs) * 0.5;
            double on = (ComparisonSummaries[1].cloudGpuMeanMs + ComparisonSummaries[2].cloudGpuMeanMs) * 0.5;
            if (off <= 0 || on <= 0)
            {
                ComparisonAssessment = "GPU timing unavailable; compare frame data manually";
                return;
            }
            double offDrift = Math.Abs(ComparisonSummaries[0].cloudGpuMeanMs - ComparisonSummaries[3].cloudGpuMeanMs) / off;
            double onDrift = Math.Abs(ComparisonSummaries[1].cloudGpuMeanMs - ComparisonSummaries[2].cloudGpuMeanMs) / on;
            double effect = Math.Abs(on / off - 1);
            if (offDrift > 0.1 || onDrift > 0.1)
            {
                ComparisonAssessment = "INCONCLUSIVE: repeated conditions drift >10%. Check clocks/load and rerun.";
            }
            else if (effect <= Math.Max(0.02, Math.Max(offDrift, onDrift)))
            {
                ComparisonAssessment = "INCONCLUSIVE: effect is within repeat variation or 2% measurement margin.";
            }
            else
            {
                ComparisonAssessment = "Cloud GPU time change: " + ((on / off - 1) * 100).ToString("+0.0;-0.0;0") + "% (enabled vs baseline).";
            }
            try
            {
                string directory = Path.GetDirectoryName(LastPath);
                string text = ComparisonAssessment + "\nOFF repeat drift: " + offDrift.ToString("P2")
                    + "\nON repeat drift: " + onDrift.ToString("P2") + "\n" + string.Join("\n", ComparisonPaths);
                File.WriteAllText(Path.Combine(directory, comparisonName + "-" + comparisonId + "-comparison.txt"), text);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        /// <summary>종료·예외·Play 종료 시 계측기를 해제하고 설정을 복원합니다.</summary>
        private void Restore()
        {
            if (gpuRecorders != null)
            {
                foreach (ProfilerRecorder recorder in gpuRecorders)
                {
                    recorder.Dispose();
                }
                gpuRecorders = null;
            }
            gcRecorder.Dispose();
            drawRecorder.Dispose();
            triangleRecorder.Dispose();
            if (hardware != null)
            {
                hardware.Dispose();
                hardware = null;
            }
            if (!saved)
            {
                return;
            }
            saved = false;
            Time.timeScale = savedTimeScale;
            QualitySettings.vSyncCount = savedVSync;
            Application.targetFrameRate = savedFrameRate;
            Application.runInBackground = savedBackground;
            Cursor.lockState = savedCursor;
            Cursor.visible = savedCursorVisible;
#if UNITY_EDITOR
            UnityEditorInternal.ProfilerDriver.enabled = savedProfiler;
            UnityEditorInternal.ProfilerDriver.profileGPU = savedGpuProfiler;
#endif
            if (cameraTarget != null)
            {
                cameraTarget.transform.SetPositionAndRotation(savedPosition, savedRotation);
                cameraTarget.fieldOfView = savedFov;
            }
            if (volume != null)
            {
                volume.targetCamera = savedTarget;
            }
            if (flight != null)
            {
                flight.enabled = savedFlightEnabled;
            }
            if (timeInput != null)
            {
                timeInput.enabled = savedTimeInputEnabled;
            }
            if (panel != null)
            {
                panel.enabled = savedPanelEnabled;
                panel.SetExternalModal(showWindow);
                panel.ResetStatistics();
            }
        }

        /// <summary>컴포넌트 비활성화와 Play 종료도 같은 복원 경로를 사용합니다.</summary>
        private void OnDisable()
        {
            Cancel();
            if (panel != null)
            {
                panel.SetExternalModal(false);
            }
        }

        /// <summary>실행 중 진행 상태를, 종료 후 저장 위치와 주요 통계를 표시합니다.</summary>
        private void OnGUI()
        {
            if (!showWindow && !IsRunning)
            {
                return;
            }
            if (IsRunning)
            {
                GUI.Box(new Rect(12, 12, 440, 56), Status + "  " + tick + "/" + totalFrames + "\nEsc: cancel and restore");
                return;
            }
            float scale = Mathf.Min(1, Mathf.Min(Screen.width / 660f, Screen.height / 580f));
            Matrix4x4 previous = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(Vector3.one * scale);
            float left = (Screen.width / scale - 620) * 0.5f;
            float top = (Screen.height / scale - 540) * 0.5f;
            Color savedColor = GUI.color;
            GUI.color = new Color(0.025f, 0.055f, 0.07f, 0.97f);
            GUI.DrawTexture(new Rect(left, top, 620, 540), Texture2D.whiteTexture);
            GUI.color = savedColor;
            GUI.Box(new Rect(left, top, 620, 540), "CLOUDREAM / REPEATABLE BENCHMARK [F9]");
            GUILayout.BeginArea(new Rect(left + 16, top + 32, 588, 492));
            scroll = GUILayout.BeginScrollView(scroll);
            GUILayout.Label("Orbit / fly-through / cloud sea. Same poses on every run.");
            GUILayout.Label("Full route warmup + 3 x " + framesPerLeg + " measured frames.");
            GUILayout.Label("Game time pauses. VSync/FPS cap are disabled temporarily.");
            GUILayout.Label("Current quality settings are preserved. GPU/CPU load includes other apps as labelled.");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Quick / 60 per view"))
            {
                framesPerLeg = 60;
            }
            if (GUILayout.Button("Standard / 180"))
            {
                framesPerLeg = 180;
            }
            if (GUILayout.Button("Long / 600"))
            {
                framesPerLeg = 600;
            }
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Run current settings", GUILayout.Height(32)))
            {
                StartBenchmark();
            }
            GUILayout.Label("Distance comparison mode: " + comparisonMode);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Density only"))
            {
                comparisonMode = DistanceMode.Density;
            }
            if (GUILayout.Button("Lighting only"))
            {
                comparisonMode = DistanceMode.Lighting;
            }
            if (GUILayout.Button("Both"))
            {
                comparisonMode = DistanceMode.Both;
            }
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Compare selected mode: OFF / ON / ON / OFF", GUILayout.Height(32)))
            {
                StartDistanceComparison();
            }
            if (GUILayout.Button("Compare empty-space skipping: OFF / ON / ON / OFF", GUILayout.Height(32)))
            {
                StartEmptySpaceComparison();
            }
            GUILayout.Label(Status);
            if (LastReport != null && !IsRunning && LastReport.summary.frames > 0)
            {
                CloudBenchmarkReport.Summary s = LastReport.summary;
                GUILayout.Label($"Average {s.averageFps:0.0} FPS / minimum {s.minimumFps:0.0} / 1% low {s.onePercentLowFps:0.0}");
                GUILayout.Label($"p95 {s.p95FrameMs:0.00} ms / p99 {s.p99FrameMs:0.00} ms / cloud GPU {s.cloudGpuMeanMs:0.00} ms");
                GUILayout.Label("CPU process " + Available(s.processCpuMeanPercent, "%") + " / system " + Available(s.systemCpuMeanPercent, "%")
                    + " / GPU adapter " + Available(s.gpuMeanPercent, "%"));
                GUILayout.Label("Sampled peak RAM " + MemoryText(s.peakWorkingSetBytes) + " / adapter VRAM " + MemoryText(s.peakGpuUsedBytes));
                GUILayout.Label("CPU main " + Available(s.mainThreadMeanMs, " ms") + " / render " + Available(s.renderThreadMeanMs, " ms")
                    + " / GPU samples " + s.cloudGpuSamples + "/" + s.frames);
                GUILayout.Label("-1: unavailable. JSON contains raw CPU/GPU timings, memory and cloud diagnostics.");
                GUILayout.Label(LastPath);
            }
            for (int index = 0; index < ComparisonSummaries.Count; index++)
            {
                CloudBenchmarkReport.Summary s = ComparisonSummaries[index];
                string mode = "OFF";
                if (index == 1 || index == 2)
                {
                    mode = "ON";
                }
                if (comparingEmptySpace && comparisonReferenceShader != null && mode == "OFF")
                {
                    mode = "LEGACY";
                }
                GUILayout.Label($"{index + 1}. {comparisonName} {mode}: {s.averageFps:0.0} FPS / cloud {s.cloudGpuMeanMs:0.00} ms");
            }
            GUILayout.Label(ComparisonAssessment);
            if (GUILayout.Button("Close [F9]"))
            {
                showWindow = false;
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            GUI.matrix = previous;
        }

        /// <summary>지원하지 않는 센서를 0 사용량으로 표시하지 않습니다.</summary>
        private static string Available(double value, string unit)
        {
            if (value < 0)
            {
                return "N/A";
            }
            return value.ToString("0.0") + unit;
        }

        /// <summary>바이트가 유효한 경우에만 MiB로 변환합니다.</summary>
        private static string MemoryText(long bytes)
        {
            if (bytes < 0)
            {
                return "N/A";
            }
            return Available(bytes / 1048576d, " MiB");
        }
    }
}
