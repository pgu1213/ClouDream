#ifndef CLOUD_CONCEPT_CELL_CACHE_INCLUDED
#define CLOUD_CONCEPT_CELL_CACHE_INCLUDED

// CloudShapeCellCache.cs와 공유: 셀당 float4 32개, header 2개 + 14개 타원체의 중심/반경 쌍.
// RW 버퍼는 BuildShapeCells에서만 씁니다. 카메라/프로브 dispatch는 읽기 전용입니다.
RWStructuredBuffer<float4> _CloudShapeCells;
int4 _CloudShapeCacheRegion;
int _CloudShapeCacheEnabled;
int _CloudSupportRejection;

// 월드 정수 셀을 창 내부 주소로 변환합니다. 외부 조명·원거리 조회에는 절차식 fallback을 사용합니다.
bool ConceptTryCellOffset(int2 cell, out int offset)
{
    int2 local = cell - _CloudShapeCacheRegion.xy;
    offset = 0;
    if (_CloudShapeCacheEnabled == 0 || any(local < 0) || any(local >= _CloudShapeCacheRegion.z))
    {
        return false;
    }

    offset = (local.y * _CloudShapeCacheRegion.z + local.x) * _CloudShapeCacheRegion.w;
    return true;
}

// 한 타원체의 중심과 반경을 정렬된 두 레코드로 저장합니다.
void ConceptStoreBody(int offset, int bodyIndex, float3 center, float3 radius)
{
    _CloudShapeCells[offset + 2 + bodyIndex * 2] = float4(center, 0);
    _CloudShapeCells[offset + 3 + bodyIndex * 2] = float4(radius, 0);
}

// GPU에서 기존 생성식을 실행해 프로필 또는 창 변경 시에만 불변 정보를 준비합니다.
[numthreads(8, 8, 1)]
void BuildShapeCells(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= (uint)_CloudShapeCacheRegion.z))
    {
        return;
    }

    int2 cell = (int2)id.xy + _CloudShapeCacheRegion.xy;
    int offset = (id.y * _CloudShapeCacheRegion.z + id.x) * _CloudShapeCacheRegion.w;
    float3 radius = float3(lerp(_SkyShape.x, _SkyShape.y, CellRandom(cell, 4)),
        lerp(_SkyAltitude.z, _SkyAltitude.w, CellRandom(cell, 6)),
        lerp(_SkyShape.x, _SkyShape.y, CellRandom(cell, 5)));
    radius.xz = min(radius.xz, max(4000, _SkyPlacement.x) * 0.38);
    ConceptBodyParameters body = ConceptBuildBody(radius, cell);
    _CloudShapeCells[offset] = float4(body.growth, body.characteristicSize, body.joinWidth, 0);
    _CloudShapeCells[offset + 1] = float4(body.growthZ, 0, 0, 0);
    ConceptStoreBody(offset, 0, body.centerA, body.radiusA);
    ConceptStoreBody(offset, 1, body.centerB, body.radiusB);
    ConceptStoreBody(offset, 2, body.centerC, body.radiusC);
    ConceptStoreBody(offset, 3, body.centerA + body.radiusA * float3(-0.62, 0.30, 0.48),
        body.radiusA * float3(0.48, 0.55, 0.46));
    ConceptStoreBody(offset, 4, body.centerB + body.radiusB * float3(0.61, 0.26, -0.50),
        body.radiusB * float3(0.46, 0.53, 0.49));

    [loop]
    for (int lobe = 0; lobe < 9; lobe++)
    {
        float3 parentCenter = body.centerA;
        float3 parentRadius = body.radiusA;
        if (lobe >= 6)
        {
            parentCenter = body.centerC;
            parentRadius = body.radiusC;
        }
        else if (lobe >= 3)
        {
            parentCenter = body.centerB;
            parentRadius = body.radiusB;
        }

        float3 center;
        float3 lobeRadius;
        ConceptBuildLobe(cell, lobe, parentCenter, parentRadius, center, lobeRadius);
        ConceptStoreBody(offset, lobe + 5, center, lobeRadius);
    }
}

// 원래 smooth union 순서를 유지하며 상세 렌더와 완만한 조명 형태를 각각 평가합니다.
float ConceptCachedSkyBody(float3 position, float3 radius, int offset, bool useDetail, out float characteristicSize)
{
    float4 header = _CloudShapeCells[offset];
    float normalizedHeight = position.y / max(10, radius.y);
    position.x -= normalizedHeight * radius.x * header.x;
    position.z -= normalizedHeight * radius.z * _CloudShapeCells[offset + 1].x * 0.22;
    characteristicSize = header.y;
    float joinWidth = header.z;
    float field = ConceptEllipsoidField(position - _CloudShapeCells[offset + 2].xyz,
        _CloudShapeCells[offset + 3].xyz);
    int count = 5;
    if (useDetail)
    {
        count = 14;
    }

    [loop]
    for (int body = 1; body < count; body++)
    {
        float width = joinWidth;
        if (body >= 5)
        {
            width *= 0.60;
        }
        else if (body >= 3)
        {
            width *= 0.80;
        }

        float next = ConceptEllipsoidField(position - _CloudShapeCells[offset + 2 + body * 2].xyz,
            _CloudShapeCells[offset + 3 + body * 2].xyz);
        field = SmoothCloudUnion(field, next, width);
    }

    return field;
}

#endif
