using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    /// <summary>카메라별 현재 프레임의 원경 불투명 표면을 보관하는 실험용 GPU 자원입니다.</summary>
    internal sealed class CloudFarSurface : IDisposable
    {
        public const int CounterCount = 20;

        public static readonly string[] CounterNames =
        {
            "build-rays", "build-opaque-surfaces", "build-near-or-inside", "build-thin-or-separated",
            "build-no-opaque-or-exhausted", "build-invalid", "build-direct-density-calls",
            "build-boundary-normal-requests", "build-occlusion-requests", "consumer-first-entry-attempts",
            "consumer-surface-accepted", "consumer-invalid-proxy", "consumer-discontinuity",
            "consumer-scene-depth", "consumer-entry-mismatch", "consumer-thin-or-separated",
            "consumer-near-or-prior-volume", "consumer-guard-density-calls", "consumer-surface-shading",
            "consumer-volume-fallback"
        };

        private RenderTexture geometry;

        private RenderTexture normalHit;

        private RenderTexture occlusion;

        private RenderTexture usage;

        private ComputeBuffer diagnostics;

        private readonly uint[] zeros = new uint[CounterCount];

        private ProfilingSampler buildSampler;

        private int width;

        private int height;

        private bool collectDiagnostics;

        public bool Active { get; private set; }

        public int Builds { get; private set; }

        public int Allocations { get; private set; }

        public string Status { get; private set; } = "Off";

        /// <summary>ARGBFloat 표면 3장과 uint 진단 20개의 실제 할당 바이트 수입니다.</summary>
        public long Bytes
        {
            get
            {
                if (geometry == null)
                {
                    return 0;
                }

                return (long)width * height * 48 + (long)usage.width * usage.height * 4 + CounterCount * sizeof(uint);
            }
        }

        /// <summary>진단 실행에만 저장하는 성공 픽셀 마스크이며 시간 재사용 픽셀은 0입니다.</summary>
        public RenderTexture Usage
        {
            get
            {
                if (!Active || !collectDiagnostics)
                {
                    return null;
                }

                return usage;
            }
        }

        /// <summary>원경 표면용 float32 UAV 지원 여부를 확인합니다.</summary>
        public static bool Supported()
        {
            return SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat)
                && SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBFloat)
                && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat)
                && SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.RFloat);
        }

        /// <summary>기능/호환성/해상도에 맞춰 자원을 준비하며 OFF에서는 큰 자원을 모두 반환합니다.</summary>
        public void Prepare(CommandBuffer commands, ComputeShader shader, int cloudWidth, int cloudHeight,
            bool enabled, bool compatible, bool diagnosticsEnabled, float nearDistance = 12000f,
            float minimumThickness = 120f, float maximumThickness = 1600f, float entryTolerance = 80f)
        {
            Active = false;
            collectDiagnostics = false;
            shader.DisableKeyword("CLOUD_FAR_SURFACE");
            if (!enabled || !compatible || !shader.HasKernel("BuildFarSurface") || !Supported())
            {
                ReleaseTargets();
                Status = "Off";
                if (enabled)
                {
                    Status = "Unsupported combination; volume fallback";
                }

                return;
            }

            int nextWidth = Mathf.Max(1, (cloudWidth + 1) / 2);
            int nextHeight = Mathf.Max(1, (cloudHeight + 1) / 2);
            int usageWidth = 1;
            int usageHeight = 1;
            if (diagnosticsEnabled)
            {
                usageWidth = cloudWidth;
                usageHeight = cloudHeight;
            }

            if (nextWidth != width || nextHeight != height || geometry == null || usage == null
                || usage.width != usageWidth || usage.height != usageHeight)
            {
                ReleaseTargets();
                width = nextWidth;
                height = nextHeight;
                geometry = CreateTarget("Cloud far surface geometry");
                normalHit = CreateTarget("Cloud far surface normal and threshold depth");
                occlusion = CreateTarget("Cloud far surface occlusion");
                usage = new RenderTexture(usageWidth, usageHeight, 0, RenderTextureFormat.RFloat);
                usage.enableRandomWrite = true;
                usage.filterMode = FilterMode.Point;
                usage.wrapMode = TextureWrapMode.Clamp;
                usage.name = "Cloud far surface diagnostic usage";
                usage.Create();
                diagnostics = new ComputeBuffer(CounterCount, sizeof(uint));
                diagnostics.SetData(zeros);
                Allocations++;
            }

            if (buildSampler == null)
            {
                buildSampler = new ProfilingSampler("Cloud.FarSurfaceBuild");
            }

            shader.EnableKeyword("CLOUD_FAR_SURFACE");
            Active = true;
            collectDiagnostics = diagnosticsEnabled;
            Status = "Rebuilding current-frame surface";
            commands.SetComputeIntParam(shader, "_CloudFarSurfaceEnabled", 1);
            int counterEnabled = 0;
            if (collectDiagnostics)
            {
                counterEnabled = 1;
                commands.SetBufferData(diagnostics, zeros);
            }

            commands.SetComputeIntParam(shader, "_CloudFarSurfaceDiagnostics", counterEnabled);
            commands.SetComputeVectorParam(shader, "_CloudFarSurfaceSize",
                new Vector4(width, height, 1f / width, 1f / height));
            commands.SetComputeVectorParam(shader, "_CloudFarSurfaceLimits",
                new Vector4(Mathf.Max(3000f, nearDistance), Mathf.Max(60f, minimumThickness),
                    Mathf.Max(minimumThickness, maximumThickness), Mathf.Max(1f, entryTolerance)));
        }

        /// <summary>같은 프레임의 표면 생성과 주 광선에 동일한 GPU 자원을 연결합니다.</summary>
        public void BindKernel(CommandBuffer commands, ComputeShader shader, int kernel, bool writer)
        {
            if (!Active)
            {
                return;
            }

            commands.SetComputeBufferParam(shader, kernel, "_CloudFarSurfaceCounters", diagnostics);
            commands.SetComputeTextureParam(shader, kernel, "_CloudFarSurfaceUsage", usage);
            if (writer)
            {
                commands.SetComputeTextureParam(shader, kernel, "_CloudFarSurfaceGeometryWrite", geometry);
                commands.SetComputeTextureParam(shader, kernel, "_CloudFarSurfaceNormalWrite", normalHit);
                commands.SetComputeTextureParam(shader, kernel, "_CloudFarSurfaceOcclusionWrite", occlusion);
                return;
            }

            commands.SetComputeTextureParam(shader, kernel, "_CloudFarSurfaceGeometry", geometry);
            commands.SetComputeTextureParam(shader, kernel, "_CloudFarSurfaceNormal", normalHit);
            commands.SetComputeTextureParam(shader, kernel, "_CloudFarSurfaceOcclusion", occlusion);
        }

        /// <summary>매 렌더 모든 texel을 갱신하므로 카메라/날씨/바람/원점 이력을 재사용하지 않습니다.</summary>
        public void DispatchBuild(CommandBuffer commands, ComputeShader shader, int kernel)
        {
            if (!Active)
            {
                return;
            }

            using (new ProfilingScope(commands, buildSampler))
            {
                commands.DispatchCompute(shader, kernel, (width + 7) / 8, (height + 7) / 8, 1);
            }

            Builds++;
        }

        /// <summary>명시적 진단 실행에서만 직전 완료 렌더 카운터를 동기 읽습니다.</summary>
        public uint[] ReadDiagnostics()
        {
            uint[] result = new uint[CounterCount];
            if (Active && collectDiagnostics && diagnostics != null)
            {
                diagnostics.GetData(result);
            }

            return result;
        }

        /// <summary>필터링 없는 float32 2D 표면 타깃을 만듭니다.</summary>
        private RenderTexture CreateTarget(string targetName)
        {
            RenderTexture target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat);
            target.dimension = TextureDimension.Tex2D;
            target.enableRandomWrite = true;
            target.filterMode = FilterMode.Point;
            target.wrapMode = TextureWrapMode.Clamp;
            target.name = targetName;
            target.Create();
            return target;
        }

        /// <summary>설정 OFF, 출력 재할당, 카메라 퇴출 때 할당된 표면과 카운터를 반환합니다.</summary>
        private void ReleaseTargets()
        {
            CloudRenderTargets.Release(geometry);
            CloudRenderTargets.Release(normalHit);
            CloudRenderTargets.Release(occlusion);
            CloudRenderTargets.Release(usage);
            geometry = null;
            normalHit = null;
            occlusion = null;
            usage = null;
            if (diagnostics != null)
            {
                diagnostics.Release();
                diagnostics = null;
            }

            width = 0;
            height = 0;
            Active = false;
            collectDiagnostics = false;
        }

        /// <summary>소유 카메라가 종료될 때 GPU 자원과 계측 오브젝트를 해제합니다.</summary>
        public void Dispose()
        {
            ReleaseTargets();
            if (buildSampler != null)
            {
                buildSampler.Dispose();
                buildSampler = null;
            }

            Status = "Off";
        }
    }
}
