#ifndef CLOUD_PROCEDURAL_SURFACE_INCLUDED
#define CLOUD_PROCEDURAL_SURFACE_INCLUDED

#include "CloudLobeGeometry.hlsl"
float _SurfaceRepresentation, _SurfaceSegments;
float _SurfaceOceanCached;
#if defined(CLOUD_LOBE_SURFACE)
StructuredBuffer<float4> _OceanSurface;
#endif

// 인덱스 없는 규칙 구의 정점 계약을 A 임시 표면과 B 표면이 공유합니다.
float3 CloudSphereDirection(uint vertex, uint segments)
{
    uint face = vertex / 6;
    uint corner = vertex % 6;
    uint2 offset[6] = { uint2(0, 0), uint2(0, 1), uint2(1, 0), uint2(1, 0), uint2(0, 1), uint2(1, 1) };
    float2 uv = (float2(face % segments, face / segments) + offset[corner]) / segments;
    float longitude = uv.x * 6.2831853;
    float latitude = uv.y * 3.14159265;
    return float3(sin(latitude) * cos(longitude), cos(latitude), sin(latitude) * sin(longitude));
}

// 기존 상층 로브는 A의 임시 표면에만 씁니다. B의 아트 로브와 구분합니다.
void CloudOriginalLobe(int2 cell, float3 radius, uint instance, out float3 center, out float3 extent)
{
    ConceptBodyParameters body = ConceptBuildBody(radius, cell);
    center = body.centerA;
    extent = body.radiusA;
    if (instance == 1 || (instance >= 8 && instance < 11))
    {
        center = body.centerB;
        extent = body.radiusB;
    }
    else if (instance == 2 || instance >= 11)
    {
        center = body.centerC;
        extent = body.radiusC;
    }
    if (instance == 3)
    {
        center = body.centerA + body.radiusA * float3(-0.62, 0.30, 0.48);
        extent = body.radiusA * float3(0.48, 0.55, 0.46);
    }
    else if (instance == 4)
    {
        center = body.centerB + body.radiusB * float3(0.61, 0.26, -0.50);
        extent = body.radiusB * float3(0.46, 0.53, 0.49);
    }
    else if (instance >= 5)
    {
        float3 parentCenter = center;
        float3 parentRadius = extent;
        ConceptBuildLobe(cell, instance - 5, parentCenter, parentRadius, center, extent);
    }
}

// 휘어진 띠도 입체 타원체로 바로 표시합니다.
void CloudRibbonPoint(float3 direction, uint instance, int2 cell, out float3 position, out float3 normal)
{
    float lengthRadius = lerp(3500, 4900, CellRandom(cell, 143));
    float thickness = lerp(180, 300, CellRandom(cell, 144));
    float width = lerp(750, 1150, CellRandom(cell, 146));
    float3 local = direction * float3(lengthRadius, thickness, width);
    if (instance == 1)
    {
        local *= float3(0.66, 0.77, 0.70);
        local += float3(lengthRadius * 0.18, -thickness * 0.48, width * 0.27);
    }
    float along = local.x / lengthRadius;
    local.y += sin(along * 2.4 + CellRandom(cell, 147) * 6.2831853) * 140
        + along * (CellRandom(cell, 145) - 0.5) * 240;
    float angle = _ConceptDetail.z + (CellRandom(cell, 142) - 0.5) * 0.44;
    float sine, cosine;
    sincos(angle, sine, cosine);
    local.xz = float2(local.x * cosine + local.z * sine, -local.x * sine + local.z * cosine);
    float2 center = (float2(cell) + 0.5) * 14000;
    position = local + float3(center.x, lerp(4500, 9900, CellRandom(cell, 141)), center.y);
    normal = normalize(direction / float3(lengthRadius, thickness, width));
    normal.xz = float2(normal.x * cosine + normal.z * sine, -normal.x * sine + normal.z * cosine);
}

// 같은 절대 높이장을 규칙 격자로 만들고 하부를 닫아 자유 비행을 유지합니다.
void CloudOceanPoint(uint vertex, out float3 position, out float3 normal)
{
    uint quad = vertex / 12;
    uint corner = vertex % 6;
    bool lower = vertex % 12 >= 6;
    uint2 offsets[6] = { uint2(0, 0), uint2(0, 1), uint2(1, 0), uint2(1, 0), uint2(0, 1), uint2(1, 1) };
    float2 uv = (float2(quad % 16, quad / 16) + offsets[corner]) / 16;
    float2 horizontal = _HybridGridMin.xz + uv * _HybridGridSize.xz;
#if defined(CLOUD_LOBE_SURFACE)
    {
        uint2 address = (uint2)round(uv * 16);
        uint index = (address.x + address.y * 17) * 2;
        float4 cached = _OceanSurface[index];
        float2 detail = _OceanSurface[index + 1].xy;
        position = float3(horizontal.x, cached.x, horizontal.y);
        normal = cached.yzw;
        if (lower)
        {
            position.y = _OceanBounds.x;
            normal = float3(0, -1, 0);
        }
        else
        {
            float4 field = float4(0, detail.x, detail.y, 1);
            if (_HybridStability.y > 0.5)
            {
                // 캐시의 높이 기울기에 현재 변위 기울기를 더합니다. 노이즈만 네 번 조회하며 큰 운해를 재계산하지 않습니다.
                float step = 8;
                float dx = HybridDisplacement(position + float3(step, 0, 0), field)
                    - HybridDisplacement(position - float3(step, 0, 0), field);
                float dz = HybridDisplacement(position + float3(0, 0, step), field)
                    - HybridDisplacement(position - float3(0, 0, step), field);
                normal = normalize(float3(normal.x / max(0.05, normal.y) + dx / (2 * step),
                    1, normal.z / max(0.05, normal.y) + dz / (2 * step)));
            }
            position.y -= HybridDisplacement(position, field);
        }
        return;
    }
#else
    float height = HybridOceanDeformedHeight(horizontal);
    normal = float3(0, 1, 0);
    if (lower)
    {
        height = _OceanBounds.x;
        normal = float3(0, -1, 0);
    }
    else
    {
        float dx = HybridOceanDeformedHeight(horizontal + float2(75, 0)) - height;
        float dz = HybridOceanDeformedHeight(horizontal + float2(0, 75)) - height;
        normal = normalize(float3(-dx, 75, -dz));
    }
    position = float3(horizontal.x, height, horizontal.y);
#endif
}

// GPU에서 A는 기존 형태, B는 아트 로브 등가면으로 투영합니다.
void CloudProceduralPoint(uint vertex, uint instance, out float3 position, out float3 normal)
{
    int kind = (int)_HybridObject.x;
    int2 cell = (int2)_HybridObject.yz;
    if (kind == 0)
    {
        CloudOceanPoint(vertex, position, normal);
        return;
    }
    float3 direction = CloudSphereDirection(vertex, (uint)_SurfaceSegments);
    if (kind == 2)
    {
        CloudRibbonPoint(direction, instance, cell, position, normal);
        return;
    }
    float3 center, radius;
    float2 rotation;
    LobeCellTransform(cell, center, radius, rotation);
    float3 lobeCenter, extent;
#if defined(CLOUD_LOBE_SURFACE)
    LobeParameters(cell, radius, instance, lobeCenter, extent);
#else
    CloudOriginalLobe(cell, radius, instance, lobeCenter, extent);
#endif
    float3 local = lobeCenter + direction * extent;
    normal = normalize(direction / extent);
    local.xz = float2(local.x * rotation.y + local.z * rotation.x,
        -local.x * rotation.x + local.z * rotation.y);
    normal.xz = float2(normal.x * rotation.y + normal.z * rotation.x,
        -normal.x * rotation.x + normal.z * rotation.y);
    position = local + center;
    [unroll]
    for (int iteration = 0; iteration < 3; iteration++)
    {
        float4 field;
        float ahead;
#if defined(CLOUD_LOBE_SURFACE)
        field = LobeField(position, kind, cell);
        ahead = LobeField(position + normal * 35, kind, cell).x;
#else
        field = HybridBaseField(position);
        ahead = HybridBaseField(position + normal * 35).x;
#endif
        float slope = max(0.25, (ahead - field.x) / 35);
        position -= normal * clamp(field.x / slope, -350, 350);
    }
#if defined(CLOUD_LOBE_SURFACE)
    normal = LobeSurfaceNormal(position, cell);
    position -= normal * LobeDisplacement(position, LobeField(position, kind, cell), kind);
#endif
}
#endif
