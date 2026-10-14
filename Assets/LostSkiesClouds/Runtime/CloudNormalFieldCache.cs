using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    /// <summary>정확한 기존 차분 법선을 월드 격자에 준비하여 먼 진입점 사이에서 공유합니다.</summary>
    internal sealed class CloudNormalFieldCache : IDisposable
    {
        // HLSL 계약: 32³ 세 격자를 Z에 쌓습니다. RGB는 최종 단위 법선, A는 유효성입니다.
        // 위치·바람·날씨·형태가 변경되면 해당 입력으로 다시 생성하며 오래된 법선을 허용하지 않습니다.
        private const int Side = 32;
        private const int Cascades = 3;
        private const int CounterCount = 4;

        private readonly Vector4[] regions = new Vector4[Cascades];
        private readonly bool[] valid = new bool[Cascades];
        private readonly uint[] zeroCounters = new uint[CounterCount];
        private readonly ProfilingSampler buildSampler = new ProfilingSampler("Cloud.NormalFieldBuild");

        private RenderTexture texture;
        private Texture3D fallback;
        private ComputeBuffer counters;
        private Vector3 previousWind;
        private int previousShapeKey;
        private int previousNoisePreparation;
        private bool hasKey;
        private bool diagnosticsEnabled;

        public bool Active { get; private set; }

        public int BuildCount { get; private set; }

        public int AllocationCount { get; private set; }

        public string LastInvalidation { get; private set; }

        public long Bytes
        {
            get
            {
                if (texture == null)
                {
                    return 0;
                }

                return (long)Side * Side * Side * Cascades * 8;
            }
        }

        /// <summary>검사용 월드 정점 위치를 조회합니다. 반환 배열은 내부 주소와 공유하지 않습니다.</summary>
        public Vector4[] GetRegions()
        {
            return (Vector4[])regions.Clone();
        }

        /// <summary>현 입력과 다른 격자만 정확한 생산 법선으로 다시 채운 뒤 추적 커널에 연결합니다.</summary>
        public void Prepare(CommandBuffer commands, ComputeShader shader, CloudFormationProfile profile,
            Vector3 cameraPosition, Vector3 wind, int shapeKey, int noisePreparation, bool enabled,
            float firstCellSize, float exactNearDistance, float minimumCoherence, bool diagnostics, int traceKernel)
        {
            EnsureBindings();
            Active = false;
            shader.DisableKeyword("CLOUD_NORMAL_FIELD_CACHE");
            diagnosticsEnabled = diagnostics && enabled;
            commands.SetComputeIntParam(shader, "_CloudNormalFieldEnabled", 0);
            commands.SetComputeIntParam(shader, "_CloudNormalDiagnosticsEnabled", Convert.ToInt32(diagnosticsEnabled));
            if (diagnosticsEnabled)
            {
                commands.SetBufferData(counters, zeroCounters);
            }

            if (!enabled || profile == null || !profile.SupportsNormalFieldCache
                || !SystemInfo.supports3DRenderTextures
                || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            {
                ReleaseTexture();
                LastInvalidation = "Off or unsupported";
                BindKernel(commands, shader, traceKernel);
                return;
            }

            AllocateTexture();
            shader.EnableKeyword("CLOUD_NORMAL_FIELD_CACHE");
            bool inputChanged = !hasKey || previousShapeKey != shapeKey
                || previousNoisePreparation != noisePreparation || !previousWind.Equals(wind);
            LastInvalidation = "Reused exact input";
            if (inputChanged)
            {
                LastInvalidation = "Shape, weather, wind or noise changed";
            }

            float cellSize = Mathf.Clamp(firstCellSize, 64f, 1024f);
            int build = shader.FindKernel("BuildNormalFields");
            commands.SetComputeTextureParam(shader, build, "_CloudNormalFieldWrite", texture);
            for (int cascade = 0; cascade < Cascades; cascade++)
            {
                float snap = cellSize * 4f;
                Vector4 region = new Vector4(
                    Mathf.Floor(cameraPosition.x / snap) * snap - cellSize * Side * 0.5f,
                    Mathf.Floor(cameraPosition.y / snap) * snap - cellSize * Side * 0.5f,
                    Mathf.Floor(cameraPosition.z / snap) * snap - cellSize * Side * 0.5f,
                    cellSize);
                bool windowChanged = !regions[cascade].Equals(region);
                regions[cascade] = region;
                if (inputChanged || !valid[cascade] || windowChanged)
                {
                    commands.SetComputeVectorParam(shader, "_CloudNormalBuildRegion", region);
                    commands.SetComputeIntParam(shader, "_CloudNormalBuildCascade", cascade);
                    using (new ProfilingScope(commands, buildSampler))
                    {
                        commands.DispatchCompute(shader, build, Side / 4, Side / 4, Side / 4);
                    }

                    valid[cascade] = true;
                    BuildCount++;
                    if (!inputChanged && windowChanged)
                    {
                        LastInvalidation = "World grid window moved";
                    }
                }

                cellSize *= 4f;
            }

            previousWind = wind;
            previousShapeKey = shapeKey;
            previousNoisePreparation = noisePreparation;
            hasKey = true;
            float nearDistance = Mathf.Max(0f, exactNearDistance);
            commands.SetComputeVectorArrayParam(shader, "_CloudNormalRegions", regions);
            commands.SetComputeVectorParam(shader, "_CloudNormalSettings", new Vector4(nearDistance,
                nearDistance + Mathf.Max(500f, nearDistance * 0.5f), Mathf.Clamp(minimumCoherence, 0.9f, 0.99999f), 0f));
            commands.SetComputeIntParam(shader, "_CloudNormalFieldEnabled", 1);
            Active = true;
            BindKernel(commands, shader, traceKernel);
        }

        /// <summary>독립 옵션이 꺼져도 동적 셰이더 분기에 필요한 작은 유효 리소스를 제공합니다.</summary>
        private void EnsureBindings()
        {
            if (fallback == null)
            {
                fallback = new Texture3D(1, 1, 1, TextureFormat.RGBAHalf, false);
                fallback.name = "Cloud normal disabled binding";
                fallback.SetPixels(new Color[] { Color.clear });
                fallback.Apply(false, true);
            }

            if (counters == null)
            {
                counters = new ComputeBuffer(CounterCount, sizeof(uint));
                counters.SetData(zeroCounters);
            }
        }

        /// <summary>최대 786,432바이트의 법선 체적을 처음 활성화할 때 생성합니다.</summary>
        private void AllocateTexture()
        {
            if (texture != null)
            {
                return;
            }

            texture = new RenderTexture(Side, Side, 0, RenderTextureFormat.ARGBHalf);
            texture.name = "Cloud exact-producer normal field";
            texture.dimension = TextureDimension.Tex3D;
            texture.volumeDepth = Side * Cascades;
            texture.enableRandomWrite = true;
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            if (!texture.Create())
            {
                ReleaseTexture();
                throw new InvalidOperationException("Normal field texture creation failed.");
            }

            AllocationCount++;
        }

        /// <summary>직접/간접 광선 커널에 같은 법선 필드와 호출 계수를 연결합니다.</summary>
        public void BindKernel(CommandBuffer commands, ComputeShader shader, int kernel)
        {
            Texture source = fallback;
            if (Active)
            {
                source = texture;
            }

            commands.SetComputeTextureParam(shader, kernel, "_CloudNormalField", source);
            commands.SetComputeBufferParam(shader, kernel, "_CloudNormalCounters", counters);
        }

        /// <summary>검사 프로브가 현재 렌더와 동일한 유효 필드 및 월드 좌표를 읽도록 연결합니다.</summary>
        public void BindProbe(ComputeShader shader, int kernel)
        {
            EnsureBindings();
            Texture source = fallback;
            if (Active)
            {
                source = texture;
            }

            shader.SetTexture(kernel, "_CloudNormalField", source);
            shader.SetBuffer(kernel, "_CloudNormalCounters", counters);
            shader.SetVectorArray("_CloudNormalRegions", regions);
        }

        /// <summary>명시적인 진단 요청에서만 query/full/blend/exact의 마지막 렌더 계수를 읽습니다.</summary>
        public uint[] ReadDiagnostics()
        {
            uint[] result = new uint[CounterCount];
            if (diagnosticsEnabled && counters != null)
            {
                counters.GetData(result);
            }

            return result;
        }

        /// <summary>OFF일 때 큰 법선 텍스처를 해제하고 다음 활성화에 재생성을 요구합니다.</summary>
        private void ReleaseTexture()
        {
            CloudRenderTargets.Release(texture);
            texture = null;
            Array.Clear(valid, 0, valid.Length);
            hasKey = false;
            Active = false;
        }

        /// <summary>렌더러 소유의 법선 체적·작은 바인딩·계수·GPU 마커를 모두 반환합니다.</summary>
        public void Dispose()
        {
            ReleaseTexture();
            CloudRenderTargets.Release(fallback);
            fallback = null;
            if (counters != null)
            {
                counters.Release();
                counters = null;
            }

            buildSampler.Dispose();
        }
    }
}
