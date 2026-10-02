using System;
using UnityEngine;

namespace ClouDream.LostSkies
{
    /// <summary>주 광선의 밀도 적분과 입사광 재평가 간격을 서로 독립적으로 제어합니다.</summary>
    [Serializable]
    public struct CloudDistanceQualitySettings
    {
        public bool density;
        public bool lighting;

        [Min(0)] public float densityNear;
        [Min(1)] public float densityFar;
        [Range(1, 2)] public float densityStepScale;

        [Min(0)] public float lightingNear;
        [Min(1)] public float lightingFar;
        [Range(1, 3)] public float lightingSpacingScale;

        /// <summary>근거리 3km를 유지하고 12km까지 완만하게 품질을 전환하는 기본값입니다. 기본 활성화는 하지 않습니다.</summary>
        public static CloudDistanceQualitySettings Default
        {
            get
            {
                CloudDistanceQualitySettings settings = new CloudDistanceQualitySettings();
                settings.densityNear = 3000;
                settings.densityFar = 12000;
                settings.densityStepScale = 1.5f;
                settings.lightingNear = 3000;
                settings.lightingFar = 12000;
                settings.lightingSpacingScale = 1.5f;
                return settings;
            }
        }

        /// <summary>GPU 계약은 (시작m, 종료m, 최대 배율, 활성 여부)입니다. 외부 코드의 잘못된 값도 제한합니다.</summary>
        public Vector4 DensityParameters(bool supported)
        {
            return Parameters(density && supported, densityNear, densityFar, densityStepScale, 2);
        }

        /// <summary>밀도와 별개로 조명의 기준점 간격만 늘립니다. 그림자 범위/표본 수는 유지합니다.</summary>
        public Vector4 LightingParameters(bool supported)
        {
            return Parameters(lighting && supported, lightingNear, lightingFar, lightingSpacingScale, 3);
        }

        /// <summary>유한 범위와 최소 1m 전환 폭을 보장하고 비활성 경로는 배율 1을 사용합니다.</summary>
        private static Vector4 Parameters(bool enabled, float near, float far, float scale, float maximum)
        {
            if (!float.IsFinite(near))
            {
                near = 3000;
            }
            if (!float.IsFinite(far))
            {
                far = 12000;
            }
            if (!float.IsFinite(scale))
            {
                scale = 1;
            }
            near = Mathf.Clamp(near, 0, 41999);
            far = Mathf.Clamp(far, near + 1, 42000);
            scale = Mathf.Clamp(scale, 1, maximum);
            if (!enabled)
            {
                return new Vector4(near, far, 1, 0);
            }
            return new Vector4(near, far, scale, 1);
        }
    }
}
