using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

namespace ClouDream.LostSkies
{
    /// <summary>벤치마크 중에만 1초 간격으로 읽는 프로세스/Windows/NVIDIA 계측입니다.</summary>
    public sealed class CloudBenchmarkHardware : IDisposable
    {
        [Serializable]
        public struct Sample
        {
            // -1은 미지원/실패입니다. GPU 비율과 VRAM은 다른 앱을 포함한 어댑터 전체 값입니다.
            public double seconds;
            public double intervalSeconds;
            public double processCpuPercent;
            public double systemCpuPercent;
            public double gpuPercent;
            public double gpuMemoryBusyPercent;
            public double gpuClockMHz;
            public double gpuTemperatureC;
            public double gpuPowerWatts;
            public int gpuPerformanceState;
            public long processWorkingSetBytes;
            public long processPrivateBytes;
            public long unityAllocatedBytes;
            public long unityReservedBytes;
            public long managedUsedBytes;
            public long gpuUsedBytes;
            public double pollMilliseconds;
        }

        public string GpuStatus { get; private set; } = "NVML unavailable";

        private readonly Process process;
        private double previousSeconds;
        private double previousCpuSeconds;
        private ulong previousIdle;
        private ulong previousTotal;
        private bool systemReady;
        private bool nvmlInitialized;
        private IntPtr gpu;

        // NVML C ABI: unsigned int 두 개, unsigned long long 세 개입니다.
        [StructLayout(LayoutKind.Sequential)]
        private struct Utilization
        {
            public uint gpu;
            public uint memory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Memory
        {
            public ulong total;
            public ulong free;
            public ulong used;
        }

        // PROCESS_MEMORY_COUNTERS_EX: SIZE_T는 프로세스 비트 수와 같은 크기입니다.
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemory
        {
            public uint size;
            public uint pageFaults;
            public UIntPtr peakWorkingSet;
            public UIntPtr workingSet;
            public UIntPtr peakPagedPool;
            public UIntPtr pagedPool;
            public UIntPtr peakNonPagedPool;
            public UIntPtr nonPagedPool;
            public UIntPtr pageFile;
            public UIntPtr peakPageFile;
            public UIntPtr privateBytes;
        }

        /// <summary>닫을 필요가 없는 현재 프로세스의 의사 핸들을 얻습니다.</summary>
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        /// <summary>Unity Mono에서 Process 메모리가 0인 경우에도 실제 Windows RAM/커밋 사용량을 읽습니다.</summary>
        [DllImport("psapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemory memory, uint size);

        /// <summary>Windows FILETIME 값을 부호 없는 64비트 틱으로 읽습니다.</summary>
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

        /// <summary>드라이버가 제공하는 NVML 세션을 엽니다.</summary>
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlInit_v2();

        /// <summary>이 계측기가 획득한 NVML 세션 참조를 반환합니다.</summary>
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlShutdown();

        /// <summary>드라이버에 연결된 어댑터 수를 읽습니다.</summary>
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetCount_v2(out uint count);

        /// <summary>어댑터 인덱스를 NVML 핸들로 변환합니다.</summary>
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

        /// <summary>Unity 렌더 장치와 비교할 어댑터 이름을 읽습니다.</summary>
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);

        /// <summary>어댑터 전체의 GPU 실행/메모리 작업 비율을 읽습니다.</summary>
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);

        /// <summary>어댑터 전체의 점유 VRAM을 읽습니다. 메모리 busy 비율과 구분합니다.</summary>
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory memory);

        /// <summary>GPU 실행 클럭을 읽어 같은 설정의 성능 변동을 진단합니다.</summary>
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetClockInfo(IntPtr device, uint clockType, out uint clock);

        /// <summary>GPU 센서 온도를 섭씨로 읽습니다.</summary>
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint temperature);

        /// <summary>어댑터의 전력을 밀리와트로 읽습니다.</summary>
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint power);

        /// <summary>P0 등 드라이버 성능 상태 번호를 읽습니다.</summary>
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetPerformanceState(IntPtr device, out uint state);

        /// <summary>기준 CPU 틱을 저장하고 동일 이름 GPU가 하나일 때만 NVML 계측을 연결합니다.</summary>
        public CloudBenchmarkHardware(double seconds)
        {
            process = Process.GetCurrentProcess();
            previousSeconds = seconds;
            previousCpuSeconds = process.TotalProcessorTime.TotalSeconds;
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            systemReady = GetSystemTimes(out previousIdle, out ulong kernel, out ulong user);
            previousTotal = kernel + user;
            try
            {
                if (nvmlInit_v2() != 0)
                {
                    return;
                }
                nvmlInitialized = true;
                if (nvmlDeviceGetCount_v2(out uint count) != 0)
                {
                    return;
                }
                int matches = 0;
                for (uint index = 0; index < count; index++)
                {
                    if (nvmlDeviceGetHandleByIndex_v2(index, out IntPtr candidate) != 0)
                    {
                        continue;
                    }
                    StringBuilder name = new StringBuilder(128);
                    if (nvmlDeviceGetName(candidate, name, 128) == 0 && name.ToString() == SystemInfo.graphicsDeviceName)
                    {
                        gpu = candidate;
                        matches++;
                    }
                }
                if (matches != 1)
                {
                    gpu = IntPtr.Zero;
                    GpuStatus = "NVML device match missing or ambiguous";
                }
                else
                {
                    GpuStatus = "NVML adapter-wide: " + SystemInfo.graphicsDeviceName;
                }
            }
            catch (DllNotFoundException)
            {
                GpuStatus = "NVML driver library not installed";
            }
            catch (EntryPointNotFoundException)
            {
                GpuStatus = "NVML entry point unavailable";
            }
            catch (BadImageFormatException)
            {
                GpuStatus = "NVML architecture mismatch";
            }
#endif
        }

        /// <summary>실제 경과 시간으로 CPU 비율을 정규화합니다. Unity 프로세스 비율의 100%는 모든 논리 코어입니다.</summary>
        public Sample Read(double seconds)
        {
            double started = Time.realtimeSinceStartupAsDouble;
            Sample sample = new Sample();
            sample.seconds = seconds;
            sample.intervalSeconds = seconds - previousSeconds;
            sample.systemCpuPercent = -1;
            sample.gpuPercent = -1;
            sample.gpuMemoryBusyPercent = -1;
            sample.gpuUsedBytes = -1;
            sample.gpuClockMHz = -1;
            sample.gpuTemperatureC = -1;
            sample.gpuPowerWatts = -1;
            sample.gpuPerformanceState = -1;
            process.Refresh();
            double cpu = process.TotalProcessorTime.TotalSeconds;
            sample.processCpuPercent = Math.Max(0, Math.Min(100, (cpu - previousCpuSeconds)
                / Math.Max(0.001, sample.intervalSeconds) / Environment.ProcessorCount * 100));
            previousCpuSeconds = cpu;
            previousSeconds = seconds;
            sample.processWorkingSetBytes = process.WorkingSet64;
            sample.processPrivateBytes = process.PrivateMemorySize64;
            if (sample.processWorkingSetBytes <= 0)
            {
                sample.processWorkingSetBytes = -1;
            }
            if (sample.processPrivateBytes <= 0)
            {
                sample.processPrivateBytes = -1;
            }
            sample.unityAllocatedBytes = Profiler.GetTotalAllocatedMemoryLong();
            sample.unityReservedBytes = Profiler.GetTotalReservedMemoryLong();
            sample.managedUsedBytes = Profiler.GetMonoUsedSizeLong();
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (GetProcessMemoryInfo(GetCurrentProcess(), out ProcessMemory processMemory, (uint)Marshal.SizeOf<ProcessMemory>()))
            {
                sample.processWorkingSetBytes = (long)processMemory.workingSet.ToUInt64();
                sample.processPrivateBytes = (long)processMemory.privateBytes.ToUInt64();
            }
            if (GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
            {
                ulong total = kernel + user;
                if (systemReady && total > previousTotal)
                {
                    sample.systemCpuPercent = 100.0 * (1.0 - (double)(idle - previousIdle) / (total - previousTotal));
                }
                previousIdle = idle;
                previousTotal = total;
                systemReady = true;
            }
            if (gpu != IntPtr.Zero)
            {
                if (nvmlDeviceGetUtilizationRates(gpu, out Utilization utilization) == 0)
                {
                    sample.gpuPercent = utilization.gpu;
                    sample.gpuMemoryBusyPercent = utilization.memory;
                }
                if (nvmlDeviceGetMemoryInfo(gpu, out Memory memory) == 0)
                {
                    sample.gpuUsedBytes = (long)memory.used;
                }
                if (nvmlDeviceGetClockInfo(gpu, 0, out uint clock) == 0)
                {
                    sample.gpuClockMHz = clock;
                }
                if (nvmlDeviceGetTemperature(gpu, 0, out uint temperature) == 0)
                {
                    sample.gpuTemperatureC = temperature;
                }
                if (nvmlDeviceGetPowerUsage(gpu, out uint power) == 0)
                {
                    sample.gpuPowerWatts = power / 1000d;
                }
                if (nvmlDeviceGetPerformanceState(gpu, out uint state) == 0)
                {
                    sample.gpuPerformanceState = (int)state;
                }
            }
#endif
            sample.pollMilliseconds = (Time.realtimeSinceStartupAsDouble - started) * 1000;
            return sample;
        }

        /// <summary>네이티브/프로세스 핸들을 반환합니다. 벤치마크 외 시간에는 폴링하지 않습니다.</summary>
        public void Dispose()
        {
            process.Dispose();
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (nvmlInitialized)
            {
                nvmlShutdown();
                nvmlInitialized = false;
            }
#endif
        }
    }
}
