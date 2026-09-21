using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>동일 게임 카메라에서 기존 표면과 스타일 표면의 동기 렌더 완료 시간을 비교합니다.</summary>
    public static class CloudStylePerformance
    {
        private const int WarmupFrames = 3;

        [Serializable]
        public sealed class Measurement
        {
            public string label;
            public string styleAsset;
            public int warmupFrames = WarmupFrames;
            public int completedFrames;
            public double[] milliseconds;
            public double meanMilliseconds;
            public double medianMilliseconds;
            public double minimumMilliseconds;
            public double maximumMilliseconds;

            public int cloudWidth;
            public int cloudHeight;
            public string cloudTextureFormat;
            public int passExecutions;
            public int noiseGenerations;
        }

        [Serializable]
        public sealed class Report
        {
            public bool completed;
            public string capturedAtUtc;
            public string metric = "Editor wall-clock milliseconds: Camera.Render plus target AsyncGPUReadback and WaitForCompletion. Includes CPU, GPU completion, synchronization and readback; not isolated GPU time and not game FPS.";
            public string limitations = "One fixed view; baseline measured first. Scene View and other Editor rendering, system load, driver queues and thermal state may affect results. Three warmup renders per method exclude most first-use work. This is comparative instrumentation, not a performance test or frame-rate guarantee.";
            public string unityVersion;
            public string operatingSystem;
            public string processor;
            public int logicalProcessorCount;
            public int systemMemoryMegabytes;
            public string graphicsDevice;
            public string graphicsApi;
            public string graphicsDriver;
            public int graphicsMemoryMegabytes;
            public bool multithreadedGraphics;

            public string qualityLevel;
            public string renderPipelineAsset;
            public string colorSpace;
            public string antialiasing;
            public int vSyncCount;
            public int applicationTargetFrameRate;
            public int openSceneViewCount;
            public bool wasPlaying;
            public bool cameraAllowsDynamicResolution;

            public string cameraName;
            public Vector3 cameraPosition;
            public Quaternion cameraRotation;
            public float fieldOfView;
            public float timeOfDay;
            public string skyProfile;
            public string oceanProfile;
            public float resolutionScale;
            public int targetWidth;
            public int targetHeight;
            public string targetFormat;
            public int requestedFramesPerMethod;
            public float frozenWindSpeed;

            public Measurement baseline;
            public Measurement styled;
            public double styledToBaselineMedianRatio;
            public bool sameCloudTargetSize;
            public bool sceneStateRestored;
            public string error;
        }

        /// <summary>시점과 조명을 고정한 채 기본·스타일 표면을 각각 준비 렌더 3회 후 계측하고 모든 임시 상태를 복구합니다.</summary>
        public static Report Run(int width = 1280, int height = 720, int frames = 8)
        {
            if (width <= 0 || height <= 0 || frames <= 0)
            {
                throw new ArgumentOutOfRangeException("width, height, frames", "출력 크기와 계측 프레임 수는 양수여야 합니다.");
            }

            Camera camera = Camera.main;
            Require(camera != null, "MainCamera 태그가 지정된 게임 카메라가 필요합니다.");
            LostSkiesCloudPass pass = FindPass();
            CloudTimeOfDayController clock = pass.environmentSource as CloudTimeOfDayController;
            Require(clock != null && clock.isActiveAndEnabled, "공유 조명을 사용하는 활성 CloudTimeOfDayController가 필요합니다.");
            Require(pass.styleProfile != null, "비교할 스타일 프로필을 먼저 활성 구름 패스에 연결해야 합니다.");
            Require(!pass.useExtractedValues && (Application.isPlaying || pass.renderInEditor), "현재 설정에서는 스타일 구름 패스가 렌더되지 않습니다.");

            CloudStyleProfile savedStyle = pass.styleProfile;
            CloudSkyProfile savedSky = pass.skyProfile;
            float savedWind = pass.windSpeed;
            float savedHour = clock.TimeOfDay;
            bool savedAutoAdvance = clock.autoAdvance;
            float savedAspect = camera.aspect;
            RenderTexture savedTarget = camera.targetTexture;
            RenderTexture savedActive = RenderTexture.active;

            Report report = DescribeConfiguration(camera, pass, clock, width, height, frames);
            RenderTexture target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            try
            {
                target.name = "Cloud style performance target";
                target.useDynamicScale = false;
                target.Create();
                Require(target.IsCreated(), "성능 계측용 렌더 타깃 생성이 실패했습니다.");

                camera.targetTexture = target;
                camera.aspect = width / (float)height;
                pass.windSpeed = 0f;
                clock.autoAdvance = false;
                // 동기 호출 동안 Update가 진행되지 않으므로 진행 중인 시간 전환도 중단 없이 보존됩니다.
                clock.ApplyCurrent();

                pass.styleProfile = null;
                report.baseline = Measure(camera, pass, target, frames, "Baseline / no surface style", "");
                pass.styleProfile = savedStyle;
                report.styled = Measure(camera, pass, target, frames, "Styled / active surface style", AssetDatabase.GetAssetPath(savedStyle));

                Require(pass.skyProfile == savedSky, "두 계측 사이에 상층 분포 프로필이 변경되었습니다.");
                report.sameCloudTargetSize = report.baseline.cloudWidth == report.styled.cloudWidth
                    && report.baseline.cloudHeight == report.styled.cloudHeight;
                Require(report.sameCloudTargetSize, "두 렌더 경로의 실제 구름 출력 해상도가 달라 직접 비교할 수 없습니다.");
                if (report.baseline.medianMilliseconds > 0d)
                {
                    report.styledToBaselineMedianRatio = report.styled.medianMilliseconds / report.baseline.medianMilliseconds;
                }

                report.completed = true;
            }
            catch (Exception exception)
            {
                report.error = exception.ToString();
                UnityEngine.Debug.LogException(exception);
            }
            finally
            {
                pass.styleProfile = savedStyle;
                pass.windSpeed = savedWind;
                camera.targetTexture = savedTarget;
                camera.aspect = savedAspect;
                RenderTexture.active = savedActive;

                try
                {
                    // 시각이 그대로이면 SetTime을 호출하지 않아 기존 수동 전환의 진행 상태까지 유지합니다.
                    if (clock.TimeOfDay != savedHour)
                    {
                        clock.SetTime(savedHour);
                    }
                    else
                    {
                        clock.ApplyCurrent();
                    }
                }
                finally
                {
                    clock.autoAdvance = savedAutoAdvance;
                    target.Release();
                    UnityEngine.Object.DestroyImmediate(target);
                    report.sceneStateRestored = camera.targetTexture == savedTarget
                        && RenderTexture.active == savedActive
                        && camera.aspect == savedAspect
                        && pass.styleProfile == savedStyle
                        && pass.skyProfile == savedSky
                        && pass.windSpeed == savedWind
                        && clock.TimeOfDay == savedHour
                        && clock.autoAdvance == savedAutoAdvance;
                    Directory.CreateDirectory("Screenshots");
                    File.WriteAllText("Screenshots/Style-Performance.json", JsonUtility.ToJson(report, true));
                }
            }

            return report;
        }

        /// <summary>게임 카메라가 사용하는 활성 볼륨에서 구름 패스를 찾습니다.</summary>
        private static LostSkiesCloudPass FindPass()
        {
            CustomPassVolume[] volumes = UnityEngine.Object.FindObjectsByType<CustomPassVolume>(FindObjectsSortMode.None);
            foreach (CustomPassVolume volume in volumes)
            {
                if (!volume.isActiveAndEnabled)
                {
                    continue;
                }

                foreach (CustomPass customPass in volume.customPasses)
                {
                    LostSkiesCloudPass cloudPass = customPass as LostSkiesCloudPass;
                    if (cloudPass != null && cloudPass.enabled)
                    {
                        return cloudPass;
                    }
                }
            }

            throw new InvalidOperationException("활성 LostSkiesCloudPass가 없습니다.");
        }

        /// <summary>시간에 영향을 줄 수 있는 장치, Unity 품질, 카메라 및 볼륨 설정을 결과와 함께 기록합니다.</summary>
        private static Report DescribeConfiguration(Camera camera, LostSkiesCloudPass pass, CloudTimeOfDayController clock, int width, int height, int frames)
        {
            Report report = new Report();
            report.capturedAtUtc = DateTime.UtcNow.ToString("O");
            report.unityVersion = Application.unityVersion;
            report.operatingSystem = SystemInfo.operatingSystem;
            report.processor = SystemInfo.processorType;
            report.logicalProcessorCount = SystemInfo.processorCount;
            report.systemMemoryMegabytes = SystemInfo.systemMemorySize;
            report.graphicsDevice = SystemInfo.graphicsDeviceName;
            report.graphicsApi = SystemInfo.graphicsDeviceType.ToString();
            report.graphicsDriver = SystemInfo.graphicsDeviceVersion;
            report.graphicsMemoryMegabytes = SystemInfo.graphicsMemorySize;
            report.multithreadedGraphics = SystemInfo.graphicsMultiThreaded;

            report.qualityLevel = QualitySettings.names[QualitySettings.GetQualityLevel()];
            report.renderPipelineAsset = AssetDatabase.GetAssetPath(GraphicsSettings.currentRenderPipeline);
            report.colorSpace = QualitySettings.activeColorSpace.ToString();
            report.vSyncCount = QualitySettings.vSyncCount;
            report.applicationTargetFrameRate = Application.targetFrameRate;
            report.openSceneViewCount = SceneView.sceneViews.Count;
            report.wasPlaying = Application.isPlaying;
            report.cameraAllowsDynamicResolution = camera.allowDynamicResolution;
            HDAdditionalCameraData additional = camera.GetComponent<HDAdditionalCameraData>();
            if (additional != null)
            {
                report.antialiasing = additional.antialiasing.ToString();
            }

            report.cameraName = camera.name;
            report.cameraPosition = camera.transform.position;
            report.cameraRotation = camera.transform.rotation;
            report.fieldOfView = camera.fieldOfView;
            report.timeOfDay = clock.TimeOfDay;
            report.skyProfile = AssetDatabase.GetAssetPath(pass.skyProfile);
            report.oceanProfile = AssetDatabase.GetAssetPath(pass.oceanProfile);
            report.resolutionScale = pass.resolutionScale;
            report.targetWidth = width;
            report.targetHeight = height;
            report.targetFormat = RenderTextureFormat.ARGB32.ToString();
            report.requestedFramesPerMethod = frames;
            report.frozenWindSpeed = 0f;
            return report;
        }

        /// <summary>같은 프로필에서 세 번 준비 렌더 후 요청한 횟수만큼 CPU와 GPU 완료 대기 시간을 함께 측정합니다.</summary>
        private static Measurement Measure(Camera camera, LostSkiesCloudPass pass, RenderTexture target, int frames, string label, string styleAsset)
        {
            Measurement measurement = new Measurement();
            measurement.label = label;
            measurement.styleAsset = styleAsset;
            measurement.milliseconds = new double[frames];

            for (int frame = 0; frame < WarmupFrames; frame++)
            {
                RenderAndWait(camera, pass, target);
            }

            int initialExecutions = pass.executions;
            Stopwatch watch = new Stopwatch();
            for (int frame = 0; frame < frames; frame++)
            {
                watch.Restart();
                RenderAndWait(camera, pass, target);
                watch.Stop();
                measurement.milliseconds[frame] = watch.Elapsed.TotalMilliseconds;
                measurement.completedFrames++;
            }

            measurement.passExecutions = pass.executions - initialExecutions;
            Require(measurement.passExecutions >= frames, "계측 중 구름 패스가 요청한 프레임마다 실행되지 않았습니다.");
            DescribeCloudTargets(pass, measurement);
            Summarize(measurement);
            return measurement;
        }

        /// <summary>게임 카메라 렌더와 출력 타깃 읽기의 GPU 완료까지 동기적으로 기다립니다.</summary>
        private static void RenderAndWait(Camera camera, LostSkiesCloudPass pass, RenderTexture target)
        {
            int executions = pass.executions;
            camera.Render();
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(target, 0, TextureFormat.RGBA32);
            request.WaitForCompletion();
            Require(!request.hasError, "계측 중 렌더 타깃 GPU 읽기가 실패했습니다.");
            Require(pass.executions > executions && pass.lastCamera == camera.name, "게임 카메라에서 대상 구름 패스가 실행되지 않았습니다.");
        }

        /// <summary>해상도 추정 대신 실제 패스 소유 렌더러의 출력 크기를 진단 목적으로만 읽습니다.</summary>
        private static void DescribeCloudTargets(LostSkiesCloudPass pass, Measurement measurement)
        {
            FieldInfo field = typeof(LostSkiesCloudPass).GetField("clouds", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(field != null, "구름 출력 해상도를 읽을 진단 필드가 없습니다.");
            LostSkiesCloudRenderer renderer = field.GetValue(pass) as LostSkiesCloudRenderer;
            Require(renderer != null && renderer.lighting != null, "구름 렌더러의 실제 출력 타깃이 준비되지 않았습니다.");
            measurement.cloudWidth = renderer.Width;
            measurement.cloudHeight = renderer.Height;
            measurement.cloudTextureFormat = renderer.lighting.format.ToString();
            measurement.noiseGenerations = renderer.NoiseGenerationCount;
        }

        /// <summary>원본 샘플 배열은 보존하고 정렬한 복사본으로 중앙값과 범위를 계산합니다.</summary>
        private static void Summarize(Measurement measurement)
        {
            double total = 0d;
            foreach (double milliseconds in measurement.milliseconds)
            {
                total += milliseconds;
            }

            measurement.meanMilliseconds = total / measurement.milliseconds.Length;
            double[] sorted = (double[])measurement.milliseconds.Clone();
            Array.Sort(sorted);
            measurement.minimumMilliseconds = sorted[0];
            measurement.maximumMilliseconds = sorted[sorted.Length - 1];
            int middle = sorted.Length / 2;
            if (sorted.Length % 2 == 0)
            {
                measurement.medianMilliseconds = (sorted[middle - 1] + sorted[middle]) * 0.5d;
            }
            else
            {
                measurement.medianMilliseconds = sorted[middle];
            }
        }

        /// <summary>계측 조건이나 GPU 완료가 유효하지 않을 때 불완전한 수치를 정상 결과로 기록하지 않습니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
