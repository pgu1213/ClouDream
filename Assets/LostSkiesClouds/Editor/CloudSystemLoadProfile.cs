using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>생산 설정을 복원하는 진단 전용 경로 비교와 CPU 표본 내보내기입니다.</summary>
    public static class CloudSystemLoadProfile
    {
        [Serializable]
        public sealed class CpuMarker
        {
            public string thread;
            public string name;
            public int calls;
            public double inclusiveMs;
            public double selfMs;
        }

        [Serializable]
        public sealed class CpuCapture
        {
            public string notes = "Last 60 available frames ending 40 frames before benchmark completion; ocean route tail. Inclusive markers overlap. Self time excludes immediate children. CPU waits remain explicit; GPU times are separate.";
            public int firstFrame;
            public int lastFrame;
            public int mainFrames;
            public int renderFrames;
            public List<double> frameGpuMs = new List<double>();
            public List<CpuMarker> markers = new List<CpuMarker>();
        }

        [Serializable]
        public sealed class Run
        {
            public string label;
            public string path;
            public string cpuPath;
        }

        [Serializable]
        public sealed class Report
        {
            public bool running;
            public string status;
            public string id;
            public string originalPass;
            public string originalClock;
            public string notes = "Diagnostic ablations alter shading, not the primary density integration path. Differences are marginal costs, not additive exclusive shader function timings. Editor, 1920x1080, noon, wind zero, current quality options. Forward and reverse order, identical CloudRoute-v1 poses.";
            public List<Run> runs = new List<Run>();
        }

        public static Report LastReport = new Report();

        private const string Root = "Assets/LostSkiesClouds/Runtime/";
        private static readonly string[] Names = { "baseline", "no-sun-shadow", "no-sky-occlusion", "no-boundary-normal", "constant-light", "cloud-pass-disabled" };

        private static CloudBenchmark benchmark;
        private static LostSkiesCloudPass pass;
        private static CloudTimeOfDayController clock;
        private static ComputeShader originalShader;
        private static float originalHour;
        private static bool originalAdvance;
        private static float originalWind;
        private static float originalTimeScale;
        private static bool originalEnabled;
        private static int originalFrames;
        private static int next;
        private static string output;
        private static string[] activeNames;

        /// <summary>원본과 동일한 적분을 유지하고 선택한 조명 계산만 생략한 임시 자산을 만듭니다.</summary>
        public static void Prepare()
        {
            if (LastReport.running || CloudSystemWorkProfile.LastReport.running)
            {
                throw new InvalidOperationException("Cannot prepare shaders while profiling.");
            }

            string source = File.ReadAllText(Root + "CloudRaymarch.compute").Replace("\r\n", "\n");
            string shadow = ReplaceRequired(source,
                "shadow += Density(position + sun * lightDistance, false) * lightStep;",
                "shadow += 0; // Diagnostic: omit solar density probes.");
            WriteVariant("no-sun-shadow", shadow);
            string sky = ReplaceRequired(source, "if (_UseSkyLighting != 0)\n    {\n        skyDepth =", "if (false)\n    {\n        skyDepth =");
            WriteVariant("no-sky-occlusion", sky);
            string normal = ReplaceRequired(source, "float3 CloudBoundaryNormal(float3 position)\n{", "float3 CloudBoundaryNormal(float3 position)\n{\n    return float3(0, 1, 0); // Diagnostic: omit boundary normal evaluation.");
            WriteVariant("no-boundary-normal", normal);
            string constant = ReplaceRequired(source,
                "float3 EvaluateCloudLight(float3 position, float3 sun, float extinction, float phase, float sunCosine, float3 surfaceNormal)\n{",
                "float3 EvaluateCloudLight(float3 position, float3 sun, float extinction, float phase, float sunCosine, float3 surfaceNormal)\n{\n    return 1; // Diagnostic: density integration with constant incident light.");
            WriteVariant("constant-light", constant);
        }

        /// <summary>계측 대상이 변경되면 조용히 잘못된 비교를 만들지 않고 중단합니다.</summary>
        private static string ReplaceRequired(string source, string before, string after)
        {
            if (!source.Contains(before))
            {
                throw new InvalidOperationException("Diagnostic source anchor changed: " + before);
            }
            return source.Replace(before, after);
        }

        /// <summary>임시 셰이더 원문을 증거 폴더에도 보존하고 Unity를 통해 가져옵니다.</summary>
        private static void WriteVariant(string label, string source)
        {
            Directory.CreateDirectory("Benchmarks/SystemLoadSources");
            File.WriteAllText("Benchmarks/SystemLoadSources/" + label + ".compute.txt", source);
            string path = Root + "CloudLoad-" + label + ".compute";
            File.WriteAllText(path, source);
            AssetDatabase.ImportAsset(path);
        }

        /// <summary>현재 품질 설정을 보존하며 같은 경로를 정순과 역순으로 측정합니다.</summary>
        public static void Start(string[] caseNames = null)
        {
            if (LastReport.running || CloudSystemWorkProfile.LastReport.running
                || !Application.isPlaying || Screen.width != 1920 || Screen.height != 1080)
            {
                throw new InvalidOperationException("Requires idle profiling and FHD Play mode.");
            }
            benchmark = UnityEngine.Object.FindFirstObjectByType<CloudBenchmark>();
            if (benchmark == null || benchmark.IsRunning)
            {
                throw new InvalidOperationException("Benchmark unavailable or busy.");
            }
            pass = null;
            foreach (CustomPass candidate in benchmark.GetComponent<CustomPassVolume>().customPasses)
            {
                if (candidate is LostSkiesCloudPass cloudPass)
                {
                    pass = cloudPass;
                    break;
                }
            }
            if (pass == null)
            {
                throw new InvalidOperationException("Cloud pass unavailable.");
            }
            clock = benchmark.GetComponent<CloudTimeOfDayController>();
            originalShader = pass.raymarchShader;
            originalEnabled = pass.enabled;
            originalWind = pass.windSpeed;
            originalHour = clock.TimeOfDay;
            originalAdvance = clock.autoAdvance;
            originalTimeScale = Time.timeScale;
            originalFrames = benchmark.framesPerLeg;
            activeNames = Names;
            if (caseNames != null && caseNames.Length > 0)
            {
                foreach (string name in caseNames)
                {
                    if (Array.IndexOf(Names, name) < 0)
                    {
                        throw new ArgumentException("Unknown diagnostic case: " + name);
                    }
                }
                activeNames = caseNames;
            }
            LastReport = new Report();
            LastReport.id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            LastReport.originalPass = JsonUtility.ToJson(pass);
            LastReport.originalClock = JsonUtility.ToJson(clock);
            LastReport.running = true;
            LastReport.status = "starting";
            output = "Benchmarks/system-load-" + LastReport.id + ".json";
            next = 0;
            clock.autoAdvance = false;
            clock.SetTime(12);
            Time.timeScale = 0;
            pass.windSpeed = 0;
            benchmark.framesPerLeg = 180;
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Cancel;
            try
            {
                StartCase();
            }
            catch (Exception)
            {
                Finish("Failed to start diagnostic benchmark");
                throw;
            }
        }

        /// <summary>완료된 측정 뒤에만 CPU 기록을 읽어 계측 중 파일 쓰기 비용을 피합니다.</summary>
        private static void Tick()
        {
            try
            {
                if (!Application.isPlaying || benchmark == null)
                {
                    Finish("Play ended");
                    return;
                }
                if (benchmark.IsRunning)
                {
                    return;
                }
                if (benchmark.LastReport == null || benchmark.LastReport.status != "completed")
                {
                    Finish("Benchmark interrupted");
                    return;
                }
                Run run = new Run();
                run.label = LastReport.status;
                run.path = benchmark.LastPath;
                run.cpuPath = "Benchmarks/system-load-" + LastReport.id + "-" + next + "-cpu.json";
                File.WriteAllText(run.cpuPath, JsonUtility.ToJson(CaptureCpu(), true));
                LastReport.runs.Add(run);
                next++;
                if (next >= activeNames.Length * 2)
                {
                    Finish("completed");
                    return;
                }
                StartCase();
            }
            catch (Exception exception)
            {
                Finish(exception.ToString());
                Debug.LogException(exception);
            }
        }

        /// <summary>후반에는 같은 진단을 역순으로 실행해 기준선 변동을 관찰합니다.</summary>
        private static void StartCase()
        {
            int index = next;
            if (index >= activeNames.Length)
            {
                index = activeNames.Length * 2 - 1 - next;
            }
            string name = activeNames[index];
            pass.enabled = name != "cloud-pass-disabled";
            pass.raymarchShader = originalShader;
            if (name != "baseline" && name != "cloud-pass-disabled")
            {
                pass.raymarchShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(Root + "CloudLoad-" + name + ".compute");
                if (pass.raymarchShader == null)
                {
                    throw new InvalidOperationException("Diagnostic shader missing: " + name);
                }
            }
            LastReport.status = next + "-" + name;
            benchmark.StartBenchmark("system-load-" + LastReport.id + "-" + LastReport.status);
        }

        /// <summary>생산 설정을 되돌리고 측정 원본의 경로를 보존합니다.</summary>
        private static void Finish(string status)
        {
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= Cancel;
            LastReport.running = false;
            LastReport.status = status;
            if (benchmark != null)
            {
                if (benchmark.IsRunning)
                {
                    benchmark.Cancel();
                }
                benchmark.framesPerLeg = originalFrames;
            }
            if (pass != null)
            {
                pass.raymarchShader = originalShader;
                pass.enabled = originalEnabled;
                pass.windSpeed = originalWind;
            }
            if (clock != null)
            {
                clock.autoAdvance = originalAdvance;
                clock.SetTime(originalHour);
            }
            Time.timeScale = originalTimeScale;
            if (!string.IsNullOrEmpty(output))
            {
                File.WriteAllText(output, JsonUtility.ToJson(LastReport, true));
            }
        }

        /// <summary>사용자 취소 또는 도메인 재시작에서도 임시 상태를 반환합니다.</summary>
        public static void Cancel()
        {
            if (LastReport.running)
            {
                Finish("cancelled");
            }
        }

        /// <summary>RawFrameDataView의 직접 자식을 빼 CPU 자체 시간과 대기를 구분할 근거를 남깁니다.</summary>
        public static CpuCapture CaptureCpu()
        {
            CpuCapture capture = new CpuCapture();
            capture.lastFrame = ProfilerDriver.lastFrameIndex - 40;
            capture.firstFrame = Math.Max(ProfilerDriver.firstFrameIndex, capture.lastFrame - 59);
            Dictionary<string, CpuMarker> totals = new Dictionary<string, CpuMarker>();
            for (int frame = capture.firstFrame; frame <= capture.lastFrame; frame++)
            {
                for (int thread = 0; thread < 128; thread++)
                {
                    using (RawFrameDataView data = ProfilerDriver.GetRawFrameDataView(frame, thread))
                    {
                        if (!data.valid)
                        {
                            break;
                        }
                        if (data.threadName != "Main Thread" && data.threadName != "Render Thread")
                        {
                            continue;
                        }
                        if (data.threadName == "Main Thread")
                        {
                            capture.mainFrames++;
                            capture.frameGpuMs.Add(data.frameGpuTimeMs);
                        }
                        else
                        {
                            capture.renderFrames++;
                        }
                        for (int sample = 0; sample < data.sampleCount; sample++)
                        {
                            string name = data.GetSampleName(sample);
                            double inclusive = data.GetSampleTimeMs(sample);
                            double self = inclusive;
                            int child = sample + 1;
                            int children = data.GetSampleChildrenCount(sample);
                            for (int index = 0; index < children; index++)
                            {
                                self -= data.GetSampleTimeMs(child);
                                child += data.GetSampleChildrenCountRecursive(child) + 1;
                            }
                            string key = data.threadName + "/" + name;
                            if (!totals.TryGetValue(key, out CpuMarker marker))
                            {
                                marker = new CpuMarker();
                                marker.thread = data.threadName;
                                marker.name = name;
                                totals.Add(key, marker);
                            }
                            marker.calls++;
                            marker.inclusiveMs += inclusive;
                            marker.selfMs += Math.Max(0, self);
                        }
                    }
                }
            }
            capture.markers.AddRange(totals.Values);
            return capture;
        }

        /// <summary>측정 종료 뒤 임시 셰이더만 Unity AssetDatabase로 제거합니다.</summary>
        public static void Cleanup()
        {
            if (LastReport.running)
            {
                throw new InvalidOperationException("Cannot remove running diagnostic assets.");
            }
            for (int index = 1; index < Names.Length - 1; index++)
            {
                AssetDatabase.DeleteAsset(Root + "CloudLoad-" + Names[index] + ".compute");
            }
        }
    }
}
