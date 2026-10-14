using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    /// <summary>현재 카메라와 차폐 조회에 필요한 연속 운해의 변위 전 몸체만 제한된 체적 격자에 저장합니다.</summary>
    internal sealed class CloudOceanShapeFieldCache : IDisposable
    {
        private const int Side = 96;
        private const int Height = 48;
        private const int Cascades = 4;

        private readonly Vector4[] regions = new Vector4[Cascades];
        private readonly bool[] valid = new bool[Cascades];
        private readonly bool[] dirty = new bool[Cascades];
        private readonly ProfilingSampler buildSampler = new ProfilingSampler("Cloud.OceanShapeFieldBuild");

        private RenderTexture texture;
        private Texture3D fallback;
        private Vector4 shapeKey;
        private Vector4 detailKey;
        private Vector2 boundsKey;
        private float seedKey;
        private Texture noiseKey;
        private bool hasKey;

        public bool Active { get; private set; }

        public int BuildCount { get; private set; }

        public long Bytes
        {
            get
            {
                if (texture == null)
                {
                    return 0;
                }

                return (long)Side * Height * Side * Cascades * 8;
            }
        }

        /// <summary>프레임마다 밀도를 굽지 않고 프로필·노이즈·창이 바뀐 캐스케이드만 갱신합니다.</summary>
        public void Prepare(CommandBuffer commands, ComputeShader shader, CloudFormationProfile style,
            CloudFormationProfile ocean, CloudFormationProfile sky, Vector3 position, Texture noise,
            bool enabled, bool shapeFieldKeywordActive, float exactNearDistance)
        {
            Active = false;
            commands.SetComputeIntParam(shader, "_CloudOceanShapeFieldEnabled", 0);
            if (!shapeFieldKeywordActive)
            {
                ReleaseTexture();
                return;
            }

            EnsureFallback();
            if (!enabled || style == null || ocean == null || sky == null || noise == null
                || !SystemInfo.supports3DRenderTextures
                || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBHalf)
                || !style.TryGetOceanShapeFieldParameters(out Vector4 shape, out Vector4 detail)
                || !ocean.TryGetOceanShapeFieldBounds(out Vector2 bounds)
                || !sky.TryGetSkyCellParameters(out Vector4 placement, out Vector4 altitude, out Vector4 skyShape))
            {
                ReleaseTexture();
                BindKnownKernels(commands, shader);
                return;
            }

            float spacing = Mathf.Max(3200f, shape.x * 0.22f);
            float maximumRadius = spacing * 0.31f;
            float top = bounds.x + bounds.y * 0.48f + shape.y + maximumRadius * 1.72f
                + 600f + shape.z + detail.y + 140f;
            float minimumY = bounds.x - 600f;
            float maximumY = top + 200f;
            if (maximumY - minimumY > 50000f || maximumRadius > 20000f)
            {
                ReleaseTexture();
                BindKnownKernels(commands, shader);
                return;
            }

            Vector4 nextShapeKey = new Vector4(shape.x, shape.y, shape.z, 0f);
            Vector4 nextDetailKey = new Vector4(0f, detail.y, detail.z, detail.w);
            Texture nextNoiseKey = noise;
            bool changed = !hasKey || !shapeKey.Equals(nextShapeKey) || !detailKey.Equals(nextDetailKey)
                || !boundsKey.Equals(bounds) || seedKey != placement.z || noiseKey != nextNoiseKey;
            AllocateTexture();
            float cellSize = 64f;
            for (int cascade = 0; cascade < Cascades; cascade++)
            {
                float snap = cellSize * 8f;
                Vector4 region = new Vector4(Mathf.Floor(position.x / snap) * snap - cellSize * Side * 0.5f,
                    minimumY, Mathf.Floor(position.z / snap) * snap - cellSize * Side * 0.5f, cellSize);
                dirty[cascade] = changed || !valid[cascade] || !regions[cascade].Equals(region);
                regions[cascade] = region;
                cellSize *= 3f;
            }

            shapeKey = nextShapeKey;
            detailKey = nextDetailKey;
            boundsKey = bounds;
            seedKey = placement.z;
            noiseKey = nextNoiseKey;
            hasKey = true;
            float nearDistance = Mathf.Max(0f, exactNearDistance);
            commands.SetComputeVectorArrayParam(shader, "_CloudOceanShapeRegions", regions);
            commands.SetComputeVectorParam(shader, "_CloudOceanShapeSettings", new Vector4(nearDistance,
                nearDistance + Mathf.Max(500f, nearDistance * 0.5f), (maximumY - minimumY) / (Height - 1), 0f));
            int build = shader.FindKernel("BuildOceanShapeFields");
            commands.SetComputeTextureParam(shader, build, "_SculptNoise", noise);
            commands.SetComputeTextureParam(shader, build, "_CloudOceanShapeFieldWrite", texture);
            for (int cascade = 0; cascade < Cascades; cascade++)
            {
                if (dirty[cascade])
                {
                    commands.SetComputeIntParam(shader, "_CloudOceanShapeBuildCascade", cascade);
                    using (new ProfilingScope(commands, buildSampler))
                    {
                        commands.DispatchCompute(shader, build, Side / 4, Height / 4, Side / 4);
                    }

                    valid[cascade] = true;
                    BuildCount++;
                }
            }

            Active = true;
            commands.SetComputeIntParam(shader, "_CloudOceanShapeFieldEnabled", 1);
            BindKnownKernels(commands, shader);
        }

        /// <summary>기능이 꺼진 필드 셰이더 변형에도 작은 유효 리소스를 연결합니다.</summary>
        private void EnsureFallback()
        {
            if (fallback != null)
            {
                return;
            }

            fallback = new Texture3D(1, 1, 1, TextureFormat.RGBAHalf, false);
            fallback.name = "Cloud ocean shape disabled binding";
            fallback.SetPixels(new Color[] { Color.clear });
            fallback.Apply(false, true);
        }

        /// <summary>96×48×384의 얇은 운해 체적을 처음 활성화할 때 할당합니다.</summary>
        private void AllocateTexture()
        {
            if (texture != null)
            {
                return;
            }

            texture = new RenderTexture(Side, Height, 0, RenderTextureFormat.ARGBHalf);
            texture.name = "Cloud camera-region ocean body fields";
            texture.dimension = TextureDimension.Tex3D;
            texture.volumeDepth = Side * Cascades;
            texture.enableRandomWrite = true;
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.Create();
        }

        /// <summary>일반 광선·시간 광선·프로브·차폐 생성이 같은 운해 필드를 읽게 합니다.</summary>
        private void BindKnownKernels(CommandBuffer commands, ComputeShader shader)
        {
            BindNamedKernel(commands, shader, "Raymarch");
            BindNamedKernel(commands, shader, "RaymarchTemporal");
            BindNamedKernel(commands, shader, "RaymarchTemporalCompact");
            BindNamedKernel(commands, shader, "ProbeDensity");
            BindNamedKernel(commands, shader, "ProbeSpatialLighting");
            BindNamedKernel(commands, shader, "BuildSpatialLighting");
        }

        /// <summary>선택적 추적 커널의 존재를 확인한 뒤 리소스를 연결합니다.</summary>
        private void BindNamedKernel(CommandBuffer commands, ComputeShader shader, string name)
        {
            if (shader.HasKernel(name))
            {
                BindKernel(commands, shader, shader.FindKernel(name));
            }
        }

        /// <summary>활성 필드 또는 비활성 바인딩을 현재 추적 커널에 연결합니다.</summary>
        public void BindKernel(CommandBuffer commands, ComputeShader shader, int kernel)
        {
            Texture source = fallback;
            if (Active)
            {
                source = texture;
            }

            if (source != null)
            {
                commands.SetComputeTextureParam(shader, kernel, "_CloudOceanShapeField", source);
            }
        }

        /// <summary>명시적인 검사용 밀도 조회도 현재 필드와 같은 리소스를 사용합니다.</summary>
        public void BindProbe(ComputeShader shader, int kernel)
        {
            Texture source = fallback;
            if (Active)
            {
                source = texture;
            }

            if (source != null)
            {
                shader.SetTexture(kernel, "_CloudOceanShapeField", source);
            }
        }

        /// <summary>체적 필드가 비활성일 때 GPU 메모리를 반환합니다.</summary>
        private void ReleaseTexture()
        {
            CloudRenderTargets.Release(texture);
            texture = null;
            Array.Clear(valid, 0, valid.Length);
            hasKey = false;
            Active = false;
        }

        /// <summary>체적 필드와 작은 비활성 바인딩을 렌더러 수명에 맞춰 반환합니다.</summary>
        public void Dispose()
        {
            ReleaseTexture();
            if (fallback != null)
            {
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(fallback);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(fallback);
                }

                fallback = null;
            }

            buildSampler.Dispose();
        }
    }
}
