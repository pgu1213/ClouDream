#ifndef CLOUD_LOBE_PREPARED_INCLUDED
#define CLOUD_LOBE_PREPARED_INCLUDED

// 48바이트 후보와 셀당 18개 float4. 0은 중심, 1은 회전/대표 크기/밀도, 2~17은 여덟 로브의 중심/반경입니다.
struct NearObject { float4 cell; float4 minimum; float4 size; };
#if defined(LOBE_PREPARE_RW)
RWStructuredBuffer<NearObject> _LobeObjects;
RWStructuredBuffer<float4> _LobePrepared;
#else
StructuredBuffer<NearObject> _LobeObjects;
StructuredBuffer<float4> _LobePrepared;
#endif

// 셀의 난수·부모 배치를 반복하지 않고 같은 순서의 매끈한 결합을 평가합니다.
float4 LobePreparedField(float3 position, uint objectIndex)
{
    NearObject cloud = _LobeObjects[objectIndex];
    int kind = (int)cloud.cell.x;
    int2 cell = (int2)cloud.cell.yz;
    if (kind != 1)
    {
        return LobeField(position, kind, cell);
    }
    uint first = objectIndex * 18;
    float4 center = _LobePrepared[first];
    float4 parameters = _LobePrepared[first + 1];
    float3 local = position - center.xyz;
    local.xz = float2(local.x * parameters.y - local.z * parameters.x,
        local.x * parameters.x + local.z * parameters.y);
    float field = 60000;
    [loop]
    for (uint lobe = 0; lobe < 8; lobe++)
    {
        float3 lobeCenter = _LobePrepared[first + 2 + lobe * 2].xyz;
        float3 radius = _LobePrepared[first + 3 + lobe * 2].xyz;
        field = SmoothCloudUnion(field, ConceptEllipsoidField(local - lobeCenter, radius), max(30, parameters.z * _LobeBody.z));
    }
    return float4(field, parameters.z, 0.5, parameters.w);
}

// 파라미터 준비와 무관하게 생산용 밀도 변환은 한 함수로 유지합니다.
float LobePreparedDensity(float3 position, uint objectIndex)
{
    return LobeDensityFromField(position, (int)_LobeObjects[objectIndex].cell.x, LobePreparedField(position, objectIndex));
}

// 같은 준비된 필드에서 법선을 구합니다. 미분용 스칼라에는 포화나 카메라 입력이 없습니다.
float LobePreparedScalar(float3 position, uint objectIndex)
{
    int kind = (int)_LobeObjects[objectIndex].cell.x;
    float4 field = LobePreparedField(position, objectIndex);
    float scalar = field.x;
    if (_HybridStability.y > 0.5)
    {
        scalar += LobeDisplacement(position, field, kind);
    }
    if (kind == 0)
    {
        scalar = max(scalar, _OceanBounds.x - position.y);
    }
    return scalar;
}

// 첫 진입의 월드 법선만 계산하며 48개 밀도 표본마다 반복하지 않습니다.
float3 LobePreparedLightingNormal(float3 position, uint objectIndex)
{
    float step = 8;
    return HybridTetrahedralNormal(float4(
        LobePreparedScalar(position + float3(1, -1, -1) * step, objectIndex),
        LobePreparedScalar(position + float3(-1, -1, 1) * step, objectIndex),
        LobePreparedScalar(position + float3(-1, 1, -1) * step, objectIndex),
        LobePreparedScalar(position + float3(1, 1, 1) * step, objectIndex)));
}

// 진단용 기존 시선 법선과 생산용 월드 법선을 한 진입점에서 선택합니다. 조밀한 내부에는 방향 면을 만들지 않습니다.
float3 LobeVolumeLightingNormal(float3 position, uint objectIndex, float3 ray, bool inside)
{
    if (inside)
    {
        return 0;
    }
    if (_HybridStability.x > 0.5)
    {
        return LobePreparedLightingNormal(position, objectIndex);
    }
    return -ray;
}
#endif
