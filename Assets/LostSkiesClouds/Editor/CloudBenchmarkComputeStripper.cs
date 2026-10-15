using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>전용 벤치마크 빌드만 명시된 프리셋의 키워드 조합으로 제한합니다.</summary>
    public sealed class CloudBenchmarkComputeStripper : IPreprocessComputeShaders
    {
        public static bool Enabled;

        // 전용 확장 빌드에서만 사용합니다. 일반 benchmark/UI용 빌드는 기존 유효 조합을 보존합니다.
        public static bool ExtensionOnly;

        // 독립 하이브리드 비교 Player는 50% + compact + distance 품질의 기준 경로만 사용합니다.
        public static bool HybridOnly;

        public int callbackOrder
        {
            get { return 100; }
        }

        /// <summary>벤치마크에서 사용하지 않거나 런타임상 도달할 수 없는 키워드 조합만 제외합니다.</summary>
        public void OnProcessComputeShader(ComputeShader shader, string kernelName, IList<ShaderCompilerData> data)
        {
            if (!Enabled || shader.name != "CloudRaymarch")
            {
                return;
            }

            ShaderKeyword compact = new ShaderKeyword("CLOUD_COMPACT_TARGETS");
            ShaderKeyword height = new ShaderKeyword("CLOUD_OCEAN_HEIGHT_CACHE");
            ShaderKeyword palette = new ShaderKeyword("CLOUD_PALETTE_LIGHTING_FAST_PATH");
            ShaderKeyword density = new ShaderKeyword("CLOUD_DISTANCE_DENSITY");
            ShaderKeyword lighting = new ShaderKeyword("CLOUD_DISTANCE_LIGHTING");
            ShaderKeyword farSurface = new ShaderKeyword("CLOUD_FAR_SURFACE");
            ShaderKeyword normalField = new ShaderKeyword("CLOUD_NORMAL_FIELD_CACHE");
            ShaderKeyword shapeField = new ShaderKeyword("CLOUD_SHAPE_FIELD_CACHE");
            ShaderKeyword spatialLight = new ShaderKeyword("CLOUD_SPATIAL_LIGHT_CACHE");
            ShaderKeyword traversal = new ShaderKeyword("CLOUD_RAY_INTERVALS");
            for (int index = data.Count - 1; index >= 0; index--)
            {
                ShaderKeywordSet keywords = data[index].shaderKeywordSet;
                // 렌더러는 이 조합에서 표면 키워드를 끄고 volume 경로로 복귀합니다.
                // 법선과 다른 캐시의 조합, temporal 커널, 거리 도약 키워드는 계속 남깁니다.
                bool unsupportedSurface = keywords.IsEnabled(farSurface)
                    && (keywords.IsEnabled(normalField) || keywords.IsEnabled(shapeField)
                        || keywords.IsEnabled(spatialLight) || keywords.IsEnabled(height));
                bool outsideExtensionSuite = ExtensionOnly
                    && (keywords.IsEnabled(shapeField) || keywords.IsEnabled(spatialLight) || keywords.IsEnabled(traversal));
                bool outsideHybridSuite = HybridOnly && (!keywords.IsEnabled(density) || !keywords.IsEnabled(lighting)
                    || keywords.IsEnabled(farSurface) || keywords.IsEnabled(normalField)
                    || keywords.IsEnabled(shapeField) || keywords.IsEnabled(spatialLight) || keywords.IsEnabled(traversal));
                if (!keywords.IsEnabled(compact) || keywords.IsEnabled(height) || keywords.IsEnabled(palette)
                    || keywords.IsEnabled(density) != keywords.IsEnabled(lighting) || unsupportedSurface
                    || outsideExtensionSuite || outsideHybridSuite)
                {
                    data.RemoveAt(index);
                }
            }
        }
    }
}
