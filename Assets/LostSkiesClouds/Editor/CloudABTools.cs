using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>MCP와 메뉴에서 같은 A/B 비교 씬과 명시적 빌드를 사용합니다.</summary>
    public static class CloudABTools
    {
        public const string ScenePath = "Assets/LostSkiesClouds/Scenes/CloudABComparison.unity";

        public const string QualityPath = "Assets/LostSkiesClouds/Presets/CloudAB-4080.asset";

        public const string LobePath = "Assets/LostSkiesClouds/Presets/CloudAB-Lobes.asset";

        /// <summary>기존 씬·기준 프로필의 GUID를 보존하고 별도의 비교 씬에 A/B 자원을 연결합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Comparison/Create Or Open A-B Scene")]
        public static string CreateScene()
        {
            if (EditorApplication.isPlaying)
            {
                throw new System.InvalidOperationException("Stop Play before opening the comparison scene.");
            }
            if (!File.Exists(ScenePath))
            {
                AssetDatabase.CopyAsset(CloudHybridTools.ScenePath, ScenePath);
            }
            var scene = EditorSceneManager.OpenScene(ScenePath);
            CloudHybridQualityProfile quality = AssetDatabase.LoadAssetAtPath<CloudHybridQualityProfile>(QualityPath);
            if (quality == null)
            {
                quality = ScriptableObject.CreateInstance<CloudHybridQualityProfile>();
                quality.buildSlicesPerFrame = 12;
                quality.buildsInFlight = 3;
                quality.maximumCells = 512;
                AssetDatabase.CreateAsset(quality, QualityPath);
            }
            quality.Sanitize();
            CloudLobeFormationProfile lobes = AssetDatabase.LoadAssetAtPath<CloudLobeFormationProfile>(LobePath);
            if (lobes == null)
            {
                lobes = ScriptableObject.CreateInstance<CloudLobeFormationProfile>();
                AssetDatabase.CreateAsset(lobes, LobePath);
            }
            foreach (CloudHybridSwitch routing in Object.FindObjectsByType<CloudHybridSwitch>(FindObjectsSortMode.None))
            {
                routing.Connect();
                CloudHybridPass pass = routing.Hybrid;
                pass.quality = quality;
                pass.proxyShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/LostSkiesClouds/Runtime/CloudHybridProxy.shader");
                pass.lobeSurfaceShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/LostSkiesClouds/Runtime/CloudLobeSurface.shader");
                pass.lobeCompositeShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/LostSkiesClouds/Runtime/CloudLobeComposite.shader");
                pass.lobeTileShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/LostSkiesClouds/Runtime/CloudLobeTiles.compute");
                pass.oceanSurfaceShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/LostSkiesClouds/Runtime/CloudOceanSurface.compute");
                pass.lobeProfile = lobes;
                routing.Reference.renderInEditor = false;
                routing.SetComparison(CloudComparisonVariant.HybridA, CloudComparisonView.Full);
                CloudHybridBenchmark oldBenchmark = routing.GetComponent<CloudHybridBenchmark>();
                if (oldBenchmark != null && !(oldBenchmark is CloudABBenchmark))
                {
                    Object.DestroyImmediate(oldBenchmark);
                }
                if (routing.GetComponent<CloudABBenchmark>() == null)
                {
                    routing.gameObject.AddComponent<CloudABBenchmark>();
                }
                if (routing.GetComponent<CloudComparisonPanel>() == null)
                {
                    routing.gameObject.AddComponent<CloudComparisonPanel>();
                }
                EditorUtility.SetDirty(routing.GetComponent<CustomPassVolume>());
                EditorUtility.SetDirty(routing);
            }
            EditorUtility.SetDirty(quality);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            return ScenePath;
        }

        /// <summary>비교 씬만 포함한 Development Player를 예약합니다.</summary>
        public static string QueueDevelopment()
        {
            CloudHybridTools.OverrideScenePath = ScenePath;
            CloudHybridTools.OverrideQualityPath = QualityPath;
            return CloudHybridTools.QueueDevelopment();
        }

        /// <summary>비교 씬만 포함한 Release Player를 예약합니다.</summary>
        public static string QueueRelease()
        {
            CloudHybridTools.OverrideScenePath = ScenePath;
            CloudHybridTools.OverrideQualityPath = QualityPath;
            return CloudHybridTools.QueueRelease();
        }
    }
}
