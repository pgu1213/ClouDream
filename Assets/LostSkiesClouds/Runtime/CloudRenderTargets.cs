using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    /// <summary>한 카메라의 출력 버퍼를 보관하여 Game/Scene 뷰 전환에 의한 재할당을 줄입니다.</summary>
    internal sealed class CloudRenderTargets : IDisposable
    {
        public RenderTexture lighting;
        public RenderTexture transmittance;
        public RenderTexture depth;
        public int width;

        public int height;
        public int lastUsed;
        public int allocationCount;

        public bool compact;

        /// <summary>현재 장치가 축소 형식의 렌더·UAV 접근을 모두 지원하는지 확인합니다.</summary>
        public static bool SupportsCompactTargets()
        {
            return SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGHalf)
                && SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.RGHalf)
                && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat);
        }

        /// <summary>출력 해상도가 바뀐 경우에만 8픽셀 단위로 버퍼를 다시 만듭니다.</summary>
        public void Resize(int requestedWidth, int requestedHeight, bool useCompactTargets)
        {
            bool nextCompact = useCompactTargets && SupportsCompactTargets();
            int nextWidth = Mathf.Max(8, (requestedWidth + 7) / 8 * 8);
            int nextHeight = Mathf.Max(8, (requestedHeight + 7) / 8 * 8);
            if (width == nextWidth && height == nextHeight && compact == nextCompact)
            {
                return;
            }

            Dispose();
            width = nextWidth;
            height = nextHeight;
            compact = nextCompact;
            RenderTextureFormat transmissionFormat = RenderTextureFormat.ARGBHalf;
            RenderTextureFormat depthFormat = RenderTextureFormat.ARGBHalf;
            if (compact)
            {
                transmissionFormat = RenderTextureFormat.RGHalf;
                depthFormat = RenderTextureFormat.RFloat;
            }

            lighting = CreateTarget("Cloud lighting", RenderTextureFormat.ARGBHalf, true);
            transmittance = CreateTarget("Cloud transmittance", transmissionFormat, true);
            depth = CreateTarget("Cloud scene depth", depthFormat, false);
            allocationCount++;
            RenderTexture previous = RenderTexture.active;
            Graphics.SetRenderTarget(depth, 0, CubemapFace.Unknown, 0);
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = previous;
        }

        /// <summary>레이마칭과 깊이 복사에서 공유하는 배열 형식의 RT를 만듭니다.</summary>
        private RenderTexture CreateTarget(string targetName, RenderTextureFormat format, bool randomWrite)
        {
            RenderTexture texture = new RenderTexture(width, height, 0, format);
            texture.dimension = TextureDimension.Tex2DArray;
            texture.volumeDepth = 1;
            texture.enableRandomWrite = randomWrite;
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.name = targetName;
            texture.Create();
            return texture;
        }

        /// <summary>렌더 타깃의 GPU 메모리와 Unity 오브젝트를 함께 해제합니다.</summary>
        public void Dispose()
        {
            Release(lighting);
            Release(transmittance);
            Release(depth);
            lighting = null;
            transmittance = null;
            depth = null;
            width = 0;
            height = 0;
        }

        /// <summary>편집/실행 모드에 맞는 Unity 오브젝트 수명 관리로 자원을 해제합니다.</summary>
        public static void Release(UnityEngine.Object resource)
        {
            if (resource == null)
            {
                return;
            }

            RenderTexture texture = resource as RenderTexture;
            if (texture != null)
            {
                texture.Release();
            }

            CoreUtils.Destroy(resource);
        }
    }
}
