using System;
using System.Globalization;
using UnityEngine;

namespace ClouDream.LostSkies
{
    /// <summary>소스가 없는 추출 셰이더 대신 Editor에서 검증하여 저장한 동일한 GPU 노이즈 텍스처를 제공합니다.</summary>
    public static class CloudNoiseLibrary
    {
        // 원래 GPU 출력을 새 노이즈 식으로 교체하지 않고 저장한 RGBAHalf 전체 mip를 공유합니다.
        public const string SourceDescription = "Preserved original CloudGenerator GPU output; shared Resources Texture3D RGBAHalf with original mip levels.";

        /// <summary>기존 여섯 설정을 모두 읽고 형식·크기·샘플러 계약을 검사합니다. GPU readback은 수행하지 않습니다.</summary>
        public static Texture3D[] LoadAll()
        {
            return new Texture3D[]
            {
                Load("WORLEY3D", 128, 4f, 6, 0.55f),
                Load("PERLIN3D", 64, 12f, 4, 0.75f),
                Load("WORLEY3D", 64, 12f, 5, 0.75f),
                Load("PERLIN3D", 64, 16f, 4, 0.5f),
                Load("CURL3D", 32, 8f, 3, 0.3f),
                Load("WORLEY3D", 128, 4f, 1, 0.5f)
            };
        }

        /// <summary>생성 입력 전체를 포함하고 운영체제의 소수점 표기와 무관한 리소스 키를 만듭니다.</summary>
        public static string ResourceKey(string kernelName, int size, float scale, int octaves, float persistence)
        {
            CultureInfo culture = CultureInfo.InvariantCulture;
            return "CloudNoise/" + kernelName + "_" + size.ToString(culture)
                + "_" + scale.ToString("R", culture) + "_" + octaves.ToString(culture)
                + "_" + persistence.ToString("R", culture);
        }

        /// <summary>공유 에셋을 반환합니다. 호출자는 소유 오브젝트 목록에 추가하거나 직접 파괴하지 않습니다.</summary>
        public static Texture3D Load(string kernelName, int size, float scale, int octaves, float persistence)
        {
            string key = ResourceKey(kernelName, size, scale, octaves, persistence);
            Texture3D texture = Resources.Load<Texture3D>(key);
            int mipCount = 1;
            for (int dimension = size; dimension > 1; dimension /= 2)
            {
                mipCount++;
            }

            if (texture == null || texture.width != size || texture.height != size || texture.depth != size
                || texture.format != TextureFormat.RGBAHalf || texture.mipmapCount != mipCount
                || texture.wrapModeU != TextureWrapMode.Repeat || texture.wrapModeV != TextureWrapMode.Repeat
                || texture.wrapModeW != TextureWrapMode.Repeat || texture.filterMode != FilterMode.Trilinear)
            {
                throw new InvalidOperationException("Verified cloud noise is unavailable: " + key
                    + ". Restore the verified CloudNoise Resources assets and compare them with Docs/Validation/CloudNoise-Bake.json.");
            }

            return texture;
        }
    }
}
