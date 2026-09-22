using System;
using System.Collections.Generic;
using System.IO;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>장면 렌더와 GPU 읽기 대기 없이 독립 구름 dispatch의 GPU 타임스탬프를 비동기로 수집합니다.</summary>
    public static class CloudConceptGpuPerformance
    {
        private const string MarkerName = "ClouDream.ConceptBenchmark.Raymarch";
        private const string OutputPath = "Screenshots/ConceptV2-GPU-Performance.json";
        private const int Width = 960;
        private const int Height = 544;
        private const int WarmupSamples = 8;
        private const int CollectedSamples = 24;
        private const int MaximumUpdatesPerCase = 600;
        private const int RegistrationUpdates = 8;

        [Serializable]
        public sealed class Measurement
        {
            public string view;
            public string poseBasis;
            public string styleAsset;
            public string styleValues;
            public Vector3 densityPosition;
            public Quaternion cameraRotation;
            public CloudEnvironment environment;
            public int actualWidth;
            public int actualHeight;
            public int warmupSamples;
            public int sampleCount;
            public int submittedDispatches;
            public int rejectedGpuSamples;
            public int emptyGpuFrames;
            public int rejectedMultipleHits;
            public int rejectedNonPositiveTime;
            public int rejectedNonFiniteTime;
            public int unexpectedGpuSamples;
            public int wrapperCountDifferences;
            public int wrapperTimingDifferences;
            public int updates;
            public bool available;
            public double[] milliseconds;
            public double medianMilliseconds = -1d;
            public double p95Milliseconds = -1d;
            public double minimumMilliseconds = -1d;
            public double maximumMilliseconds = -1d;
        }

        [Serializable]
        public sealed class Report
        {
            public string status = "idle";
            public bool completed;
            public bool running;
            public bool supportsGpuRecorder;
            public string startedAtUtc;
            public string finishedAtUtc;
            public string markerName;
            public string outputPath;
            public string acquisitionMode = "One unmeasured marker-registration dispatch precedes recorder creation. SceneView repaint and player-loop requests drive Editor GPU frames. Then one pending dispatch is allowed; each fresh ProfilerRecorder GPU sample is authoritative. ProfilingSampler last-frame differences are diagnostics only.";
            public string metric = "GPU timestamp elapsed time of the Raymarch DispatchCompute command only, milliseconds. Independent offscreen cloud targets; no Camera.Render or GPU readback.";
            public string limitations = "Editor GPU measurement, not Player FPS or total frame time. Excludes noise generation, CPU bindings, scene-depth copy, composite, sky and post-processing. Other Editor/GPU work can still affect scheduling. V1 and V2 render different shapes; this is not equal-image optimization. Inside is a fixed baseline interior candidate and may differ with the new shape. Depth target is clear, so opaque scene occlusion is excluded.";
            public string unityVersion;
            public string graphicsDevice;
            public string graphicsApi;
            public string graphicsDriver;
            public string operatingSystem;
            public string processor;
            public bool wasPlaying;
            public int openSceneViews;
            public string sourceCamera;
            public float fieldOfView;
            public float aspect;
            public float nearClipPlane;
            public float farClipPlane;
            public Vector3 worldOriginOffset;
            public string oceanAsset;
            public string skyAsset;
            public string oceanValues;
            public string skyValues;
            public string sourcePassValues;
            public string sourceEnvironmentValues;
            public string sourceSettings;
            public float frozenWindDistance;
            public int requestedWarmupSamples = WarmupSamples;
            public int requestedSamplesPerCase = CollectedSamples;
            public int activeCase;
            public int requestedFirstCase;
            public int requestedCases;
            public int totalUpdates;
            public int registrationDispatches;
            public int registrationUpdates;
            public bool recorderReady;
            public int rawRecorderCount;
            public int requestedSceneRepaints;
            public int skippedBufferedFrames;
            public int noiseGenerations;
            public bool cleanedUp;
            public Measurement[] measurements;
            public string error;
        }

        [Serializable]
        private sealed class BaselineManifest
        {
            public CloudStyleCapture.PathDescription path;
        }

        private sealed class Pose
        {
            public string name;
            public string basis;
            public Vector3 position;
            public Quaternion rotation;
            public CloudEnvironment environment;
        }

        private static Report report = new Report();
        private static readonly List<UnityEngine.Object> ownedProfiles = new List<UnityEngine.Object>();
        private static readonly List<double> currentSamples = new List<double>();
        private static LostSkiesCloudRenderer renderer;
        private static Camera camera;
        private static CommandBuffer commands;
        private static ProfilingSampler sampler;
        private static ProfilerRecorder freshnessRecorder;
        private static CloudStyleProfile[] styles;
        private static Pose[] poses;
        private static UniversalCloudLayerRenderSettings originalSettings;
        private static float coverage;
        private static float density;
        private static Vector3 fallbackSunDirection;
        private static int consumedRecorderCount;
        private static bool dispatchSubmitted;
        private static double registrationStartedAt;
        private static string activeOutputPath = OutputPath;

        /// <summary>독립 계측의 현재 진행 상태와 이미 수집한 표본을 반환합니다.</summary>
        public static Report Status()
        {
            return report;
        }

        /// <summary>현재 입력을 복제하고 측정을 예약합니다. GPU 결과 대기는 Editor 업데이트로 나눕니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Concept V2/Measure Raymarch GPU")]
        public static Report Start()
        {
            return StartCase(0);
        }

        /// <summary>지정 케이스부터 재시도하며 부분 재시도는 별도 JSON에 저장해 기존 전체 보고서를 보존합니다.</summary>
        public static Report StartCase(int firstCase)
        {
            if (report.running)
            {
                return report;
            }

            report = new Report();
            if (firstCase < 0 || firstCase > 5)
            {
                throw new ArgumentOutOfRangeException(nameof(firstCase), "firstCase must be between 0 and 5.");
            }

            activeOutputPath = OutputPath;
            if (firstCase > 0)
            {
                activeOutputPath = "Screenshots/ConceptV2-GPU-Performance-FromCase" + firstCase + ".json";
            }

            report.status = "preparing";
            report.running = true;
            report.startedAtUtc = DateTime.UtcNow.ToString("O");
            report.markerName = MarkerName + "." + DateTime.UtcNow.Ticks;
            report.outputPath = activeOutputPath;
            report.requestedFirstCase = firstCase;
            report.requestedCases = 6 - firstCase;
            report.activeCase = firstCase;
            try
            {
                DescribeHardware();
                if (!report.supportsGpuRecorder)
                {
                    Finish("unavailable", "SystemInfo.supportsGpuRecorder is false; no GPU timing value was measured.");
                    return report;
                }

                PrepareInputs();
                EditorApplication.update += Update;
                AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
                EditorApplication.quitting += BeforeEditorQuit;
                EditorApplication.playModeStateChanged += OnPlayModeChanged;
                report.status = "running";
                SaveReport();
                DriveEditorFrame();
            }
            catch (Exception exception)
            {
                Finish("failed", exception.ToString());
            }

            return report;
        }

        /// <summary>GPU 기록 지원 여부와 실제 실행 환경을 남기며 측정 불가를 0ms로 해석하지 않습니다.</summary>
        private static void DescribeHardware()
        {
            report.supportsGpuRecorder = SystemInfo.supportsGpuRecorder;
            report.unityVersion = Application.unityVersion;
            report.graphicsDevice = SystemInfo.graphicsDeviceName;
            report.graphicsApi = SystemInfo.graphicsDeviceType.ToString();
            report.graphicsDriver = SystemInfo.graphicsDeviceVersion;
            report.operatingSystem = SystemInfo.operatingSystem;
            report.processor = SystemInfo.processorType;
            report.wasPlaying = Application.isPlaying;
            report.openSceneViews = SceneView.sceneViews.Count;
        }

        /// <summary>장면과 에셋을 건드리지 않는 카메라·프로필·환경 스냅샷을 준비합니다.</summary>
        private static void PrepareInputs()
        {
            Camera sourceCamera = Camera.main;
            Require(sourceCamera != null, "A MainCamera is required.");
            LostSkiesCloudPass pass = FindPass();
            Require(!pass.useExtractedValues, "The source pass must use formation profiles.");
            Require(pass.raymarchShader != null && pass.originalGenerator != null && pass.originalPreset != null,
                "Cloud rendering resources are missing.");

            report.sourceCamera = sourceCamera.name;
            report.sourcePassValues = JsonUtility.ToJson(pass);
            if (pass.environmentSource != null)
            {
                report.sourceEnvironmentValues = EditorJsonUtility.ToJson(pass.environmentSource);
            }

            report.worldOriginOffset = Vector3.zero;
            if (pass.worldOrigin != null)
            {
                report.worldOriginOffset = pass.worldOrigin.SamplingOffset;
            }

            GameObject host = new GameObject("Cloud GPU benchmark camera");
            host.hideFlags = HideFlags.HideAndDontSave;
            camera = host.AddComponent<Camera>();
            camera.CopyFrom(sourceCamera);
            camera.enabled = false;
            camera.targetTexture = null;
            camera.allowDynamicResolution = false;
            camera.aspect = (float)Width / Height;
            report.fieldOfView = camera.fieldOfView;
            report.aspect = camera.aspect;
            report.nearClipPlane = camera.nearClipPlane;
            report.farClipPlane = camera.farClipPlane;

            styles = new CloudStyleProfile[2];
            styles[0] = CloneProfile(AssetDatabase.LoadAssetAtPath<CloudStyleProfile>("Assets/LostSkiesClouds/Presets/Style-Layered.asset"));
            styles[1] = CloneProfile(AssetDatabase.LoadAssetAtPath<CloudStyleProfile>("Assets/LostSkiesClouds/Presets/Style-ConceptV2.asset"));
            Require(styles[0] != null && styles[1] != null, "V1 Layered and Concept V2 style assets are required.");

            poses = new Pose[3];
            poses[0] = new Pose();
            poses[0].name = "home";
            poses[0].basis = "Fixed density-space home (1222,3300,-6800), Euler(10,15,0).";
            poses[0].position = new Vector3(1222f, 3300f, -6800f);
            poses[0].rotation = Quaternion.Euler(10f, 15f, 0f);
            poses[1] = ReadPose("side", "orbit-12", 26);
            poses[2] = ReadPose("inside-candidate", "traverse-12", 60);
            foreach (Pose pose in poses)
            {
                pose.environment = ResolveEnvironment(pass, pose.position);
            }

            coverage = pass.towerCoverage;
            density = pass.cloudDensity;
            fallbackSunDirection = new Vector3(0.4f, 0.8f, 0.2f).normalized;
            if (pass.sun != null)
            {
                fallbackSunDirection = -pass.sun.transform.forward;
            }

            // 노이즈 생성 명령에는 계측 마커가 없습니다. 렌더러 전체를 감싸지 않습니다.
            renderer = new LostSkiesCloudRenderer(pass.raymarchShader, pass.originalGenerator, pass.originalPreset);
            renderer.includeOcean = true;
            renderer.oceanProfile = CloneProfile(pass.oceanProfile);
            renderer.skyProfile = CloneProfile(pass.skyProfile);
            renderer.worldOriginOffset = report.worldOriginOffset;
            renderer.Resize(Width, Height);
            originalSettings = renderer.settings;
            if (Application.isPlaying)
            {
                report.frozenWindDistance = Time.time * pass.windSpeed;
                originalSettings.baseOffset += new Vector3(report.frozenWindDistance * originalSettings.baseTile
                    / (originalSettings.geometryXExtent.y - originalSettings.geometryXExtent.x), 0f, 0f);
            }

            report.sourceSettings = JsonUtility.ToJson(originalSettings);
            report.oceanAsset = AssetDatabase.GetAssetPath(pass.oceanProfile);
            report.skyAsset = AssetDatabase.GetAssetPath(pass.skyProfile);
            if (renderer.oceanProfile != null)
            {
                report.oceanValues = JsonUtility.ToJson(renderer.oceanProfile);
            }

            if (renderer.skyProfile != null)
            {
                report.skyValues = JsonUtility.ToJson(renderer.skyProfile);
            }

            commands = new CommandBuffer();
            sampler = new ProfilingSampler(report.markerName);
            sampler.enableRecording = true;
            renderer.raymarchSampler = sampler;
            // 마커가 실제 명령에서 실행되고 Editor GPU 프레임이 진행된 뒤 별도 누적 레코더를 연결합니다.
            freshnessRecorder = default;
            consumedRecorderCount = 0;
            dispatchSubmitted = false;
            registrationStartedAt = 0d;

            report.measurements = new Measurement[6];
            for (int index = 0; index < report.measurements.Length; index++)
            {
                Pose pose = poses[index / 2];
                int styleIndex = index % 2;
                Measurement measurement = new Measurement();
                measurement.view = pose.name;
                measurement.poseBasis = pose.basis;
                measurement.styleAsset = "Assets/LostSkiesClouds/Presets/Style-Layered.asset";
                if (styleIndex == 1)
                {
                    measurement.styleAsset = "Assets/LostSkiesClouds/Presets/Style-ConceptV2.asset";
                }

                measurement.styleValues = JsonUtility.ToJson(styles[styleIndex]);
                measurement.densityPosition = pose.position;
                measurement.cameraRotation = pose.rotation;
                measurement.environment = pose.environment;
                measurement.actualWidth = renderer.Width;
                measurement.actualHeight = renderer.Height;
                report.measurements[index] = measurement;
            }

            BeginCase();
        }

        /// <summary>다음 고정 시점과 스타일을 독립 렌더러에 적용하고 이전 GPU 지연 표본을 준비 구간에서 버립니다.</summary>
        private static void BeginCase()
        {
            currentSamples.Clear();
            Measurement measurement = report.measurements[report.activeCase];
            Pose pose = poses[report.activeCase / 2];
            camera.transform.SetPositionAndRotation(pose.position - report.worldOriginOffset, pose.rotation);
            renderer.styleProfile = styles[report.activeCase % 2];
            renderer.skyLighting = pose.environment.lighting;
            renderer.skyDensityMultiplier = pose.environment.skyDensityMultiplier;
            UniversalCloudLayerRenderSettings settings = originalSettings;
            settings.coverageIntensity = coverage + pose.environment.coverageOffset;
            settings.density = density * Mathf.Max(0f, pose.environment.densityMultiplier);
            renderer.settings = settings;
            measurement.warmupSamples = 0;
        }

        /// <summary>Editor 프레임을 막지 않고 도착한 GPU 기록 하나를 수집한 뒤 다음 dispatch를 제출합니다.</summary>
        private static void Update()
        {
            if (!report.running)
            {
                return;
            }

            try
            {
                report.totalUpdates++;
                Measurement measurement = report.measurements[report.activeCase];
                measurement.updates++;
                if (measurement.updates > MaximumUpdatesPerCase)
                {
                    Finish("unavailable", "No sufficient valid GPU samples arrived within 600 Editor updates for "
                        + measurement.view + ". Unavailable timings remain -1, never 0 ms.");
                    return;
                }

                if (!PrepareRecorder(measurement))
                {
                    DriveEditorFrame();
                    return;
                }

                ConsumeFreshGpuSample(measurement);
                if (measurement.sampleCount >= CollectedSamples)
                {
                    Summarize(measurement);
                    report.activeCase++;
                    SaveReport();
                    if (report.activeCase >= report.measurements.Length)
                    {
                        Finish("completed", null);
                        return;
                    }

                    BeginCase();
                    measurement = report.measurements[report.activeCase];
                }

                // Editor.update가 GPU 프레임보다 자주 와도 이전 dispatch 결과가 올 때까지 새 작업을 넣지 않습니다.
                if (dispatchSubmitted)
                {
                    DriveEditorFrame();
                    return;
                }

                commands.Clear();
                renderer.Render(commands, camera, fallbackSunDirection, measurement.environment.sunlight, 3f);
                Graphics.ExecuteCommandBuffer(commands);
                measurement.submittedDispatches++;
                dispatchSubmitted = true;
                report.noiseGenerations = renderer.NoiseGenerationCount;
                DriveEditorFrame();
            }
            catch (Exception exception)
            {
                Finish("failed", exception.ToString());
            }
        }

        /// <summary>새 마커를 실제 GPU 명령에 먼저 등록하고 준비 프레임 뒤 수집 레코더를 시작합니다.</summary>
        private static bool PrepareRecorder(Measurement measurement)
        {
            if (report.recorderReady)
            {
                return true;
            }

            if (report.registrationDispatches == 0)
            {
                commands.Clear();
                renderer.Render(commands, camera, fallbackSunDirection, measurement.environment.sunlight, 3f);
                Graphics.ExecuteCommandBuffer(commands);
                report.registrationDispatches++;
                report.noiseGenerations = renderer.NoiseGenerationCount;
                registrationStartedAt = EditorApplication.timeSinceStartup;
                return false;
            }

            report.registrationUpdates++;
            if (report.registrationUpdates < RegistrationUpdates
                || EditorApplication.timeSinceStartup - registrationStartedAt < 0.25d)
            {
                return false;
            }

            // 누적 Count를 통해 같은 GPU 표본을 여러 Editor 업데이트에서 중복 수집하지 않습니다.
            // 지연된 등록 명령의 기록이 도착하더라도 이후 8개 준비 표본에서는 측정값을 채택하지 않습니다.
            freshnessRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, report.markerName, 4096,
                ProfilerRecorderOptions.GpuRecorder | ProfilerRecorderOptions.SumAllSamplesInFrame);
            Require(freshnessRecorder.Valid, "GPU recorder creation failed after marker registration.");
            consumedRecorderCount = 0;
            report.recorderReady = true;
            SaveReport();
            return true;
        }

        /// <summary>Editor 업데이트만 반복되는 대기 상태에서도 실제 렌더 프레임이 진행되도록 재그리기를 요청합니다.</summary>
        private static void DriveEditorFrame()
        {
            SceneView.RepaintAll();
            report.requestedSceneRepaints++;
            EditorApplication.QueuePlayerLoopUpdate();
        }

        /// <summary>새 raw GPU 기록을 한 번씩 소비하며 독립 last-frame 캐시의 시점 차이는 거절 사유로 사용하지 않습니다.</summary>
        private static void ConsumeFreshGpuSample(Measurement measurement)
        {
            int count = freshnessRecorder.Count;
            report.rawRecorderCount = count;
            while (consumedRecorderCount < count)
            {
                ProfilerRecorderSample sample = freshnessRecorder.GetSample(consumedRecorderCount);
                consumedRecorderCount++;
                if (sample.Count == 0)
                {
                    measurement.emptyGpuFrames++;
                    continue;
                }

                if (!dispatchSubmitted)
                {
                    measurement.unexpectedGpuSamples++;
                    measurement.rejectedGpuSamples++;
                    continue;
                }

                // GPU 결과가 도착했으므로 다음 업데이트부터 한 작업만 다시 제출할 수 있습니다.
                dispatchSubmitted = false;
                double milliseconds = sample.Value / 1000000d;
                if (sample.Count != 1)
                {
                    measurement.rejectedMultipleHits++;
                    measurement.rejectedGpuSamples++;
                    continue;
                }

                if (milliseconds <= 0d)
                {
                    measurement.rejectedNonPositiveTime++;
                    measurement.rejectedGpuSamples++;
                    continue;
                }

                if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds))
                {
                    measurement.rejectedNonFiniteTime++;
                    measurement.rejectedGpuSamples++;
                    continue;
                }

                // 두 레코더의 최신 GPU 프레임 갱신 시점은 다를 수 있으므로 진단만 남깁니다.
                if (sampler.gpuSampleCount != sample.Count)
                {
                    measurement.wrapperCountDifferences++;
                }

                if (Math.Abs(sampler.gpuElapsedTime - milliseconds) > 0.001d)
                {
                    measurement.wrapperTimingDifferences++;
                }

                if (measurement.warmupSamples < WarmupSamples)
                {
                    measurement.warmupSamples++;
                }
                else
                {
                    currentSamples.Add(milliseconds);
                    measurement.sampleCount = currentSamples.Count;
                }
            }
        }

        /// <summary>측정 원본과 중앙값, nearest-rank p95를 남깁니다. GPU 결과가 없는 케이스에는 수치를 채우지 않습니다.</summary>
        private static void Summarize(Measurement measurement)
        {
            measurement.milliseconds = currentSamples.ToArray();
            double[] sorted = (double[])measurement.milliseconds.Clone();
            Array.Sort(sorted);
            int middle = sorted.Length / 2;
            measurement.medianMilliseconds = (sorted[middle - 1] + sorted[middle]) * 0.5d;
            measurement.p95Milliseconds = sorted[Mathf.Clamp(Mathf.CeilToInt(sorted.Length * 0.95f) - 1, 0, sorted.Length - 1)];
            measurement.minimumMilliseconds = sorted[0];
            measurement.maximumMilliseconds = sorted[sorted.Length - 1];
            measurement.available = true;
        }

        /// <summary>공통 형상 에셋을 복제하여 외부 튜닝이 진행 중 측정 입력을 바꾸지 않게 합니다.</summary>
        private static T CloneProfile<T>(T source) where T : ScriptableObject
        {
            if (source == null)
            {
                return null;
            }

            T copy = UnityEngine.Object.Instantiate(source);
            copy.hideFlags = HideFlags.HideAndDontSave;
            ownedProfiles.Add(copy);
            return copy;
        }

        /// <summary>동일한 월드 좌표에서 생산 패스의 환경 공급자 또는 기본 색상 계약을 한 번 평가합니다.</summary>
        private static CloudEnvironment ResolveEnvironment(LostSkiesCloudPass pass, Vector3 position)
        {
            if (pass.environmentSource != null && pass.environmentSource.isActiveAndEnabled)
            {
                return pass.environmentSource.Evaluate(position);
            }

            CloudEnvironment environment = CloudEnvironment.ClearDay();
            environment.brightness = pass.brightness;
            environment.sunlight = pass.sunlight;
            environment.shadowTint = pass.shadowTint;
            environment.highlightTint = pass.highlightTint;
            return environment;
        }

        /// <summary>이전 연속 캡처의 고정 좌표를 읽어 시점 변경과 형태 변경을 구분합니다.</summary>
        private static Pose ReadPose(string name, string sequence, int frame)
        {
            string path = "Screenshots/StyleMotion-Final/" + sequence + "/poses.json";
            Require(File.Exists(path), "A fixed baseline pose manifest is missing: " + path);
            BaselineManifest manifest = JsonUtility.FromJson<BaselineManifest>(File.ReadAllText(path));
            Require(manifest != null && manifest.path != null && manifest.path.poses != null
                && frame < manifest.path.poses.Length, "Invalid baseline camera manifest: " + path);
            CloudStyleCapture.CameraPose source = manifest.path.poses[frame];
            Require(source != null && source.frame == frame, "The baseline camera frame does not match.");
            Pose pose = new Pose();
            pose.name = name;
            pose.basis = path + " frame " + frame;
            pose.position = source.densityPosition;
            pose.rotation = source.worldRotation;
            return pose;
        }

        /// <summary>사용자의 현재 장면에서 활성 구름 패스의 입력만 찾으며 연결을 변경하지 않습니다.</summary>
        private static LostSkiesCloudPass FindPass()
        {
            foreach (CustomPassVolume volume in UnityEngine.Object.FindObjectsByType<CustomPassVolume>(FindObjectsSortMode.None))
            {
                if (!volume.isActiveAndEnabled)
                {
                    continue;
                }

                foreach (CustomPass customPass in volume.customPasses)
                {
                    LostSkiesCloudPass pass = customPass as LostSkiesCloudPass;
                    if (pass != null && pass.enabled)
                    {
                        return pass;
                    }
                }
            }

            throw new InvalidOperationException("An active LostSkiesCloudPass is required.");
        }

        /// <summary>측정을 명시적으로 중단하고 모든 임시 자원을 정리합니다.</summary>
        public static void Cancel()
        {
            if (report.running)
            {
                Finish("cancelled", "The GPU benchmark was cancelled.");
            }
        }

        /// <summary>어셈블리 재로드 전에 업데이트 구독과 네이티브 GPU 기록 자원을 해제합니다.</summary>
        private static void BeforeAssemblyReload()
        {
            Finish("cancelled", "Assembly reload interrupted GPU collection.");
        }

        /// <summary>Editor 종료 시 아직 남은 독립 렌더러 자원을 해제합니다.</summary>
        private static void BeforeEditorQuit()
        {
            Finish("cancelled", "Editor shutdown interrupted GPU collection.");
        }

        /// <summary>편집/실행 전환으로 계측 조건이 달라지기 전에 측정을 종료합니다.</summary>
        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode)
            {
                Finish("cancelled", "Play mode changed during GPU collection.");
            }
        }

        /// <summary>성공·실패·미지원·중단 모두 같은 정리 경로를 거쳐 결과를 저장합니다.</summary>
        private static void Finish(string status, string error)
        {
            report.status = status;
            report.running = false;
            report.completed = status == "completed";
            report.error = error;
            report.finishedAtUtc = DateTime.UtcNow.ToString("O");
            if (report.measurements != null && report.activeCase < report.measurements.Length)
            {
                Measurement pending = report.measurements[report.activeCase];
                if (pending != null && !pending.available)
                {
                    pending.milliseconds = currentSamples.ToArray();
                }
            }

            try
            {
                Cleanup();
            }
            finally
            {
                SaveReport();
            }
        }

        /// <summary>계측을 위해 소유한 자원만 해제하며 장면 카메라와 원본 프로필은 수정하지 않습니다.</summary>
        private static void Cleanup()
        {
            EditorApplication.update -= Update;
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeAssemblyReload;
            EditorApplication.quitting -= BeforeEditorQuit;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            if (freshnessRecorder.Valid)
            {
                freshnessRecorder.Stop();
                freshnessRecorder.Dispose();
            }

            if (sampler != null)
            {
                sampler.enableRecording = false;
                sampler.Dispose();
                sampler = null;
            }

            if (commands != null)
            {
                commands.Release();
                commands = null;
            }

            if (renderer != null)
            {
                renderer.raymarchSampler = null;
                renderer.Dispose();
                renderer = null;
            }

            if (camera != null)
            {
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
                camera = null;
            }

            foreach (UnityEngine.Object profile in ownedProfiles)
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }

            ownedProfiles.Clear();
            currentSamples.Clear();
            report.cleanedUp = true;
        }

        /// <summary>프레임 수집을 차단하지 않도록 시작·케이스 완료·종료 시에만 보고서를 저장합니다.</summary>
        private static void SaveReport()
        {
            Directory.CreateDirectory("Screenshots");
            File.WriteAllText(activeOutputPath, JsonUtility.ToJson(report, true));
        }

        /// <summary>유효하지 않은 입력을 0ms 측정 결과로 숨기지 않고 실패 원인을 보고합니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
