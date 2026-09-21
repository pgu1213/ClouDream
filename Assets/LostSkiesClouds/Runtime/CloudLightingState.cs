using System;
using UnityEngine;

namespace ClouDream.LostSkies
{
    /// <summary>태양, HDRP 하늘, 구름이 함께 사용하는 한 시점의 조명 상태입니다.</summary>
    [Serializable]
    public struct CloudLightingState
    {
        public bool active;

        [Header("태양: 방향과 입사광")]
        public float sunElevation;
        public float sunAzimuth;
        [Min(0f)] public float sunLux;
        public Color sunColor;

        [Header("하늘의 색상 보정")]
        public Color horizonTint;
        public Color zenithTint;
        public float skyExposure;
        public float exposureEV;

        [Header("컨셉 하늘 팔레트")]
        [ColorUsage(false, true)]
        public Color skyTopColor;
        [ColorUsage(false, true)]
        public Color skyHorizonColor;
        [ColorUsage(false, true)]
        public Color skyLowerColor;
        [Range(0f, 1f)] public float skyBlend;

        [Header("구름 주변광과 가장자리 산란")]
        public Color ambientSkyColor;
        public Color ambientHorizonColor;
        [Min(0f)] public float ambientIntensity;
        [Min(0f)] public float directMultiplier;
        [Range(0f, 2f)] public float silverLining;

        /// <summary>밝은 청록 하늘과 따뜻한 백색광의 낮 기준을 만듭니다.</summary>
        public static CloudLightingState Day()
        {
            CloudLightingState state = new CloudLightingState();
            state.active = true;
            state.sunElevation = 30f;
            state.sunAzimuth = 55f;
            state.sunLux = 100000f;
            state.sunColor = new Color(1f, 0.97f, 0.88f);

            state.horizonTint = new Color(0.85f, 1f, 1f);
            state.zenithTint = new Color(0.63f, 1f, 0.93f);
            state.exposureEV = 12f;

            state.skyTopColor = new Color(0.19f, 0.66f, 0.87f);
            state.skyHorizonColor = new Color(0.66f, 0.89f, 0.94f);
            state.skyLowerColor = new Color(0.42f, 0.65f, 0.78f);
            state.skyBlend = 0.45f;

            state.ambientSkyColor = new Color(0.40f, 0.74f, 0.85f);
            state.ambientHorizonColor = new Color(0.59f, 0.77f, 0.83f);
            state.ambientIntensity = 0.7f;
            state.directMultiplier = 1.15f;
            state.silverLining = 0.65f;
            return state;
        }

        /// <summary>낮게 뜬 태양, 주황 입사광, 보랏빛 그늘의 일몰 기준을 만듭니다.</summary>
        public static CloudLightingState Sunset()
        {
            CloudLightingState state = Day();
            state.sunElevation = 5f;
            state.sunAzimuth = 60f;
            state.sunLux = 68000f;
            state.sunColor = new Color(1f, 0.69f, 0.34f);

            state.horizonTint = new Color(1f, 0.74f, 0.53f);
            state.zenithTint = new Color(0.80f, 0.68f, 0.94f);
            state.skyExposure = 0.25f;
            state.exposureEV = 11.2f;

            state.skyTopColor = new Color(0.30f, 0.43f, 0.74f);
            state.skyHorizonColor = new Color(1f, 0.61f, 0.34f);
            state.skyLowerColor = new Color(0.54f, 0.34f, 0.50f);
            state.skyBlend = 0.94f;

            state.ambientSkyColor = new Color(0.46f, 0.44f, 0.75f);
            state.ambientHorizonColor = new Color(0.80f, 0.49f, 0.48f);
            state.ambientIntensity = 0.55f;
            state.directMultiplier = 1.35f;
            state.silverLining = 1.05f;
            return state;
        }

        /// <summary>태양이 지평선 아래로 내려간 후의 보라·청색 주변광 기준을 만듭니다.</summary>
        public static CloudLightingState Twilight()
        {
            CloudLightingState state = Sunset();
            state.sunElevation = -4f;
            state.sunAzimuth = 63f;
            state.sunLux = 9000f;
            state.sunColor = new Color(1f, 0.42f, 0.25f);

            state.horizonTint = new Color(0.90f, 0.55f, 0.77f);
            state.zenithTint = new Color(0.51f, 0.49f, 0.85f);
            state.skyExposure = 0.5f;
            state.exposureEV = 10.3f;

            state.skyTopColor = new Color(0.18f, 0.23f, 0.44f);
            state.skyHorizonColor = new Color(0.68f, 0.39f, 0.48f);
            state.skyLowerColor = new Color(0.28f, 0.24f, 0.42f);
            state.skyBlend = 1f;

            state.ambientSkyColor = new Color(0.36f, 0.39f, 0.69f);
            state.ambientHorizonColor = new Color(0.62f, 0.38f, 0.59f);
            state.ambientIntensity = 0.32f;
            state.directMultiplier = 0.8f;
            state.silverLining = 0.3f;
            return state;
        }

        /// <summary>각도와 색, 강도를 연속적으로 혼합합니다. 밀도나 구름 시드는 바꾸지 않습니다.</summary>
        public static CloudLightingState Blend(CloudLightingState from, CloudLightingState to, float amount)
        {
            float t = Mathf.Clamp01(amount);
            CloudLightingState state = new CloudLightingState();
            state.active = from.active || to.active;
            state.sunElevation = Mathf.Lerp(from.sunElevation, to.sunElevation, t);
            state.sunAzimuth = Mathf.LerpAngle(from.sunAzimuth, to.sunAzimuth, t);
            state.sunLux = Mathf.Lerp(from.sunLux, to.sunLux, t);
            state.sunColor = Color.Lerp(from.sunColor, to.sunColor, t);

            state.horizonTint = Color.Lerp(from.horizonTint, to.horizonTint, t);
            state.zenithTint = Color.Lerp(from.zenithTint, to.zenithTint, t);
            state.skyExposure = Mathf.Lerp(from.skyExposure, to.skyExposure, t);
            state.exposureEV = Mathf.Lerp(from.exposureEV, to.exposureEV, t);

            state.skyTopColor = Color.Lerp(from.skyTopColor, to.skyTopColor, t);
            state.skyHorizonColor = Color.Lerp(from.skyHorizonColor, to.skyHorizonColor, t);
            state.skyLowerColor = Color.Lerp(from.skyLowerColor, to.skyLowerColor, t);
            state.skyBlend = Mathf.Lerp(from.skyBlend, to.skyBlend, t);

            state.ambientSkyColor = Color.Lerp(from.ambientSkyColor, to.ambientSkyColor, t);
            state.ambientHorizonColor = Color.Lerp(from.ambientHorizonColor, to.ambientHorizonColor, t);
            state.ambientIntensity = Mathf.Lerp(from.ambientIntensity, to.ambientIntensity, t);
            state.directMultiplier = Mathf.Lerp(from.directMultiplier, to.directMultiplier, t);
            state.silverLining = Mathf.Lerp(from.silverLining, to.silverLining, t);
            return state;
        }

        /// <summary>HDRP Directional Light의 회전과 동일한 표면에서 태양을 향하는 방향을 반환합니다.</summary>
        public Vector3 GetSunDirection()
        {
            Quaternion rotation = Quaternion.Euler(sunElevation, sunAzimuth + 180f, 0f);
            return -(rotation * Vector3.forward);
        }

        /// <summary>태양이 지평선 아래로 내려갈 때 직접광을 부드럽게 줄입니다.</summary>
        public float GetDirectStrength()
        {
            float visibility = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-4f, 1f, sunElevation));
            return Mathf.Max(0f, sunLux) / 100000f * Mathf.Max(0f, directMultiplier) * visibility;
        }
    }
}

