#ifndef CLOUD_CONCEPT_SHAPES_INCLUDED
#define CLOUD_CONCEPT_SHAPES_INCLUDED

// mode 4 전용 계약입니다. 기존 CloudSculpting.hlsl과 CellRandom 선언 뒤에 포함합니다.
// Shape: 큰 형태 텍스처 주기(m), 운해 높이 변화(m), 렌더 변위(m), 조명 변위(m).
float4 _ConceptShape;

// Lighting: 큰 법선 혼합, 최대 차분 간격(m), 팔레트 대비, 공기 원근 강도.
float4 _ConceptLighting;

// Detail: 굴곡 텍스처 주기(m), 미세 변위(m), 지역 흐름 방향(rad), 능선 비중.
float4 _ConceptDetail;

// 법선 결과 캐시 생성/정확 fallback은 다른 형태·높이 근사 캐시를 재사용하지 않습니다.
#if defined(CLOUD_EXACT_NORMAL_PRODUCER)
// 생성 커널은 정확 생산만 컴파일하여 불필요한 캐시 분기 분석을 없앱니다.
#define CloudNormalProducerExactOnly true
#elif defined(CLOUD_NORMAL_FIELD_CACHE) || defined(CLOUD_NORMAL_FIELD_PROBE)
static bool CloudNormalProducerExactOnly = false;
#else
// 일반 OFF 광선에서는 정확 생산 전환 자체가 없어 기존 경로만 남습니다.
#define CloudNormalProducerExactOnly false
#endif

// XZ 격자의 R은 정규화한 높이, G는 봉우리 마스크입니다. 실제 고도는 조회 시 현재 프로필로 복원합니다.
Texture2D<float2> _CloudOceanHeightCache;
RWTexture2D<float2> _CloudOceanHeightWrite;
float4 _CloudOceanHeightRegion;

static const float ConceptEmptyField = 1000000.0;

// 캐시와 하이브리드가 같은 원식을 사용합니다. 높이 조회는 아래에서 정의합니다.
float2 ConceptOceanHeight(float2 position);
#include "CloudConceptGeometry.hlsl"







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



// 월드에 고정된 정수 격자를 생성합니다. 높이·날씨·바람 변화는 이 정규화 필드를 다시 만들 필요가 없습니다.
[numthreads(8, 8, 1)]
void BuildOceanHeight(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= (uint)_CloudOceanHeightRegion.w))
    {
        return;
    }

    float2 position = _CloudOceanHeightRegion.xy + id.xy * _CloudOceanHeightRegion.z;
    _CloudOceanHeightWrite[id.xy] = ConceptOceanTerrain(position);
}

// 캐시 밖과 2 texel 경계는 원래 함수를 사용합니다. 경계 보간은 연속 운해의 이음새를 억제합니다.
float2 ConceptOceanHeight(float2 position)
{
    if (CloudNormalProducerExactOnly)
    {
        float2 exactTerrain = ConceptOceanTerrain(position);
        float exactHeight = _OceanBounds.x + _OceanBounds.y * 0.48 + _ConceptShape.y * exactTerrain.x;
        return float2(exactHeight, exactTerrain.y);
    }

    float2 terrain;
    #ifdef CLOUD_OCEAN_HEIGHT_CACHE
    float2 pixel = (position - _CloudOceanHeightRegion.xy) / _CloudOceanHeightRegion.z;
    float2 border = min(pixel, _CloudOceanHeightRegion.w - 1 - pixel);
    float edge = min(border.x, border.y);
    if (edge > 0)
    {
        terrain = _CloudOceanHeightCache.SampleLevel(sampler_linear_clamp,
            (pixel + 0.5) / _CloudOceanHeightRegion.w, 0);
        if (edge < 2)
        {
            terrain = lerp(ConceptOceanTerrain(position), terrain, smoothstep(0, 2, edge));
        }
    }
    else
    {
        terrain = ConceptOceanTerrain(position);
    }
    #else
    terrain = ConceptOceanTerrain(position);
    #endif

    float baseHeight = _OceanBounds.x + _OceanBounds.y * 0.48;
    return float2(baseHeight + _ConceptShape.y * terrain.x, terrain.y);
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



#include "CloudOceanShapeFieldCache.hlsl"

// 연속 운해의 날씨 팽창·하단 경계·밀도를 현재 프레임에 적용하고 캐시 밖에서는 원식으로 복귀합니다.
ConceptShapeSample ConceptSampleOcean(float3 position)
{
    if (_Ocean == 0 || position.y < _OceanBounds.x - 400 || position.y > ConceptOceanTop())
    {
        return ConceptEmptySample();
    }

    ConceptShapeSample cached;
    float weight;
    bool available = ConceptTryOceanShapeField(position, cached, weight);
    ConceptShapeSample sample;
    if (available && weight >= 1)
    {
        sample = cached;
    }
    else
    {
        sample = ConceptSampleOceanBody(position, false);
        if (available)
        {
            sample.distance = lerp(sample.distance, cached.distance, weight);
            sample.characteristicSize = lerp(sample.characteristicSize, cached.characteristicSize, weight);
            sample.detailMask = lerp(sample.detailMask, cached.detailMask, weight);
        }
    }

    float seaHeight = (position.y - _OceanBounds.x) / max(100, _OceanBounds.y);
    float coverage = clamp((_Layer.w - 0.165) * 2, -0.18, 0.25);
    float field = sample.distance - coverage * 1700 * smoothstep(0.16, 0.48, seaHeight);
    sample.distance = max(field, _OceanBounds.x - position.y);
    sample.densityMultiplier = smoothstep(0, 0.12, seaHeight);
    return sample;
}









// 기존 호출 계약을 유지하고 캐시 생성에서만 큰 몸체 결과를 함께 재사용합니다.
float ConceptSkyBody(float3 position, float3 radius, int2 cell, bool useDetail, out float characteristicSize)
{
    float macroField;
    return ConceptSkyBodyFields(position, radius, cell, useDetail, characteristicSize, macroField);
}

#include "CloudConceptCellCache.hlsl"
#include "CloudShapeFieldCache.hlsl"

// 기존 셀 점유율, 시드, 반경 제한을 유지하면서 상층의 큰 몸체를 평가합니다.
ConceptShapeSample ConceptSampleSky(float3 position, bool useDetail, bool densityOnly = false)
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

    float boundary = ConceptEllipsoidField(local, radius);
    // 최종 밀도는 boundary + edge로 잘립니다. 그 밖의 점은 모든 변위에서도 밀도가 0입니다.
    // 큰 법선은 포화 전 필드가 필요하므로 densityOnly 조회에만 이 검사를 적용합니다.
    if (_CloudSupportRejection != 0 && densityOnly && boundary >= 0)
    {
        return sample;
    }

    float characteristicSize;
    float field;
    float cachedField;
    float cachedSize;
    float cachedWeight;
    bool fieldAvailable = ConceptTryShapeField(local, radius, cell, position, useDetail,
        cachedField, cachedSize, cachedWeight);
    if (fieldAvailable && cachedWeight >= 1)
    {
        field = cachedField;
        characteristicSize = cachedSize;
    }
    else
    {
        int cacheOffset;
        if (!CloudNormalProducerExactOnly && ConceptTryCellOffset(cell, cacheOffset))
        {
            field = ConceptCachedSkyBody(local, radius, cacheOffset, useDetail, characteristicSize);
        }
        else
        {
            field = ConceptSkyBody(local, radius, cell, useDetail, characteristicSize);
        }

        if (fieldAvailable)
        {
            field = lerp(field, cachedField, cachedWeight);
        }
    }

    float edge = ConceptEdgeWidth(characteristicSize);
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
    ConceptShapeSample sky = ConceptSampleSky(position, useDetail, true);
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
    ConceptShapeSample sky = ConceptSampleSky(position, useDetail, true);
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
