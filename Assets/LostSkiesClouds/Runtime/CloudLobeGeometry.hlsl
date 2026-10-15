#ifndef CLOUD_LOBE_GEOMETRY_INCLUDED
#define CLOUD_LOBE_GEOMETRY_INCLUDED

float4 _LobeBody, _LobeDetail;

// B안은 원본 위치·시드만 공유하고 더 둥근 몸체와 다섯 어깨를 새로 설계합니다.
void LobeParameters(int2 cell, float3 radius, int index, out float3 center, out float3 extent)
{
    ConceptBodyParameters original = ConceptBuildBody(radius, cell);
    float3 centers[3] = { original.centerA, original.centerB, original.centerC };
    float3 radii[3] = { original.radiusA, original.radiusB, original.radiusC };
    int parent = index % 3;
    center = centers[parent];
    extent = radii[parent];
    extent = lerp(extent, min(extent.x, extent.z).xxx, 0.18);
    if (index >= 3)
    {
        ConceptBuildLobe(cell, index + 11, centers[parent], radii[parent], center, extent);
        extent *= _LobeBody.w;
    }
    center *= float3(_LobeBody.x, _LobeBody.y, _LobeBody.x);
    extent *= float3(_LobeBody.x, _LobeBody.y, _LobeBody.x);
}

// 배치 계약은 A와 같고, 메모리에 3D texture를 보관하지 않습니다.
void LobeCellTransform(int2 cell, out float3 center, out float3 radius, out float2 rotation)
{
    float spacing = max(4000, _SkyPlacement.x);
    float2 jitter = (float2(CellRandom(cell, 1), CellRandom(cell, 2)) - 0.5) * 0.12;
    float2 horizontal = (float2(cell) + 0.5 + jitter) * spacing;
    center = float3(horizontal.x, lerp(_SkyAltitude.x, _SkyAltitude.y, CellRandom(cell, 3)), horizontal.y);
    radius = float3(lerp(_SkyShape.x, _SkyShape.y, CellRandom(cell, 4)),
        lerp(_SkyAltitude.z, _SkyAltitude.w, CellRandom(cell, 6)),
        lerp(_SkyShape.x, _SkyShape.y, CellRandom(cell, 5)));
    radius.xz = min(radius.xz, spacing * 0.38);
    sincos(CellRandom(cell, 7) * 6.2831853, rotation.x, rotation.y);
}

// 여덟 해석적 타원체로 큰 형태를 정의합니다. 스칼라는 정확한 SDF가 아닙니다.
float4 LobeField(float3 position, int kind, int2 cell)
{
    if (kind == 0)
    {
        ConceptShapeSample ocean = ConceptSampleOceanBody(position, true);
        return float4(max(ocean.distance, _OceanBounds.x - position.y), ocean.characteristicSize, ocean.detailMask, 1);
    }
    if (kind == 2)
    {
        ConceptShapeSample ribbon = ConceptSampleRibbon(position);
        return float4(ribbon.distance, ribbon.characteristicSize, 0.35, ribbon.densityMultiplier);
    }
    if (CellRandom(cell, 0) >= _SkyPlacement.y || _SkyPlacement.w <= 0)
    {
        return float4(60000, 1000, 0, 0);
    }
    float3 center, radius;
    float2 rotation;
    LobeCellTransform(cell, center, radius, rotation);
    float3 local = position - center;
    local.xz = float2(local.x * rotation.y - local.z * rotation.x,
        local.x * rotation.x + local.z * rotation.y);
    float field = 60000;
    float characteristic = min(radius.x, min(radius.y, radius.z)) * 0.45;
    [loop]
    for (int index = 0; index < 8; index++)
    {
        float3 lobeCenter, extent;
        LobeParameters(cell, radius, index, lobeCenter, extent);
        float value = ConceptEllipsoidField(local - lobeCenter, extent);
        field = SmoothCloudUnion(field, value, max(30, characteristic * _LobeBody.z));
    }
    return float4(field, characteristic, 0.5, _SkyPlacement.w);
}

// 작은 변위만 더해 큰 명암 면과 실루엣을 보존합니다.
float LobeDisplacement(float3 position, float4 field, int kind)
{
    if (kind == 0)
    {
        return HybridDisplacementForKind(position, field, kind);
    }
    float3 drift = float3(_HybridWind.x, 0, _HybridWind.z) * 0.13;
    float noise = _SculptNoise.SampleLevel(sampler_linear_repeat, position / _ConceptDetail.x + drift, 1).r;
    return (noise * 2 - 1) * _LobeDetail.x * field.z;
}

// B안 표면과 내부 적분은 같은 해석적 밀도를 사용합니다.
float LobeDensityFromField(float3 position, int kind, float4 field)
{
    float scalar = field.x + LobeDisplacement(position, field, kind);
    float multiplier = field.w * _HybridEnvironment.x;
    if (kind == 0)
    {
        scalar = max(scalar, _OceanBounds.x - position.y);
        multiplier *= smoothstep(0, 0.12, (position.y - _OceanBounds.x) / max(100, _OceanBounds.y));
    }
    else
    {
        multiplier *= _HybridEnvironment.y;
    }
    float edge = clamp(field.y * 0.025, 12, 70);
    return saturate(smoothstep(edge, -edge, scalar) * multiplier);
}

// 미리 준비된 로브와 직접 평가한 로브가 같은 밀도 변환을 사용합니다.
float LobeDensity(float3 position, int kind, int2 cell)
{
    return LobeDensityFromField(position, kind, LobeField(position, kind, cell));
}

// 표면과 적분의 밀도 변환 앞에 있는 동일한 스칼라를 미분합니다.
float LobeDisplacedScalar(float3 position, int kind, int2 cell)
{
    float4 field = LobeField(position, kind, cell);
    float scalar = field.x + LobeDisplacement(position, field, kind);
    if (kind == 0)
    {
        scalar = max(scalar, _OceanBounds.x - position.y);
    }
    return scalar;
}

// 실제 변위의 월드 미터 기울기를 네 표본으로 구합니다. 셸별 구 법선으로 조명하지 않습니다.
float3 LobeLightingNormal(float3 position, int kind, int2 cell)
{
    float step = 8;
    return HybridTetrahedralNormal(float4(
        LobeDisplacedScalar(position + float3(1, -1, -1) * step, kind, cell),
        LobeDisplacedScalar(position + float3(-1, -1, 1) * step, kind, cell),
        LobeDisplacedScalar(position + float3(-1, 1, -1) * step, kind, cell),
        LobeDisplacedScalar(position + float3(1, 1, 1) * step, kind, cell)));
}

// 같은 smooth union 가중치로 타원체의 해석적 기울기를 결합해 로브 접합의 명암 이음새를 줄입니다.
float3 LobeSurfaceNormal(float3 position, int2 cell)
{
    float3 center, radius;
    float2 rotation;
    LobeCellTransform(cell, center, radius, rotation);
    float3 local = position - center;
    local.xz = float2(local.x * rotation.y - local.z * rotation.x,
        local.x * rotation.x + local.z * rotation.y);
    float characteristic = min(radius.x, min(radius.y, radius.z)) * 0.45;
    float join = max(30, characteristic * _LobeBody.z);
    float field = 60000;
    float3 gradient = 0;
    [loop]
    for (int index = 0; index < 8; index++)
    {
        float3 lobeCenter, extent;
        LobeParameters(cell, radius, index, lobeCenter, extent);
        float3 delta = local - lobeCenter;
        float value = ConceptEllipsoidField(delta, extent);
        float normalizedLength = length(delta / extent);
        float gradientLength = length(delta / (extent * extent));
        float3 derivative = 0;
        if (gradientLength >= 0.0000001 && normalizedLength > 0.00001)
        {
            float3 firstGradient = delta / (extent * extent * normalizedLength);
            float3 secondGradient = delta / (extent * extent * extent * extent * gradientLength);
            derivative = (2 * normalizedLength - 1) / gradientLength * firstGradient
                - normalizedLength * (normalizedLength - 1) / (gradientLength * gradientLength) * secondGradient;
        }
        float blend = saturate(0.5 + 0.5 * (value - field) / join);
        gradient = lerp(derivative, gradient, blend);
        field = SmoothCloudUnion(field, value, join);
    }
    if (dot(gradient, gradient) < 0.00001)
    {
        return float3(0, 1, 0);
    }
    gradient.xz = float2(gradient.x * rotation.y + gradient.z * rotation.x,
        -gradient.x * rotation.x + gradient.z * rotation.y);
    return normalize(gradient);
}

// 카메라에서 큰 표면까지의 보수적 전환 값입니다. 구 추적 도약에는 쓰지 않습니다.
float LobeLocalWeight(int kind, int2 cell)
{
    float4 field = LobeField(_HybridCamera.xyz, kind, cell);
    float distance = max(0, field.x - _LobeDetail.x - 70);
    return 1 - smoothstep(_HybridQuality.x, _HybridQuality.y, distance);
}

#endif
