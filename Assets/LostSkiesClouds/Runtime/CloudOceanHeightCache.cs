using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    /// <summary>월드 XZ의 낮은 주파수 운해 필드를 보관합니다. 창 밖은 생산용 절차식으로 복귀합니다.</summary>
    internal sealed class CloudOceanHeightCache : IDisposable
    {
        private const int Side = 512;

        private RenderTexture texture;
        private bool valid;
        private Vector4 parameters;
        private Vector4 region;
        private bool halfPrecision;

        public int BuildCount { get; private set; }

        public bool Active { get; private set; }

        public long Bytes
        {
            get
            {
                if (texture == null)
                {
                    return 0;
                }

                int stride = 8;
                if (halfPrecision)
                {
                    stride = 4;
                }

                return (long)Side * Side * stride;
            }
        }

        /// <summary>지원되는 형식과 프로필일 때만 캐시를 연결하고 창·형태·정밀도 변경 시 갱신합니다.</summary>
        public void Prepare(CommandBuffer commands, ComputeShader shader, CloudFormationProfile profile,
            Vector3 position, Texture noise, bool enabled, float texelSize, bool useHalf)
        {
            Active = false;
            shader.DisableKeyword("CLOUD_OCEAN_HEIGHT_CACHE");
            RenderTextureFormat format = RenderTextureFormat.RGFloat;
            if (useHalf)
            {
                format = RenderTextureFormat.RGHalf;
            }

            if (!enabled || profile == null || !profile.TryGetOceanHeightParameters(out Vector4 nextParameters)
                || !SystemInfo.SupportsRenderTextureFormat(format) || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(format))
            {
                return;
            }

            texelSize = Mathf.Clamp(texelSize, 16f, 128f);
            float snap = texelSize * 64f;
            Vector4 nextRegion = new Vector4(Mathf.Floor(position.x / snap) * snap - texelSize * Side * 0.5f,
                Mathf.Floor(position.z / snap) * snap - texelSize * Side * 0.5f, texelSize, Side);
            if (texture == null || halfPrecision != useHalf)
            {
                Dispose();
                texture = new RenderTexture(Side, Side, 0, format);
                texture.name = "Cloud ocean height and peak mask";
                texture.enableRandomWrite = true;
                texture.filterMode = FilterMode.Bilinear;
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.Create();
                halfPrecision = useHalf;
            }

            commands.SetComputeVectorParam(shader, "_CloudOceanHeightRegion", nextRegion);
            if (!valid || !region.Equals(nextRegion) || !parameters.Equals(nextParameters))
            {
                int build = shader.FindKernel("BuildOceanHeight");
                commands.SetComputeTextureParam(shader, build, "_SculptNoise", noise);
                commands.SetComputeTextureParam(shader, build, "_CloudOceanHeightWrite", texture);
                commands.DispatchCompute(shader, build, Side / 8, Side / 8, 1);
                region = nextRegion;
                parameters = nextParameters;
                valid = true;
                BuildCount++;
            }

            shader.EnableKeyword("CLOUD_OCEAN_HEIGHT_CACHE");
            commands.SetComputeTextureParam(shader, shader.FindKernel("Raymarch"), "_CloudOceanHeightCache", texture);
            commands.SetComputeTextureParam(shader, shader.FindKernel("ProbeDensity"), "_CloudOceanHeightCache", texture);
            commands.SetComputeTextureParam(shader, shader.FindKernel("ProbeOceanHeight"), "_CloudOceanHeightCache", texture);
            Active = true;
        }

        /// <summary>즉시 실행하는 진단 프로브가 같은 캐시 텍스처를 읽도록 연결합니다.</summary>
        public void BindProbe(ComputeShader shader, int kernel)
        {
            if (Active && texture != null)
            {
                shader.SetTexture(kernel, "_CloudOceanHeightCache", texture);
            }
        }

        /// <summary>재구축 비용 검사 또는 외부 GPU 상태 복구를 위해 저장 내용만 무효화합니다.</summary>
        public void Invalidate()
        {
            valid = false;
        }

        /// <summary>생성한 RT를 해제하고 다음 사용 시 새 필드를 만들도록 무효화합니다.</summary>
        public void Dispose()
        {
            CloudRenderTargets.Release(texture);
            texture = null;
            valid = false;
            Active = false;
        }
    }
}
