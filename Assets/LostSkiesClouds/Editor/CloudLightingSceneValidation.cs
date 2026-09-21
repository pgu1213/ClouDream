using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies.Editor
{
    public static class CloudLightingSceneValidation
    {
        [Serializable]
        public sealed class Report
        {
            public bool passed;
            public float exposureAtBaseEV;
            public float exposureAtPlusOneEV;
            public float exposureRatio;

            public float foregroundPaletteDifference;
            public bool skyAndSunSynchronized;
            public bool sharedSkyAssetUnchanged;
            public string error;
        }

        /// <summary>실제 HDRP 카메라의 노출 배율과 하늘 팔레트의 전경 가림을 검사합니다.</summary>
        public static Report Run()
        {
            Report report = new Report();
            CloudTimeOfDayController time = UnityEngine.Object.FindFirstObjectByType<CloudTimeOfDayController>();
            Camera camera = Camera.main;
            if (time == null || camera == null)
            {
                throw new InvalidOperationException("A configured lighting scene is required.");
            }

            CloudLightingProfile previousProfile = time.profile;
            float previousTime = time.TimeOfDay;
            bool previousPlayback = time.autoAdvance;
            CloudLightingProfile testProfile = UnityEngine.Object.Instantiate(previousProfile);
            string skyAssetPath = AssetDatabase.GetAssetPath(time.skyVolume.sharedProfile);
            byte[] skyBefore = File.ReadAllBytes(skyAssetPath);

            GameObject cube = null;
            Material material = null;
            HDAdditionalCameraData cameraData = camera.GetComponent<HDAdditionalCameraData>();
            bool previousCustomSettings = cameraData.customRenderingSettings;
            FrameSettings previousFrameSettings = cameraData.renderingPathCustomFrameSettings;
            FrameSettingsOverrideMask previousMask = cameraData.renderingPathCustomFrameSettingsOverrideMask;
            try
            {
                time.autoAdvance = false;
                time.profile = testProfile;
                time.SetTime(testProfile.StartHour);
                RenderPixels(camera);
                report.exposureAtBaseEV = ReadExposure();

                testProfile.day.exposureEV += 1f;
                time.ApplyCurrent();
                RenderPixels(camera);
                report.exposureAtPlusOneEV = ReadExposure();
                report.exposureRatio = report.exposureAtPlusOneEV / report.exposureAtBaseEV;
                Require(Mathf.Abs(report.exposureRatio - 0.5f) < 0.005f, "HDRP EV+1 did not halve the shared exposure multiplier.");

                testProfile.day.exposureEV -= 1f;
                // 배경의 블룸/화면 공간 반사가 물체에 미치는 영향을 제외하고 깊이 마스크만 검사합니다.
                cameraData.customRenderingSettings = true;
                cameraData.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.Postprocess, false);
                cameraData.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.Postprocess] = true;
                cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "Temporary lighting occlusion check";
                cube.hideFlags = HideFlags.HideAndDontSave;
                cube.transform.position = camera.transform.position + camera.transform.forward * 1800f;
                cube.transform.localScale = Vector3.one * 600f;
                material = new Material(Shader.Find("HDRP/Unlit"));
                material.SetColor("_UnlitColor", new Color(0.2f, 0.8f, 0.3f));
                cube.GetComponent<Renderer>().sharedMaterial = material;

                testProfile.day.skyBlend = 0f;
                time.ApplyCurrent();
                Color[] physicalSky = RenderPixels(camera);
                testProfile.day.skyBlend = 1f;
                time.ApplyCurrent();
                Color[] paletteSky = RenderPixels(camera);
                for (int y = 62; y < 82; y++)
                {
                    for (int x = 118; x < 138; x++)
                    {
                        Color difference = physicalSky[y * 256 + x] - paletteSky[y * 256 + x];
                        report.foregroundPaletteDifference = Mathf.Max(report.foregroundPaletteDifference,
                            Mathf.Abs(difference.r), Mathf.Abs(difference.g), Mathf.Abs(difference.b));
                    }
                }

                Require(report.foregroundPaletteDifference < 0.015f, "Sky palette affected foreground object pixels.");
                report.skyAndSunSynchronized = true;
                float[] hours = { testProfile.StartHour, testProfile.sunsetHour, testProfile.EndHour };
                foreach (float hour in hours)
                {
                    time.SetTime(hour);
                    CloudLightingState state = time.CurrentLighting;
                    bool directionMatches = Vector3.Dot(-time.sun.transform.forward, state.GetSunDirection()) > 0.99999f;
                    bool intensityMatches = Mathf.Abs(time.sun.intensity - state.sunLux) < 0.01f;
                    report.skyAndSunSynchronized &= directionMatches && intensityMatches;
                }

                Require(report.skyAndSunSynchronized, "Scene sun differs from the shared cloud lighting state.");
                report.sharedSkyAssetUnchanged = SameBytes(skyBefore, File.ReadAllBytes(skyAssetPath));
                Require(report.sharedSkyAssetUnchanged, "Preview modified the shared sky asset.");
                report.passed = true;
            }
            catch (Exception exception)
            {
                report.error = exception.ToString();
                Debug.LogException(exception);
            }
            finally
            {
                cameraData.customRenderingSettings = previousCustomSettings;
                cameraData.renderingPathCustomFrameSettings = previousFrameSettings;
                cameraData.renderingPathCustomFrameSettingsOverrideMask = previousMask;

                if (cube != null)
                {
                    UnityEngine.Object.DestroyImmediate(cube);
                }

                if (material != null)
                {
                    UnityEngine.Object.DestroyImmediate(material);
                }

                time.profile = previousProfile;
                time.SetTime(previousTime);
                time.autoAdvance = previousPlayback;
                UnityEngine.Object.DestroyImmediate(testProfile);
                Directory.CreateDirectory("Screenshots");
                File.WriteAllText("Screenshots/CloudLighting-SceneValidation.json", JsonUtility.ToJson(report, true));
            }

            return report;
        }

        /// <summary>고정 크기의 실제 카메라 출력을 렌더하고 임시 상태를 복원합니다.</summary>
        private static Color[] RenderPixels(Camera camera)
        {
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture target = new RenderTexture(256, 144, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Texture2D pixels = new Texture2D(256, 144, TextureFormat.RGB24, false);
            target.Create();
            try
            {
                camera.targetTexture = target;
                for (int frame = 0; frame < 4; frame++)
                {
                    camera.Render();
                }

                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0f, 0f, 256f, 144f), 0, 0);
                pixels.Apply();
                return pixels.GetPixels();
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(pixels);
            }
        }

        /// <summary>직전 카메라에 실제 바인딩된 HDRP 노출 텍스처의 배율을 읽습니다.</summary>
        private static float ReadExposure()
        {
            Texture exposure = Shader.GetGlobalTexture("_ExposureTexture");
            Require(exposure != null, "HDRP exposure texture was not bound.");
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(exposure, 0, TextureFormat.RGBAFloat);
            request.WaitForCompletion();
            Require(!request.hasError, "HDRP exposure readback failed.");
            return request.GetData<Color>()[0].r;
        }

        /// <summary>공유 에셋 파일이 미리보기 전후 동일한지 비교합니다.</summary>
        private static bool SameBytes(byte[] first, byte[] second)
        {
            if (first.Length != second.Length)
            {
                return false;
            }

            for (int i = 0; i < first.Length; i++)
            {
                if (first[i] != second[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>검증 실패를 원인 메시지와 함께 기록하도록 예외를 발생시킵니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
