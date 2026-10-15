using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>Unity MCP에서 호출하는 독립 검증 씬·명시적 Player 빌드 연결점입니다.</summary>
    public static class CloudHybridTools
    {
        public const string ScenePath = "Assets/LostSkiesClouds/Scenes/CloudHybridValidation.unity";

        public const string QualityPath = "Assets/LostSkiesClouds/Presets/Hybrid-4080-FHD144.asset";

        public static string BuildStatus { get; private set; } = "idle";

        public static string BuildError { get; private set; }

        public static string BuildPath { get; private set; }

        public static string OverrideScenePath;

        public static string OverrideQualityPath;

        private static bool development;

        [Serializable]
        private sealed class Manifest
        {
            public string createdUtc;
            public string unityVersion;
            public string scene;
            public string sceneHash;
            public string runtimeSourceSha256;
            public string quality;
            public bool development;
            public string variantPolicy = "Dedicated reference: compact targets, distance density+lighting, other experimental variants stripped. Hybrid uses its independent kernels.";
        }

        /// <summary>원본 씬을 바꾸지 않고 저장된 복사본에 별도 패스와 설정을 연결합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Hybrid/Create Or Open Validation Scene")]
        public static string CreateValidationScene()
        {
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("Stop Play before creating the hybrid validation scene.");
            }

            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty)
            {
                throw new InvalidOperationException("The current scene has unsaved changes; save them before changing scenes.");
            }

            if (!File.Exists(ScenePath))
            {
                if (!AssetDatabase.CopyAsset("Assets/LostSkiesClouds/Scenes/LostSkiesCloudSea.unity", ScenePath))
                {
                    throw new InvalidOperationException("Could not copy the reference scene.");
                }
            }

            var scene = EditorSceneManager.OpenScene(ScenePath);
            CloudHybridQualityProfile quality = AssetDatabase.LoadAssetAtPath<CloudHybridQualityProfile>(QualityPath);
            if (quality == null)
            {
                quality = ScriptableObject.CreateInstance<CloudHybridQualityProfile>();
                quality.Sanitize();
                AssetDatabase.CreateAsset(quality, QualityPath);
            }

            foreach (CustomPassVolume volume in UnityEngine.Object.FindObjectsByType<CustomPassVolume>(FindObjectsSortMode.None))
            {
                LostSkiesCloudPass reference = null;
                CloudHybridPass hybrid = null;
                foreach (CustomPass pass in volume.customPasses)
                {
                    if (pass is LostSkiesCloudPass source)
                    {
                        reference = source;
                    }

                    if (pass is CloudHybridPass existing)
                    {
                        hybrid = existing;
                    }
                }

                if (reference == null)
                {
                    continue;
                }

                if (hybrid == null)
                {
                    hybrid = new CloudHybridPass();
                    hybrid.name = "ClouDream / hybrid meshes and near density";
                    volume.customPasses.Insert(0, hybrid);
                }

                hybrid.meshGenerator = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/LostSkiesClouds/Runtime/CloudHybridMesh.compute");
                hybrid.surfaceShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/LostSkiesClouds/Runtime/CloudHybridSurface.shader");
                hybrid.compositeShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/LostSkiesClouds/Runtime/CloudHybridComposite.shader");
                hybrid.proxyShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/LostSkiesClouds/Runtime/CloudHybridProxy.shader");
                hybrid.quality = quality;
                reference.resolutionScale = 0.5f;
                reference.renderInEditor = false;
                reference.useDepthAwareUpsampling = true;
                reference.distanceQuality = CloudDistanceQualitySettings.Default;
                reference.distanceQuality.density = true;
                reference.distanceQuality.lighting = true;
                reference.distanceQuality.densityStepScale = 1.5f;
                reference.distanceQuality.lightingSpacingScale = 1.5f;
                CloudHybridSwitch routing = volume.GetComponent<CloudHybridSwitch>();
                if (routing == null)
                {
                    routing = volume.gameObject.AddComponent<CloudHybridSwitch>();
                }

                routing.useHybrid = true;
                routing.Connect();
                CloudHybridBenchmark benchmark = volume.GetComponent<CloudHybridBenchmark>();
                if (benchmark == null)
                {
                    volume.gameObject.AddComponent<CloudHybridBenchmark>();
                }

                EditorUtility.SetDirty(volume);
                EditorUtility.SetDirty(routing);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            return ScenePath;
        }

        /// <summary>GPU 패스 측정용 Development 빌드를 예약합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Hybrid/Build Development")]
        public static string QueueDevelopment()
        {
            return QueueBuild(true);
        }

        /// <summary>전체 프레임 판정용 Release 빌드를 예약합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Hybrid/Build Release")]
        public static string QueueRelease()
        {
            return QueueBuild(false);
        }

        /// <summary>에디터 GUI 지연 콜이 멈춘 환경에서도 MCP가 예약된 빌드를 직접 시작할 수 있게 합니다.</summary>
        public static string RunQueuedBuild()
        {
            if (BuildStatus != "queued")
            {
                throw new InvalidOperationException("No queued hybrid build.");
            }

            EditorApplication.update -= BuildQueued;
            BuildQueued();
            return BuildStatus;
        }

        /// <summary>MCP 호출 시간 제한과 빌드 실행을 분리합니다.</summary>
        private static string QueueBuild(bool isDevelopment)
        {
            if (BuildStatus == "queued" || BuildStatus == "building")
            {
                throw new InvalidOperationException("A hybrid Player build is already running.");
            }

            if (EditorApplication.isPlaying || !File.Exists(ScenePath))
            {
                throw new InvalidOperationException("Create the saved validation scene and stop Play before building.");
            }

            development = isDevelopment;
            BuildStatus = "queued";
            BuildError = null;
            EditorApplication.update += BuildQueued;
            return BuildStatus;
        }

        /// <summary>빌드 대상 씬을 명시하고 프로젝트 빌드 목록을 보존합니다.</summary>
        private static void BuildQueued()
        {
            EditorApplication.update -= BuildQueued;
            BuildStatus = "building";
            Dictionary<string, byte[]> preservedFiles = CaptureBuildFiles();
            bool previousTiming = PlayerSettings.enableFrameTimingStats;
            int previousWidth = PlayerSettings.defaultScreenWidth;
            int previousHeight = PlayerSettings.defaultScreenHeight;
            FullScreenMode previousMode = PlayerSettings.fullScreenMode;
            bool previousAutomaticApi = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
            GraphicsDeviceType[] previousApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
            try
            {
                PlayerSettings.enableFrameTimingStats = true;
                PlayerSettings.defaultScreenWidth = 1920;
                PlayerSettings.defaultScreenHeight = 1080;
                PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D12 });
                CloudBenchmarkComputeStripper.Enabled = true;
                CloudBenchmarkComputeStripper.HybridOnly = true;
                string folder = "Builds/Hybrid-Release";
                BuildOptions options = BuildOptions.None;
                if (development)
                {
                    folder = "Builds/Hybrid-Development";
                    options = BuildOptions.Development;
                }

                if (!string.IsNullOrEmpty(OverrideScenePath))
                {
                    folder = folder.Replace("Hybrid-", "CloudAB-");
                }
                Directory.CreateDirectory(folder);
                Manifest manifest = new Manifest();
                manifest.createdUtc = DateTime.UtcNow.ToString("O");
                manifest.unityVersion = Application.unityVersion;
                string scenePath = ScenePath;
                if (!string.IsNullOrEmpty(OverrideScenePath))
                {
                    scenePath = OverrideScenePath;
                }
                manifest.scene = scenePath;
                manifest.sceneHash = AssetDatabase.GetAssetDependencyHash(scenePath).ToString();
                manifest.runtimeSourceSha256 = RuntimeHash();
                string qualityPath = QualityPath;
                if (!string.IsNullOrEmpty(OverrideQualityPath))
                {
                    qualityPath = OverrideQualityPath;
                }
                manifest.quality = JsonUtility.ToJson(AssetDatabase.LoadAssetAtPath<CloudHybridQualityProfile>(qualityPath));
                manifest.development = development;
                File.WriteAllText(folder + "/build-manifest.json", JsonUtility.ToJson(manifest, true));
                BuildPath = Path.GetFullPath(folder + "/ClouDreamHybrid.exe");
                BuildPlayerOptions build = new BuildPlayerOptions();
                build.scenes = new[] { scenePath };
                build.target = BuildTarget.StandaloneWindows64;
                build.locationPathName = BuildPath;
                build.options = options;
                BuildReport report = BuildPipeline.BuildPlayer(build);
                BuildStatus = report.summary.result.ToString();
                if (report.summary.result != BuildResult.Succeeded)
                {
                    BuildError = "Build errors: " + report.summary.totalErrors;
                }
            }
            catch (Exception exception)
            {
                BuildStatus = "failed";
                BuildError = exception.ToString();
                Debug.LogException(exception);
            }
            finally
            {
                PlayerSettings.enableFrameTimingStats = previousTiming;
                PlayerSettings.defaultScreenWidth = previousWidth;
                PlayerSettings.defaultScreenHeight = previousHeight;
                PlayerSettings.fullScreenMode = previousMode;
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, previousApis);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, previousAutomaticApi);
                CloudBenchmarkComputeStripper.Enabled = false;
                CloudBenchmarkComputeStripper.HybridOnly = false;
                AssetDatabase.SaveAssets();
                RestoreBuildFiles(preservedFiles);
                OverrideScenePath = null;
                OverrideQualityPath = null;
            }
        }

        /// <summary>HDRP 빌드 콜백과 서비스 설정의 자동 직렬화가 기존 프리셋을 남겨 바꾸지 않게 보관합니다.</summary>
        private static Dictionary<string, byte[]> CaptureBuildFiles()
        {
            string[] paths =
            {
                "Assets/CloudSea/Settings/CloudSeaHDRP.asset",
                "Assets/Settings/HDRP Balanced.asset",
                "Assets/Settings/HDRP Performant.asset",
                "Assets/Settings/HDRPDefaultResources/HDRenderPipelineGlobalSettings.asset",
                "ProjectSettings/UnityConnectSettings.asset"
            };
            Dictionary<string, byte[]> files = new Dictionary<string, byte[]>();
            foreach (string path in paths)
            {
                if (File.Exists(path))
                {
                    files.Add(path, File.ReadAllBytes(path));
                }
            }
            return files;
        }

        /// <summary>사용자에게서 받은 원문 바이트를 빌드 후 복원하고 에셋 DB에 알립니다. 메타 GUID는 건드리지 않습니다.</summary>
        private static void RestoreBuildFiles(Dictionary<string, byte[]> files)
        {
            foreach (KeyValuePair<string, byte[]> file in files)
            {
                // Unity가 원본을 메모리 매핑한 상태에서도 truncate 없이 새 파일로 교체합니다.
                string temporary = file.Key + ".cloud-restore.tmp";
                File.WriteAllBytes(temporary, file.Value);
                File.Replace(temporary, file.Key, null);
                if (file.Key.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    AssetDatabase.ImportAsset(file.Key, ImportAssetOptions.ForceUpdate);
                }
            }
        }

        /// <summary>파일명과 원문 바이트를 정렬해 실제 런타임 소스의 재현 키를 기록합니다.</summary>
        private static string RuntimeHash()
        {
            string[] files = Directory.GetFiles("Assets/LostSkiesClouds/Runtime", "*.*");
            Array.Sort(files, StringComparer.Ordinal);
            using (SHA256 hash = SHA256.Create())
            {
                foreach (string file in files)
                {
                    string extension = Path.GetExtension(file);
                    if (extension != ".cs" && extension != ".hlsl" && extension != ".shader" && extension != ".compute")
                    {
                        continue;
                    }

                    byte[] name = Encoding.UTF8.GetBytes(Path.GetFileName(file));
                    byte[] content = File.ReadAllBytes(file);
                    hash.TransformBlock(name, 0, name.Length, name, 0);
                    hash.TransformBlock(content, 0, content.Length, content, 0);
                }

                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
