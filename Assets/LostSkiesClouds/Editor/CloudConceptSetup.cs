using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>이전 조형을 보존하면서 컨셉 V2의 에셋과 장면 연결을 관리합니다.</summary>
    public static class CloudConceptSetup
    {
        private const string Presets = "Assets/LostSkiesClouds/Presets/";

        /// <summary>없는 V2 에셋만 생성합니다. 사용자가 바꾼 프리셋은 재실행해도 덮어쓰지 않습니다.</summary>
        public static void CreatePresets()
        {
            if (AssetDatabase.LoadAssetAtPath<CloudStyleProfile>(Presets + "Style-ConceptV2.asset") == null)
            {
                CloudStyleProfile style = ScriptableObject.CreateInstance<CloudStyleProfile>();
                style.method = CloudStyleProfile.ShapeMethod.ConceptV2;
                style.edgeSoftness = 0.02f;
                style.extinctionScale = 1.8f;
                style.ambientOcclusion = 0.42f;
                style.shadowLift = 0.015f;
                style.viewBillowDisplacement = 360f;
                style.fineBillowDisplacement = 100f;
                style.conceptBillowPeriod = 2700f;
                style.macroNormalBlend = 0.68f;
                style.aerialScale = 0.45f;
                AssetDatabase.CreateAsset(style, Presets + "Style-ConceptV2.asset");
            }

            if (AssetDatabase.LoadAssetAtPath<CloudSkyProfile>(Presets + "Sky-ConceptV2.asset") == null)
            {
                CloudSkyProfile source = AssetDatabase.LoadAssetAtPath<CloudSkyProfile>(Presets + "Sky-Concept.asset");
                CloudSkyProfile sky = Object.Instantiate(source);
                sky.name = "Sky-ConceptV2";
                AssetDatabase.CreateAsset(sky, Presets + "Sky-ConceptV2.asset");
            }

            if (AssetDatabase.LoadAssetAtPath<CloudLightingProfile>(Presets + "TimeOfDay-ConceptV2.asset") == null)
            {
                CloudLightingProfile lighting = ScriptableObject.CreateInstance<CloudLightingProfile>();
                lighting.enableMoonlitNight = true;
                lighting.day = CloudLightingState.ConceptDay();
                lighting.sunset = CloudLightingState.ConceptSunset();
                lighting.twilight = CloudLightingState.ConceptTwilight();
                lighting.night = CloudLightingState.MoonlitNight();
                AssetDatabase.CreateAsset(lighting, Presets + "TimeOfDay-ConceptV2.asset");
            }

            AssetDatabase.SaveAssets();
        }

        /// <summary>V2 형태와 네 시간대를 연결합니다. 기존 운해·날씨·월드 원점 계약은 패스가 계속 사용합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Concept V2/Apply")]
        public static void Apply()
        {
            CreatePresets();
            CloudStyleProfile style = AssetDatabase.LoadAssetAtPath<CloudStyleProfile>(Presets + "Style-ConceptV2.asset");
            CloudSkyProfile sky = AssetDatabase.LoadAssetAtPath<CloudSkyProfile>(Presets + "Sky-ConceptV2.asset");
            CloudLightingProfile lighting = AssetDatabase.LoadAssetAtPath<CloudLightingProfile>(Presets + "TimeOfDay-ConceptV2.asset");
            foreach (CustomPassVolume volume in Object.FindObjectsByType<CustomPassVolume>(FindObjectsInactive.Include))
            {
                foreach (CustomPass customPass in volume.customPasses)
                {
                    LostSkiesCloudPass pass = customPass as LostSkiesCloudPass;
                    if (pass != null)
                    {
                        pass.styleProfile = style;
                        pass.skyProfile = sky;
                        EditorUtility.SetDirty(volume);
                    }
                }
            }

            foreach (CloudTimeOfDayController controller in Object.FindObjectsByType<CloudTimeOfDayController>(FindObjectsInactive.Include))
            {
                if (controller.moon == null)
                {
                    GameObject host = new GameObject("Cloud Moon");
                    Light moon = host.AddComponent<Light>();
                    moon.type = LightType.Directional;
                    moon.enabled = false;
                    controller.moon = moon;
                }

                controller.profile = lighting;
                controller.SetTime(lighting.StartHour);
                EditorUtility.SetDirty(controller);
                EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
            }

            EditorSceneManager.SaveOpenScenes();
        }

        /// <summary>변경 전의 세 시간대와 LayeredBillows 연결을 복원하고 V2 에셋은 보존합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Concept V2/Restore September 21 Look")]
        public static void RestorePrevious()
        {
            CloudStyleSetup.Select("Style-Layered");
            EditorSceneManager.SaveOpenScenes();
        }
    }
}
