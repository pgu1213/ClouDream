using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    /// <summary>픽셀 사이에서 공유하는 태양·하늘 차폐량을 매 렌더의 현재 밀도로 계산합니다.</summary>
    internal sealed class CloudSpatialLightCache : IDisposable
    {
        // HLSL 계약: 64³ 격자 3개를 Z에 쌓습니다. RGFloat의 R/G는 태양/하늘 적분 밀도입니다.
        private const int Side = 64;
        private const int Cascades = 3;

        private readonly Vector4[] regions = new Vector4[Cascades];
        private readonly ProfilingSampler buildSampler = new ProfilingSampler("Cloud.SpatialLightBuild");
        private RenderTexture texture;

        public bool Active { get; private set; }

        public int BuildCount { get; private set; }

        public int AllocationCount { get; private set; }

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

        /// <summary>현재 밀도 값으로 모든 셀을 갱신하여 이전 날씨의 그림자가 남지 않게 합니다.</summary>
        public void Prepare(CommandBuffer commands, ComputeShader shader, Vector3 position, bool enabled, float cellSize)
        {
            Active = false;
            if (!enabled || !SystemInfo.supports3DRenderTextures
                || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.RGFloat))
            {
                // OFF에서는 실험용 GPU 메모리를 유지하지 않고 다음 ON에서 다시 할당합니다.
                ReleaseTexture();
                return;
            }

            if (texture == null)
            {
                texture = new RenderTexture(Side, Side, 0, RenderTextureFormat.RGFloat);
                texture.name = "Cloud shared sun and sky optical density";
                texture.dimension = TextureDimension.Tex3D;
                texture.volumeDepth = Side * Cascades;
                texture.enableRandomWrite = true;
                texture.filterMode = FilterMode.Bilinear;
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.Create();
                AllocationCount++;
            }

            cellSize = Mathf.Clamp(cellSize, 16f, 64f);
            for (int index = 0; index < Cascades; index++)
            {
                float snap = cellSize * 8f;
                regions[index] = new Vector4(Mathf.Floor(position.x / snap) * snap - cellSize * Side * 0.5f,
                    Mathf.Floor(position.y / snap) * snap - cellSize * Side * 0.5f,
                    Mathf.Floor(position.z / snap) * snap - cellSize * Side * 0.5f, cellSize);
                cellSize *= 2f;
            }

            commands.SetComputeVectorArrayParam(shader, "_CloudSpatialRegions", regions);
            int build = shader.FindKernel("BuildSpatialLighting");
            commands.SetComputeTextureParam(shader, build, "_CloudSpatialLightWrite", texture);
            using (new ProfilingScope(commands, buildSampler))
            {
                commands.DispatchCompute(shader, build, Side / 4, Side / 4, Side * Cascades / 4);
            }

            commands.SetComputeTextureParam(shader, shader.FindKernel("Raymarch"), "_CloudSpatialLight", texture);
            BuildCount++;
            Active = true;
        }

        /// <summary>옵션을 끌 때 RT의 Unity 오브젝트와 GPU 자원을 함께 반환합니다.</summary>
        private void ReleaseTexture()
        {
            CloudRenderTargets.Release(texture);
            texture = null;
            Active = false;
        }

        /// <summary>생산 경로와 같은 체적을 직접 실행하는 검사 프로브에 연결합니다.</summary>
        public void BindProbe(ComputeShader shader, int kernel)
        {
            if (Active && texture != null)
            {
                shader.SetVectorArray("_CloudSpatialRegions", regions);
                shader.SetTexture(kernel, "_CloudSpatialLight", texture);
            }
        }

        /// <summary>렌더러 수명이 끝나면 체적 텍스처와 계측 자원을 반환합니다.</summary>
        public void Dispose()
        {
            ReleaseTexture();
            buildSampler.Dispose();
        }
    }
}
