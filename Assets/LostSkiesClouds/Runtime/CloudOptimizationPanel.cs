using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies
{
    /// <summary>Play 중 렌더 품질과 최적화를 각각 비교합니다. 변경은 현재 실행의 패스에만 적용합니다.</summary>
    [RequireComponent(typeof(CustomPassVolume))]
    public sealed class CloudOptimizationPanel : MonoBehaviour
    {
        public bool showPanel;

        private LostSkiesCloudPass pass;
        private CloudSeaFlight flight;
        private CloudTimeOfDayInput timeInput;
        private bool flightWasEnabled;
        private bool timeInputWasEnabled;
        private bool inputSuspended;
        private bool externalModal;
        private float originalScale;
        private bool originalDepthUpsampling;
        private bool originalCache;
        private bool originalRejection;
        private bool originalCompact;

        private bool originalIntervals;

        private bool originalShadowTermination;

        private bool originalPaletteFastPath;

        private bool originalTemporal;
        private int originalTemporalPhases;
        private CloudDistanceQualitySettings originalDistanceQuality;
        private Vector2 scrollPosition;

        private bool originalSpatial;

        private float originalSpatialCellSize;

        // 최근 120프레임의 전체 실행 간격입니다. GPU 시간으로 표시하지 않습니다.
        private readonly float[] frameTimes = new float[120];
        private int frameIndex;
        private int frameCount;
        private float frameSum;
        private float displayMilliseconds;
        private float nextDisplayTime;

        /// <summary>같은 볼륨의 패스와 입력을 찾아 실행 시작 설정을 저장합니다.</summary>
        private void Awake()
        {
            CustomPassVolume volume = GetComponent<CustomPassVolume>();
            foreach (CustomPass candidate in volume.customPasses)
            {
                pass = candidate as LostSkiesCloudPass;
                if (pass != null)
                {
                    break;
                }
            }

            if (pass == null)
            {
                enabled = false;
                return;
            }

            originalScale = pass.resolutionScale;
            originalDepthUpsampling = pass.useDepthAwareUpsampling;
            originalCache = pass.useShapeCellCache;
            originalRejection = pass.useSupportRejection;
            originalCompact = pass.useCompactTargets;
            originalIntervals = pass.useRayIntervals;
            originalShadowTermination = pass.useShadowTermination;
            originalPaletteFastPath = pass.usePaletteLightingFastPath;
            originalTemporal = pass.useTemporalReprojection;
            originalTemporalPhases = pass.temporalUpdatePhases;
            originalDistanceQuality = pass.distanceQuality;
            originalSpatial = pass.useSpatialLightCache;
            originalSpatialCellSize = pass.spatialLightCellSize;
            timeInput = GetComponent<CloudTimeOfDayInput>();
            if (Camera.main != null)
            {
                flight = Camera.main.GetComponent<CloudSeaFlight>();
            }
        }

        /// <summary>F8/Escape 입력과 통계 갱신을 처리하고 패널이 열린 동안 비행 입력을 잠시 멈춥니다.</summary>
        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.f8Key.wasPressedThisFrame)
                {
                    showPanel = !showPanel;
                }

                if (keyboard.escapeKey.wasPressedThisFrame)
                {
                    showPanel = false;
                }
            }

            UpdateInputOwnership();
            float milliseconds = Time.unscaledDeltaTime * 1000f;
            frameSum -= frameTimes[frameIndex];
            frameTimes[frameIndex] = milliseconds;
            frameSum += milliseconds;
            frameIndex = (frameIndex + 1) % frameTimes.Length;
            frameCount = Mathf.Min(frameCount + 1, frameTimes.Length);
            if (Time.unscaledTime >= nextDisplayTime)
            {
                displayMilliseconds = frameSum / Mathf.Max(1, frameCount);
                nextDisplayTime = Time.unscaledTime + 0.25f;
            }
        }

        /// <summary>열기 전 컴포넌트 상태를 보존하여 원래 꺼진 입력을 임의로 활성화하지 않습니다.</summary>
        private void UpdateInputOwnership()
        {
            bool ownsInput = showPanel || externalModal;
            if (ownsInput && !inputSuspended)
            {
                if (flight != null)
                {
                    flightWasEnabled = flight.enabled;
                    flight.enabled = false;
                }

                if (timeInput != null)
                {
                    timeInputWasEnabled = timeInput.enabled;
                    timeInput.enabled = false;
                }

                inputSuspended = true;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (!ownsInput && inputSuspended)
            {
                RestoreInput();
            }
        }

        /// <summary>벤치마크 창도 같은 입력 보존 경로를 사용해 스크롤 중 비행 속도나 시간이 바뀌지 않게 합니다.</summary>
        public void SetExternalModal(bool visible)
        {
            externalModal = visible;
            if (isActiveAndEnabled)
            {
                UpdateInputOwnership();
            }
        }

        /// <summary>패널을 닫거나 컴포넌트가 해제되면 원래의 비행·시간 입력을 복원합니다.</summary>
        private void RestoreInput()
        {
            if (!inputSuspended)
            {
                return;
            }

            if (flight != null)
            {
                flight.enabled = flightWasEnabled;
            }

            if (timeInput != null)
            {
                timeInput.enabled = timeInputWasEnabled;
            }

            inputSuspended = false;
        }

        /// <summary>실행 중 해제되어도 입력 잠금이 남지 않게 합니다.</summary>
        private void OnDisable()
        {
            RestoreInput();
        }

        /// <summary>설정 변경 뒤 이전 설정의 프레임을 새 평균에 섞지 않습니다.</summary>
        public void ResetStatistics()
        {
            System.Array.Clear(frameTimes, 0, frameTimes.Length);
            frameIndex = 0;
            frameCount = 0;
            frameSum = 0f;
            displayMilliseconds = 0f;
        }

        /// <summary>패스 값을 직접 조작하는 작은 설정창을 표시합니다. 에셋과 PlayerPrefs에는 저장하지 않습니다.</summary>
        private void OnGUI()
        {
            if (pass == null)
            {
                return;
            }

            float scale = Mathf.Min(1f, Mathf.Min(Screen.width / 660f, Screen.height / 720f));
            Matrix4x4 previous = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(Vector3.one * scale);
            if (!showPanel)
            {
                if (GUI.Button(new Rect(Screen.width / scale - 212f, Screen.height / scale - 42f, 192f, 28f), "Cloud settings  [F8]"))
                {
                    showPanel = true;
                }

                GUI.matrix = previous;
                return;
            }

            Rect area = new Rect((Screen.width / scale - 620f) * 0.5f, (Screen.height / scale - 680f) * 0.5f, 620f, 680f);
            GUI.Box(area, "CLOUDREAM / CLOUD PERFORMANCE");
            GUILayout.BeginArea(new Rect(area.x + 20f, area.y + 30f, 580f, 635f));
            scrollPosition = GUILayout.BeginScrollView(scrollPosition);
            GUILayout.Label("Target: GTX 1660 / 1920 x 1080 / 60 FPS (16.67 ms)");
            GUILayout.Label("Current device: " + SystemInfo.graphicsDeviceName);
            if (displayMilliseconds > 0f)
            {
                GUILayout.Label($"Whole frame: {displayMilliseconds:0.00} ms / {1000f / displayMilliseconds:0.0} FPS");
            }
            else
            {
                GUILayout.Label("Whole frame: collecting samples...");
            }
            GUILayout.Label("Editor overhead and VSync are included. This is not cloud GPU time.");
            GUILayout.Space(10f);
            GUILayout.Label($"Cloud resolution: {pass.resolutionScale:P0}  ({pass.OutputWidth} x {pass.OutputHeight})");
            bool changed = false;
            GUILayout.BeginHorizontal();
            changed |= ScaleButton("50% / Fast", 0.5f);
            changed |= ScaleButton("67%", 0.67f);
            changed |= ScaleButton("75% / Original", 0.75f);
            changed |= ScaleButton("100%", 1f);
            GUILayout.EndHorizontal();
            GUILayout.Label("Lower resolution can soften edges and thin clouds.");
            bool depthUpsampling = GUILayout.Toggle(pass.useDepthAwareUpsampling, "Depth-aware upsampling (foreground edges)");
            if (depthUpsampling != pass.useDepthAwareUpsampling)
            {
                pass.useDepthAwareUpsampling = depthUpsampling;
                changed = true;
            }
            GUILayout.Space(10f);
            bool cache = GUILayout.Toggle(pass.useShapeCellCache, "Reuse upper-cloud shape calculations");
            bool rejection = GUILayout.Toggle(pass.useSupportRejection, "Skip density work outside upper-cloud support");
            bool compact = GUILayout.Toggle(pass.useCompactTargets, "Compact render buffers (full-float scene depth)");
            bool intervals = GUILayout.Toggle(pass.useRayIntervals, "Skip empty cloud regions (tighter bounds)");
            bool shadows = GUILayout.Toggle(pass.useShadowTermination, "Stop negligible shadow work (palette lighting)");
            bool palette = GUILayout.Toggle(pass.usePaletteLightingFastPath, "Skip unused physical lighting (experimental)");
            GUILayout.Label($"Palette-only active: {pass.PaletteLightingFastPathActive}  /  requires 100% palette lighting");
            bool temporal = GUILayout.Toggle(pass.useTemporalReprojection, "Temporal reprojection / split updates (experimental)");
            if (temporal != pass.useTemporalReprojection)
            {
                pass.useTemporalReprojection = temporal;
                changed = true;
            }

            GUILayout.BeginHorizontal();
            changed |= TemporalButton("2 phases / 50% tiles", 2);
            changed |= TemporalButton("4 phases / 25% tiles", 4);
            GUILayout.EndHorizontal();
            GUILayout.Label($"Temporal: {pass.temporalUpdatePhases} phases / {pass.TemporalBytes / 1048576f:0.0} MiB (all cameras)");
            GUILayout.Label($"History: {pass.TemporalStatus} / resets: {pass.TemporalResets}");
            GUILayout.Label("May trail during motion. Invalid history is traced immediately.");
            changed |= DrawDistanceQuality();
            bool spatial = GUILayout.Toggle(pass.useSpatialLightCache, "Share cloud lighting across pixels (experimental)");
            if (cache != pass.useShapeCellCache || rejection != pass.useSupportRejection || compact != pass.useCompactTargets
                || intervals != pass.useRayIntervals || shadows != pass.useShadowTermination || spatial != pass.useSpatialLightCache
                || palette != pass.usePaletteLightingFastPath)
            {
                pass.useShapeCellCache = cache;
                pass.useSupportRejection = rejection;
                pass.useCompactTargets = compact;
                pass.useRayIntervals = intervals;
                pass.useShadowTermination = shadows;
                pass.usePaletteLightingFastPath = palette;
                pass.useSpatialLightCache = spatial;
                changed = true;
            }

            GUILayout.BeginHorizontal();
            changed |= SpatialButton("16 m / Fine", 16f);
            changed |= SpatialButton("32 m / Medium", 32f);
            changed |= SpatialButton("64 m / Wide", 64f);
            GUILayout.EndHorizontal();
            GUILayout.Label($"Shared lighting: {pass.spatialLightCellSize:0} m / {pass.SpatialLightBytes / 1048576f:0.0} MiB");
            GUILayout.Label("Can be slower in distant views. Wider grids can change cloud shading.");
            GUILayout.Label($"Active camera buffers: {pass.GetActiveTargetBytes() / 1048576f:0.00} MiB");
            GUILayout.Label($"Compact active: {pass.CompactTargetsActive}   Shape-cache rebuilds: {pass.ShapeCacheBuilds}");
            GUILayout.Space(10f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Optimizations OFF / compare"))
            {
                DisableOptimizations();
                changed = true;
            }

            if (GUILayout.Button("Restore session defaults"))
            {
                RestoreSessionDefaults();
                changed = true;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Changes apply immediately for this Play session. Compare the same view.");
            CloudBenchmark benchmark = GetComponent<CloudBenchmark>();
            if (benchmark != null && GUILayout.Button("Open repeatable benchmark  [F9]", GUILayout.Height(28f)))
            {
                showPanel = false;
                benchmark.showWindow = true;
            }
            GUILayout.Space(8f);
            if (GUILayout.Button("Close  [F8 / Esc]", GUILayout.Height(28f)))
            {
                showPanel = false;
            }

            if (changed)
            {
                ResetStatistics();
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
            GUI.matrix = previous;
        }

        /// <summary>설정창의 비교 버튼과 Play 검사에서 모든 선택적 최적화를 같은 경로로 끕니다.</summary>
        public void DisableOptimizations()
        {
            pass.useShapeCellCache = false;
            pass.useSupportRejection = false;
            pass.useCompactTargets = false;
            pass.useRayIntervals = false;
            pass.useShadowTermination = false;
            pass.usePaletteLightingFastPath = false;
            pass.useTemporalReprojection = false;
            pass.distanceQuality.density = false;
            pass.distanceQuality.lighting = false;
            pass.useSpatialLightCache = false;
            pass.useDepthAwareUpsampling = false;
            ResetStatistics();
        }

        /// <summary>현재 Play 실행이 시작할 때 저장한 품질과 최적화 선택을 복원합니다.</summary>
        public void RestoreSessionDefaults()
        {
            pass.resolutionScale = originalScale;
            pass.useDepthAwareUpsampling = originalDepthUpsampling;
            pass.useShapeCellCache = originalCache;
            pass.useSupportRejection = originalRejection;
            pass.useCompactTargets = originalCompact;
            pass.useRayIntervals = originalIntervals;
            pass.useShadowTermination = originalShadowTermination;
            pass.usePaletteLightingFastPath = originalPaletteFastPath;
            pass.useTemporalReprojection = originalTemporal;
            pass.temporalUpdatePhases = originalTemporalPhases;
            pass.distanceQuality = originalDistanceQuality;
            pass.useSpatialLightCache = originalSpatial;
            pass.spatialLightCellSize = originalSpatialCellSize;
            ResetStatistics();
        }

        /// <summary>두 표본 간격의 전환 거리와 배율을 독립적으로 조절합니다. 원경 표면/명암 차이가 생길 수 있습니다.</summary>
        private bool DrawDistanceQuality()
        {
            GUILayout.Space(10);
            GUILayout.Label("Distance quality / Concept V2 supported: " + pass.DistanceQualitySupported);
            CloudDistanceQualitySettings before = pass.distanceQuality;
            CloudDistanceQualitySettings value = before;
            value.density = GUILayout.Toggle(value.density, "Distance density / fewer interior samples");
            value.densityNear = QualitySlider("Density start (m)", value.densityNear, 0, 20000);
            value.densityFar = QualitySlider("Density full reduction (m)", value.densityFar, value.densityNear + 100, 42000);
            value.densityStepScale = QualitySlider("Density far step multiplier", value.densityStepScale, 1, 2);
            value.lighting = GUILayout.Toggle(value.lighting, "Distance lighting / wider interpolation intervals");
            value.lightingNear = QualitySlider("Lighting start (m)", value.lightingNear, 0, 20000);
            value.lightingFar = QualitySlider("Lighting full reduction (m)", value.lightingFar, value.lightingNear + 100, 42000);
            value.lightingSpacingScale = QualitySlider("Lighting far spacing multiplier", value.lightingSpacingScale, 1, 3);
            GUILayout.Label("Near boundaries and shadow reach stay unchanged. Distant opacity/shading can differ.");
            pass.distanceQuality = value;
            return !before.Equals(value);
        }

        /// <summary>거리/배율의 현재 숫자를 함께 보여 주는 품질 슬라이더입니다.</summary>
        private static float QualitySlider(string label, float value, float minimum, float maximum)
        {
            GUILayout.Label(label + ": " + value.ToString("0.##"));
            return GUILayout.HorizontalSlider(value, minimum, maximum);
        }

        /// <summary>순환 갱신 단계 변경은 버퍼를 재할당하지 않고 이력만 새로 시작합니다.</summary>
        private bool TemporalButton(string label, int phases)
        {
            if (GUILayout.Button(label) && pass.temporalUpdatePhases != phases)
            {
                pass.temporalUpdatePhases = phases;
                return true;
            }

            return false;
        }

        /// <summary>공간 조명 격자의 간격을 바꿉니다. OFF에서 선택해도 조명을 자동으로 켜지 않습니다.</summary>
        private bool SpatialButton(string label, float value)
        {
            if (GUILayout.Button(label) && !Mathf.Approximately(pass.spatialLightCellSize, value))
            {
                pass.spatialLightCellSize = value;
                return true;
            }

            return false;
        }

        /// <summary>단계별 버튼을 사용하여 드래그 중 연속적인 RT 재할당을 피합니다.</summary>
        private bool ScaleButton(string label, float value)
        {
            if (GUILayout.Button(label) && !Mathf.Approximately(pass.resolutionScale, value))
            {
                pass.resolutionScale = value;
                return true;
            }

            return false;
        }
    }
}
