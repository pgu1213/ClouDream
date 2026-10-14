// 월드 공간의 매끈한 볼륨을 구성합니다. 카메라 방향이나 화면 좌표에 의존하지 않습니다.
int _StyleMode;
float4 _StyleShape;
float4 _StyleLight;
float4 _StyleDetail;
Texture3D<float4> _SculptNoise;

// 겹치는 덩어리의 연결부를 둥글게 만들어 구슬이 따로 놓인 인상을 줄입니다.
float SmoothCloudUnion(float first, float second, float radius)
{
    float blend = saturate(0.5 + 0.5 * (second - first) / radius);
    return lerp(second, first, blend) - radius * blend * (1 - blend);
}

// 미세 침식 대신 저주파 변위만 남겨 큰 면을 보존합니다.
float CloudSurfaceVariation(float3 position)
{
    float3 drift = float3(_Wind.x, 0, _Wind.z) * 0.13;
    return _StructureNoise.SampleLevel(sampler_linear_repeat, position / 14500 + drift, _StyleShape.z).r - 0.5;
}

// 거리장을 유한 두께의 부드러운 볼륨 경계로 바꿉니다. 내부에서는 밀도가 유지됩니다.
float SculptedDensity(float distance, float3 position, bool useDetail)
{
    // 지지 영역 밖은 원래도 0입니다. 텍스처를 읽기 전에 같은 결과로 종료합니다.
    if (distance >= 0.15)
    {
        return 0;
    }

    // 저주파 단일 Worley는 미세한 침식 없이 중간 크기의 둥근 표면 굴곡을 제공합니다.
    float3 drift = float3(_Wind.x, 0, _Wind.z) * 0.13;
    float billows = _SculptNoise.SampleLevel(sampler_linear_repeat, position / _StyleDetail.x + drift, 0.8).r;
    float support = 1 - smoothstep(0.025, 0.15, distance);
    distance += (billows - 0.44) * _StyleDetail.y;
    float fineBillows = _SculptNoise.SampleLevel(sampler_linear_repeat, position / _StyleDetail.z + drift + 0.37, 1).r;
    distance += (fineBillows - 0.44) * _StyleDetail.w;
    if (useDetail)
    {
        distance += CloudSurfaceVariation(position) * _StyleShape.y;
    }

    return smoothstep(_StyleShape.x, -_StyleShape.x, distance) * support;
}

// 높이별 크기와 중심축이 다른 둥근 로브를 겹쳐 하나의 입체 구름을 만듭니다.
float TowerDistance(float3 position, int2 cell)
{
    // 덩어리마다 장축의 기울기와 무게중심을 바꾸어 같은 수직 기둥의 반복을 줄입니다.
    position.x -= position.y * (CellRandom(cell, 126) - 0.5) * 0.7;
    position.z -= position.y * (CellRandom(cell, 127) - 0.5) * 0.6;
    float aspect = CellRandom(cell, 128);
    if (aspect < 0.30)
    {
        position.y *= 1.45;
    }

    float distance = 10;
    float phase = CellRandom(cell, 51) * 6.2831853;
    [loop]
    for (int lobe = 0; lobe < 7; lobe++)
    {
        float level = lobe / 6.0;
        float angle = phase + lobe * 2.399963;
        float weight = lerp(0.38, 0.25, level);
        if (aspect > 0.75)
        {
            weight = lerp(0.28, 0.38, level);
        }

        float radius = weight * lerp(0.85, 1.16, CellRandom(cell, 60 + lobe));
        float verticalOffset = (CellRandom(cell, 90 + lobe) - 0.5) * 0.14;
        float3 center = float3(cos(angle) * 0.32, -0.48 + level * 1.12 + verticalOffset, sin(angle) * 0.32);
        float3 offset = position - center;
        offset.y *= lerp(1.05, 0.8, CellRandom(cell, 80 + lobe));
        float lobeDistance = length(offset) - radius;
        distance = SmoothCloudUnion(distance, lobeDistance, 0.08);
    }

    return distance;
}

// 상층 윤곽을 셀 안에 유지하고 회전 시에도 같은 둥근 가지와 뒷면을 보여줍니다.
float SculptedSkyDensity(float3 normalizedPosition, float3 worldPosition, int2 cell, bool useDetail)
{
    float envelopeRadius = length(normalizedPosition);
    if (envelopeRadius >= 1)
    {
        return 0;
    }

    float distance;
    if (_StyleMode == 3 && CellRandom(cell, 95) < 0.25)
    {
        float3 lens = normalizedPosition;
        lens.y *= 3.6;
        distance = length(lens) - 0.83;
    }
    else
    {
        distance = TowerDistance(normalizedPosition, cell);
    }

    // 기본 반경 밖에서는 자연스럽게 0이 되어 셀 경계 절단면이 생기지 않습니다.
    distance = max(distance, (envelopeRadius - 0.96) * 0.7);
    float support = 1 - smoothstep(0.86, 1.0, envelopeRadius);
    return SculptedDensity(distance, worldPosition, useDetail) * support;
}

// 연속 바닥 위에 넓고 낮은 타원체를 겹쳐 구름 바다의 둥근 표면을 만듭니다.
float SculptedOceanDensity(float3 position, bool useDetail)
{
    float seaHeight = (position.y - _OceanBounds.x) / _OceanBounds.y;
    if (_Ocean == 0 || seaHeight <= 0)
    {
        return 0;
    }

    float spacing = _StyleShape.w;
    float baseHeight = _OceanBounds.x + _OceanBounds.y * 0.48;
    if (position.y > baseHeight + 4000)
    {
        return 0;
    }

    float distance = (position.y - baseHeight) / spacing;
    int2 baseCell = (int2)floor(position.xz / spacing);

    [loop]
    for (int z = -1; z <= 1; z++)
    {
        [loop]
        for (int x = -1; x <= 1; x++)
        {
            int2 cell = baseCell + int2(x, z);
            float2 center = (float2(cell) + 0.5) * spacing;
            center += (float2(CellRandom(cell, 102), CellRandom(cell, 103)) - 0.5) * spacing * 0.25;
            float radius = lerp(0.40, 1.05, CellRandom(cell, 104));
            float rise = lerp(0.30, 1.10, CellRandom(cell, 105));
            float3 local = float3((position.xz - center) / spacing, 0).xzy;
            local.y = (position.y - baseHeight) / spacing + 0.22;
            local.y *= radius / rise;
            float lobe = length(local) - radius;
            distance = SmoothCloudUnion(distance, lobe, 0.14);
        }
    }

    // 드문 저층 타워는 운해와 이어져 수평면에 세로 리듬을 더합니다.
    float towerSpacing = spacing * 4.2;
    int2 towerCell = (int2)floor(position.xz / towerSpacing);
    if (CellRandom(towerCell, 111) < 0.62)
    {
        float2 center = (float2(towerCell) + 0.5) * towerSpacing;
        float3 local = float3((position.xz - center) / (towerSpacing * 0.36), 0).xzy;
        float radiusY = lerp(1500, 2400, CellRandom(towerCell, 112));
        local.y = (position.y - (baseHeight + radiusY * 0.45)) / radiusY;
        if (length(local.xz) < 1.1 && abs(local.y) < 1.1)
        {
            float tower = TowerDistance(local, towerCell);
            distance = SmoothCloudUnion(distance, tower, 0.12);
        }
    }

    // 기존 날씨의 coverageOffset을 둥근 표면의 부피 변화로 연결하되 깊은 연속 바닥은 유지합니다.
    float coverageAdjustment = clamp((_Layer.w - 0.165) * 2, -0.18, 0.25);
    distance -= coverageAdjustment * smoothstep(0.16, 0.48, seaHeight);
    float density = SculptedDensity(distance, position, useDetail);
    return density * smoothstep(0, 0.12, seaHeight);
}

// 얇고 긴 구름도 실제 두께와 굴곡이 있는 3D 밀도로 만들며 앞뒤에서 동일하게 보입니다.
float CloudRibbonDensity(float3 position)
{
    if (_StyleMode != 3 || _SkyShape.w < 0.5 || _SkyPlacement.y <= 0 || _SkyPlacement.w <= 0
        || _SkyDensityMultiplier <= 0 || position.y < 3200 || position.y > 11600)
    {
        return 0;
    }

    float spacing = 14000;
    int2 cell = (int2)floor(position.xz / spacing);
    // 기본 상층 확률 0.72에서는 기존 띠구름 확률 0.65를 유지합니다.
    float ribbonOccupancy = saturate(_SkyPlacement.y * (0.65 / 0.72));
    if (CellRandom(cell, 140) > ribbonOccupancy)
    {
        return 0;
    }

    float2 center = (float2(cell) + 0.5) * spacing;
    float centerHeight = lerp(4100, 10200, CellRandom(cell, 141));
    float3 local = position - float3(center.x, centerHeight, center.y);
    if (any(abs(local.xz) > 6000) || abs(local.y) > 950)
    {
        return 0;
    }

    // 각각의 층운에 다른 방향과 기울기를 주어 일정 고도의 평행 줄무늬를 만들지 않습니다.
    float sine, cosine;
    sincos(CellRandom(cell, 142) * 6.2831853, sine, cosine);
    float2 rotated = float2(local.x * cosine - local.z * sine, local.x * sine + local.z * cosine);
    float radius = lerp(3300, 5100, CellRandom(cell, 143));
    float thickness = lerp(130, 260, CellRandom(cell, 144));
    float slope = (CellRandom(cell, 145) - 0.5) * 0.10;
    float3 shapePosition = float3(rotated.x / radius, (local.y + rotated.x * slope) / thickness, rotated.y / 1200);
    float envelope = length(shapePosition);
    if (envelope > 1.2)
    {
        return 0;
    }

    float billows = _SculptNoise.SampleLevel(sampler_linear_repeat, position / 2700, 1).r;
    float distance = envelope - 0.90 + (billows - 0.44) * 0.50;
    return smoothstep(0.12, -0.12, distance) * _SkyPlacement.w * _SkyDensityMultiplier * 0.7;
}
