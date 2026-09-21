using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies.Editor
{
    public static class CloudLightingSetup
    {
        private const string ProfilePath = "Assets/LostSkiesClouds/Presets/TimeOfDay-Lighting.asset";

        /// <summary>현재 구름 장면에 공유 시간대 조명을 연결하고 기존 날씨 공급자를 재사용합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Configure Time Of Day")]
        public static void Configure()
        {
            if (Application.isPlaying)
            {
                throw new InvalidOperationException("Configure time of day outside Play mode.");
            }

            CloudLightingProfile profile = AssetDatabase.LoadAssetAtPath<CloudLightingProfile>(ProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<CloudLightingProfile>();
                AssetDatabase.CreateAsset(profile, ProfilePath);
            }

            Volume skyVolume = FindSkyVolume();
            if (skyVolume == null || RenderSettings.sun == null)
            {
                throw new InvalidOperationException("A PhysicallyBasedSky volume and sun are required.");
            }

            int configured = 0;
            foreach (CustomPassVolume volume in UnityEngine.Object.FindObjectsByType<CustomPassVolume>(FindObjectsSortMode.None))
            {
                foreach (CustomPass customPass in volume.customPasses)
                {
                    LostSkiesCloudPass pass = customPass as LostSkiesCloudPass;
                    if (pass == null)
                    {
                        continue;
                    }

                    Undo.RecordObject(volume, "Connect shared sky lighting");
                    CloudTimeOfDayController time = volume.GetComponent<CloudTimeOfDayController>();
                    if (time == null)
                    {
                        time = Undo.AddComponent<CloudTimeOfDayController>(volume.gameObject);
                        time.weatherSource = pass.environmentSource;
                    }

                    time.profile = profile;
                    time.skyVolume = skyVolume;
                    time.sun = RenderSettings.sun;
                    if (volume.GetComponent<CloudTimeOfDayInput>() == null)
                    {
                        Undo.AddComponent<CloudTimeOfDayInput>(volume.gameObject);
                    }

                    pass.environmentSource = time;
                    time.SetTime(profile.StartHour);

                    EditorUtility.SetDirty(time);
                    EditorUtility.SetDirty(volume);
                    EditorSceneManager.MarkSceneDirty(volume.gameObject.scene);
                    configured++;
                }
            }

            if (configured == 0)
            {
                throw new InvalidOperationException("Open the LostSkiesCloudSea scene first.");
            }

            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveOpenScenes();
        }

        /// <summary>현재 장면의 물리 기반 하늘 볼륨을 찾습니다.</summary>
        private static Volume FindSkyVolume()
        {
            foreach (Volume volume in UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
            {
                PhysicallyBasedSky sky;
                if (volume.sharedProfile != null && volume.sharedProfile.TryGet(out sky))
                {
                    return volume;
                }
            }

            return null;
        }

        /// <summary>세 시간대의 동일 시점을 저장하고 검사 전 시간과 자동 재생 상태를 복원합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Capture Lighting References")]
        public static string CaptureReferences()
        {
            CloudTimeOfDayController time = UnityEngine.Object.FindFirstObjectByType<CloudTimeOfDayController>();
            if (time == null)
            {
                throw new InvalidOperationException("Configure time of day before capture.");
            }

            float savedTime = time.TimeOfDay;
            bool savedPlayback = time.autoAdvance;
            time.autoAdvance = false;
            try
            {
                time.SetTime(time.profile.StartHour);
                string day = LostSkiesCloudTools.Capture("Lighting-Day", 1600, 900);
                time.SetTime(time.profile.sunsetHour);
                string sunset = LostSkiesCloudTools.Capture("Lighting-Sunset", 1600, 900);
                time.SetTime(time.profile.EndHour);
                string twilight = LostSkiesCloudTools.Capture("Lighting-Twilight", 1600, 900);
                return day + "\n" + sunset + "\n" + twilight;
            }
            finally
            {
                time.SetTime(savedTime);
                time.autoAdvance = savedPlayback;
            }
        }
    }
}
