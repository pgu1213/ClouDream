using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies.Editor
{
    public static class CloudSkySetup
    {
        private const string PresetFolder = "Assets/LostSkiesClouds/Presets/";

        /// <summary>현재 구름 패스에 운해/상층 프로필과 날씨/원점 서비스를 연결합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Configure Sky Clouds")]
        public static void Configure()
        {
            if (Application.isPlaying)
            {
                throw new InvalidOperationException("Configure sky clouds outside Play mode.");
            }

            CloudOceanProfile ocean = GetOrCreate<CloudOceanProfile>("ContinuousOcean");
            CloudSkyProfile sky = GetOrCreate<CloudSkyProfile>("SparseSky");
            CloudWeatherProfile clear = GetOrCreate<CloudWeatherProfile>("Weather-Clear");
            CloudWeatherProfile sunset = AssetDatabase.LoadAssetAtPath<CloudWeatherProfile>(PresetFolder + "Weather-Sunset.asset");
            if (sunset == null)
            {
                sunset = GetOrCreate<CloudWeatherProfile>("Weather-Sunset");
                CloudEnvironment colors = CloudEnvironment.ClearDay();
                colors.shadowTint = new Color(0.29f, 0.28f, 0.48f);
                colors.highlightTint = new Color(1.15f, 0.72f, 0.40f);
                colors.sunlight = new Color(1f, 0.66f, 0.40f);
                sunset.environment = colors;
                EditorUtility.SetDirty(sunset);
            }

            CustomPassVolume[] volumes = UnityEngine.Object.FindObjectsByType<CustomPassVolume>(FindObjectsSortMode.None);
            int configured = 0;
            foreach (CustomPassVolume volume in volumes)
            {
                foreach (CustomPass customPass in volume.customPasses)
                {
                    LostSkiesCloudPass pass = customPass as LostSkiesCloudPass;
                    if (pass == null)
                    {
                        continue;
                    }

                    Undo.RecordObject(volume, "Configure upper clouds");
                    pass.oceanProfile = ocean;
                    pass.skyProfile = sky;
                    CloudWorldOrigin origin = volume.GetComponent<CloudWorldOrigin>();
                    if (origin == null)
                    {
                        origin = Undo.AddComponent<CloudWorldOrigin>(volume.gameObject);
                    }

                    CloudWeatherController weather = volume.GetComponent<CloudWeatherController>();
                    if (weather == null)
                    {
                        weather = Undo.AddComponent<CloudWeatherController>(volume.gameObject);
                        weather.initialProfile = clear;
                        weather.selectedProfile = sunset;
                    }

                    pass.worldOrigin = origin;
                    if (pass.environmentSource == null)
                    {
                        pass.environmentSource = weather;
                    }

                    EditorUtility.SetDirty(volume);
                    EditorUtility.SetDirty(weather);
                    EditorSceneManager.MarkSceneDirty(volume.gameObject.scene);
                    configured++;
                }
            }

            if (configured == 0)
            {
                throw new InvalidOperationException("Open the LostSkiesCloudSea scene before configuring clouds.");
            }

            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveOpenScenes();
        }

        /// <summary>기존 프로필을 재사용하고 없는 경우에만 기본값으로 생성합니다.</summary>
        private static T GetOrCreate<T>(string assetName)
            where T : ScriptableObject
        {
            string path = PresetFolder + assetName + ".asset";
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<T>();
                AssetDatabase.CreateAsset(asset, path);
            }

            return asset;
        }
    }
}
