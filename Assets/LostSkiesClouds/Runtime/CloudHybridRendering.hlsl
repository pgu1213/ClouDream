#ifndef CLOUD_HYBRID_RENDERING_INCLUDED
#define CLOUD_HYBRID_RENDERING_INCLUDED

#include "CloudHybridShape.hlsl"

float4 _HybridCamera, _HybridOrigin;
float4 _HybridForward, _HybridRight, _HybridUp;
float4 _HybridOutputSize, _HybridQuality;
float4 _HybridLightDirection, _HybridShadow, _HybridMid, _HybridHighlight;
float4 _HybridLightSettings, _HybridAir;
float4 _HybridAirColor;
float4 _HybridAmbientSky, _HybridAmbientHorizon, _HybridSunColor;

// 팔레트 응답 원식은 기존 렌더러와 공유하고 차폐만 두 표본으로 근사합니다.
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

// 완전한 선형 premultiplied 계약: RGB는 누적 방사량 L, alpha는 투과율 T입니다.
float3 HybridShade(float3 position, float3 normal, float3 ray, bool inside)
{
    float opticalDepth = 0;
    [loop]
    for (int i = 0; i < (int)_HybridLightSettings.y; i++)
    {
        float distance = (i + 1) * 140.0;
        opticalDepth += HybridDensity(position + _HybridLightDirection.xyz * distance)
            * 140 * 0.009 * _HybridEnvironment.w;
    }

    if (inside)
    {
        normal = 0;
    }

    float skyVisibility = saturate(1 - opticalDepth * 0.08);
    float3 color = ConceptIncidentLight(opticalDepth, skyVisibility,
        dot(ray, _HybridLightDirection.xyz), normal);
    float air = smoothstep(_HybridAir.x, max(_HybridAir.x + 1, _HybridAir.y),
        length(position - _HybridCamera.xyz)) * _HybridAir.z;
    return lerp(color, _HybridAirColor.rgb, saturate(air));
}

// HDRP의 실제 투영을 역변환해 Game/RenderTexture의 Y 방향과 원점 보정을 동시에 맞춥니다.
float3 HybridRay(float2 uv)
{
    float3 nearPosition = ComputeWorldSpacePosition(uv, 1, UNITY_MATRIX_UNJITTERED_I_VP);
    float3 distantPosition = ComputeWorldSpacePosition(uv, 0.0001, UNITY_MATRIX_UNJITTERED_I_VP);
    return normalize(distantPosition - nearPosition);
}

// 장면 깊이는 광선 거리로 변환합니다. 먼 평면 배경은 무한한 가림 거리입니다.
float HybridSceneDistance(float2 uv, float3 ray)
{
    uint2 pixel = (uint2)(uv * _ScreenSize.xy);
    float depth = LoadCameraDepth(pixel);
    if (depth == 0)
    {
        return 1000000;
    }

    return LinearEyeDepth(depth, _ZBufferParams) / max(0.001, dot(ray, _HybridForward.xyz));
}

// 평행한 축에서도 NaN을 만들지 않는 보수적 AABB 광선 구간입니다.
bool HybridRayBounds(float3 ray, out float start, out float end)
{
    start = 0;
    end = 1000000;
    float3 regionMinimum = HybridRegionMinimum();
    float3 regionSize = HybridRegionSize();
    [unroll]
    for (int axis = 0; axis < 3; axis++)
    {
        float minimum = regionMinimum[axis];
        float maximum = minimum + regionSize[axis];
        float origin = _HybridCamera[axis];
        if (abs(ray[axis]) < 0.000001)
        {
            if (origin < minimum || origin > maximum)
            {
                return false;
            }
        }
        else
        {
            float first = (minimum - origin) / ray[axis];
            float second = (maximum - origin) / ray[axis];
            start = max(start, min(first, second));
            end = min(end, max(first, second));
        }
    }

    return end > start;
}

// 같은 큰 형태에서 카메라의 전환 가중치를 구합니다. 이 값은 구 추적용 거리로 사용하지 않습니다.
float HybridVolumeWeight()
{
    float3 minimum = HybridRegionMinimum();
    float3 closest = clamp(_HybridCamera.xyz, minimum, minimum + HybridRegionSize());
    float outside = length(_HybridCamera.xyz - closest);
    float field = max(0, HybridReadField(closest).x - _ConceptShape.z - _ConceptDetail.y);
    float distance = max(outside, field);
    if (_HybridObject.x < 0.5)
    {
        float4 ocean = HybridBaseField(_HybridCamera.xyz);
        distance = max(outside, max(0, ocean.x - _ConceptShape.z - _ConceptDetail.y));
    }

    return 1 - smoothstep(_HybridQuality.x, _HybridQuality.y, distance);
}

#endif
