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

        [Header("달: 방향과 입사광")]
        public float moonElevation;
        public float moonAzimuth;
        [Min(0f)] public float moonLux;
        public Color moonColor;
        [Min(0f)] public float moonDirectMultiplier;

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

        [Header("V2 입사광 팔레트와 천체")]
        [Range(0f, 1f)] public float conceptSky;
        public float atmosphereElevation;
        public float atmosphereAzimuth;
        public Color cloudShadowColor;
        public Color cloudMidColor;
        public Color cloudLightColor;
        [Range(0f, 1f)] public float cloudPaletteBlend;

        [Min(0f)] public float celestialDiskDegrees;
        [Min(0f)] public float celestialDiskIntensity;
        [Min(0f)] public float skyGlowStrength;
        [Min(0f)] public float skyGlowPower;
        [Min(0f)] public float starsIntensity;

        [Header("구름 깊이 기반 공기 원근")]
        [Min(0f)] public float aerialStrength;
        [Min(0f)] public float aerialStart;
        [Min(0f)] public float aerialEnd;

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

        /// <summary>기존 낮과 분리된 V2의 크림색 밝은 면과 청록 그림자를 만듭니다.</summary>
        public static CloudLightingState ConceptDay()
        {
            CloudLightingState state = Day();
            ConfigureConcept(ref state);
            state.sunColor = new Color(1f, 0.93f, 0.80f);
            state.skyGlowPower = 5f;
            state.celestialDiskIntensity = 4f;
            state.exposureEV = 11.1f;
            state.skyTopColor = new Color(0.17f, 0.76f, 1.08f);
            state.skyHorizonColor = new Color(0.56f, 0.95f, 1f);
            state.ambientIntensity = 0.75f;
            state.directMultiplier = 1.2f;
            state.cloudShadowColor = new Color(0.32f, 0.77f, 0.86f);
            state.cloudMidColor = new Color(1f, 0.81f, 0.73f);
            state.cloudLightColor = new Color(1.05f, 0.91f, 0.68f);
            state.skyGlowStrength = 0.14f;
            return state;
        }

        /// <summary>금빛 밝은 면, 살구 중간 면, 푸른 라일락 그림자의 V2 석양을 만듭니다.</summary>
        public static CloudLightingState ConceptSunset()
        {
            CloudLightingState state = Sunset();
            ConfigureConcept(ref state);
            state.sunLux = 85000f;
            state.ambientSkyColor = new Color(0.44f, 0.51f, 0.75f);
            state.ambientHorizonColor = new Color(0.71f, 0.47f, 0.59f);
            state.celestialDiskIntensity = 3f;
            state.exposureEV = 10.8f;
            state.skyGlowStrength = 0.16f;
            state.skyGlowPower = 6f;
            state.directMultiplier = 2f;
            state.cloudLightColor = new Color(1.18f, 0.62f, 0.26f);
            state.cloudMidColor = new Color(1.05f, 0.39f, 0.35f);
            state.cloudShadowColor = new Color(0.44f, 0.49f, 0.68f);
            state.ambientIntensity = 0.62f;
            state.skyHorizonColor = new Color(1.12f, 0.52f, 0.20f);
            return state;
        }

        /// <summary>태양 직접광이 꺼진 뒤 방향 잔광이 남는 V2 황혼을 만듭니다.</summary>
        public static CloudLightingState ConceptTwilight()
        {
            CloudLightingState state = Twilight();
            ConfigureConcept(ref state);
            state.sunLux = 0f;
            state.cloudShadowColor = new Color(0.26f, 0.33f, 0.57f);
            state.cloudMidColor = new Color(0.59f, 0.53f, 0.72f);
            state.cloudLightColor = new Color(0.94f, 0.67f, 0.69f);
            state.skyGlowStrength = 0.4f;
            state.skyGlowPower = 4f;
            state.celestialDiskIntensity = 0f;
            state.starsIntensity = 0.08f;
            return state;
        }

        /// <summary>청백색 달빛과 남청 하늘의 밤 기준을 만듭니다. Lux는 컨셉 미리보기용 아트 값입니다.</summary>
        public static CloudLightingState MoonlitNight()
        {
            CloudLightingState state = ConceptTwilight();
            state.sunElevation = -20f;
            state.sunLux = 0f;
            state.moonLux = 24000f;
            state.moonDirectMultiplier = 2f;
            state.atmosphereElevation = state.moonElevation;
            state.atmosphereAzimuth = state.moonAzimuth;
            state.exposureEV = 10f;

            state.skyTopColor = new Color(0.035f, 0.075f, 0.19f);
            state.skyHorizonColor = new Color(0.13f, 0.24f, 0.43f);
            state.skyLowerColor = new Color(0.055f, 0.10f, 0.24f);
            state.horizonTint = new Color(0.36f, 0.55f, 0.85f);
            state.zenithTint = new Color(0.20f, 0.32f, 0.64f);

            state.cloudShadowColor = new Color(0.20f, 0.31f, 0.63f);
            state.cloudMidColor = new Color(0.42f, 0.59f, 0.95f);
            state.cloudLightColor = new Color(0.66f, 0.86f, 1f);
            state.ambientSkyColor = new Color(0.26f, 0.42f, 0.74f);
            state.ambientHorizonColor = new Color(0.20f, 0.30f, 0.57f);
            state.ambientIntensity = 0.65f;
            state.silverLining = 0.6f;

            state.celestialDiskDegrees = 1.5f;
            state.celestialDiskIntensity = 0.9f;
            state.skyGlowStrength = 0.08f;
            state.skyGlowPower = 12f;
            state.starsIntensity = 0.32f;
            return state;
        }

        /// <summary>V2 전용 공통 설정을 초기화하며 기존 프리셋의 값은 변경하지 않습니다.</summary>
        private static void ConfigureConcept(ref CloudLightingState state)
        {
            // 팔레트는 작가가 고른 sRGB 입사광색이며 GPU 바인딩에서 한 번만 선형화합니다.
            state.conceptSky = 1f;
            state.atmosphereElevation = state.sunElevation;
            state.atmosphereAzimuth = state.sunAzimuth;
            state.skyBlend = 1f;
            state.cloudPaletteBlend = 1f;
            state.moonElevation = 32f;
            state.moonAzimuth = 72f;
            state.moonColor = new Color(0.66f, 0.86f, 1f);
            state.moonDirectMultiplier = 2f;
            state.celestialDiskDegrees = 1.1f;
            state.aerialStrength = 0.55f;
            state.aerialStart = 4500f;
            state.aerialEnd = 30000f;
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

            state.moonElevation = Mathf.Lerp(from.moonElevation, to.moonElevation, t);
            state.moonAzimuth = Mathf.LerpAngle(from.moonAzimuth, to.moonAzimuth, t);
            state.moonLux = Mathf.Lerp(from.moonLux, to.moonLux, t);
            state.moonColor = Color.Lerp(from.moonColor, to.moonColor, t);
            state.moonDirectMultiplier = Mathf.Lerp(from.moonDirectMultiplier, to.moonDirectMultiplier, t);

            state.horizonTint = Color.Lerp(from.horizonTint, to.horizonTint, t);
            state.zenithTint = Color.Lerp(from.zenithTint, to.zenithTint, t);
            state.skyExposure = Mathf.Lerp(from.skyExposure, to.skyExposure, t);
            state.exposureEV = Mathf.Lerp(from.exposureEV, to.exposureEV, t);

            state.skyTopColor = Color.Lerp(from.skyTopColor, to.skyTopColor, t);
            state.skyHorizonColor = Color.Lerp(from.skyHorizonColor, to.skyHorizonColor, t);
            state.skyLowerColor = Color.Lerp(from.skyLowerColor, to.skyLowerColor, t);
            state.skyBlend = Mathf.Lerp(from.skyBlend, to.skyBlend, t);

            state.conceptSky = Mathf.Lerp(from.conceptSky, to.conceptSky, t);
            state.atmosphereElevation = Mathf.Lerp(from.atmosphereElevation, to.atmosphereElevation, t);
            state.atmosphereAzimuth = Mathf.LerpAngle(from.atmosphereAzimuth, to.atmosphereAzimuth, t);
            state.cloudShadowColor = Color.Lerp(from.cloudShadowColor, to.cloudShadowColor, t);
            state.cloudMidColor = Color.Lerp(from.cloudMidColor, to.cloudMidColor, t);
            state.cloudLightColor = Color.Lerp(from.cloudLightColor, to.cloudLightColor, t);
            state.cloudPaletteBlend = Mathf.Lerp(from.cloudPaletteBlend, to.cloudPaletteBlend, t);

            state.celestialDiskDegrees = Mathf.Lerp(from.celestialDiskDegrees, to.celestialDiskDegrees, t);
            state.celestialDiskIntensity = Mathf.Lerp(from.celestialDiskIntensity, to.celestialDiskIntensity, t);
            state.skyGlowStrength = Mathf.Lerp(from.skyGlowStrength, to.skyGlowStrength, t);
            state.skyGlowPower = Mathf.Lerp(from.skyGlowPower, to.skyGlowPower, t);
            state.starsIntensity = Mathf.Lerp(from.starsIntensity, to.starsIntensity, t);
            state.aerialStrength = Mathf.Lerp(from.aerialStrength, to.aerialStrength, t);
            state.aerialStart = Mathf.Lerp(from.aerialStart, to.aerialStart, t);
            state.aerialEnd = Mathf.Lerp(from.aerialEnd, to.aerialEnd, t);

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

        /// <summary>달 Light와 천체 디스크가 공유하는 표면에서 달을 향하는 월드 방향을 반환합니다.</summary>
        public Vector3 GetMoonDirection()
        {
            Quaternion rotation = Quaternion.Euler(moonElevation, moonAzimuth + 180f, 0f);
            return -(rotation * Vector3.forward);
        }

        /// <summary>대표광 교체와 독립적으로 부드럽게 이동하는 하늘 잔광과 공기 원근의 방향을 반환합니다.</summary>
        public Vector3 GetAtmosphereDirection()
        {
            Quaternion rotation = Quaternion.Euler(atmosphereElevation, atmosphereAzimuth + 180f, 0f);
            return -(rotation * Vector3.forward);
        }

        /// <summary>태양과 동일한 100000 Lux 기준으로 달의 구름 직접광 세기를 반환합니다.</summary>
        public float GetMoonStrength()
        {
            float visibility = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-4f, 1f, moonElevation));
            return Mathf.Max(0f, moonLux) / 100000f * Mathf.Max(0f, moonDirectMultiplier) * visibility;
        }

        /// <summary>비용을 유지하기 위해 더 강한 천체 하나를 대표광으로 고릅니다. 표준 전환은 황혼의 무직접광 구간에서 교대합니다.</summary>
        public bool UsesMoonKey()
        {
            return GetMoonStrength() > GetDirectStrength();
        }

        /// <summary>구름 자기 그림자를 추적할 대표 태양 또는 달의 방향을 반환합니다.</summary>
        public Vector3 GetKeyDirection()
        {
            if (UsesMoonKey())
            {
                return GetMoonDirection();
            }

            return GetSunDirection();
        }

        /// <summary>대표광의 sRGB 색을 반환합니다. 선형 변환은 GPU 바인딩에서 수행합니다.</summary>
        public Color GetKeyColor()
        {
            if (UsesMoonKey())
            {
                return moonColor;
            }

            return sunColor;
        }

        /// <summary>대표광의 스칼라 세기를 반환합니다. 출력 입사광 팔레트에는 광원 RGB를 다시 곱하지 않습니다.</summary>
        public float GetKeyStrength()
        {
            return Mathf.Max(GetDirectStrength(), GetMoonStrength());
        }
    }
}

