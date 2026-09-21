using System;
using UnityEngine;

namespace ClouDream.LostSkies
{
    [Serializable]
    public struct CloudEnvironment
    {
        [Header("기존 형태에 적용하는 변화량")]
        [Min(0f)]
        public float densityMultiplier;
        [Min(0f)]
        public float skyDensityMultiplier;
        [Range(-0.1f, 0.1f)]
        public float coverageOffset;

        [Header("표면 색과 밝기")]
        public Color shadowTint;
        public Color highlightTint;
        public Color sunlight;
        [Min(0f)]
        public float brightness;

        [Header("시간대 공급자가 연결한 공유 조명")]
        public CloudLightingState lighting;

        /// <summary>현재 장면과 같은 맑은 날 기본값을 만듭니다.</summary>
        public static CloudEnvironment ClearDay()
        {
            CloudEnvironment value = new CloudEnvironment();
            value.densityMultiplier = 1f;
            value.skyDensityMultiplier = 1f;
            value.shadowTint = new Color(0.15f, 0.37f, 0.48f);
            value.highlightTint = new Color(1.12f, 1.08f, 0.97f);
            value.sunlight = new Color(1f, 0.93f, 0.8f);
            value.brightness = 1.4f;
            return value;
        }

        /// <summary>형태의 시드를 유지한 채 날씨와 색상만 연속적으로 보간합니다.</summary>
        public static CloudEnvironment Blend(CloudEnvironment from, CloudEnvironment to, float amount)
        {
            float t = Mathf.Clamp01(amount);
            CloudEnvironment result = new CloudEnvironment();
            result.densityMultiplier = Mathf.Lerp(from.densityMultiplier, to.densityMultiplier, t);
            result.skyDensityMultiplier = Mathf.Lerp(from.skyDensityMultiplier, to.skyDensityMultiplier, t);
            result.coverageOffset = Mathf.Lerp(from.coverageOffset, to.coverageOffset, t);
            result.shadowTint = Color.Lerp(from.shadowTint, to.shadowTint, t);
            result.highlightTint = Color.Lerp(from.highlightTint, to.highlightTint, t);
            result.sunlight = Color.Lerp(from.sunlight, to.sunlight, t);
            result.brightness = Mathf.Lerp(from.brightness, to.brightness, t);
            result.lighting = CloudLightingState.Blend(from.lighting, to.lighting, t);
            return result;
        }

        /// <summary>공유 입사광에 날씨/바이옴의 표면 색 보정을 적용합니다. 맑은 날 기본색은 중립입니다.</summary>
        public CloudLightingState TintLighting(CloudLightingState incident)
        {
            CloudEnvironment neutral = ClearDay();
            Color directTint = RelativeTint(sunlight, neutral.sunlight) * RelativeTint(highlightTint, neutral.highlightTint);
            Color ambientTint = RelativeTint(shadowTint, neutral.shadowTint);

            incident.sunColor *= directTint;
            incident.ambientSkyColor *= ambientTint;
            incident.ambientHorizonColor *= ambientTint;
            return incident;
        }

        /// <summary>기존 절대 팔레트를 중립 환경 대비 배수로 변환하여 시간대 조명과 함께 사용합니다.</summary>
        private static Color RelativeTint(Color value, Color neutral)
        {
            return new Color(
                Mathf.Max(0f, value.r) / Mathf.Max(0.001f, neutral.r),
                Mathf.Max(0f, value.g) / Mathf.Max(0.001f, neutral.g),
                Mathf.Max(0f, value.b) / Mathf.Max(0.001f, neutral.b),
                1f);
        }
    }
}
