using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ClouDream.LostSkies
{
    /// <summary>같은 시점에서 A/B를 전환하고 시간을 고정한 두 캡처를 나란히 비교합니다.</summary>
    [RequireComponent(typeof(CloudHybridSwitch))]
    public sealed class CloudComparisonPanel : MonoBehaviour
    {
        public bool showPanel = true;

        public bool showHud = true;

        private CloudHybridSwitch routing;

        private CloudOptimizationPanel settings;

        private Texture2D captureA, captureB;

        private bool capturing;

        private bool paired;

        private string status = "F6 controls / Tab A-B / F7 capture pair";

        private Coroutine captureRoutine;

        private float savedScale;

        private bool savedAuto;

        private bool savedOverride;

        private float savedSimulation;

        private CloudComparisonVariant savedVariant;

        private CloudComparisonView savedView;

        private CloudTimeOfDayController clock;

        private bool savedFlightHelp;

        private bool savedTimeHelp;

        private bool savedSettingsOverlay;

        private CloudSeaFlight flight;

        private CloudTimeOfDayInput timeInput;

        /// <summary>같은 볼륨의 경로 선택과 기존 입력 소유권을 재사용합니다.</summary>
        private void Awake()
        {
            routing = GetComponent<CloudHybridSwitch>();
            routing.Connect();
            settings = GetComponent<CloudOptimizationPanel>();
            clock = GetComponent<CloudTimeOfDayController>();
        }

        /// <summary>자유 비행 중에도 한 키로 전환하며 패널이 열렸을 때만 입력을 멈춥니다.</summary>
        private void Update()
        {
            Keyboard keys = Keyboard.current;
            if (keys != null && !capturing)
            {
                if (keys.f6Key.wasPressedThisFrame)
                {
                    showPanel = !showPanel;
                }
                if (keys.tabKey.wasPressedThisFrame && !paired)
                {
                    CloudComparisonVariant next = CloudComparisonVariant.SurfaceB;
                    if (routing.variant == CloudComparisonVariant.SurfaceB)
                    {
                        next = CloudComparisonVariant.HybridA;
                    }
                    routing.SetComparison(next, CloudComparisonView.Full);
                }
                if (keys.f7Key.wasPressedThisFrame && !paired)
                {
                    CapturePair();
                }
            }
            if (settings != null)
            {
                settings.SetExternalModal(showPanel || paired || capturing);
            }
        }

        /// <summary>실제 적용 상태와 GPU 자원을 표시하고 프레임 시간과 GPU 시간을 구분합니다.</summary>
        private void OnGUI()
        {
            if (!showHud || capturing || routing == null)
            {
                return;
            }
            if (paired)
            {
                int depth = GUI.depth;
                GUI.depth = -100;
                Color color = GUI.color;
                GUI.color = Color.black;
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = color;
                GUI.Box(new Rect(0, 0, Screen.width, Screen.height), "Same pose / same time / same weather");
                float half = Screen.width * 0.5f;
                GUI.DrawTexture(new Rect(0, 40, half, Screen.height - 100), captureA, ScaleMode.ScaleToFit, false);
                GUI.DrawTexture(new Rect(half, 40, half, Screen.height - 100), captureB, ScaleMode.ScaleToFit, false);
                GUI.Label(new Rect(20, 20, half - 20, 25), "A - Hybrid + predictive residency");
                GUI.Label(new Rect(half + 20, 20, half - 20, 25), "B - Stylized lobes + local volume");
                if (GUI.Button(new Rect(20, Screen.height - 45, 160, 30), "Close comparison"))
                {
                    paired = false;
                }
                GUI.depth = depth;
                return;
            }
            if (!showPanel)
            {
                GUI.Label(new Rect(16, 15, 850, 30), routing.variant + " / " + routing.view + " / " + status);
                return;
            }
            GUILayout.BeginArea(new Rect(16, 16, 480, 310), GUI.skin.box);
            GUILayout.Label("ClouDream / A-B cloud comparison");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("A / Hybrid"))
            {
                routing.SetComparison(CloudComparisonVariant.HybridA, CloudComparisonView.Full);
            }
            if (GUILayout.Button("B / Lobe surface"))
            {
                routing.SetComparison(CloudComparisonVariant.SurfaceB, CloudComparisonView.Full);
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            StageButton("Cloud OFF", CloudComparisonView.Off);
            StageButton("Surface", CloudComparisonView.SurfaceOnly);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            StageButton("Surface + local volume", CloudComparisonView.SurfaceAndVolume);
            StageButton("Full / streaming", CloudComparisonView.Full);
            StageButton("Reference", CloudComparisonView.Reference);
            GUILayout.EndHorizontal();
            GUILayout.Label("Selected: " + routing.variant + " / " + routing.view);
            GUILayout.Label("Actual: " + routing.Hybrid.Status);
            GUILayout.Label("Owned GPU: " + (routing.Hybrid.Bytes / 1048576f).ToString("0.0") + " MiB");
            GUILayout.Label("A cells using proxy: " + routing.Hybrid.Missing + " / jobs: " + routing.Hybrid.Pending);
            if (GUILayout.Button("Capture same-pose A / B pair (F7)"))
            {
                CapturePair();
            }
            GUILayout.Label(status);
            if (GUILayout.Button("Fly / close controls (F6)"))
            {
                showPanel = false;
            }
            GUILayout.EndArea();
        }

        /// <summary>카메라와 환경을 보존하며 표시 단계만 바꿉니다.</summary>
        private void StageButton(string label, CloudComparisonView view)
        {
            if (GUILayout.Button(label))
            {
                routing.SetComparison(routing.variant, view);
            }
        }

        /// <summary>입력·날씨·시간을 고정하는 비교 캡처를 시작합니다.</summary>
        public void CapturePair()
        {
            if (!capturing)
            {
                captureRoutine = StartCoroutine(Capture());
            }
        }

        /// <summary>A의 준비를 기다린 뒤 같은 포즈의 B를 저장하고 좌우 비교를 표시합니다.</summary>
        private IEnumerator Capture()
        {
            capturing = true;
            savedScale = Time.timeScale;
            savedAuto = clock.autoAdvance;
            savedOverride = routing.Reference.useSimulationTimeOverride;
            savedSimulation = routing.Reference.simulationTimeOverride;
            savedVariant = routing.variant;
            savedView = routing.view;
            if (settings != null)
            {
                savedSettingsOverlay = settings.suppressOverlay;
                settings.suppressOverlay = true;
            }
            flight = Camera.main.GetComponent<CloudSeaFlight>();
            timeInput = GetComponent<CloudTimeOfDayInput>();
            if (flight != null)
            {
                savedFlightHelp = flight.showHelp;
                flight.showHelp = false;
            }
            if (timeInput != null)
            {
                savedTimeHelp = timeInput.showHelp;
                timeInput.showHelp = false;
            }
            routing.Reference.simulationTimeOverride = routing.Reference.GetSimulationTime();
            routing.Reference.useSimulationTimeOverride = true;
            clock.autoAdvance = false;
            Time.timeScale = 0f;
            routing.SetComparison(CloudComparisonVariant.HybridA, CloudComparisonView.Full);
            double started = Time.realtimeSinceStartupAsDouble;
            do
            {
                yield return null;
            }
            while ((routing.Hybrid.ResidencyMissing > 0 || routing.Hybrid.Pending > 0 || !routing.Hybrid.actual)
                && Time.realtimeSinceStartupAsDouble - started < 60);
            yield return new WaitForEndOfFrame();
            ReleaseCaptures();
            captureA = MakeOpaque(ScreenCapture.CaptureScreenshotAsTexture());
            routing.SetComparison(CloudComparisonVariant.SurfaceB, CloudComparisonView.Full);
            for (int frame = 0; frame < 8; frame++)
            {
                yield return null;
            }
            yield return new WaitForEndOfFrame();
            captureB = MakeOpaque(ScreenCapture.CaptureScreenshotAsTexture());
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../Screenshots/CloudAB"));
            Directory.CreateDirectory(folder);
            string stamp = System.DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.WriteAllBytes(Path.Combine(folder, stamp + "-A.png"), captureA.EncodeToPNG());
            File.WriteAllBytes(Path.Combine(folder, stamp + "-B.png"), captureB.EncodeToPNG());
            status = "Saved pair: Screenshots/CloudAB/" + stamp;
            RestoreCaptureState();
            paired = true;
            capturing = false;
            captureRoutine = null;
        }

        /// <summary>중단돼도 원래 시간·바람 위상·선택을 복구합니다.</summary>
        private void RestoreCaptureState()
        {
            Time.timeScale = savedScale;
            clock.autoAdvance = savedAuto;
            routing.Reference.useSimulationTimeOverride = savedOverride;
            routing.Reference.simulationTimeOverride = savedSimulation;
            routing.SetComparison(savedVariant, savedView);
            if (settings != null)
            {
                settings.suppressOverlay = savedSettingsOverlay;
            }
            if (flight != null)
            {
                flight.showHelp = savedFlightHelp;
            }
            if (timeInput != null)
            {
                timeInput.showHelp = savedTimeHelp;
            }
        }

        /// <summary>HDRP 색 버퍼의 합성 알파를 파일 투명도로 해석하지 않도록 캡처를 불투명하게 만듭니다.</summary>
        private static Texture2D MakeOpaque(Texture2D texture)
        {
            Color32[] pixels = texture.GetPixels32();
            for (int index = 0; index < pixels.Length; index++)
            {
                pixels[index].a = 255;
            }
            // 캡처의 디스플레이 RGB를 sRGB 텍스처로 명시해 IMGUI에서 감마가 중복 적용되지 않게 합니다.
            Texture2D result = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, false);
            result.SetPixels32(pixels);
            result.Apply();
            Destroy(texture);
            return result;
        }

        /// <summary>이전 비교 이미지를 해제해 반복 촬영의 메모리 증가를 막습니다.</summary>
        private void ReleaseCaptures()
        {
            if (captureA != null)
            {
                Destroy(captureA);
            }
            if (captureB != null)
            {
                Destroy(captureB);
            }
        }

        /// <summary>Play 종료와 컴포넌트 비활성화에서 입력과 촬영 자원을 복구합니다.</summary>
        private void OnDisable()
        {
            if (captureRoutine != null)
            {
                StopCoroutine(captureRoutine);
                RestoreCaptureState();
                capturing = false;
                captureRoutine = null;
            }
            ReleaseCaptures();
            if (settings != null)
            {
                settings.SetExternalModal(false);
            }
        }
    }
}
