using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ClouDream.LostSkies.Editor
{
    public static class CloudStyleSetup
    {
        private const string Presets = "Assets/LostSkiesClouds/Presets/";

        /// <summary>기존 프리셋을 보존하면서 비교할 세 방식과 컨셉 팔레트를 준비합니다.</summary>
        public static void CreatePresets()
        {
            CreateStyle("Style-SoftNoise", CloudStyleProfile.ShapeMethod.SoftNoise);
            CreateStyle("Style-Sculpted", CloudStyleProfile.ShapeMethod.SculptedLobes);
            CreateStyle("Style-Layered", CloudStyleProfile.ShapeMethod.LayeredBillows);

            if (AssetDatabase.LoadAssetAtPath<CloudSkyProfile>(Presets + "Sky-Concept.asset") == null)
            {
                CloudSkyProfile sky = Object.Instantiate(RequireAsset<CloudSkyProfile>("SparseSky"));
                sky.name = "Sky-Concept";
                sky.altitude = new Vector2(5100f, 8000f);
                sky.horizontalRadius = new Vector2(1800f, 4300f);
                sky.verticalRadius = new Vector2(2300f, 5500f);
                AssetDatabase.CreateAsset(sky, Presets + "Sky-Concept.asset");
            }

            if (AssetDatabase.LoadAssetAtPath<CloudLightingProfile>(Presets + "TimeOfDay-Concept.asset") == null)
            {
                CloudLightingProfile lighting = Object.Instantiate(RequireAsset<CloudLightingProfile>("TimeOfDay-Lighting"));
                lighting.name = "TimeOfDay-Concept";
                lighting.day.sunColor = new Color(1f, 0.87f, 0.74f);
                lighting.day.exposureEV = 11f;
                lighting.day.ambientSkyColor = new Color(0.40f, 0.85f, 0.95f);
                lighting.day.ambientHorizonColor = new Color(0.33f, 0.74f, 0.86f);
                lighting.day.ambientIntensity = 0.85f;
                lighting.day.directMultiplier = 1.25f;
                lighting.day.skyBlend = 0.98f;
                lighting.day.skyTopColor = new Color(0.17f, 0.76f, 1.18f);
                lighting.day.skyHorizonColor = new Color(0.65f, 1f, 1f);

                lighting.sunset.sunColor = new Color(1f, 0.60f, 0.24f);
                lighting.sunset.sunLux = 95000f;
                lighting.sunset.exposureEV = 11.2f;
                lighting.sunset.ambientSkyColor = new Color(0.51f, 0.53f, 0.74f);
                lighting.sunset.ambientHorizonColor = new Color(0.64f, 0.46f, 0.61f);
                lighting.sunset.ambientIntensity = 0.65f;
                lighting.sunset.directMultiplier = 2.45f;
                AssetDatabase.CreateAsset(lighting, Presets + "TimeOfDay-Concept.asset");
            }

            AssetDatabase.SaveAssets();
        }

        /// <summary>조정한 컨셉 형태와 하늘 및 조명을 연결하고 해당 장면을 저장합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Apply Concept Cloud Style")]
        public static void Apply()
        {
            CreatePresets();
            Select("Style-Layered");
            SaveCloudScenes();
        }

        /// <summary>추가 스타일을 해제하고 이전 상층 분포와 시간대 조명으로 돌아간 뒤 장면을 저장합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Use Previous Volumetric Look")]
        public static void Restore()
        {
            ApplyProfiles(null, RequireAsset<CloudSkyProfile>("SparseSky"), RequireAsset<CloudLightingProfile>("TimeOfDay-Lighting"));
            SaveCloudScenes();
        }

        /// <summary>같은 카메라와 조명에서 형태 방식을 전환하고 비교 화면을 저장합니다.</summary>
        public static string Compare(string styleName)
        {
            Select(styleName);
            return LostSkiesCloudTools.Capture(styleName, 1280, 720);
        }

        /// <summary>기존 패스에 추가 스타일을 연결하고 원본 프리셋을 보존합니다.</summary>
        public static void Select(string styleName)
        {
            ApplyProfiles(RequireAsset<CloudStyleProfile>(styleName), RequireAsset<CloudSkyProfile>("Sky-Concept"),
                RequireAsset<CloudLightingProfile>("TimeOfDay-Concept"));
        }

        /// <summary>전환 방향에 관계없이 같은 연결 절차로 스타일·상층·조명을 적용하며 프로필의 내용은 수정하지 않습니다.</summary>
        private static void ApplyProfiles(CloudStyleProfile style, CloudSkyProfile sky, CloudLightingProfile lighting)
        {
            foreach (CloudTimeOfDayController time in Object.FindObjectsByType<CloudTimeOfDayController>(FindObjectsInactive.Include))
            {
                time.profile = lighting;
                time.SetTime(lighting.StartHour);
                EditorUtility.SetDirty(time);
                MarkCloudScene(time.gameObject.scene);
            }

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
                        MarkCloudScene(volume.gameObject.scene);
                    }
                }
            }
        }

        /// <summary>없는 비교 프로필만 생성하여 사용자가 조정한 값을 보존합니다.</summary>
        private static void CreateStyle(string name, CloudStyleProfile.ShapeMethod method)
        {
            string path = Presets + name + ".asset";
            if (AssetDatabase.LoadAssetAtPath<CloudStyleProfile>(path) != null)
            {
                return;
            }

            CloudStyleProfile style = ScriptableObject.CreateInstance<CloudStyleProfile>();
            style.method = method;
            if (method == CloudStyleProfile.ShapeMethod.LayeredBillows)
            {
                style.edgeSoftness = 0.02f;
                style.extinctionScale = 1.8f;
                style.ambientOcclusion = 0.65f;
                style.shadowLift = 0.015f;
                style.fineBillowStrength = 0.13f;
            }

            AssetDatabase.CreateAsset(style, path);
        }

        /// <summary>모든 연결 변경 전에 필요한 에셋을 확인하여 누락된 프로필로 장면을 덮어쓰지 않습니다.</summary>
        private static T RequireAsset<T>(string name) where T : Object
        {
            string path = Presets + name + ".asset";
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
            {
                throw new InvalidOperationException("구름 스타일 전환에 필요한 에셋이 없습니다: " + path);
            }

            return asset;
        }

        /// <summary>실제로 로드된 장면에 속한 구름 연결만 저장 대상으로 표시합니다.</summary>
        private static void MarkCloudScene(Scene scene)
        {
            if (scene.IsValid() && scene.isLoaded)
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }
        }

        /// <summary>구름 패스 또는 시간 공급자가 있는 장면만 저장하고 기존 프리셋 수정값은 보존합니다.</summary>
        private static void SaveCloudScenes()
        {
            HashSet<Scene> scenes = new HashSet<Scene>();
            foreach (CloudTimeOfDayController time in Object.FindObjectsByType<CloudTimeOfDayController>(FindObjectsInactive.Include))
            {
                scenes.Add(time.gameObject.scene);
            }

            foreach (CustomPassVolume volume in Object.FindObjectsByType<CustomPassVolume>(FindObjectsInactive.Include))
            {
                foreach (CustomPass customPass in volume.customPasses)
                {
                    if (customPass is LostSkiesCloudPass)
                    {
                        scenes.Add(volume.gameObject.scene);
                        break;
                    }
                }
            }

            foreach (Scene scene in scenes)
            {
                if (scene.IsValid() && scene.isLoaded)
                {
                    MarkCloudScene(scene);
                    if (!EditorSceneManager.SaveScene(scene))
                    {
                        throw new InvalidOperationException("구름 스타일 장면을 저장하지 못했습니다: " + scene.path);
                    }
                }
            }

            AssetDatabase.SaveAssets();
        }
    }
}
