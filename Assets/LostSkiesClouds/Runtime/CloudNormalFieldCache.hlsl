#ifndef CLOUD_NORMAL_FIELD_CACHE_INCLUDED
#define CLOUD_NORMAL_FIELD_CACHE_INCLUDED

// 원래 상세/큰 형태 차분의 최종 방향만 저장합니다. RGB=단위 법선, A=유효성입니다.
Texture3D<float4> _CloudNormalField;
RWTexture3D<float4> _CloudNormalFieldWrite;
float4 _CloudNormalRegions[3];
float4 _CloudNormalBuildRegion;
float4 _CloudNormalSettings;
int _CloudNormalBuildCascade;
int _CloudNormalFieldEnabled;

// query/full/blend/exact를 1/64 화면 픽셀에서 발생한 실제 법선 호출 기준으로 집계합니다.
RWStructuredBuffer<uint> _CloudNormalCounters;
int _CloudNormalDiagnosticsEnabled;
static bool CloudNormalDiagnosticSample = false;

RWStructuredBuffer<float4> _CloudNormalProbeResults;
int _CloudNormalProbeMode;
int _CloudNormalProbeCascade;

// 생성 커널에서는 화면 계수 플래그를 켜지 않아 빌드 비용을 광선 적중률에 섞지 않습니다.
void CloudBeginNormalSample(uint2 pixel)
{
    CloudNormalDiagnosticSample = _CloudNormalDiagnosticsEnabled != 0
        && ((pixel.x * 1973u + pixel.y * 9277u) & 63u) == 0u;
}

// 정확한 기존 차분 법선을 현재 날씨·바람 입력에서 생성합니다. 다른 근사 캐시는 사용하지 않습니다.
#ifdef CLOUD_EXACT_NORMAL_PRODUCER
[numthreads(4, 4, 4)]
void BuildNormalFields(uint3 id : SV_DispatchThreadID)
{
    float3 position = _CloudNormalBuildRegion.xyz + float3(id) * _CloudNormalBuildRegion.w;
    float3 normal = CloudBoundaryNormalExact(position);
    float valid = 0;
    if (all(isfinite(normal)) && dot(normal, normal) > 0.5)
    {
        valid = 1;
    }
    else
    {
        // 비유한 생산 결과를 캐시 텍스처에 남기지 않습니다. 정확 원식 검사는 별도로 실패시킵니다.
        normal = 0;
    }

    uint3 address = uint3(id.xy, id.z + (uint)_CloudNormalBuildCascade * 32u);
    _CloudNormalFieldWrite[address] = float4(normal, valid);
}
#endif

// 8개 코너가 유효하고 방향이 합의하는 경우만 보간합니다. 이 검사는 출력 오차의 수학적 상한은 아닙니다.
bool CloudTryNormalField(float3 position, out float3 normal, out float weight)
{
    normal = 0;
    weight = 0;
    float distanceToCamera = length(position - _CameraPosition.xyz);
    if (_CloudNormalFieldEnabled == 0 || distanceToCamera <= _CloudNormalSettings.x)
    {
        return false;
    }

    [unroll]
    for (int cascade = 0; cascade < 3; cascade++)
    {
        float4 region = _CloudNormalRegions[cascade];
        float3 cell = (position - region.xyz) / region.w;
        float3 cameraDistance = abs(position - _CameraPosition.xyz) / region.w;
        float radialWeight = 1 - smoothstep(10, 12, max(cameraDistance.x, max(cameraDistance.y, cameraDistance.z)));
        if (radialWeight <= 0 || any(cell < 0) || any(cell >= 31))
        {
            continue;
        }

        int3 lower = (int3)floor(cell);
        float3 fraction = frac(cell);
        float3 values[8];
        float3 sum = 0;
        [unroll]
        for (int corner = 0; corner < 8; corner++)
        {
            int3 offset = int3(corner & 1, (corner >> 1) & 1, (corner >> 2) & 1);
            int3 address = lower + offset;
            address.z += cascade * 32;
            float4 value = _CloudNormalField.Load(int4(address, 0));
            if (value.w < 0.5 || any(!isfinite(value)) || dot(value.xyz, value.xyz) < 0.5)
            {
                return false;
            }

            values[corner] = normalize(value.xyz);
            sum += values[corner];
        }

        if (dot(sum, sum) < 0.0001)
        {
            return false;
        }

        float3 sharedDirection = normalize(sum);
        [unroll]
        for (int corner = 0; corner < 8; corner++)
        {
            if (dot(sharedDirection, values[corner]) < _CloudNormalSettings.z)
            {
                return false;
            }
        }

        float3 result = 0;
        [unroll]
        for (int corner = 0; corner < 8; corner++)
        {
            float3 offset = float3(corner & 1, (corner >> 1) & 1, (corner >> 2) & 1);
            float3 blend = lerp(1 - fraction, fraction, offset);
            result += values[corner] * blend.x * blend.y * blend.z;
        }

        normal = normalize(result);
        float3 border = min(cell, 31 - cell);
        float edgeWeight = smoothstep(0, 2, min(border.x, min(border.y, border.z)));
        weight = radialWeight * edgeWeight * smoothstep(_CloudNormalSettings.x, _CloudNormalSettings.y, distanceToCamera);
        return weight > 0;
    }

    return false;
}

// 독립 옵션이 켜진 경우에만 공간 법선을 조회하고 부족한 경계/범위는 정확한 생산 법선과 혼합합니다.
float3 CloudReadBoundaryNormal(float3 position, out float cachedWeight)
{
    cachedWeight = 0;
    #ifndef CLOUD_NORMAL_FIELD_CACHE
    return CloudBoundaryNormalFromFields(position);
    #else
    if (_CloudNormalFieldEnabled == 0)
    {
        return CloudBoundaryNormalFromFields(position);
    }

    if (CloudNormalDiagnosticSample)
    {
        InterlockedAdd(_CloudNormalCounters[0], 1u);
    }

    float3 cached;
    bool available = CloudTryNormalField(position, cached, cachedWeight);
    if (available && cachedWeight >= 1)
    {
        if (CloudNormalDiagnosticSample)
        {
            InterlockedAdd(_CloudNormalCounters[1], 1u);
        }

        return cached;
    }

    // 무거운 생산 함수를 한 호출 지점에서만 평가하여 fallback/혼합 경로의 인라인 중복을 줄입니다.
    float3 exact = CloudBoundaryNormalExact(position);
    if (!available)
    {
        if (CloudNormalDiagnosticSample)
        {
            InterlockedAdd(_CloudNormalCounters[3], 1u);
        }

        return exact;
    }

    if (CloudNormalDiagnosticSample)
    {
        InterlockedAdd(_CloudNormalCounters[2], 1u);
    }

    float3 mixed = lerp(exact, cached, cachedWeight);
    if (dot(mixed, mixed) < 0.000001)
    {
        return exact;
    }

    return normalize(mixed);
    #endif
}

// 기존 호출 경계를 보존하고 최종 법선 조회만 공간 캐시 후보로 교체합니다.
float3 CloudBoundaryNormal(float3 position)
{
    float cachedWeight;
    return CloudReadBoundaryNormal(position, cachedWeight);
}

// mode0=현재조회, mode1=정확원식, mode2=격자원소입니다. W는 현재 캐시 가중치 또는 원소 유효성입니다.
#ifdef CLOUD_NORMAL_FIELD_PROBE
[numthreads(64, 1, 1)]
void ProbeNormalField(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)_ProbeCount)
    {
        return;
    }

    float3 position = _ProbePositions[id.x];
    if (_CloudNormalProbeMode == 1)
    {
        _CloudNormalProbeResults[id.x] = float4(CloudBoundaryNormalExact(position), 0);
        return;
    }

    if (_CloudNormalProbeMode == 2)
    {
        int cascade = clamp(_CloudNormalProbeCascade, 0, 2);
        float4 region = _CloudNormalRegions[cascade];
        int3 cell = (int3)round((position - region.xyz) / region.w);
        if (any(cell < 0) || any(cell >= 32) || _CloudNormalFieldEnabled == 0)
        {
            _CloudNormalProbeResults[id.x] = 0;
            return;
        }

        cell.z += cascade * 32;
        _CloudNormalProbeResults[id.x] = _CloudNormalField.Load(int4(cell, 0));
        return;
    }

    float cachedWeight;
    float3 normal = CloudReadBoundaryNormal(position, cachedWeight);
    _CloudNormalProbeResults[id.x] = float4(normal, cachedWeight);
}
#endif

#endif
