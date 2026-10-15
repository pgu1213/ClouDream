#ifndef CLOUD_HYBRID_SHAPE_INCLUDED
#define CLOUD_HYBRID_SHAPE_INCLUDED

// 공유 생성 계약: 절대 월드 미터입니다. 큰 형태는 불변이며 바람과 날씨는 조회 시 적용합니다.
Texture3D<float4> _SculptNoise;
SamplerState sampler_linear_repeat;
SamplerState sampler_linear_clamp;

float4 _OceanBounds, _SkyPlacement, _SkyAltitude, _SkyShape;
float4 _ConceptShape, _ConceptDetail, _ConceptLighting, _StyleShape;
float _SkyDensityMultiplier;
static const float ConceptEmptyField = 1000000.0;

// 형태 종류 / 절대 셀 X / 절대 셀 Z / 격자 간격 수입니다.
float4 _HybridObject;
float4 _HybridGridMin, _HybridGridSize;
float4 _HybridEnvironment; // density, upper density, coverage delta, extinction scale
float4 _HybridWind;

#include "CloudCellRandom.hlsl"

// 띠구름의 실제 고도 범위도 공유 GPU 난수에서 계산해 먼 LOD의 수직 표본 손실을 막습니다.
float3 HybridRegionMinimum()
{
    float3 minimum = _HybridGridMin.xyz;
    if (_HybridObject.x > 1.5)
    {
        minimum.y = lerp(4500, 9900, CellRandom((int2)_HybridObject.yz, 141)) - 1000;
    }

    return minimum;
}

// 정사각 텍스처 격자를 얇은 구름의 실제 수직 도메인에 대응시킵니다.
float3 HybridRegionSize()
{
    float3 size = _HybridGridSize.xyz;
    if (_HybridObject.x > 1.5)
    {
        size.y = 2000;
    }

    return size;
}

// 원래 Concept V2와 동일한 매끈한 몸체 결합입니다.
float SmoothCloudUnion(float first, float second, float radius)
{
    float blend = saturate(0.5 + 0.5 * (second - first) / radius);
    return lerp(second, first, blend) - radius * blend * (1 - blend);
}

float2 ConceptOceanHeight(float2 position);
#include "CloudConceptGeometry.hlsl"

// 메시 준비는 높이 근사 캐시 없이 원래 저주파 높이를 사용합니다.
float2 ConceptOceanHeight(float2 position)
{
    float2 terrain = ConceptOceanTerrain(position);
    return float2(_OceanBounds.x + _OceanBounds.y * 0.48 + _ConceptShape.y * terrain.x, terrain.y);
}

// 시드·회전·로브를 기존 공유 생성기로 평가하고 같은 지지 타원체로 제한합니다.
ConceptShapeSample HybridSkyBody(float3 position)
{
    ConceptShapeSample result = ConceptEmptySample();
    int2 cell = (int2)_HybridObject.yz;
    if (CellRandom(cell, 0) >= _SkyPlacement.y || _SkyPlacement.w <= 0)
    {
        return result;
    }

    float spacing = max(4000, _SkyPlacement.x);
    float2 jitter = (float2(CellRandom(cell, 1), CellRandom(cell, 2)) - 0.5) * 0.12;
    float2 center = (float2(cell) + 0.5 + jitter) * spacing;
    float altitude = lerp(_SkyAltitude.x, _SkyAltitude.y, CellRandom(cell, 3));
    float3 radius = float3(lerp(_SkyShape.x, _SkyShape.y, CellRandom(cell, 4)),
        lerp(_SkyAltitude.z, _SkyAltitude.w, CellRandom(cell, 6)),
        lerp(_SkyShape.x, _SkyShape.y, CellRandom(cell, 5)));
    radius.xz = min(radius.xz, spacing * 0.38);
    float3 local = position - float3(center.x, altitude, center.y);
    float sine, cosine;
    sincos(CellRandom(cell, 7) * 6.2831853, sine, cosine);
    local.xz = float2(local.x * cosine - local.z * sine, local.x * sine + local.z * cosine);
    float macroField;
    result.distance = ConceptSkyBodyFields(local, radius, cell, true, result.characteristicSize, macroField);
    float edge = clamp(_StyleShape.x * result.characteristicSize, 6, 70);
    result.boundaryField = ConceptEllipsoidField(local, radius) + edge;
    result.distance = max(result.distance, result.boundaryField);
    result.detailMask = lerp(0.72, 1, CellRandom(cell, 226));
    result.densityMultiplier = _SkyPlacement.w;
    return result;
}

// 셀의 큰 형태를 샘플링합니다. RGBA = 미터 스칼라, 크기, 변위 마스크, 기본 밀도 배수입니다.
float4 HybridBaseField(float3 position)
{
    ConceptShapeSample sample;
    if (_HybridObject.x < 0.5)
    {
        sample = ConceptSampleOceanBody(position, true);
        sample.distance = max(sample.distance, _OceanBounds.x - position.y);
    }
    else if (_HybridObject.x < 1.5)
    {
        sample = HybridSkyBody(position);
    }
    else
    {
        sample = ConceptSampleRibbon(position);
    }

    return float4(clamp(sample.distance, -60000, 60000), sample.characteristicSize,
        sample.detailMask, sample.densityMultiplier);
}

Texture3D<float4> _HybridField;

// 미터 좌표를 텍셀 중심에 맞춰 읽습니다. 외부는 빈 공간이며 clamp로 구름을 연장하지 않습니다.
float4 HybridReadField(float3 position)
{
    float3 relative = (position - HybridRegionMinimum()) / HybridRegionSize();
    if (any(relative < 0) || any(relative > 1))
    {
        if (_HybridObject.x < 0.5)
        {
            return HybridBaseField(position);
        }

        return float4(60000, 1000, 0, 0);
    }

    float side = _HybridObject.w + 1;
    return _HybridField.SampleLevel(sampler_linear_clamp, (relative * _HybridObject.w + 0.5) / side, 0);
}

// 바람은 노이즈 좌표만 이동합니다. 전체 구름 몸체의 강체 이동으로 바꾸지 않습니다.
float HybridDisplacement(float3 position, float4 sample)
{
    float3 drift = float3(_HybridWind.x, 0, _HybridWind.z) * 0.13;
    float broad = _SculptNoise.SampleLevel(sampler_linear_repeat, position / _ConceptDetail.x + drift, 1).r;
    float fine = _SculptNoise.SampleLevel(sampler_linear_repeat,
        position / (_ConceptDetail.x * 0.43) + drift + 0.37, 1.5).r;
    float value = (broad * 2 - 1) * min(_ConceptShape.z, sample.y * 0.60);
    value += (fine * 2 - 1) * min(_ConceptDetail.y, sample.y * 0.14);
    if (_HybridObject.x < 0.5)
    {
        float height = (position.y - _OceanBounds.x) / max(100, _OceanBounds.y);
        value -= clamp(_HybridEnvironment.z * 2, -0.18, 0.25) * 1700 * smoothstep(0.16, 0.48, height);
    }

    return value * sample.z;
}

// 동일한 큰 형태와 변위에서 부드러운 밀도를 계산합니다. 스칼라를 구 추적 거리로 사용하지 않습니다.
float HybridDensity(float3 position)
{
    float4 sample = HybridReadField(position);
    if (sample.w <= 0 || sample.x > _ConceptShape.z + _ConceptDetail.y + 100)
    {
        return 0;
    }

    float edge = clamp(_StyleShape.x * sample.y, 6, 70);
    float field = sample.x + HybridDisplacement(position, sample);
    float multiplier = sample.w * _HybridEnvironment.x;
    if (_HybridObject.x > 0.5)
    {
        multiplier *= _HybridEnvironment.y;
    }
    else
    {
        field = max(field, _OceanBounds.x - position.y);
        multiplier *= smoothstep(0, 0.12, (position.y - _OceanBounds.x) / max(100, _OceanBounds.y));
    }

    return saturate(smoothstep(edge, -edge, field) * multiplier);
}

// 큰 형태의 차분 법선만 사용합니다. 유한하지 않은 중심에서는 위쪽 법선으로 복귀합니다.
float3 HybridNormal(float3 position)
{
    float3 step = max(2, HybridRegionSize() / max(16, _HybridObject.w));
    float3 gradient;
    gradient.x = (HybridReadField(position + float3(step.x, 0, 0)).x
        - HybridReadField(position - float3(step.x, 0, 0)).x) / (2 * step.x);
    gradient.y = (HybridReadField(position + float3(0, step.y, 0)).x
        - HybridReadField(position - float3(0, step.y, 0)).x) / (2 * step.y);
    gradient.z = (HybridReadField(position + float3(0, 0, step.z)).x
        - HybridReadField(position - float3(0, 0, step.z)).x) / (2 * step.z);
    if (dot(gradient, gradient) < 0.000001)
    {
        return float3(0, 1, 0);
    }

    return normalize(gradient);
}

// 연속 운해의 최상단 교차만 이분 탐색합니다. 구 추적이나 거리 기반 도약은 사용하지 않습니다.
float HybridOceanSurfaceHeight(float2 position)
{
    float lower = ConceptOceanHeight(position).x;
    float upper = _HybridGridMin.y + _HybridGridSize.y;
    [unroll]
    for (int iteration = 0; iteration < 12; iteration++)
    {
        float midpoint = (lower + upper) * 0.5;
        if (HybridBaseField(float3(position.x, midpoint, position.y)).x <= 0)
        {
            lower = midpoint;
        }
        else
        {
            upper = midpoint;
        }
    }

    return (lower + upper) * 0.5;
}

// 운해는 XZ를 움직이지 않고 높이만 변위해 이웃 타일에 수평 틈을 만들지 않습니다.
float HybridOceanDeformedHeight(float2 position)
{
    float height = HybridOceanSurfaceHeight(position);
    float3 surfacePosition = float3(position.x, height, position.y);
    return height - HybridDisplacement(surfacePosition, HybridBaseField(surfacePosition));
}

// LOD 경계는 공통 16분할 격자의 두 앵커를 보간합니다. 바람 변위도 앵커에서 먼저 계산합니다.
float HybridOceanEdgeHeight(float2 position)
{
    float2 relative = (position - _HybridGridMin.xz) / _HybridGridSize.xz;
    float2 border = min(relative, 1 - relative);
    float step = _HybridGridSize.x / 16;
    if (border.x < 0.00001)
    {
        float lower = floor(position.y / step) * step;
        return lerp(HybridOceanDeformedHeight(float2(position.x, lower)),
            HybridOceanDeformedHeight(float2(position.x, lower + step)), saturate((position.y - lower) / step));
    }

    if (border.y < 0.00001)
    {
        float lower = floor(position.x / step) * step;
        return lerp(HybridOceanDeformedHeight(float2(lower, position.y)),
            HybridOceanDeformedHeight(float2(lower + step, position.y)), saturate((position.x - lower) / step));
    }

    return HybridOceanDeformedHeight(position);
}

#endif
