using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies
{
    /// <summary>성능 측정이 끝난 뒤에만 FHD 정지 캡처와 시각 검토용 비행 프레임을 저장합니다.</summary>
    public sealed class CloudBenchmarkEvidence : MonoBehaviour
    {
        [Serializable]
        private sealed class Capture
        {
            public string path;
            public string label;
            public int width;
            public int height;
            public float simulationSeconds;
            public float hour;
            public Vector3 position;
            public Quaternion rotation;
        }

        [Serializable]
        private sealed class Manifest
        {
            public string buildGuid;
            public string buildManifest;
            public string settings;
            public string notes = "Captures run separately from performance timings. Flight frames are fixed simulation timestamps at 12 fps over 18 seconds; capture execution cost is not gameplay frame rate. Camera cuts separate three 6 second legs. Flight lighting requests an accelerated 24 hour cycle over 18 seconds and is clamped to the active lighting profile range; it is not a complete astronomical day cycle. Static captures use the profile day/sunset/end hours; the end frame is labelled Night only when moonlit night is enabled, otherwise Twilight. Actual applied hours are recorded per capture. Images retain original FHD output.";
            public int flightFramesPerSecond = 12;
            public List<Capture> captures = new List<Capture>();
        }

        public bool Running { get; private set; }
        public string Error { get; private set; }

        private LostSkiesCloudPass pass;
        private Camera cameraTarget;
        private CloudTimeOfDayController clock;
        private CustomPassVolume volume;
        private CloudSeaFlight input;
        private CloudOptimizationPanel panel;
        private CloudTimeOfDayInput timeInput;
        private Vector3 center;
        private string directory;
        private Manifest manifest;
        private Vector3 originalPosition;
        private Quaternion originalRotation;
        private float originalFov;
        private float originalHour;
        private bool originalAdvance;
        private bool originalInput;
        private bool originalPanel;
        private bool originalTimeInput;
        private float originalWind;
        private bool originalOverride;
        private float originalSimulation;
        private float originalTimeScale;
        private Camera originalCamera;
        private CloudBenchmarkFlight flight;

        /// <summary>캡처가 소유할 카메라·시간·입력 상태를 보관한 뒤 순서를 시작합니다.</summary>
        public void Begin(LostSkiesCloudPass target, Vector3 routeCenter, string outputDirectory)
        {
            pass = target;
            center = routeCenter;
            directory = outputDirectory;
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(Path.Combine(directory, "Flight"));
            cameraTarget = Camera.main;
            clock = GetComponent<CloudTimeOfDayController>();
            volume = GetComponent<CustomPassVolume>();
            input = cameraTarget.GetComponent<CloudSeaFlight>();
            panel = GetComponent<CloudOptimizationPanel>();
            timeInput = GetComponent<CloudTimeOfDayInput>();
            originalPosition = cameraTarget.transform.position;
            originalRotation = cameraTarget.transform.rotation;
            originalFov = cameraTarget.fieldOfView;
            originalWind = pass.windSpeed;
            originalOverride = pass.useSimulationTimeOverride;
            originalSimulation = pass.simulationTimeOverride;
            originalTimeScale = Time.timeScale;
            originalCamera = volume.targetCamera;
            if (clock != null)
            {
                originalHour = clock.TimeOfDay;
                originalAdvance = clock.autoAdvance;
                clock.autoAdvance = false;
            }

            if (input != null)
            {
                originalInput = input.enabled;
                input.enabled = false;
            }

            if (panel != null)
            {
                originalPanel = panel.enabled;
                panel.enabled = false;
            }

            if (timeInput != null)
            {
                originalTimeInput = timeInput.enabled;
                timeInput.enabled = false;
            }

            Time.timeScale = 0f;
            volume.targetCamera = cameraTarget;
            cameraTarget.fieldOfView = 68f;
            pass.windSpeed = 0f;
            pass.useSimulationTimeOverride = true;
            pass.simulationTimeOverride = 0f;
            manifest = new Manifest();
            manifest.buildGuid = Application.buildGUID;
            TextAsset build = Resources.Load<TextAsset>("CloudBenchmarkBuild");
            if (build != null)
            {
                manifest.buildManifest = build.text;
            }

            manifest.settings = JsonUtility.ToJson(pass);
            Running = true;
            StartCoroutine(CaptureAll());
        }

        /// <summary>동일한 낮·석양·밤·관통·원경 자세를 충분히 갱신한 뒤 원시 PNG로 보관합니다.</summary>
        private IEnumerator CaptureAll()
        {
            bool completed = false;
            try
            {
                if (clock == null || clock.profile == null)
                {
                    throw new InvalidOperationException("A lighting profile is required for labelled day, sunset and night captures.");
                }

                CloudLightingProfile lightingProfile = clock.profile;
                float dayHour = lightingProfile.StartHour;
                float sunsetHour = Mathf.Clamp(lightingProfile.sunsetHour, lightingProfile.StartHour + 0.01f, lightingProfile.TwilightHour - 0.01f);
                float endHour = lightingProfile.EndHour;
                string endLabel = "Twilight";
                if (lightingProfile.enableMoonlitNight)
                {
                    endLabel = "Night";
                }

                string[] names = { "Day", "Sunset", endLabel, "Inside", "Far" };
                float[] hours = { dayHour, sunsetHour, endHour, dayHour, dayHour };
                for (int index = 0; index < names.Length; index++)
                {
                    Vector3 position = new Vector3(1222f, 3300f, -6800f);
                    Quaternion rotation = Quaternion.Euler(10f, 15f, 0f);
                    if (index == 3)
                    {
                        position = center;
                        rotation = Quaternion.identity;
                    }
                    else if (index == 4)
                    {
                        position = new Vector3(0f, 9000f, -14000f);
                        rotation = Quaternion.Euler(22f, 20f, 0f);
                    }

                    ApplyPose(position, rotation);
                    if (clock != null)
                    {
                        clock.SetTime(hours[index]);
                    }

                    for (int warm = 0; warm < 30; warm++)
                    {
                        yield return new WaitForEndOfFrame();
                    }

                    SaveCapture(names[index] + ".png", names[index], 0f);
                }

                flight = new CloudBenchmarkFlight(pass, clock, center, 6f);
                for (int index = 0; index < 216; index++)
                {
                    float seconds = index / 12f;
                    flight.Apply(seconds, out Vector3 position, out Quaternion rotation);
                    ApplyPose(position, rotation);
                    yield return new WaitForEndOfFrame();
                    SaveCapture("Flight/frame-" + index.ToString("D4") + ".png", "flight", seconds);
                }

                completed = true;
            }
            finally
            {
                if (!completed)
                {
                    Error = "Evidence capture interrupted before all 221 frames were saved.";
                }

                File.WriteAllText(Path.Combine(directory, "capture-manifest.json"), JsonUtility.ToJson(manifest, true));
                Restore();
            }
        }

        /// <summary>밀도장 기준 좌표를 현재 원점에 맞춰 카메라에 적용합니다.</summary>
        private void ApplyPose(Vector3 position, Quaternion rotation)
        {
            Vector3 origin = Vector3.zero;
            if (pass.worldOrigin != null)
            {
                origin = pass.worldOrigin.SamplingOffset;
            }

            cameraTarget.transform.SetPositionAndRotation(position - origin, rotation);
        }

        /// <summary>동기 PNG 저장을 측정 바깥에서 수행하여 Player 종료 전에 파일 완성을 보장합니다.</summary>
        private void SaveCapture(string relativePath, string label, float seconds)
        {
            Texture2D texture = ScreenCapture.CaptureScreenshotAsTexture();
            try
            {
                // HDRP 최종 화면의 알파가 0이어도 표시된 RGB를 불투명한 증거 이미지로 보존합니다.
                Color32[] pixels = texture.GetPixels32();
                for (int index = 0; index < pixels.Length; index++)
                {
                    pixels[index].a = 255;
                }

                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                File.WriteAllBytes(Path.Combine(directory, relativePath), texture.EncodeToPNG());
                Capture capture = new Capture();
                capture.path = relativePath;
                capture.label = label;
                capture.width = texture.width;
                capture.height = texture.height;
                capture.simulationSeconds = seconds;
                capture.position = cameraTarget.transform.position;
                capture.rotation = cameraTarget.transform.rotation;
                if (clock != null)
                {
                    capture.hour = clock.TimeOfDay;
                }

                manifest.captures.Add(capture);
            }
            finally
            {
                Destroy(texture);
            }
        }

        /// <summary>정상 종료와 Play 종료 모두 임시 상태를 돌려줍니다.</summary>
        private void Restore()
        {
            if (!Running)
            {
                return;
            }

            Running = false;
            if (flight != null)
            {
                flight.Dispose();
                flight = null;
            }

            pass.windSpeed = originalWind;
            pass.useSimulationTimeOverride = originalOverride;
            pass.simulationTimeOverride = originalSimulation;
            Time.timeScale = originalTimeScale;
            if (cameraTarget != null)
            {
                cameraTarget.transform.SetPositionAndRotation(originalPosition, originalRotation);
                cameraTarget.fieldOfView = originalFov;
            }

            if (clock != null)
            {
                clock.autoAdvance = originalAdvance;
                clock.SetTime(originalHour);
            }

            if (volume != null)
            {
                volume.targetCamera = originalCamera;
            }

            if (input != null)
            {
                input.enabled = originalInput;
            }

            if (panel != null)
            {
                panel.enabled = originalPanel;
            }

            if (timeInput != null)
            {
                timeInput.enabled = originalTimeInput;
            }
        }

        /// <summary>컴포넌트 종료 중에도 카메라와 환경 소유권을 반환합니다.</summary>
        private void OnDisable()
        {
            Restore();
        }
    }
}
