using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.Profiling;

namespace ClouDream.LostSkies
{
    /// <summary>동일 Player에서 두 경로를 교차 3회 비교하며 실패와 누락 표본을 보존합니다.</summary>
    [RequireComponent(typeof(CloudHybridSwitch))]
    public sealed class CloudHybridBenchmark : MonoBehaviour
    {
        [Serializable]
        public sealed class Frame
        {
            public double elapsed;
            public float routeTime;
            public Vector3 position;
            public Quaternion rotation;
            public float wallMs;
            public double frameGpuMs;
            public float cloudGpuMs;
            public float buildGpuMs;
            public float surfaceGpuMs;
            public float volumeGpuMs;
            public float compositeGpuMs;
            public bool actualHybrid;
            public int missing;
            public long bytes;
        }

        [Serializable]
        public sealed class Statistics
        {
            public int samples;
            public int missing;
            public double mean;
            public double median;
            public double p95;
            public double p99;
            public double maximum;
        }

        [Serializable]
        public sealed class Report
        {
            public string createdUtc;
            public string buildGuid;
            public string unityVersion;
            public string device;
            public string graphicsApi;
            public string scene;
            public string quality;
            public string profileInputs;
            public string mode;
            public string route;
            public bool development;
            public int width;
            public int height;
            public int repeat;
            public bool completed;
            public string failure;
            public int fallbackFrames;
            public Statistics wall;
            public Statistics frameGpu;
            public Statistics cloudGpu;
            public bool frameBudgetPassed;
            public bool cloudBudgetVerified;
            public bool cloudBudgetPassed;
            public bool passed;
            public string timingSource = "Cloud.ComparisonTotal sums hybrid work and reference fallback in each frame. FrameTiming and GPU marker streams are delayed; GPU samples are not paired with individual camera poses.";
            public List<double> cloudGpuSamples = new List<double>();
            public List<Frame> frames = new List<Frame>();
        }

        public bool Running { get; private set; }

        public string Status { get; private set; } = "idle";

        public string LastReport { get; private set; }

        public float durationSeconds = 36f;

        public int fixedFrames = 360;

        private CloudHybridSwitch routing;

        private CloudTimeOfDayController clock;

        private Camera camera;

        private CloudBenchmarkFlight route;

        private Report report;

        private readonly FrameTiming[] timing = new FrameTiming[1];

        private int runIndex;

        private int warmupFrames;

        private bool warmup;

        private bool draining;

        private int drainFrames;

        private ProfilerRecorder cloudRecorder;

        private bool originalCameraEnabled;

        private bool realtime;

        private bool quitWhenFinished;

        private string outputDirectory;

        private string suiteId;

        private double phaseStart;

        private double previousFrame;

        private float originalScale;

        private int originalVsync;

        private int originalFps;

        private bool originalHybrid;

        private bool originalBackground;

        private Vector3 originalPosition;

        private Quaternion originalRotation;

        private CloudSeaFlight flight;

        private bool flightEnabled;

        private CloudTimeOfDayInput input;

        private bool inputEnabled;

        /// <summary>명시된 벤치마크 인자가 있을 때만 자동 실행합니다.</summary>
        private void Start()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            if (Array.IndexOf(arguments, "-cloudHybridBenchmark") >= 0)
            {
                quitWhenFinished = Array.IndexOf(arguments, "-cloudQuit") >= 0;
                string directory = Argument(arguments, "-cloudOutput", Path.Combine(Application.persistentDataPath, "CloudHybridBenchmarks"));
                bool moving = Array.IndexOf(arguments, "-cloudFixed") < 0;
                Begin(directory, moving);
            }
        }

        /// <summary>MCP·실행 파일이 동일한 비교 경로를 사용합니다.</summary>
        public void Begin(string directory, bool useRealtime = true)
        {
            if (!Application.isPlaying || Running)
            {
                throw new InvalidOperationException("Start an idle hybrid benchmark during Play.");
            }

            routing = GetComponent<CloudHybridSwitch>();
            if (!routing.Connect() || routing.Hybrid.quality == null)
            {
                throw new InvalidOperationException("The validation scene has no connected hybrid pass.");
            }

            camera = Camera.main;
            clock = GetComponent<CloudTimeOfDayController>();
            outputDirectory = Path.GetFullPath(directory);
            Directory.CreateDirectory(outputDirectory);
            suiteId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            realtime = useRealtime;
            originalScale = Time.timeScale;
            originalVsync = QualitySettings.vSyncCount;
            originalFps = Application.targetFrameRate;
            originalHybrid = routing.useHybrid;
            originalBackground = Application.runInBackground;
            Application.runInBackground = true;
            originalPosition = camera.transform.position;
            originalRotation = camera.transform.rotation;
            originalCameraEnabled = camera.enabled;
            flight = camera.GetComponent<CloudSeaFlight>();
            if (flight != null)
            {
                flightEnabled = flight.enabled;
                flight.enabled = false;
            }

            input = GetComponent<CloudTimeOfDayInput>();
            if (input != null)
            {
                inputEnabled = input.enabled;
                input.enabled = false;
            }

            Time.timeScale = 0f;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            runIndex = 0;
            Running = true;
            StartRun();
        }

        /// <summary>R/H/H/R/R/H 순서로 실행 순서와 열 상태의 편향을 분산합니다.</summary>
        private void StartRun()
        {
            bool hybrid = runIndex == 1 || runIndex == 2 || runIndex == 5;
            routing.SetHybrid(hybrid);
            routing.Hybrid.collectGpuTiming = Debug.isDebugBuild;
            routing.Reference.SetGpuRecording(Debug.isDebugBuild);
            route = new CloudBenchmarkFlight(routing.Reference, clock,
                new Vector3(1222f, 5700f, 248f), durationSeconds / 3f, 1200f);
            report = new Report();
            report.createdUtc = DateTime.UtcNow.ToString("O");
            report.buildGuid = Application.buildGUID;
            report.unityVersion = Application.unityVersion;
            report.device = SystemInfo.graphicsDeviceName;
            report.graphicsApi = SystemInfo.graphicsDeviceType.ToString();
            report.scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
            report.quality = JsonUtility.ToJson(routing.Hybrid.quality);
            report.profileInputs = JsonUtility.ToJson(routing.Reference.styleProfile)
                + "\n" + JsonUtility.ToJson(routing.Reference.skyProfile)
                + "\n" + JsonUtility.ToJson(routing.Reference.oceanProfile);
            report.mode = "reference";
            if (hybrid)
            {
                report.mode = "hybrid";
            }

            report.route = "fixed poses";
            if (realtime)
            {
                report.route = "720m/s, 45deg/s, weather interrupt/retransition, plus lower-ocean traversal";
            }

            report.development = Debug.isDebugBuild;
            report.width = Screen.width;
            report.height = Screen.height;
            report.repeat = runIndex;
            warmup = true;
            draining = false;
            camera.enabled = true;
            warmupFrames = 0;
            phaseStart = Time.realtimeSinceStartupAsDouble;
            previousFrame = phaseStart;
            ApplyPose(0f);
            Status = "warming " + report.mode + " " + (runIndex + 1) + "/6";
            Debug.Log("Cloud hybrid benchmark: " + Status);
        }

        /// <summary>초기 워밍업 후 생성 비용과 기존 경로 복귀 프레임까지 제외하지 않고 기록합니다.</summary>
        private void LateUpdate()
        {
            if (!Running)
            {
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            if (draining)
            {
                drainFrames++;
                FrameTimingManager.CaptureFrameTimings();
                if (drainFrames >= 16)
                {
                    FinishRun(true, null);
                }

                return;
            }

            if (warmup)
            {
                warmupFrames++;
                if (report.mode == "hybrid" && routing.Hybrid.Failures > 0)
                {
                    FinishRun(false, "Mesh overflow or asynchronous readback failure");
                    return;
                }

                if (warmupFrames >= 120 && (report.mode == "reference"
                    || (routing.Hybrid.Ready && routing.Hybrid.ResidencyMissing == 0 && routing.Hybrid.Pending == 0)))
                {
                    warmup = false;
                    phaseStart = now;
                    previousFrame = now;
                    if (Debug.isDebugBuild)
                    {
                        cloudRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Cloud.ComparisonTotal", 32768,
                            ProfilerRecorderOptions.GpuRecorder | ProfilerRecorderOptions.SumAllSamplesInFrame);
                    }

                    Status = "measuring " + report.mode + " " + (runIndex + 1) + "/6";
                    Debug.Log("Cloud hybrid benchmark: " + Status);
                }
                else if (now - phaseStart > 240)
                {
                    FinishRun(false, "Warmup timed out: " + routing.Hybrid.Status);
                }

                return;
            }

            Frame sample = new Frame();
            sample.elapsed = now - phaseStart;
            sample.routeTime = (float)sample.elapsed;
            if (!realtime)
            {
                sample.routeTime = report.frames.Count * durationSeconds / Mathf.Max(1, fixedFrames);
            }

            sample.position = camera.transform.position;
            sample.rotation = camera.transform.rotation;
            sample.wallMs = (float)((now - previousFrame) * 1000);
            previousFrame = now;
            uint available = FrameTimingManager.GetLatestTimings(1, timing);
            if (available > 0)
            {
                sample.frameGpuMs = timing[0].gpuFrameTime;
            }

            float[] markers = routing.Hybrid.ReadGpuTimes();
            if (routing.useHybrid && routing.Hybrid.actual && Debug.isDebugBuild)
            {
                sample.cloudGpuMs = markers[0];
                sample.buildGpuMs = markers[1];
                sample.surfaceGpuMs = markers[2];
                sample.volumeGpuMs = markers[3];
                sample.compositeGpuMs = markers[4];
            }

            else if (!routing.useHybrid && Debug.isDebugBuild)
            {
                float[] referenceMarkers = routing.Reference.ReadGpuTimes();
                sample.cloudGpuMs = referenceMarkers[0];
                sample.volumeGpuMs = referenceMarkers[2];
                sample.compositeGpuMs = referenceMarkers[3];
            }

            sample.actualHybrid = routing.Hybrid.actual;
            sample.missing = routing.Hybrid.Missing;
            sample.bytes = routing.Hybrid.Bytes;
            if (routing.useHybrid && !sample.actualHybrid)
            {
                report.fallbackFrames++;
            }

            report.frames.Add(sample);
            FrameTimingManager.CaptureFrameTimings();
            float nextTime = (float)(now - phaseStart);
            if (!realtime)
            {
                nextTime = report.frames.Count * durationSeconds / Mathf.Max(1, fixedFrames);
            }

            if (nextTime >= durationSeconds)
            {
                draining = true;
                drainFrames = 0;
                camera.enabled = false;
                Status = "draining GPU " + report.mode;
                return;
            }

            ApplyPose(nextTime);
        }

        /// <summary>기존 검증 비행과 안개 하부 출입을 시간 함수로 재현합니다.</summary>
        private void ApplyPose(float seconds)
        {
            route.Apply(seconds, out Vector3 position, out Quaternion rotation);
            if (seconds > durationSeconds * 0.66f)
            {
                float local = (seconds - durationSeconds * 0.66f) / (durationSeconds * 0.34f);
                position = new Vector3(1222f + seconds * 360f,
                    Mathf.Lerp(3000f, -3000f, local), -6800f);
                rotation = Quaternion.Euler(35f, 25f + seconds * 2f, 0f);
            }

            camera.transform.SetPositionAndRotation(position, rotation);
        }

        /// <summary>원시 표본을 저장하고 누락된 GPU 값을 0ms로 평균하지 않습니다.</summary>
        private void FinishRun(bool completed, string failure)
        {
            report.completed = completed;
            report.failure = failure;
            List<double> wall = new List<double>();
            List<double> frameGpu = new List<double>();
            List<double> cloudGpu = new List<double>();
            foreach (Frame frame in report.frames)
            {
                AddValid(wall, frame.wallMs);
                AddValid(frameGpu, frame.frameGpuMs);
            }

            if (cloudRecorder.Valid)
            {
                List<ProfilerRecorderSample> gpuSamples = new List<ProfilerRecorderSample>();
                cloudRecorder.CopyTo(gpuSamples);
                foreach (ProfilerRecorderSample sample in gpuSamples)
                {
                    AddValid(cloudGpu, sample.Value / 1000000d);
                }

                cloudRecorder.Dispose();
            }

            report.cloudGpuSamples = cloudGpu;

            report.wall = Calculate(wall, report.frames.Count);
            report.frameGpu = Calculate(frameGpu, report.frames.Count);
            report.cloudGpu = Calculate(cloudGpu, report.frames.Count);
            CloudHybridQualityProfile quality = routing.Hybrid.quality;
            report.frameBudgetPassed = completed && report.wall.samples > 0 && report.wall.p95 <= quality.frameP95;
            report.cloudBudgetVerified = report.cloudGpu.samples > 0
                && report.cloudGpu.samples >= report.frames.Count * 0.99f;
            report.cloudBudgetPassed = report.cloudBudgetVerified
                && report.cloudGpu.mean <= quality.cloudMean && report.cloudGpu.p95 <= quality.cloudP95;
            report.passed = report.frameBudgetPassed && report.cloudBudgetPassed && report.fallbackFrames == 0;
            LastReport = Path.Combine(outputDirectory, suiteId + "-" + runIndex + "-" + report.mode + ".json");
            File.WriteAllText(LastReport, JsonUtility.ToJson(report, true));
            Debug.Log("Cloud hybrid benchmark report: " + LastReport);
            route.Dispose();
            route = null;
            runIndex++;
            if (runIndex < 6)
            {
                StartRun();
                return;
            }

            Status = "completed";
            Restore();
            if (quitWhenFinished)
            {
                Application.Quit(0);
            }
        }

        /// <summary>유효한 양수 시간만 집계하며 원시 보고서에는 원래 값을 남깁니다.</summary>
        private static void AddValid(List<double> samples, double value)
        {
            if (!double.IsNaN(value) && !double.IsInfinity(value) && value > 0)
            {
                samples.Add(value);
            }
        }

        /// <summary>Nearest-rank 백분위수와 일반 중앙값을 같은 방식으로 계산합니다.</summary>
        public static Statistics Calculate(List<double> samples, int expected)
        {
            Statistics result = new Statistics();
            result.samples = samples.Count;
            result.missing = Mathf.Max(0, expected - samples.Count);
            if (samples.Count == 0)
            {
                return result;
            }

            samples.Sort();
            double total = 0;
            foreach (double value in samples)
            {
                total += value;
            }

            result.mean = total / samples.Count;
            result.median = samples[samples.Count / 2];
            if (samples.Count % 2 == 0)
            {
                result.median = (samples[samples.Count / 2 - 1] + result.median) * 0.5;
            }

            result.p95 = samples[Mathf.CeilToInt(samples.Count * 0.95f) - 1];
            result.p99 = samples[Mathf.CeilToInt(samples.Count * 0.99f) - 1];
            result.maximum = samples[samples.Count - 1];
            return result;
        }

        /// <summary>시작 전 입력·시간·화질·카메라를 복원합니다.</summary>
        private void Restore()
        {
            if (!Running)
            {
                return;
            }

            if (route != null)
            {
                route.Dispose();
                route = null;
            }

            if (cloudRecorder.Valid)
            {
                cloudRecorder.Dispose();
            }

            routing.Hybrid.collectGpuTiming = false;
            routing.Reference.SetGpuRecording(false);
            routing.SetHybrid(originalHybrid);
            Application.runInBackground = originalBackground;
            Time.timeScale = originalScale;
            QualitySettings.vSyncCount = originalVsync;
            Application.targetFrameRate = originalFps;
            camera.transform.SetPositionAndRotation(originalPosition, originalRotation);
            camera.enabled = originalCameraEnabled;
            if (flight != null)
            {
                flight.enabled = flightEnabled;
            }

            if (input != null)
            {
                input.enabled = inputEnabled;
            }

            Running = false;
        }

        /// <summary>플레이 중단에서도 임시 환경과 시간 설정을 남기지 않습니다.</summary>
        private void OnDisable()
        {
            Restore();
        }

        /// <summary>알려진 인자의 값을 읽고 명령 문자열로 실행하지 않습니다.</summary>
        private static string Argument(string[] arguments, string name, string fallback)
        {
            int index = Array.IndexOf(arguments, name);
            if (index >= 0 && index + 1 < arguments.Length)
            {
                return arguments[index + 1];
            }

            return fallback;
        }
    }
}
