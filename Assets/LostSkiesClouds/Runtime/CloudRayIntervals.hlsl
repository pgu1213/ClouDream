#ifndef CLOUD_RAY_INTERVALS_INCLUDED
#define CLOUD_RAY_INTERVALS_INCLUDED

// Concept V2의 최종 밀도를 포함하는 보수적인 구간입니다. 근사 거리장을 이동 거리로 쓰지 않습니다.
// 셀 구간은 광선별로 유지하며, 겹치는 운해·상층·띠 중 하나라도 있으면 기존 밀도를 평가합니다.
struct CloudRayIntervals
{
    float2 ocean;
    float2 sky;
    float2 ribbon;
    float skyCellEnd;
    float ribbonCellEnd;
};

// 평행한 축에서도 나눗셈의 NaN 없이 현재 교차 구간을 줄입니다.
bool CloudClipAxis(float origin, float direction, float minimum, float maximum, inout float2 interval)
{
    if (abs(direction) < 0.0000001)
    {
        return origin >= minimum && origin <= maximum;
    }

    float2 hit = (float2(minimum, maximum) - origin) / direction;
    interval.x = max(interval.x, min(hit.x, hit.y));
    interval.y = min(interval.y, max(hit.x, hit.y));
    return interval.y >= interval.x;
}

// 단단한 밀도 지원 범위를 포함하는 상자의 시선 구간을 구합니다.
float2 CloudBoxInterval(float3 origin, float3 ray, float3 minimum, float3 maximum, float end)
{
    float2 interval = float2(0, end);
    bool valid = CloudClipAxis(origin.x, ray.x, minimum.x, maximum.x, interval);
    valid = CloudClipAxis(origin.y, ray.y, minimum.y, maximum.y, interval) && valid;
    valid = CloudClipAxis(origin.z, ray.z, minimum.z, maximum.z, interval) && valid;
    if (!valid)
    {
        return float2(end, -1);
    }

    // 부동소수점 교차 오차가 얇은 표본을 누락하지 않도록 양끝을 1m 확장합니다.
    return float2(max(0, interval.x - 1), min(end, interval.y + 1));
}

// 광선이 현재 XZ 셀에서 나가는 거리를 구합니다. 셀 경계의 밀도는 별도 지원 범위로부터 떨어져 있습니다.
float CloudCellExit(float3 origin, float3 ray, int2 cell, float spacing, float end)
{
    float2 interval = float2(0, end);
    CloudClipAxis(origin.x, ray.x, cell.x * spacing, (cell.x + 1) * spacing, interval);
    CloudClipAxis(origin.z, ray.z, cell.y * spacing, (cell.y + 1) * spacing, interval);
    return interval.y;
}

// 운해의 전역 범위는 한 번만 구합니다. 셀 데이터는 필요한 고도에서만 계산합니다.
CloudRayIntervals CloudCreateIntervals(float3 origin, float3 ray, float end)
{
    CloudRayIntervals result;
    result.ocean = float2(end, -1);
    if (_Ocean != 0)
    {
        float2 interval = float2(0, end);
        if (CloudClipAxis(origin.y, ray.y, _OceanBounds.x - 400, ConceptOceanTop(), interval))
        {
            result.ocean = float2(max(0, interval.x - 1), min(end, interval.y + 1));
        }
    }

    result.sky = float2(end, -1);
    result.ribbon = float2(end, -1);
    result.skyCellEnd = -1;
    result.ribbonCellEnd = -1;
    return result;
}

// 회전된 상층 타원체를 감싸는 월드 AABB를 사용합니다. 회전·반경·점유율은 생산용 생성식과 같습니다.
void CloudUpdateSkyInterval(float3 origin, float3 ray, float distance, float end, inout CloudRayIntervals intervals)
{
    float spacing = max(4000, _SkyPlacement.x);
    // 0.5m 경계 편향은 최소 240m의 빈 셀 가장자리 안에 있으며 경계에서 같은 셀을 다시 고르는 것을 막습니다.
    int2 cell = (int2)floor((origin + ray * (distance + 0.5)).xz / spacing);
    intervals.skyCellEnd = CloudCellExit(origin, ray, cell, spacing, end);
    intervals.sky = float2(end, -1);
    if (CellRandom(cell, 0) >= _SkyPlacement.y)
    {
        return;
    }

    float2 center = (float2(cell) + 0.5 + (float2(CellRandom(cell, 1), CellRandom(cell, 2)) - 0.5) * 0.12) * spacing;
    float altitude = lerp(_SkyAltitude.x, _SkyAltitude.y, CellRandom(cell, 3));
    float3 radius = float3(lerp(_SkyShape.x, _SkyShape.y, CellRandom(cell, 4)),
        lerp(_SkyAltitude.z, _SkyAltitude.w, CellRandom(cell, 6)),
        lerp(_SkyShape.x, _SkyShape.y, CellRandom(cell, 5)));
    radius.xz = min(radius.xz, spacing * 0.38);
    float sine, cosine;
    sincos(CellRandom(cell, 7) * 6.2831853, sine, cosine);
    float3 extent = float3(length(float2(radius.x * cosine, radius.z * sine)), radius.y,
        length(float2(radius.x * sine, radius.z * cosine)));
    float3 center3 = float3(center.x, altitude, center.y);
    intervals.sky = CloudBoxInterval(origin, ray, center3 - extent, center3 + extent, end);
}

// 띠의 생산 함수가 사용하는 하드 지원 범위를 이용하므로 굴곡·변위의 거리장 근사가 필요하지 않습니다.
void CloudUpdateRibbonInterval(float3 origin, float3 ray, float distance, float end, inout CloudRayIntervals intervals)
{
    float spacing = 14000;
    int2 cell = (int2)floor((origin + ray * (distance + 0.5)).xz / spacing);
    intervals.ribbonCellEnd = CloudCellExit(origin, ray, cell, spacing, end);
    intervals.ribbon = float2(end, -1);
    if (CellRandom(cell, 140) >= saturate(_SkyPlacement.y * (0.65 / 0.72)))
    {
        return;
    }

    float3 center = float3((cell.x + 0.5) * spacing,
        lerp(4500, 9900, CellRandom(cell, 141)), (cell.y + 0.5) * spacing);
    float3 minimum = center - float3(5900, 1000, 5900);
    float3 maximum = center + float3(5900, 1000, 5900);
    minimum.y = max(minimum.y, 3200);
    maximum.y = min(maximum.y, 11600);
    intervals.ribbon = CloudBoxInterval(origin, ray, minimum, maximum, end);
}

// 현재 셀의 지원 범위를 지났으면 다음 셀까지만 비웁니다. 뒤쪽 구름을 통째로 종료하지 않습니다.
float CloudIntervalCandidate(float distance, float2 interval, float cellEnd)
{
    if (distance <= interval.y)
    {
        return min(max(distance, interval.x), cellEnd);
    }

    return cellEnd;
}

// 다음에 밀도를 평가할 필요가 있는 거리를 구합니다. 원래 표본 간격과 루프 예산은 호출자가 유지합니다.
float CloudNextDensityDistance(float3 origin, float3 ray, float distance, float end, inout CloudRayIntervals intervals)
{
    float candidate = CloudIntervalCandidate(distance, intervals.ocean, end);
    if (candidate <= distance)
    {
        return distance;
    }

    if (_SkyShape.w < 0.5 || _SkyPlacement.y <= 0 || _SkyPlacement.w <= 0 || _SkyDensityMultiplier <= 0)
    {
        return candidate;
    }

    if (distance >= intervals.skyCellEnd)
    {
        CloudUpdateSkyInterval(origin, ray, distance, end, intervals);
    }

    candidate = min(candidate, CloudIntervalCandidate(distance, intervals.sky, intervals.skyCellEnd));
    if (candidate <= distance)
    {
        return distance;
    }

    if (distance >= intervals.ribbonCellEnd)
    {
        CloudUpdateRibbonInterval(origin, ray, distance, end, intervals);
    }

    return min(candidate, CloudIntervalCandidate(distance, intervals.ribbon, intervals.ribbonCellEnd));
}

#endif
