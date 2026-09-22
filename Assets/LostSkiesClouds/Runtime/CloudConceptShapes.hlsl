#ifndef CLOUD_CONCEPT_SHAPES_INCLUDED
#define CLOUD_CONCEPT_SHAPES_INCLUDED

// mode 4 전용 계약입니다. 기존 CloudSculpting.hlsl과 CellRandom 선언 뒤에 포함합니다.
// Shape: 큰 형태 텍스처 주기(m), 운해 높이 변화(m), 렌더 변위(m), 조명 변위(m).
float4 _ConceptShape;

// Lighting: 큰 법선 혼합, 최대 차분 간격(m), 팔레트 대비, 공기 원근 강도.
float4 _ConceptLighting;

// Detail: 굴곡 텍스처 주기(m), 미세 변위(m), 지역 흐름 방향(rad), 능선 비중.
float4 _ConceptDetail;

static const float ConceptEmptyField = 1000000.0;

// 같은 월드 형태에서 렌더 밀도, 조명 밀도, 큰 법선을 파생하기 위한 공통 정보입니다.
// distance는 미터 단위 연속 스칼라이며 정확한 구 추적용 거리로 사용하지 않습니다.
struct ConceptShapeSample
{
    float distance;
    float boundaryField;
    float characteristicSize;
    float detailMask;
    float densityMultiplier;
};

// 비활성 형태를 안전한 양수 필드와 0 밀도로 초기화합니다.
ConceptShapeSample ConceptEmptySample()
{
    ConceptShapeSample sample;
    sample.distance = ConceptEmptyField;
    sample.boundaryField = -ConceptEmptyField;
    sample.characteristicSize = 1000;
    sample.detailMask = 0;
    sample.densityMultiplier = 0;
    return sample;
}

// 비균일 타원체를 미터 단위의 연속 근사 거리로 평가합니다.
float ConceptEllipsoidField(float3 position, float3 radius)
{
    radius = max(radius, 10);
    float normalizedLength = length(position / radius);
    float gradientLength = length(position / (radius * radius));
    if (gradientLength < 0.0000001)
    {
        return -min(radius.x, min(radius.y, radius.z));
    }

    return normalizedLength * (normalizedLength - 1) / gradientLength;
}

// 밀도 경계의 두께를 형태 크기에 맞추되 작은 구름에도 유효한 두께를 남깁니다.
float ConceptEdgeWidth(float characteristicSize)
{
    return clamp(_StyleShape.x * characteristicSize, 6, 70);
}

// 작은 구름의 변위가 전체 몸체보다 커지지 않도록 대표 크기에 맞춥니다.
float ConceptBillowBudget(float characteristicSize, bool useDetail)
{
    // 조명용 변위가 렌더 변위보다 커져 보존 지지 영역을 넘지 않도록 제한합니다.
    float amplitude = min(_ConceptShape.w, _ConceptShape.z);
    if (useDetail)
    {
        amplitude = _ConceptShape.z;
    }

    return min(amplitude, characteristicSize * 0.60);
}

// 바람과 원점 보정이 적용된 월드 좌표에서 렌더/조명 공통 저주파 굴곡을 읽습니다.
float ConceptBillowNoise(float3 position)
{
    float3 drift = float3(_Wind.x, 0, _Wind.z) * 0.13;
    float value = _SculptNoise.SampleLevel(sampler_linear_repeat,
        position / _ConceptDetail.x + drift, 1).r;
    return saturate(value) * 2 - 1;
}

// 렌더 밀도만 사용할 작은 굴곡입니다. 조명 밀도와 큰 법선에는 적용하지 않습니다.
float ConceptFineNoise(float3 position)
{
    float3 drift = float3(_Wind.x, 0, _Wind.z) * 0.13;
    float value = _SculptNoise.SampleLevel(sampler_linear_repeat,
        position / (_ConceptDetail.x * 0.43) + drift + 0.37, 1.5).r;
    return saturate(value) * 2 - 1;
}

// 넓은 높이장과 흐름 방향으로 늘어난 능선을 만들고 봉우리 선택 마스크를 함께 반환합니다.
float2 ConceptOceanHeight(float2 position)
{
    float sine, cosine;
    sincos(_ConceptDetail.z, sine, cosine);
    float2 flow = float2(position.x * cosine - position.y * sine,
        position.x * sine + position.y * cosine);

    float largeNoise = _SculptNoise.SampleLevel(sampler_linear_repeat,
        float3(position.x / _ConceptShape.x, 0.173, position.y / _ConceptShape.x), 1.5).r;
    float ridgeNoise = _SculptNoise.SampleLevel(sampler_linear_repeat,
        float3(flow.x * 0.55 / _ConceptShape.x, 0.613, flow.y * 1.55 / _ConceptShape.x), 1.5).r;

    float hills = smoothstep(0.20, 0.72, saturate(1 - largeNoise));
    float ridges = smoothstep(0.36, 0.76, saturate(1 - ridgeNoise));
    float terrain = saturate(hills * (1 - _ConceptDetail.w * 0.45)
        + ridges * _ConceptDetail.w * 0.45);
    float baseHeight = _OceanBounds.x + _OceanBounds.y * 0.48;
    float height = baseHeight + _ConceptShape.y * (0.05 + terrain * 0.95);
    float peakMask = smoothstep(0.50, 0.88, terrain);
    return float2(height, peakMask);
}

// 최대 봉우리, 날씨 팽창, 표면 변위를 포함하는 운해의 보수적인 상단 범위입니다.
float ConceptOceanTop()
{
    float spacing = max(3200, _ConceptShape.x * 0.22);
    float maximumRadius = spacing * 0.31;
    float maximumPeak = maximumRadius * 1.72;
    float baseHeight = _OceanBounds.x + _OceanBounds.y * 0.48;
    return baseHeight + _ConceptShape.y + maximumPeak + 600
        + _ConceptShape.z + _ConceptDetail.y + 140;
}

// 연속 운해에 넓은 골짜기와 선택적인 중간 봉우리를 결합합니다.
ConceptShapeSample ConceptSampleOcean(float3 position)
{
    ConceptShapeSample sample = ConceptEmptySample();
    if (_Ocean == 0 || position.y < _OceanBounds.x - 400 || position.y > ConceptOceanTop())
    {
        return sample;
    }

    float2 heightAndMask = ConceptOceanHeight(position.xz);
    float height = heightAndMask.x;
    float peakMask = heightAndMask.y;
    float field = position.y - height;
    float spacing = max(3200, _ConceptShape.x * 0.22);
    int2 cell = (int2)floor(position.xz / spacing);
    float2 center = (float2(cell) + 0.5) * spacing;
    center += (float2(CellRandom(cell, 202), CellRandom(cell, 203)) - 0.5) * spacing * 0.10;

    // 셀 경계에 닿지 않는 돌출만 추가합니다. 넓은 평탄부에는 작은 구슬을 반복하지 않습니다.
    float radius = spacing * lerp(0.20, 0.31, CellRandom(cell, 204));
    float rise = radius * lerp(0.55, 1.05, CellRandom(cell, 205));
    float prominence = smoothstep(0.25, 0.80, peakMask);
    float2 cellInterior = spacing * 0.5 - abs(position.xz - (float2(cell) + 0.5) * spacing);
    float supportReserve = _ConceptShape.z + _ConceptDetail.y + 70 + 425 + 120;
    float supportField = supportReserve - min(cellInterior.x, cellInterior.y);
    if (CellRandom(cell, 206) < 0.70 && prominence > 0.001)
    {
        float3 local = position - float3(center.x, height - rise * 0.45, center.y);
        float3 radius3 = float3(radius, rise * (0.32 + 0.68 * prominence), radius * 0.82);
        float lobe = ConceptEllipsoidField(local, radius3);
        lobe = max(lobe, supportField);
        field = SmoothCloudUnion(field, lobe, 100 * prominence);

        // 일부 봉우리는 큰 몸체가 위로 자라되 바닥 높이장과 계속 연결됩니다.
        if (CellRandom(cell, 207) < 0.28)
        {
            float towerRise = radius * (0.8 + prominence * 1.4);
            // 마스크가 0일 때 전체 타워가 표면 밑에 있어 분기 경계에서 갑자기 솟지 않습니다.
            float3 towerCenter = float3(center.x + radius * 0.10,
                height - radius * (0.95 - prominence * 0.47), center.y - radius * 0.08);
            float tower = ConceptEllipsoidField(position - towerCenter,
                float3(radius * 0.66, towerRise, radius * 0.63));
            tower = max(tower, supportField);
            field = SmoothCloudUnion(field, tower, 120 * prominence);
        }
    }

    float seaHeight = (position.y - _OceanBounds.x) / max(100, _OceanBounds.y);
    float coverage = clamp((_Layer.w - 0.165) * 2, -0.18, 0.25);
    field -= coverage * 1700 * smoothstep(0.16, 0.48, seaHeight);

    sample.distance = max(field, _OceanBounds.x - position.y);
    sample.characteristicSize = max(700, radius);
    sample.detailMask = lerp(0.18, 1, peakMask);
    sample.densityMultiplier = smoothstep(0, 0.12, seaHeight);
    return sample;
}

// 큰 몸체 1~3개를 역할별로 배치한 후 그 몸체에 종속된 어깨를 추가합니다.
float ConceptSkyBody(float3 position, float3 radius, int2 cell, bool useDetail, out float characteristicSize)
{
    float role = CellRandom(cell, 220);
    float3 centerA;
    float3 centerB;
    float3 centerC;
    float3 radiusA;
    float3 radiusB;
    float3 radiusC;

    if (role < 0.50)
    {
        if (CellRandom(cell, 227) < 0.58)
        {
            // 상부에 더 큰 질량을 둔 타워는 좁은 밑동과 넓은 어깨로 자랍니다.
            centerA = radius * float3(-0.20, -0.39, 0.06);
            centerB = radius * float3(0.03, -0.02, -0.09);
            centerC = radius * float3(0.12, 0.41, 0.08);
            radiusA = radius * float3(0.39, 0.31, 0.42);
            radiusB = radius * float3(0.52, 0.39, 0.49);
            radiusC = radius * float3(0.49, 0.35, 0.44);
        }
        else
        {
            // 다른 타워는 중심축을 비껴 성장하여 같은 수직 구슬 배열이 반복되지 않게 합니다.
            centerA = radius * float3(-0.20, -0.31, 0.12);
            centerB = radius * float3(0.14, 0.05, -0.09);
            centerC = radius * float3(0.32, 0.43, -0.11);
            radiusA = radius * float3(0.53, 0.39, 0.55);
            radiusB = radius * float3(0.48, 0.40, 0.44);
            radiusC = radius * float3(0.35, 0.31, 0.33);
        }
    }
    else if (role < 0.78)
    {
        // 넓은 구름둑은 하나의 큰 수평 질량과 높이가 다른 어깨로 구성합니다.
        centerA = radius * float3(0, -0.08, 0);
        centerB = radius * float3(-0.36, 0.09, 0.08);
        centerC = radius * float3(0.39, 0.03, -0.08);
        radiusA = radius * float3(0.73, 0.24, 0.56);
        radiusB = radius * float3(0.38, 0.29, 0.39);
        radiusC = radius * float3(0.34, 0.23, 0.36);
    }
    else
    {
        // 군집은 크기가 다른 두 몸체 사이에 오목한 빈 공간을 남깁니다.
        centerA = radius * float3(-0.22, -0.18, -0.04);
        centerB = radius * float3(0.25, 0.18, 0.10);
        centerC = radius * float3(-0.10, 0.54, -0.10);
        radiusA = radius * float3(0.47, 0.41, 0.52);
        radiusB = radius * float3(0.41, 0.40, 0.39);
        radiusC = radius * float3(0.28, 0.24, 0.27);
    }

    float growth = (CellRandom(cell, 221) - 0.5) * 0.32;
    float normalizedHeight = position.y / max(10, radius.y);
    position.x -= normalizedHeight * radius.x * growth;
    position.z -= normalizedHeight * radius.z * (CellRandom(cell, 222) - 0.5) * 0.22;
    float sizeVariation = lerp(0.87, 1.04, CellRandom(cell, 223));
    radiusA *= sizeVariation;
    radiusB *= lerp(0.88, 1.08, CellRandom(cell, 224));
    radiusC *= lerp(0.86, 1.10, CellRandom(cell, 225));

    characteristicSize = min(radiusA.x, min(radiusA.y, radiusA.z));
    float joinWidth = clamp(characteristicSize * 0.10, 25, 160);
    float field = ConceptEllipsoidField(position - centerA, radiusA);
    field = SmoothCloudUnion(field, ConceptEllipsoidField(position - centerB, radiusB), joinWidth);
    field = SmoothCloudUnion(field, ConceptEllipsoidField(position - centerC, radiusC), joinWidth);

    float3 shoulderA = centerA + radiusA * float3(-0.62, 0.30, 0.48);
    float3 shoulderB = centerB + radiusB * float3(0.61, 0.26, -0.50);
    float shoulder = ConceptEllipsoidField(position - shoulderA, radiusA * float3(0.48, 0.55, 0.46));
    field = SmoothCloudUnion(field, shoulder, joinWidth * 0.80);
    shoulder = ConceptEllipsoidField(position - shoulderB, radiusB * float3(0.46, 0.53, 0.49));
    field = SmoothCloudUnion(field, shoulder, joinWidth * 0.80);

    // 렌더 실루엣에만 종속된 중간 로브를 추가합니다. 큰 법선과 자기 그림자는 위 몸체를 공유합니다.
    if (useDetail)
    {
        [loop]
        for (int lobe = 0; lobe < 9; lobe++)
        {
            float3 parentCenter = centerA;
            float3 parentRadius = radiusA;
            if (lobe >= 6)
            {
                parentCenter = centerC;
                parentRadius = radiusC;
            }
            else if (lobe >= 3)
            {
                parentCenter = centerB;
                parentRadius = radiusB;
            }

            // 해시 방향을 정규화하므로 로브마다 삼각함수를 계산하지 않습니다.
            uint salt = 300u + (uint)lobe * 5u;
            float3 direction = float3(CellRandom(cell, salt),
                CellRandom(cell, salt + 1u), CellRandom(cell, salt + 2u)) * 2 - 1;
            direction *= rsqrt(max(0.0001, dot(direction, direction)));
            float attachment = lerp(0.78, 0.91, CellRandom(cell, salt + 3u));
            float parentSize = min(parentRadius.x, min(parentRadius.y, parentRadius.z));
            float lobeSize = parentSize * lerp(0.36, 0.56, CellRandom(cell, salt + 4u));
            float3 lobeCenter = parentCenter + parentRadius * direction * attachment;
            float3 lobeRadius = float3(lobeSize, lobeSize * 0.94, lobeSize * 1.06);
            float lobeField = ConceptEllipsoidField(position - lobeCenter, lobeRadius);
            field = SmoothCloudUnion(field, lobeField, joinWidth * 0.60);
        }
    }

    return field;
}

// 기존 셀 점유율, 시드, 반경 제한을 유지하면서 상층의 큰 몸체를 평가합니다.
ConceptShapeSample ConceptSampleSky(float3 position, bool useDetail)
{
    ConceptShapeSample sample = ConceptEmptySample();
    if (_SkyShape.w < 0.5 || _SkyPlacement.y <= 0 || _SkyPlacement.w <= 0 || _SkyDensityMultiplier <= 0)
    {
        return sample;
    }

    float spacing = max(4000, _SkyPlacement.x);
    int2 cell = (int2)floor(position.xz / spacing);
    if (CellRandom(cell, 0) >= _SkyPlacement.y)
    {
        return sample;
    }

    float2 jitter = (float2(CellRandom(cell, 1), CellRandom(cell, 2)) - 0.5) * 0.12;
    float2 center = (float2(cell) + 0.5 + jitter) * spacing;
    float altitude = lerp(_SkyAltitude.x, _SkyAltitude.y, CellRandom(cell, 3));
    float3 radius = float3(
        lerp(_SkyShape.x, _SkyShape.y, CellRandom(cell, 4)),
        lerp(_SkyAltitude.z, _SkyAltitude.w, CellRandom(cell, 6)),
        lerp(_SkyShape.x, _SkyShape.y, CellRandom(cell, 5)));
    radius.xz = min(radius.xz, spacing * 0.38);

    float3 local = position - float3(center.x, altitude, center.y);
    float maximumRadius = max(radius.x, radius.z);
    if (abs(local.y) > radius.y + 500 || any(abs(local.xz) > maximumRadius + 500))
    {
        return sample;
    }

    float sine, cosine;
    sincos(CellRandom(cell, 7) * 6.2831853, sine, cosine);
    local.xz = float2(local.x * cosine - local.z * sine, local.x * sine + local.z * cosine);

    float characteristicSize;
    float field = ConceptSkyBody(local, radius, cell, useDetail, characteristicSize);
    float edge = ConceptEdgeWidth(characteristicSize);
    float boundary = ConceptEllipsoidField(local, radius);
    // 지지 영역은 변위 후에 적용합니다. 변위를 키워도 큰 몸체 자체를 안쪽으로 깎지 않습니다.
    if (!useDetail)
    {
        field = max(field, boundary + edge);
    }

    sample.distance = field;
    sample.boundaryField = boundary;
    sample.characteristicSize = characteristicSize;
    sample.detailMask = lerp(0.72, 1, CellRandom(cell, 226));
    sample.densityMultiplier = _SkyPlacement.w * _SkyDensityMultiplier;
    return sample;
}

// 지역 흐름에 맞춘 띠에 천천히 변하는 폭, 굴곡, 실제 입체 두께를 부여합니다.
ConceptShapeSample ConceptSampleRibbon(float3 position)
{
    ConceptShapeSample sample = ConceptEmptySample();
    if (_SkyShape.w < 0.5 || _SkyPlacement.y <= 0 || _SkyPlacement.w <= 0
        || _SkyDensityMultiplier <= 0 || position.y < 3200 || position.y > 11600)
    {
        return sample;
    }

    float spacing = 14000;
    int2 cell = (int2)floor(position.xz / spacing);
    if (CellRandom(cell, 140) >= saturate(_SkyPlacement.y * (0.65 / 0.72)))
    {
        return sample;
    }

    float2 center = (float2(cell) + 0.5) * spacing;
    float centerHeight = lerp(4500, 9900, CellRandom(cell, 141));
    float3 local = position - float3(center.x, centerHeight, center.y);
    if (any(abs(local.xz) > 5900) || abs(local.y) > 1000)
    {
        return sample;
    }

    float angle = _ConceptDetail.z + (CellRandom(cell, 142) - 0.5) * 0.44;
    float sine, cosine;
    sincos(angle, sine, cosine);
    float2 rotated = float2(local.x * cosine - local.z * sine, local.x * sine + local.z * cosine);
    float lengthRadius = lerp(3500, 4900, CellRandom(cell, 143));
    float along = rotated.x / lengthRadius;
    float phase = CellRandom(cell, 147) * 6.2831853;
    float curve = sin(along * 2.4 + phase);
    float width = lerp(750, 1150, CellRandom(cell, 146)) * (0.88 + curve * 0.12);
    float thickness = lerp(180, 300, CellRandom(cell, 144));
    float centerCurve = curve * 140 + along * (CellRandom(cell, 145) - 0.5) * 240;
    float3 ribbonLocal = float3(rotated.x, local.y - centerCurve, rotated.y);
    float field = ConceptEllipsoidField(ribbonLocal, float3(lengthRadius, thickness, width));

    // 일부가 겹치는 낮은 층을 추가하여 하나의 타원판이 반복되는 인상을 줄입니다.
    float3 secondLocal = ribbonLocal - float3(lengthRadius * 0.18, -thickness * 0.48, width * 0.27);
    float second = ConceptEllipsoidField(secondLocal,
        float3(lengthRadius * 0.66, thickness * 0.77, width * 0.70));
    field = SmoothCloudUnion(field, second, 45);

    sample.distance = field;
    sample.characteristicSize = thickness;
    sample.detailMask = 0.68;
    sample.densityMultiplier = _SkyPlacement.w * _SkyDensityMultiplier * 0.7;
    return sample;
}

// 공통 큰 형태를 변위하고 부드러운 경계 밀도로 바꿉니다.
float ConceptResolveDensity(ConceptShapeSample sample, float billowNoise, float fineNoise, bool useDetail)
{
    if (sample.densityMultiplier <= 0)
    {
        return 0;
    }

    float displacement = billowNoise * ConceptBillowBudget(sample.characteristicSize, useDetail);
    if (useDetail)
    {
        displacement += fineNoise * min(_ConceptDetail.y, sample.characteristicSize * 0.14);
    }

    float edge = ConceptEdgeWidth(sample.characteristicSize);
    float field = sample.distance + displacement * sample.detailMask;
    // 최종 렌더/조명 밀도가 기존 지지 영역 밖으로 확장되지 않도록 동일한 경계를 사용합니다.
    field = max(field, sample.boundaryField + edge);
    return saturate(smoothstep(edge, -edge, field) * sample.densityMultiplier);
}

// 외부의 큰 법선 평가에 운해의 포화 전 스칼라장을 제공합니다.
float ConceptOceanField(float3 position)
{
    return ConceptSampleOcean(position).distance;
}

// 상층 몸체의 포화 전 미터 스칼라장을 제공합니다.
float ConceptSkyField(float3 position)
{
    return ConceptSampleSky(position, false).distance;
}

// 조명에서도 얇은 띠가 사라지지 않도록 실제 큰 띠 형태를 제공합니다.
float ConceptRibbonField(float3 position)
{
    return ConceptSampleRibbon(position).distance;
}

// 변위나 밀도 포화 전에 공통 큰 형태를 결합하여 안정된 넓은 법선을 만듭니다.
float ConceptMacroField(float3 position)
{
    float field = SmoothCloudUnion(ConceptOceanField(position), ConceptSkyField(position), 120);
    return SmoothCloudUnion(field, ConceptRibbonField(position), 45);
}

// 가장 가까운 실제 형태의 크기를 기준으로 법선 차분 간격을 제한합니다.
float ConceptNormalStep(float3 position)
{
    ConceptShapeSample nearest = ConceptSampleOcean(position);
    ConceptShapeSample sky = ConceptSampleSky(position, false);
    ConceptShapeSample ribbon = ConceptSampleRibbon(position);
    if (sky.distance < nearest.distance)
    {
        nearest = sky;
    }

    if (ribbon.distance < nearest.distance)
    {
        nearest = ribbon;
    }

    return clamp(_ConceptLighting.y, 8, max(8, nearest.characteristicSize * 0.20));
}

// 전체 생산용 밀도입니다. 렌더는 미세 변위를 포함하고 조명은 같은 큰 형태만 약하게 변위합니다.
float ConceptDensity(float3 position, bool useDetail)
{
    if (_Layer.z <= 0)
    {
        return 0;
    }

    ConceptShapeSample ocean = ConceptSampleOcean(position);
    ConceptShapeSample sky = ConceptSampleSky(position, useDetail);
    ConceptShapeSample ribbon = ConceptSampleRibbon(position);
    float maximumDisplacement = max(_ConceptShape.z, _ConceptShape.w) + _ConceptDetail.y + 70;
    if (min(ocean.distance, min(sky.distance, ribbon.distance)) > maximumDisplacement)
    {
        return 0;
    }

    float billow = ConceptBillowNoise(position);
    float fine = 0;
    if (useDetail)
    {
        fine = ConceptFineNoise(position);
    }

    float lower = ConceptResolveDensity(ocean, billow, fine, useDetail);
    float upper = ConceptResolveDensity(sky, billow, fine, useDetail);
    float thin = ConceptResolveDensity(ribbon, billow, fine, useDetail);
    return 1 - (1 - lower) * (1 - upper) * (1 - thin);
}

// 기존 ProbeDensity의 mainSky 계약을 유지하며 띠와 운해를 제외한 상층 밀도만 반환합니다.
float ConceptMainSkyDensity(float3 position, bool useDetail)
{
    ConceptShapeSample sky = ConceptSampleSky(position, useDetail);
    if (sky.densityMultiplier <= 0)
    {
        return 0;
    }

    float fine = 0;
    if (useDetail)
    {
        fine = ConceptFineNoise(position);
    }

    return ConceptResolveDensity(sky, ConceptBillowNoise(position), fine, useDetail);
}

// 전체 상층 검사에서만 더할 수 있도록 띠 밀도를 별도로 제공합니다.
float ConceptRibbonDensity(float3 position, bool useDetail)
{
    ConceptShapeSample ribbon = ConceptSampleRibbon(position);
    if (ribbon.densityMultiplier <= 0)
    {
        return 0;
    }

    float fine = 0;
    if (useDetail)
    {
        fine = ConceptFineNoise(position);
    }

    return ConceptResolveDensity(ribbon, ConceptBillowNoise(position), fine, useDetail);
}

#endif
