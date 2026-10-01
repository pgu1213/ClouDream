using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>실제 Play 루프에서 설정 전환과 패널 입력 소유권을 검증합니다.</summary>
    public static class CloudOptimizationPlayValidation
    {
        [Serializable]
        public sealed class Report
        {
            public bool completed;
            public bool passed;
            public bool openedByF8;
            public bool closedByEscape;
            public bool flightSuspended;
            public bool flightRestored;
            public bool stableAllocations;
            public int settingTransitions;
            public long regularBytes;
            public long compactBytes;
            public string error;
        }

        public static Report LastReport = new Report();

        /// <summary>Play 모드만 허용하고 입력 설정·렌더 설정은 성공 여부와 무관하게 복원합니다.</summary>
        public static async void Start()
        {
            Report report = new Report();
            LastReport = report;
            if (!Application.isPlaying)
            {
                report.completed = true;
                report.error = "Play mode required.";
                return;
            }

            CloudOptimizationPanel panel = UnityEngine.Object.FindFirstObjectByType<CloudOptimizationPanel>();
            CustomPassVolume volume = panel.GetComponent<CustomPassVolume>();
            LostSkiesCloudPass pass = null;
            foreach (CustomPass candidate in volume.customPasses)
            {
                pass = candidate as LostSkiesCloudPass;
                if (pass != null)
                {
                    break;
                }
            }

            CloudSeaFlight flight = Camera.main.GetComponent<CloudSeaFlight>();
            bool flightEnabled = flight.enabled;
            float scale = pass.resolutionScale;
            bool cache = pass.useShapeCellCache;
            bool rejection = pass.useSupportRejection;
            bool compact = pass.useCompactTargets;
            bool background = Application.runInBackground;
            InputSettings.EditorInputBehaviorInPlayMode routing = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSettings.BackgroundBehavior focus = InputSystem.settings.backgroundBehavior;
            Keyboard keyboard = InputSystem.AddDevice<Keyboard>();
            try
            {
                Application.runInBackground = true;
                InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                panel.showPanel = false;
                await Pump();
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.F8));
                await Pump();
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                await Pump();
                report.openedByF8 = panel.showPanel;
                report.flightSuspended = !flight.enabled;
                Require(report.openedByF8 && report.flightSuspended, "F8 did not open the panel and suspend flight.");

                float[] scales = { 0.5f, 0.67f, 0.75f, 1f, 0.75f };
                foreach (float value in scales)
                {
                    pass.resolutionScale = value;
                    pass.useCompactTargets = false;
                    pass.useShapeCellCache = false;
                    pass.useSupportRejection = false;
                    await Pump();
                    report.regularBytes = pass.GetActiveTargetBytes();
                    pass.useCompactTargets = true;
                    pass.useShapeCellCache = true;
                    pass.useSupportRejection = true;
                    await Pump();
                    report.compactBytes = pass.GetActiveTargetBytes();
                    Require(pass.CompactTargetsActive && report.compactBytes * 3 == report.regularBytes * 2,
                        "Compact targets did not use the expected 16-byte contract.");
                    int allocations = pass.TargetAllocations;
                    int builds = pass.ShapeCacheBuilds;
                    await Pump();
                    Require(pass.TargetAllocations == allocations && pass.ShapeCacheBuilds == builds,
                        "Stable Play settings rebuilt buffers or cells.");
                    report.settingTransitions += 2;
                }

                report.stableAllocations = true;
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape));
                await Pump();
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                await Pump();
                report.closedByEscape = !panel.showPanel;
                report.flightRestored = flight.enabled == flightEnabled;
                Require(report.closedByEscape && report.flightRestored, "Closing the panel did not restore flight input.");
                report.passed = true;
            }
            catch (Exception exception)
            {
                report.error = exception.ToString();
            }
            finally
            {
                InputSystem.RemoveDevice(keyboard);
                panel.showPanel = false;
                pass.resolutionScale = scale;
                pass.useShapeCellCache = cache;
                pass.useSupportRejection = rejection;
                pass.useCompactTargets = compact;
                await Pump();
                Application.runInBackground = background;
                InputSystem.settings.editorInputBehaviorInPlayMode = routing;
                InputSystem.settings.backgroundBehavior = focus;
                report.completed = true;
                Directory.CreateDirectory("Screenshots");
                File.WriteAllText("Screenshots/Optimization-PlayValidation.json", JsonUtility.ToJson(report, true));
            }
        }

        /// <summary>비활성 에디터에서도 렌더와 입력이 실행될 기회를 주고 메인 스레드를 막지 않습니다.</summary>
        private static async Task Pump()
        {
            int startFrame = Time.frameCount;
            for (int attempt = 0; attempt < 80 && Time.frameCount < startFrame + 3; attempt++)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                await Task.Delay(40);
            }

            Require(Time.frameCount >= startFrame + 3, "Play loop did not advance.");
        }

        /// <summary>검사 실패를 JSON에 남길 수 있도록 구체적인 원인을 전달합니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
