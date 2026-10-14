using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies
{
    /// <summary>명령줄이나 MCP에서 같은 조건의 순방향·역방향 비교를 실행합니다.</summary>
    public sealed class CloudBenchmarkAutomation : MonoBehaviour
    {
        [Serializable]
        private sealed class SuiteReport
        {
            public string status = "running";
            public string startedUtc;
            public string[] presets;
            public int repeats;
            public bool realtime;
            public bool diagnostics;
            public string notes = "Every second repetition reverses case order. Diagnostic atomic/readback overhead is included only in explicitly labelled diagnostic runs. Compare matching route modes and sample counts. Diagnostic snapshots sample every 15th frame; cache counters sample 1/64 pixels of the latest completed render, not the whole route.";
            public List<string> paths = new List<string>();
        }

        public bool Running { get; private set; }
        public string Status { get; private set; }

        private CloudBenchmark benchmark;
        private LostSkiesCloudPass pass;
        private string[] presets;
        private string output;
        private string id;
        private int startupFrames;
        private int next;
        private int repeats = 1;
        private bool started;
        private bool quitWhenFinished;
        private bool captureEvidence;
        private bool captureStarted;
        private CloudBenchmarkEvidence evidence;
        private SuiteReport suite;
        private string originalPassSettings;
        private ComputeShader originalShader;
        private CloudTimeOfDayController suiteClock;
        private float originalHour;
        private bool originalAdvance;
        private bool originalBackground;
        private float originalTimeScale;
        private bool restored;

        /// <summary>명시한 명령줄이 있을 때만 자동 계측을 시작합니다.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            string selected = Argument(arguments, "-cloudBenchmark", null);
            if (string.IsNullOrEmpty(selected))
            {
                return;
            }

            int.TryParse(Argument(arguments, "-cloudFrames", "180"), out int frames);
            int.TryParse(Argument(arguments, "-cloudRepeat", "1"), out int repeatCount);
            bool realtime = Array.IndexOf(arguments, "-cloudRealtime") >= 0;
            bool diagnostics = Array.IndexOf(arguments, "-cloudCounters") >= 0;
            bool capture = Array.IndexOf(arguments, "-cloudCapture") >= 0;
            CloudBenchmarkAutomation automation = StartSuite(selected, Argument(arguments, "-cloudOutput", null), realtime, frames, repeatCount, diagnostics, capture);
            automation.quitWhenFinished = Array.IndexOf(arguments, "-cloudQuit") >= 0;
            if (float.TryParse(Argument(arguments, "-cloudLegSeconds", "12"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float seconds))
            {
                automation.benchmark.realtimeSecondsPerLeg = Mathf.Clamp(seconds, 6f, 60f);
            }

            if (float.TryParse(Argument(arguments, "-cloudDaySeconds", "0"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float daySeconds) && float.IsFinite(daySeconds))
            {
                automation.benchmark.realtimeDaySeconds = Mathf.Max(0f, daySeconds);
            }
        }

        /// <summary>Play 모드의 MCP 호출과 Player 명령줄이 같은 비교 구현을 사용합니다.</summary>
        public static CloudBenchmarkAutomation StartSuite(string selected, string directory, bool realtime = false,
            int frames = 180, int repeatCount = 1, bool diagnostics = false, bool capture = false)
        {
            CloudBenchmark target = UnityEngine.Object.FindFirstObjectByType<CloudBenchmark>();
            if (!Application.isPlaying || target == null || target.IsRunning)
            {
                throw new InvalidOperationException("An idle CloudBenchmark in Play mode is required.");
            }

            CloudBenchmarkAutomation existing = target.GetComponent<CloudBenchmarkAutomation>();
            if (existing != null && existing.Running)
            {
                throw new InvalidOperationException("A benchmark suite is already running.");
            }

            CloudBenchmarkAutomation automation = target.gameObject.AddComponent<CloudBenchmarkAutomation>();
            automation.benchmark = target;
            automation.presets = selected.Split(',');
            automation.output = directory;
            if (string.IsNullOrEmpty(directory))
            {
                automation.output = Path.Combine(Application.persistentDataPath, "CloudBenchmarks");
            }

            automation.output = Path.GetFullPath(automation.output);
            automation.id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            automation.repeats = Mathf.Clamp(repeatCount, 1, 4);
            automation.captureEvidence = capture;
            target.framesPerLeg = Mathf.Clamp(frames, 60, 600);
            target.realtimeFlight = realtime;
            target.collectDiagnostics = diagnostics;
            foreach (CustomPass candidate in target.GetComponent<CustomPassVolume>().customPasses)
            {
                automation.pass = candidate as LostSkiesCloudPass;
                if (automation.pass != null)
                {
                    break;
                }
            }

            if (automation.pass == null)
            {
                throw new InvalidOperationException("Cloud pass not found.");
            }

            automation.originalPassSettings = JsonUtility.ToJson(automation.pass);
            automation.originalShader = automation.pass.raymarchShader;
            automation.originalBackground = Application.runInBackground;
            automation.originalTimeScale = Time.timeScale;
            Application.runInBackground = true;
            // 조건 사이의 한 프레임에도 외부 날씨 전환이 진행되지 않게 전체 비교 동안 정지합니다.
            Time.timeScale = 0f;
            automation.suiteClock = target.GetComponent<CloudTimeOfDayController>();
            if (automation.suiteClock != null)
            {
                automation.originalHour = automation.suiteClock.TimeOfDay;
                automation.originalAdvance = automation.suiteClock.autoAdvance;
            }
#if CLOUD_EXTENSION_BENCHMARK
            // 제한된 Player는 시작 워밍업부터 지원하는 정확 volume 조합을 사용합니다.
            automation.Configure("performance");
#endif
            automation.suite = new SuiteReport();
            automation.suite.startedUtc = DateTime.UtcNow.ToString("O");
            automation.suite.presets = automation.presets;
            automation.suite.repeats = automation.repeats;
            automation.suite.realtime = realtime;
            automation.suite.diagnostics = diagnostics;
            automation.Running = true;
            automation.Status = "warming startup";
            Directory.CreateDirectory(automation.output);
            return automation;
        }

        /// <summary>값을 가진 옵션을 읽고 지정하지 않았으면 기본값을 반환합니다.</summary>
        private static string Argument(string[] arguments, string name, string fallback)
        {
            int index = Array.IndexOf(arguments, name);
            if (index >= 0 && index + 1 < arguments.Length)
            {
                return arguments[index + 1];
            }

            return fallback;
        }

        /// <summary>계측 완료 뒤에만 파일을 기록하고 다음 조건을 준비합니다.</summary>
        private void Update()
        {
            if (!Running)
            {
                return;
            }

            try
            {
                if (captureStarted)
                {
                    if (!evidence.Running)
                    {
                        if (!string.IsNullOrEmpty(evidence.Error))
                        {
                            throw new InvalidOperationException(evidence.Error);
                        }

                        Finish("completed", 0);
                    }

                    return;
                }

                if (!started)
                {
                    startupFrames++;
#if CLOUD_EXTENSION_BENCHMARK
                    if (startupFrames == 1)
                    {
                        // 첫 측정 전에 전체 요청을 검사해 중간에 지원하지 않는 조합을 실행하지 않습니다.
                        foreach (string requestedPreset in presets)
                        {
                            ValidatePreset(requestedPreset.Trim());
                        }
                    }
#endif
                    if (startupFrames >= 45)
                    {
                        StartCase();
                    }

                    return;
                }

                if (benchmark.IsRunning)
                {
                    return;
                }

                if (benchmark.LastReport == null || benchmark.LastReport.status != "completed")
                {
                    throw new InvalidOperationException("Benchmark did not complete: " + benchmark.Status);
                }

                string path = benchmark.LastReport.Save(output);
                suite.paths.Add(path);
                File.WriteAllText(Path.Combine(output, "suite-" + id + ".json"), JsonUtility.ToJson(suite, true));
                Debug.Log("Automated cloud benchmark output: " + path);
                next++;
                if (next < presets.Length * repeats)
                {
                    StartCase();
                    return;
                }

                if (captureEvidence)
                {
                    Configure(presets[presets.Length - 1].Trim());
                    captureStarted = true;
                    evidence = gameObject.AddComponent<CloudBenchmarkEvidence>();
                    evidence.Begin(pass, benchmark.routeCenter, Path.Combine(output, "Evidence-" + id));
                    Status = "capturing evidence separately from timings";
                    return;
                }

                Finish("completed", 0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Finish(exception.ToString(), 2);
            }
        }

        /// <summary>재실행의 후반 순서를 뒤집어 GPU 열과 클럭 드리프트를 비교할 수 있게 합니다.</summary>
        private void StartCase()
        {
            int index = next % presets.Length;
            if ((next / presets.Length) % 2 == 1)
            {
                index = presets.Length - 1 - index;
            }

            string selected = presets[index].Trim();
            Configure(selected);
            string label = "redesign-" + id + "-" + next + "-" + selected;
            if (benchmark.collectDiagnostics)
            {
                label += "-diagnostic";
            }

            if (benchmark.realtimeFlight)
            {
                label += "-flight";
            }

            started = true;
            Status = label;
            benchmark.StartBenchmark(label);
            if (!benchmark.IsRunning)
            {
                throw new InvalidOperationException("Benchmark did not enter its running state.");
            }
        }

        /// <summary>확장 전용 Player가 제거된 shader 조합을 조용히 실행하지 않도록 요청을 검사합니다.</summary>
        private static void ValidatePreset(string selected)
        {
#if CLOUD_EXTENSION_BENCHMARK
            string[] valid = { "baseline", "reference-performance", "performance", "normal-cache", "surface-hybrid" };
            if (Array.IndexOf(valid, selected) < 0)
            {
                throw new ArgumentException("Extension-only Player supports baseline, reference-performance, performance, normal-cache and surface-hybrid. Requested: " + selected);
            }
#else
            string[] valid = { "baseline", "reference-performance", "performance", "normal-cache", "surface-hybrid", "field-light", "field-density", "ocean-field", "occlusion", "traversal", "temporal", "tiles", "adaptive", "steady", "balanced", "final" };
            if (Array.IndexOf(valid, selected) < 0)
            {
                throw new ArgumentException("Unknown benchmark preset: " + selected);
            }
#endif
        }

        /// <summary>설정별 차이를 명시하여 기존 기준선과 각 새 기법의 비용을 분리합니다.</summary>
        private void Configure(string selected)
        {
            ValidatePreset(selected);

            pass.resolutionScale = 0.5f;
            pass.raymarchShader = originalShader;
            pass.useDepthAwareUpsampling = true;
            pass.useShapeCellCache = true;
            pass.useSupportRejection = true;
            pass.useCompactTargets = true;
            pass.useRayIntervals = false;
            pass.useShadowTermination = false;
            pass.usePaletteLightingFastPath = false;
            pass.useSpatialLightCache = false;
            pass.useTemporalReprojection = false;
            pass.temporalUpdatePhases = 4;
            pass.windSpeed = 0f;
            pass.distanceQuality = CloudDistanceQualitySettings.Default;
            pass.distanceQuality.density = true;
            pass.distanceQuality.lighting = true;
            string[] options = { "useShapeFieldCache", "useShapeFieldDensity", "useOceanShapeFieldCache", "useMacroTraversal", "useTemporalTileCompaction", "useAdaptiveIntegration", "useCacheDiagnostics", "useTemporalDiagnostics", "useNormalFieldCache", "useNormalFieldDiagnostics", "useFarSurface", "useFarSurfaceDiagnostics" };
            foreach (string option in options)
            {
                SetOptionalBoolean(option, false, false);
            }

            if (selected == "baseline" || selected == "reference-performance")
            {
                ComputeShader reference = Resources.Load<ComputeShader>("CloudRedesignReference/CloudRaymarch");
                if (reference != null)
                {
                    pass.raymarchShader = reference;
                }
                else if (selected == "reference-performance" || originalShader.HasKernel("BuildShapeFields"))
                {
                    throw new InvalidOperationException("The requested reference preset requires its preserved reference shader resource.");
                }
            }

            if (selected == "baseline")
            {
                pass.resolutionScale = 0.75f;
                pass.distanceQuality.density = false;
                pass.distanceQuality.lighting = false;
            }

            // 두 신규 실험은 기존 final 조합과 분리하며 모든 이전 프리셋에서 먼저 OFF로 초기화합니다.
            bool normalCache = selected == "normal-cache";
            bool farSurface = selected == "surface-hybrid";
            SetOptionalBoolean("useNormalFieldCache", normalCache, normalCache);
            SetOptionalBoolean("useFarSurface", farSurface, farSurface);
            SetOptionalBoolean("useNormalFieldDiagnostics", benchmark.collectDiagnostics && normalCache, normalCache);
            SetOptionalBoolean("useFarSurfaceDiagnostics", benchmark.collectDiagnostics && farSurface, farSurface);
            pass.normalFieldCellSize = 256f;
            pass.normalFieldExactNearDistance = 2500f;
            pass.normalFieldMinimumCoherence = 0.995f;
            pass.farSurfaceNearDistance = 12000f;

            if (normalCache && !pass.raymarchShader.HasKernel("BuildNormalFields"))
            {
                throw new InvalidOperationException("The normal-cache preset requires the BuildNormalFields kernel.");
            }

            if (farSurface && !pass.raymarchShader.HasKernel("BuildFarSurface"))
            {
                throw new InvalidOperationException("The surface-hybrid preset requires the BuildFarSurface kernel.");
            }

            bool field = selected == "field-light" || selected == "field-density" || selected == "ocean-field" || selected == "occlusion" || selected == "final";
            bool density = selected == "field-density" || selected == "ocean-field" || selected == "occlusion" || selected == "final";
            bool temporal = selected == "temporal" || selected == "tiles" || selected == "balanced" || selected == "final";
            bool traversal = selected == "traversal" || selected == "steady" || selected == "balanced" || selected == "final";
            bool adaptive = selected == "adaptive" || selected == "steady" || selected == "balanced" || selected == "final";
            bool tiles = selected == "tiles" || selected == "balanced" || selected == "final";
            SetOptionalBoolean("useShapeFieldCache", field, field);
            SetOptionalBoolean("useShapeFieldDensity", density, density);
            SetOptionalBoolean("useOceanShapeFieldCache", selected == "ocean-field" || selected == "final", selected == "ocean-field" || selected == "final");
            SetOptionalBoolean("useMacroTraversal", traversal, traversal);
            SetOptionalBoolean("useAdaptiveIntegration", adaptive, adaptive);
            pass.useSpatialLightCache = selected == "occlusion" || selected == "final";
            pass.useTemporalReprojection = temporal;
            SetOptionalBoolean("useTemporalTileCompaction", tiles, tiles);
            SetOptionalBoolean("useCacheDiagnostics", benchmark.collectDiagnostics, benchmark.collectDiagnostics);
            SetOptionalBoolean("useTemporalDiagnostics", benchmark.collectDiagnostics, benchmark.collectDiagnostics);
            CloudTimeOfDayController clock = benchmark.GetComponent<CloudTimeOfDayController>();
            if (clock != null)
            {
                clock.autoAdvance = false;
                clock.SetTime(12f);
            }
        }

        /// <summary>기능이 없는 기준 Player도 실행하되 요청한 새 기능이 빠졌으면 명확히 실패합니다.</summary>
        private void SetOptionalBoolean(string name, bool value, bool required)
        {
            FieldInfo field = pass.GetType().GetField(name);
            if (field == null)
            {
                if (required)
                {
                    throw new MissingFieldException(pass.GetType().Name, name);
                }

                return;
            }

            field.SetValue(pass, value);
        }

        /// <summary>완료 상태와 원시 보고서 목록을 저장하고 임시 품질 설정을 복원합니다.</summary>
        private void Finish(string state, int exitCode)
        {
            Running = false;
            Status = state;
            suite.status = state;
            File.WriteAllText(Path.Combine(output, "suite-" + id + ".json"), JsonUtility.ToJson(suite, true));
            RestoreSuite();
            if (quitWhenFinished)
            {
                Application.Quit(exitCode);
            }
        }

        /// <summary>완료·비활성화·Play 종료 모두 전역 시간과 품질 상태를 한 번만 돌려줍니다.</summary>
        private void RestoreSuite()
        {
            if (restored || string.IsNullOrEmpty(originalPassSettings))
            {
                return;
            }

            restored = true;
            if (evidence != null && evidence.Running)
            {
                evidence.enabled = false;
            }

            if (benchmark != null && benchmark.IsRunning)
            {
                benchmark.Cancel();
            }

            JsonUtility.FromJsonOverwrite(originalPassSettings, pass);
            Application.runInBackground = originalBackground;
            Time.timeScale = originalTimeScale;
            if (suiteClock != null)
            {
                suiteClock.autoAdvance = originalAdvance;
                suiteClock.SetTime(originalHour);
            }
        }

        /// <summary>외부에서 실행을 멈춰도 비교 전 상태를 복원합니다.</summary>
        private void OnDisable()
        {
            Running = false;
            RestoreSuite();
        }
    }
}
