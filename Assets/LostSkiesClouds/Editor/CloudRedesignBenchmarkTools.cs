using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>Unity MCP에서 호출하는 명시적 장면의 Development/배포 Player 빌드 도구입니다.</summary>
    public static class CloudRedesignBenchmarkTools
    {
        public static string LastBuildPath { get; private set; }

        public static string LastBuildStatus { get; private set; } = "idle";

        public static string LastBuildError { get; private set; }

        private static bool queuedDevelopment;

        private static bool queuedExtensionOnly;

        private static System.Diagnostics.Process playerProcess;

        public static int LastPlayerProcessId { get; private set; }

        /// <summary>이 도구가 소유한 Player만 실행하여 동시 GPU 계측을 방지합니다.</summary>
        public static int LaunchPlayer(string path, string arguments)
        {
            if (playerProcess != null && !playerProcess.HasExited)
            {
                throw new InvalidOperationException("The previous benchmark Player is still running.");
            }

            if (playerProcess != null)
            {
                playerProcess.Dispose();
            }

            string executable = Path.GetFullPath(path);
            if (!File.Exists(executable))
            {
                throw new FileNotFoundException("Benchmark Player does not exist.", executable);
            }

            System.Diagnostics.ProcessStartInfo start = new System.Diagnostics.ProcessStartInfo();
            start.FileName = executable;
            start.Arguments = arguments;
            start.WorkingDirectory = Path.GetDirectoryName(executable);
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
            playerProcess = System.Diagnostics.Process.Start(start);
            LastPlayerProcessId = playerProcess.Id;
            return LastPlayerProcessId;
        }

        /// <summary>도구가 시작한 프로세스의 완료 여부와 종료 코드를 반환합니다.</summary>
        public static object PlayerStatus()
        {
            if (playerProcess == null)
            {
                return new { started = false, running = false, processId = 0, exitCode = -1 };
            }

            playerProcess.Refresh();
            int exitCode = -1;
            bool running = !playerProcess.HasExited;
            if (!running)
            {
                exitCode = playerProcess.ExitCode;
            }

            return new { started = true, running, processId = LastPlayerProcessId, exitCode };
        }

        [Serializable]
        private sealed class BuildManifest
        {
            public string builtUtc;
            public string unityVersion;
            public string scene;
            public string sceneDependencyHash;
            public string shaderDependencyHash;
            public string referenceShaderDependencyHash;
            public string runtimeSourceHash;
            public bool development;
            public bool extensionOnly;
            public string variantPolicy = "Standard benchmark valid combinations";
            public string notes = "FHD window, VSync disabled by runtime harness, FrameTiming enabled. Build does not auto-connect a Profiler.";
        }

        /// <summary>GPU 계측을 위한 Development Player를 생성합니다.</summary>
        public static string BuildDevelopment()
        {
            return Build(true);
        }

        /// <summary>최종 프레임 비용을 확인할 비 Development Player를 생성합니다.</summary>
        public static string BuildRelease()
        {
            return Build(false);
        }

        /// <summary>MCP 호출을 즉시 반환하고 다음 에디터 갱신에서 Development 빌드를 시작합니다.</summary>
        public static void QueueDevelopment()
        {
            QueueBuild(true);
        }

        /// <summary>MCP 호출을 즉시 반환하고 다음 에디터 갱신에서 배포 빌드를 시작합니다.</summary>
        public static void QueueRelease()
        {
            QueueBuild(false);
        }

        /// <summary>기존 Development 결과를 보존하며 다섯 프리셋 전용 GPU 계측 Player를 예약합니다.</summary>
        public static void QueueExtensionDevelopment()
        {
            QueueBuild(true, true);
        }

        /// <summary>기존 Release 결과를 보존하며 다섯 프리셋 전용 배포 Player를 예약합니다.</summary>
        public static void QueueExtensionRelease()
        {
            QueueBuild(false, true);
        }

        /// <summary>중복 빌드를 거부하고 상태 조회에 사용할 값을 먼저 초기화합니다.</summary>
        private static void QueueBuild(bool development, bool extensionOnly = false)
        {
            if (LastBuildStatus == "queued" || LastBuildStatus == "building")
            {
                throw new InvalidOperationException("A benchmark build is already queued or running.");
            }

            queuedDevelopment = development;
            queuedExtensionOnly = extensionOnly;
            LastBuildStatus = "queued";
            LastBuildError = null;
            EditorApplication.update += ExecuteQueuedBuild;
        }

        /// <summary>에디터 스레드에서 빌드를 수행하고 예외도 조회 가능한 완료 상태로 저장합니다.</summary>
        private static void ExecuteQueuedBuild()
        {
            EditorApplication.update -= ExecuteQueuedBuild;
            if (LastBuildStatus != "queued")
            {
                return;
            }

            LastBuildStatus = "building";
            try
            {
                Build(queuedDevelopment, queuedExtensionOnly);
                LastBuildStatus = "completed";
            }
            catch (Exception exception)
            {
                LastBuildStatus = "failed";
                LastBuildError = exception.ToString();
                Debug.LogException(exception);
            }
            finally
            {
                queuedExtensionOnly = false;
            }
        }

        /// <summary>빌드에 필요한 설정을 임시 적용하고 성공 여부와 무관하게 프로젝트 설정을 복원합니다.</summary>
        private static string Build(bool development, bool extensionOnly = false)
        {
            if (Application.isPlaying)
            {
                throw new InvalidOperationException("Exit Play mode before building a benchmark Player.");
            }

            const string scene = "Assets/LostSkiesClouds/Scenes/LostSkiesCloudSea.unity";
            string kind = "Release";
            BuildOptions options = BuildOptions.None;
            if (development)
            {
                kind = "Development";
                options = BuildOptions.Development;
            }

            if (extensionOnly)
            {
                kind = "Extension" + kind;
            }

            string directory = Path.GetFullPath("Builds/CloudRedesign/" + kind);
            Directory.CreateDirectory(directory);
            LastBuildPath = Path.Combine(directory, "ClouDream.exe");
            BuildManifest manifest = new BuildManifest();
            manifest.builtUtc = DateTime.UtcNow.ToString("O");
            manifest.unityVersion = Application.unityVersion;
            manifest.scene = scene;
            manifest.sceneDependencyHash = AssetDatabase.GetAssetDependencyHash(scene).ToString();
            manifest.shaderDependencyHash = AssetDatabase.GetAssetDependencyHash("Assets/LostSkiesClouds/Runtime/CloudRaymarch.compute").ToString();
            manifest.referenceShaderDependencyHash = AssetDatabase.GetAssetDependencyHash("Assets/LostSkiesClouds/Resources/CloudRedesignReference/CloudRaymarch.compute").ToString();
            manifest.runtimeSourceHash = AssetDatabase.GetAssetDependencyHash("Assets/LostSkiesClouds/Runtime/LostSkiesCloudRenderer.cs").ToString();
            manifest.development = development;
            manifest.extensionOnly = extensionOnly;
            if (extensionOnly)
            {
                manifest.variantPolicy = "Extension-only: compact ON, height/palette/shape/spatial/traversal OFF; "
                    + "density equals lighting; normal and far surface are independent alternatives. "
                    + "Allowed presets: baseline, reference-performance, performance, normal-cache, surface-hybrid.";
                manifest.notes += " Dedicated CLI benchmark binary; use -cloudBenchmark with the allowed presets and -cloudQuit. "
                    + "Startup uses performance settings; other runtime panel combinations are not supported.";
            }
            Directory.CreateDirectory("Assets/LostSkiesClouds/Resources");
            string manifestPath = "Assets/LostSkiesClouds/Resources/CloudBenchmarkBuild.json";
            File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true));
            AssetDatabase.ImportAsset(manifestPath);

            bool timing = PlayerSettings.enableFrameTimingStats;
            int width = PlayerSettings.defaultScreenWidth;
            int height = PlayerSettings.defaultScreenHeight;
            FullScreenMode mode = PlayerSettings.fullScreenMode;
            bool automaticApi = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
            GraphicsDeviceType[] apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
            try
            {
                CloudBenchmarkComputeStripper.ExtensionOnly = extensionOnly;
                CloudBenchmarkComputeStripper.Enabled = true;
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D12 });
                PlayerSettings.enableFrameTimingStats = true;
                PlayerSettings.defaultScreenWidth = 1920;
                PlayerSettings.defaultScreenHeight = 1080;
                PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
                BuildPlayerOptions build = new BuildPlayerOptions();
                build.scenes = new[] { scene };
                build.locationPathName = LastBuildPath;
                build.target = BuildTarget.StandaloneWindows64;
                build.options = options;
                if (extensionOnly)
                {
                    // Player 전용 define이므로 Editor 및 표준 빌드의 허용 프리셋은 바꾸지 않습니다.
                    build.extraScriptingDefines = new[] { "CLOUD_EXTENSION_BENCHMARK" };
                }

                BuildReport report = BuildPipeline.BuildPlayer(build);
                File.WriteAllText(Path.Combine(directory, "build-manifest.json"), JsonUtility.ToJson(manifest, true));
                if (report.summary.result != BuildResult.Succeeded)
                {
                    throw new InvalidOperationException("Benchmark build failed: " + report.summary.result);
                }

                return LastBuildPath;
            }
            finally
            {
                CloudBenchmarkComputeStripper.Enabled = false;
                CloudBenchmarkComputeStripper.ExtensionOnly = false;
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, apis);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, automaticApi);
                PlayerSettings.enableFrameTimingStats = timing;
                PlayerSettings.defaultScreenWidth = width;
                PlayerSettings.defaultScreenHeight = height;
                PlayerSettings.fullScreenMode = mode;
            }
        }
    }
}
