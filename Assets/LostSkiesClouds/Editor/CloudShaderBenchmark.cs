using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>개발용 셰이더 변형을 동일 이동 벤치마크로 정순/역순 측정하여 변경 요소의 비용을 분리합니다.</summary>
    public static class CloudShaderBenchmark
    {
        public static bool Running { get; private set; }
        public static string LastResult { get; private set; }

        private static CloudBenchmark benchmark;
        private static LostSkiesCloudPass pass;
        private static CloudTimeOfDayController clock;
        private static ComputeShader[] shaders;
        private static string[] labels;
        private static int next;
        private static string id;
        private static readonly List<string> paths = new List<string>();
        private static float originalScale;
        private static float originalHour;
        private static bool originalAutoAdvance;
        private static ComputeShader originalShader;
        private static bool originalIntervals;

        /// <summary>전달한 셰이더 순서와 역순을 실행합니다. null 항목은 원래 셰이더의 빈 공간 옵션 OFF입니다.</summary>
        public static void Start(ComputeShader[] variants, string[] names)
        {
            if (Running || !Application.isPlaying || variants.Length != names.Length || variants.Length == 0)
            {
                throw new InvalidOperationException("Profiling requires Play and matching nonempty shader/label arrays.");
            }
            benchmark = UnityEngine.Object.FindFirstObjectByType<CloudBenchmark>();
            if (benchmark == null || benchmark.IsRunning)
            {
                throw new InvalidOperationException("Moving benchmark is missing or already running.");
            }
            foreach (CustomPass candidate in benchmark.GetComponent<CustomPassVolume>().customPasses)
            {
                pass = candidate as LostSkiesCloudPass;
                if (pass != null)
                {
                    break;
                }
            }
            if (pass == null)
            {
                throw new InvalidOperationException("Cloud pass is missing.");
            }

            clock = benchmark.GetComponent<CloudTimeOfDayController>();
            originalScale = Time.timeScale;
            originalShader = pass.raymarchShader;
            originalIntervals = pass.useRayIntervals;
            originalHour = clock.TimeOfDay;
            originalAutoAdvance = clock.autoAdvance;
            Time.timeScale = 0;
            clock.autoAdvance = false;
            clock.SetTime(12);
            shaders = variants;
            labels = names;
            next = 0;
            paths.Clear();
            id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            LastResult = "Running";
            Running = true;
            EditorApplication.update += Tick;
            StartCase();
        }

        /// <summary>측정 중에는 개입하지 않고 완료된 뒤에만 다음 설정으로 전환합니다.</summary>
        private static void Tick()
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
            paths.Add(benchmark.LastPath);
            if (benchmark.LastReport == null || benchmark.LastReport.status != "completed")
            {
                Finish("Interrupted");
                return;
            }
            next++;
            if (next >= shaders.Length * 2)
            {
                Finish("Completed");
                return;
            }
            StartCase();
        }

        /// <summary>실행 순서에 따른 변동을 확인하도록 후반은 역순으로 실행합니다.</summary>
        private static void StartCase()
        {
            int index = next;
            if (index >= shaders.Length)
            {
                index = shaders.Length * 2 - 1 - next;
            }
            pass.raymarchShader = shaders[index];
            pass.useRayIntervals = true;
            if (shaders[index] == null)
            {
                pass.raymarchShader = originalShader;
                pass.useRayIntervals = false;
            }
            benchmark.StartBenchmark("skip-profile-" + id + "-" + next + "-" + labels[index]);
        }

        /// <summary>이벤트 구독과 임시 시간/셰이더 소유권을 모든 종료 경로에서 반환합니다.</summary>
        private static void Finish(string result)
        {
            EditorApplication.update -= Tick;
            Running = false;
            LastResult = result;
            Time.timeScale = originalScale;
            if (pass != null)
            {
                pass.raymarchShader = originalShader;
                pass.useRayIntervals = originalIntervals;
            }
            if (clock != null)
            {
                clock.autoAdvance = originalAutoAdvance;
                clock.SetTime(originalHour);
            }
            Directory.CreateDirectory("Benchmarks");
            File.WriteAllText("Benchmarks/skip-profile-" + id + ".txt", result + "\n" + string.Join("\n", paths));
        }
    }
}
