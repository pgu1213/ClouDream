#ifndef CLOUD_HYBRID_LIGHTING_INCLUDED
#define CLOUD_HYBRID_LIGHTING_INCLUDED

float4 _HybridLightDirection, _HybridShadow, _HybridMid, _HybridHighlight;
float4 _HybridLightSettings;
float4 _HybridAmbientSky, _HybridAmbientHorizon, _HybridSunColor;

// Reference의 원식을 변경하지 않고 A/B에서만 기존 응답과 안정화 응답을 비교합니다.
#define _SunDirection _HybridLightDirection
#define _CloudPaletteShadow _HybridShadow
#define _CloudPaletteMid _HybridMid
#define _CloudPaletteLight _HybridHighlight
#define _AmbientSky _HybridAmbientSky
#define _AmbientHorizon _HybridAmbientHorizon
#define _SunColor _HybridSunColor
#include "CloudConceptLighting.hlsl"
#undef _SunDirection
#undef _CloudPaletteShadow
#undef _CloudPaletteMid
#undef _CloudPaletteLight
#undef _AmbientSky
#undef _AmbientHorizon
#undef _SunColor

// 기존 2표본과 거시 4표본을 분리합니다. 광원 조회 OFF는 두 모드 모두 유지합니다.
int HybridShadowCount()
{
    if (_HybridLightSettings.y <= 0)
    {
        return 0;
    }
    if (_HybridStability.z > 0.5)
    {
        return 4;
    }
    return (int)_HybridLightSettings.y;
}

// 구름 크기로 정한 월드 광원 구간의 비균등 적분입니다. 카메라와 시선은 입력하지 않습니다.
float HybridShadowDistance(int index, int count, float characteristic, float legacyStep, out float segment)
{
    if (_HybridStability.z < 0.5)
    {
        segment = legacyStep;
        return (index + 1) * legacyStep;
    }
    float extent = clamp(characteristic * 2.5, 600, 5000);
    float first = (float)index / max(1, count);
    float last = (float)(index + 1) / max(1, count);
    segment = (last * last - first * first) * extent;
    return (first * first + last * last) * 0.5 * extent;
}

// 큰 명암과 그림자 속 2차 형태는 월드 법선·높이·광원 차폐만 사용합니다.
float3 HybridStableBaseLight(float opticalDepth, float skyVisibility, float3 normal, float3 position)
{
    float facing = 0.5;
    float upward = 0.5;
    if (dot(normal, normal) > 0.1)
    {
        facing = saturate(dot(normal, normalize(_HybridLightDirection.xyz)) * 0.5 + 0.5);
        upward = saturate(normal.y * 0.5 + 0.5);
    }
    float height = saturate((position.y - _OceanBounds.x) / max(1000, _SkyAltitude.y - _OceanBounds.x));
    float visibility = exp(-max(0, opticalDepth) * 0.18);
    float coordinate = (0.75 * facing + 0.25 * skyVisibility) * (0.25 + 0.75 * visibility);
    coordinate = saturate((coordinate - 0.5) * _ConceptLighting.z + 0.5);
    float bounce = 0.04 + upward * 0.14 + height * 0.06 + skyVisibility * 0.08;
    float3 ambient = lerp(_HybridShadow.rgb, _HybridMid.rgb, bounce)
        * _HybridAmbientSky.w * lerp(0.65, 1, upward);
    float3 key = lerp(_HybridMid.rgb, _HybridHighlight.rgb, smoothstep(0.2, 0.85, coordinate));
    return ambient + key * coordinate * _HybridSunColor.w;
}

// 카메라에 따라 바뀌는 항은 이 작은 rim뿐입니다. 공기 원근은 호출자의 기존 합성에 남깁니다.
float3 HybridIncidentLight(float opticalDepth, float skyVisibility, float sunCosine, float3 normal, float3 position)
{
    if (_HybridStability.z < 0.5)
    {
        return ConceptIncidentLight(opticalDepth, skyVisibility, sunCosine, normal);
    }
    float rim = pow(saturate(sunCosine), 18) * exp(-max(0, opticalDepth) * 0.7)
        * _HybridAmbientHorizon.w * 0.18;
    return HybridStableBaseLight(opticalDepth, skyVisibility, normal, position)
        + _HybridHighlight.rgb * rim * _HybridSunColor.w;
}

#endif
