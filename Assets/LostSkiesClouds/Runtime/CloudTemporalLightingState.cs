using UnityEngine;

namespace ClouDream.LostSkies
{
    /// <summary>현재 프레임의 실제 GPU 조명 입력을 보관하여 이력 나이별 변화량을 비교합니다.</summary>
    public struct CloudTemporalLightingState
    {
        public bool active;

        public Vector3 direction;

        public Color direct;

        public float directIntensity;

        public Color ambientSky;

        public Color ambientHorizon;

        public float ambientIntensity;

        public Color shadow;

        public Color middle;

        public Color light;

        public float paletteBlend;

        public float silver;

        // 이 값은 선형 조명 입력의 변화 한계입니다. 비선형 차폐를 포함하는 출력 RGB 오차 보장은 아닙니다.
        public const float MaximumHistoryInputChange = 0.005f;

        /// <summary>렌더러가 사용하는 선형 색과 실제 대표광 방향을 그대로 기록합니다.</summary>
        public static CloudTemporalLightingState Capture(Vector3 sunDirection, Color linearSunColor,
            float intensity, CloudLightingState sky)
        {
            CloudTemporalLightingState state = new CloudTemporalLightingState();
            state.active = sky.active;
            state.direction = sunDirection.normalized;
            state.direct = linearSunColor * Mathf.Max(0f, intensity);
            state.directIntensity = Mathf.Max(0f, intensity);
            state.ambientSky = sky.ambientSkyColor.linear * Mathf.Max(0f, sky.ambientIntensity);
            state.ambientHorizon = sky.ambientHorizonColor.linear * Mathf.Max(0f, sky.ambientIntensity);
            state.ambientIntensity = Mathf.Max(0f, sky.ambientIntensity);
            state.shadow = sky.cloudShadowColor.linear;
            state.middle = sky.cloudMidColor.linear;
            state.light = sky.cloudLightColor.linear;
            state.paletteBlend = Mathf.Clamp01(sky.cloudPaletteBlend);
            state.silver = Mathf.Max(0f, sky.silverLining);
            return state;
        }

        /// <summary>태양 각도 30도와 선형 입사광 크기를 기준으로 가장 큰 입력 변화량을 반환합니다.</summary>
        public float Difference(CloudTemporalLightingState previous)
        {
            if (active != previous.active)
            {
                return 1f;
            }

            float delta = 0f;
            if (direction != previous.direction)
            {
                delta = Vector3.Angle(direction, previous.direction) / 30f;
            }

            delta = Mathf.Max(delta, ColorDifference(direct, previous.direct));
            float intensityScale = Mathf.Max(1f, Mathf.Max(directIntensity, previous.directIntensity));
            delta = Mathf.Max(delta, Mathf.Abs(directIntensity - previous.directIntensity) / intensityScale);
            if (active)
            {
                delta = Mathf.Max(delta, ColorDifference(ambientSky, previous.ambientSky));
                delta = Mathf.Max(delta, ColorDifference(ambientHorizon, previous.ambientHorizon));
                float ambientScale = Mathf.Max(1f, Mathf.Max(ambientIntensity, previous.ambientIntensity));
                delta = Mathf.Max(delta, Mathf.Abs(ambientIntensity - previous.ambientIntensity) / ambientScale);
                delta = Mathf.Max(delta, ColorDifference(shadow, previous.shadow));
                delta = Mathf.Max(delta, ColorDifference(middle, previous.middle));
                delta = Mathf.Max(delta, ColorDifference(light, previous.light));
                delta = Mathf.Max(delta, Mathf.Abs(paletteBlend - previous.paletteBlend));
                delta = Mathf.Max(delta, Mathf.Abs(silver - previous.silver));
            }

            if (float.IsNaN(delta) || float.IsInfinity(delta))
            {
                return 1f;
            }

            return delta;
        }

        /// <summary>어두운 영역의 작은 변화가 증폭되지 않도록 1 이상의 선형 RGB 범위로 정규화합니다.</summary>
        private static float ColorDifference(Color current, Color previous)
        {
            float scale = Mathf.Max(1f, Mathf.Max(current.maxColorComponent, previous.maxColorComponent));
            float difference = Mathf.Max(Mathf.Abs(current.r - previous.r), Mathf.Abs(current.g - previous.g));
            difference = Mathf.Max(difference, Mathf.Abs(current.b - previous.b));
            return difference / scale;
        }
    }

    /// <summary>GPU 픽셀 집계의 인덱스입니다. 하나의 픽셀은 정확히 한 가지 결과에만 포함됩니다.</summary>
    public enum CloudTemporalRejection
    {
        Reused = 0,
        Scheduled = 1,
        ShapeInvalid = 2,
        LightingChanged = 3,
        Offscreen = 4,
        DepthMismatch = 5,
        Foreground = 6,
        CloudBoundary = 7,
        HistoryAge = 8,
        DepthVariance = 9,
        InvalidData = 10,
        EmptyTranslation = 11,
        DeformationChanged = 12,
        Count = 13
    }
}
