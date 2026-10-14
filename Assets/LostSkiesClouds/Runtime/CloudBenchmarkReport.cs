using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace ClouDream.LostSkies
{
    /// <summary>원시 표본과 측정 조건을 함께 보관하여 다른 GPU/설정의 결과를 다시 분석할 수 있게 합니다.</summary>
    [Serializable]
    public sealed class CloudBenchmarkReport
    {
        public string version = "CloudRoute-v1";
        public string startedUtc;
        public string label;
        public string status;
        public string error;
        public string gpu;
        public string cpu;
        public string operatingSystem;
        public string unityVersion;
        public string graphicsApi;
        public int logicalCores;
        public int systemMemoryMiB;
        public int gpuMemoryMiB;
        public string scene;
        public string shaderDependencyHash;
        public string shaderSourceRole;

        // 노이즈 생성 비용이 아닌 보존한 원본 GPU 텍스처를 사용하는 경로입니다.
        public string noiseSource = CloudNoiseLibrary.SourceDescription;
        public string environmentSettings;
        public string buildGuid;
        public string buildManifest;
        public string routeMode = "fixed-pose";
        public float routeSecondsPerLeg;
        public float routeDaySeconds;
        public string routeLightingMode;
        public float flightMetresPerSecond;
        public float flightRotationDegreesPerSecond;
        public bool profilerEnabled;
        public bool gpuProfilerEnabled;
        public string gpuUtilizationSource;
        public bool editor;
        public bool developmentBuild;
        public int width;
        public int height;
        public int cloudWidth;
        public int cloudHeight;
        public int framesPerLeg;
        public float fieldOfView;
        public Vector3 routeCenter;
        public string passSettings;
        public string styleSettings;
        public string skySettings;
        public string oceanSettings;
        public float frozenGameTime;
        public float hour;
        public int originalVSync;
        public int originalTargetFrameRate;
        public string notes = "Uncapped; frozen game time; warmup traverses entire route. Fixed poses per frame, not fixed wall-clock speed. "
            + "CPU process percent normalized by logical cores. NVML is whole adapter, includes other apps. -1 means unavailable. "
            + "GPU marker and FrameTiming streams are delayed, not aligned to individual pose rows. FrameTiming GPU is not trusted as total GPU without duration sanity check. "
            + "Editor/Profiler/benchmark overhead included. No GPU target extrapolation.";
        public List<Frame> frames = new List<Frame>();
        public List<Timing> timings = new List<Timing>();
        public List<CloudBenchmarkHardware.Sample> hardware = new List<CloudBenchmarkHardware.Sample>();
        public List<double> cloudGpuMs = new List<double>();
        public List<double> rayGpuMs = new List<double>();
        public List<double> depthGpuMs = new List<double>();
        public List<double> compositeGpuMs = new List<double>();
        public List<MarkerStream> additionalGpuMarkers = new List<MarkerStream>();
        public string rendererDiagnostics;
        public bool diagnosticRun;
        public string[] cacheCounterNames =
        {
            "upper-body-queries", "upper-field-full-hits", "upper-field-exact-blends", "upper-exact-fallbacks",
            "occlusion-queries", "spatial-full-hits", "spatial-exact-blends", "spatial-exact-fallbacks",
            "ocean-body-queries", "ocean-field-full-hits", "ocean-field-exact-blends", "ocean-exact-fallbacks"
        };
        // 법선은 1/64 화면 픽셀의 호출 표본, 표면은 전체 build/consumer 호출 수로 서로 분모가 다릅니다.
        public string[] normalFieldCounterNames =
        {
            "normal-queries", "normal-field-full-hits", "normal-field-exact-blends", "normal-exact-fallbacks"
        };

        public string[] farSurfaceCounterNames = (string[])CloudFarSurface.CounterNames.Clone();

        public string diagnosticNotes = "Snapshots every 15 frames report the latest completed render. "
            + "Shape and normal cache counters sample 1/64 screen pixels; far-surface counters cover all build and consumer invocations. "
            + "Exact blends also execute procedural work. Counter readback/atomic overhead belongs only to diagnostic runs. "
            + "Optional build-marker means are per observed marker sample, not amortized over the whole route.";

        public List<DiagnosticFrame> diagnosticFrames = new List<DiagnosticFrame>();
        public Summary summary;

        [Serializable]
        public sealed class DiagnosticFrame
        {
            public int frame;
            public double routeSeconds;
            public string temporalStatus;
            public uint[] cacheCounts;
            public uint[] temporalCounts;

            // 별도 배열로 GPU 계약의 4개 법선 계수와 20개 표면 계수를 보존합니다.
            public uint[] normalFieldCounts;

            public uint[] farSurfaceCounts;

            public string farSurfaceStatus;
        }

        [Serializable]
        public sealed class MarkerStream
        {
            public string name;
            public List<double> milliseconds = new List<double>();
            public Statistics statistics;
        }

        [Serializable]
        public struct Statistics
        {
            public int samples;
            public double meanMs;
            public double medianMs;
            public double p95Ms;
        }

        [Serializable]
        public struct Frame
        {
            public int index;
            public int leg;
            public Vector3 densityPosition;
            public Quaternion rotation;
            public double wallMs;
            public long gcAllocatedBytes;
            public int gcCollections;
            public int drawCalls;
            public int triangles;
            public long activeCloudBytes;
            public long temporalBytes;
            public long spatialBytes;
            public long shapeFieldBytes;
            public long oceanShapeFieldBytes;

            public long normalFieldBytes;

            public long farSurfaceBytes;

            public int normalFieldBuilds;

            public int farSurfaceBuilds;

            public string farSurfaceStatus;
            public int shapeFieldBuilds;
            public int oceanShapeFieldBuilds;
            public int targetAllocations;
            public int shapeBuilds;
            public int temporalResets;
            public double routeSeconds;
            public float hour;
            public float simulationTime;
        }

        [Serializable]
        public struct Timing
        {
            public ulong timestamp;
            public double cpuFrameMs;
            public double mainThreadMs;
            public double renderThreadMs;
            public double presentWaitMs;
            public double gpuMs;
        }

        [Serializable]
        public struct Summary
        {
            public int frames;
            public double seconds;
            public double averageFps;
            public double minimumFps;
            public double onePercentLowFps;
            public double p50FrameMs;
            public double p95FrameMs;
            public double p99FrameMs;
            public double maxFrameMs;
            public int over16ms;
            public int over33ms;
            public double cloudGpuMeanMs;
            public double cloudGpuP95Ms;
            public double cloudGpuMedianMs;
            public double meanFrameMs;
            public Statistics rayGpu;
            public Statistics depthGpu;
            public Statistics compositeGpu;
            public Statistics frameTimingGpu;
            public Statistics cpuFrame;
            public double frameTimingGpuMeanMs;
            public double cpuFrameMeanMs;
            public double mainThreadMeanMs;
            public double renderThreadMeanMs;
            public double presentWaitMeanMs;
            public int cloudGpuSamples;
            public int frameTimingSamples;
            public bool frameGpuDurationSanity;
            public double processCpuMeanPercent;
            public double systemCpuMeanPercent;
            public double gpuMeanPercent;
            public long peakWorkingSetBytes;
            public long peakUnityAllocatedBytes;
            public long peakGpuUsedBytes;
        }

        /// <summary>FPS는 총 프레임/총 시간, 1% low는 느린 상위 1%의 평균 시간의 역수입니다.</summary>
        public void Summarize()
        {
            Summary result = new Summary();
            List<double> values = new List<double>(frames.Count);
            foreach (Frame frame in frames)
            {
                values.Add(frame.wallMs);
                result.seconds += frame.wallMs / 1000;
                if (frame.wallMs > 1000.0 / 60)
                {
                    result.over16ms++;
                }
                if (frame.wallMs > 1000.0 / 30)
                {
                    result.over33ms++;
                }
            }
            values.Sort();
            result.frames = frames.Count;
            if (values.Count > 0)
            {
                result.averageFps = frames.Count / result.seconds;
                result.maxFrameMs = values[values.Count - 1];
                result.minimumFps = 1000 / result.maxFrameMs;
                double slow = 0;
                int count = Math.Max(1, (int)Math.Ceiling(values.Count * 0.01));
                for (int index = values.Count - count; index < values.Count; index++)
                {
                    slow += values[index];
                }
                result.onePercentLowFps = 1000 * count / slow;
                result.p50FrameMs = Percentile(values, 0.5);
                result.p95FrameMs = Percentile(values, 0.95);
                result.p99FrameMs = Percentile(values, 0.99);
            }
            result.cloudGpuMeanMs = Mean(cloudGpuMs);
            List<double> gpuSorted = new List<double>(cloudGpuMs);
            gpuSorted.Sort();
            result.cloudGpuP95Ms = Percentile(gpuSorted, 0.95);
            result.cloudGpuMedianMs = Median(gpuSorted);
            result.meanFrameMs = Mean(values);
            result.rayGpu = Describe(rayGpuMs);
            result.depthGpu = Describe(depthGpuMs);
            result.compositeGpu = Describe(compositeGpuMs);
            foreach (MarkerStream marker in additionalGpuMarkers)
            {
                marker.statistics = Describe(marker.milliseconds);
            }
            List<double> gpuTimes = new List<double>();
            List<double> cpuTimes = new List<double>();
            List<double> mainTimes = new List<double>();
            List<double> renderTimes = new List<double>();
            double presentTotal = 0;
            foreach (Timing timing in timings)
            {
                gpuTimes.Add(timing.gpuMs);
                cpuTimes.Add(timing.cpuFrameMs);
                mainTimes.Add(timing.mainThreadMs);
                renderTimes.Add(timing.renderThreadMs);
                presentTotal += timing.presentWaitMs;
            }
            result.frameTimingGpuMeanMs = Mean(gpuTimes);
            result.frameTimingGpu = Describe(gpuTimes);
            result.cpuFrame = Describe(cpuTimes);
            result.cpuFrameMeanMs = Mean(cpuTimes);
            result.mainThreadMeanMs = Mean(mainTimes);
            result.renderThreadMeanMs = Mean(renderTimes);
            result.presentWaitMeanMs = -1;
            if (timings.Count > 0)
            {
                result.presentWaitMeanMs = presentTotal / timings.Count;
            }
            result.cloudGpuSamples = cloudGpuMs.Count;
            result.frameTimingSamples = timings.Count;
            result.frameGpuDurationSanity = result.cloudGpuMeanMs > 0 && result.frameTimingGpuMeanMs >= result.cloudGpuMeanMs;
            double processCpu = 0;
            double systemCpu = 0;
            double gpu = 0;
            double processWeight = 0;
            double systemWeight = 0;
            double gpuWeight = 0;
            result.peakGpuUsedBytes = -1;
            result.peakWorkingSetBytes = -1;
            result.peakUnityAllocatedBytes = -1;
            foreach (CloudBenchmarkHardware.Sample sample in hardware)
            {
                Accumulate(sample.processCpuPercent, sample.intervalSeconds, ref processCpu, ref processWeight);
                Accumulate(sample.systemCpuPercent, sample.intervalSeconds, ref systemCpu, ref systemWeight);
                Accumulate(sample.gpuPercent, sample.intervalSeconds, ref gpu, ref gpuWeight);
                result.peakWorkingSetBytes = Math.Max(result.peakWorkingSetBytes, sample.processWorkingSetBytes);
                result.peakUnityAllocatedBytes = Math.Max(result.peakUnityAllocatedBytes, sample.unityAllocatedBytes);
                result.peakGpuUsedBytes = Math.Max(result.peakGpuUsedBytes, sample.gpuUsedBytes);
            }
            result.processCpuMeanPercent = WeightedMean(processCpu, processWeight);
            result.systemCpuMeanPercent = WeightedMean(systemCpu, systemWeight);
            result.gpuMeanPercent = WeightedMean(gpu, gpuWeight);
            summary = result;
        }

        /// <summary>미지원 표본을 제외하고 실제 계측 간격으로 가중합니다.</summary>
        private static void Accumulate(double value, double seconds, ref double total, ref double weight)
        {
            if (value >= 0 && seconds > 0)
            {
                total += value * seconds;
                weight += seconds;
            }
        }

        /// <summary>유효 계측이 없을 때 0%로 오인하지 않도록 -1을 반환합니다.</summary>
        private static double WeightedMean(double total, double weight)
        {
            if (weight <= 0)
            {
                return -1;
            }
            return total / weight;
        }

        /// <summary>지원되지 않는 시간값을 제외하고 평균을 구합니다.</summary>
        public static double Mean(List<double> values)
        {
            double sum = 0;
            int count = 0;
            foreach (double value in values)
            {
                if (value > 0)
                {
                    sum += value;
                    count++;
                }
            }
            if (count == 0)
            {
                return -1;
            }
            return sum / count;
        }

        /// <summary>정렬된 시간값에서 nearest-rank 백분위수를 반환합니다.</summary>
        public static double Percentile(List<double> sorted, double fraction)
        {
            if (sorted.Count == 0)
            {
                return -1;
            }
            return sorted[Math.Max(0, (int)Math.Ceiling(sorted.Count * fraction) - 1)];
        }

        /// <summary>유효한 시간 표본의 평균·중앙값·p95를 같은 규칙으로 계산합니다.</summary>
        public static Statistics Describe(List<double> values)
        {
            List<double> sorted = new List<double>();
            foreach (double value in values)
            {
                if (value > 0 && !double.IsInfinity(value) && !double.IsNaN(value))
                {
                    sorted.Add(value);
                }
            }

            sorted.Sort();
            Statistics result = new Statistics();
            result.samples = sorted.Count;
            result.meanMs = Mean(sorted);
            result.medianMs = Median(sorted);
            result.p95Ms = Percentile(sorted, 0.95);
            return result;
        }

        /// <summary>짝수 표본은 가운데 두 값의 평균을 사용하며 미지원 표본은 -1로 표시합니다.</summary>
        private static double Median(List<double> sorted)
        {
            if (sorted.Count == 0)
            {
                return -1;
            }

            int middle = sorted.Count / 2;
            if (sorted.Count % 2 == 0)
            {
                return (sorted[middle - 1] + sorted[middle]) * 0.5;
            }

            return sorted[middle];
        }

        /// <summary>전체 JSON과 프레임 CSV를 측정 종료 후 한 번만 기록합니다.</summary>
        public string Save(string directory)
        {
            Directory.CreateDirectory(directory);
            string name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + label;
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }
            string path = Path.Combine(directory, name);
            File.WriteAllText(path + ".json", JsonUtility.ToJson(this, true));
            StringBuilder csv = new StringBuilder("frame,leg,x,y,z,wall_ms,gc_bytes,gc_collections,draw_calls,triangles,cloud_bytes,temporal_bytes,spatial_bytes,rt_allocations,shape_builds,history_resets,shape_field_bytes,ocean_field_bytes,shape_field_builds,ocean_field_builds,normal_field_bytes,far_surface_bytes,normal_field_builds,far_surface_builds\n");
            foreach (Frame frame in frames)
            {
                csv.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2:R},{3:R},{4:R},{5:R},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17},{18},{19},{20},{21},{22},{23}\n",
                    frame.index, frame.leg, frame.densityPosition.x, frame.densityPosition.y, frame.densityPosition.z,
                    frame.wallMs, frame.gcAllocatedBytes, frame.gcCollections, frame.drawCalls, frame.triangles,
                    frame.activeCloudBytes, frame.temporalBytes, frame.spatialBytes, frame.targetAllocations, frame.shapeBuilds, frame.temporalResets,
                    frame.shapeFieldBytes, frame.oceanShapeFieldBytes, frame.shapeFieldBuilds, frame.oceanShapeFieldBuilds,
                    frame.normalFieldBytes, frame.farSurfaceBytes, frame.normalFieldBuilds, frame.farSurfaceBuilds);
            }
            File.WriteAllText(path + ".csv", csv.ToString());
            return path + ".json";
        }
    }
}
