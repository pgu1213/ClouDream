#ifndef CLOUD_FAR_SURFACE_INCLUDED
#define CLOUD_FAR_SURFACE_INCLUDED

#ifdef CLOUD_FAR_SURFACE

// Experimental representation of a current-frame, distant, contiguous opaque cloud body.
// Geometry: first density crossing, opaque endpoint, opacity-weighted mean distance, variance.
// NormalHit: exact boundary normal.xyz and the original T > .99 threshold distance.
// Occlusion: sun density integral, sky density integral, remaining T, valid bit.
// These are camera-ray surfaces, not a signed distance field and not cached final RGB.
RWTexture2D<float4> _CloudFarSurfaceGeometryWrite;
RWTexture2D<float4> _CloudFarSurfaceNormalWrite;
RWTexture2D<float4> _CloudFarSurfaceOcclusionWrite;
Texture2D<float4> _CloudFarSurfaceGeometry;
Texture2D<float4> _CloudFarSurfaceNormal;
Texture2D<float4> _CloudFarSurfaceOcclusion;
RWStructuredBuffer<uint> _CloudFarSurfaceCounters;
RWTexture2D<float> _CloudFarSurfaceUsage;
float4 _CloudFarSurfaceSize;
// near limit, minimum continuous thickness, maximum continuous thickness, entry tolerance.
float4 _CloudFarSurfaceLimits;
int _CloudFarSurfaceEnabled;
int _CloudFarSurfaceDiagnostics;

// Counters are full dispatch counts, optional and reset once before the build each render.
void CloudFarSurfaceRecord(uint index)
{
    if (_CloudFarSurfaceDiagnostics != 0)
    {
        InterlockedAdd(_CloudFarSurfaceCounters[index], 1u);
    }
}

// Count explicit discovery/guard density calls. Boundary/occlusion requests are separate counters.
float CloudFarSurfaceDensity(float3 position, uint counter)
{
    CloudFarSurfaceRecord(counter);
    return Density(position, true);
}

// Preserve the current styled renderer's empty-space sample spacing, without introducing a larger step.
float CloudFarSurfaceSpaceStep(float distance)
{
    float nearStep = clamp(8 + distance * 0.0035, 8, 100);
    float farStep = clamp(12 + distance * 0.008, 12, 240);
    return lerp(nearStep, farStep, smoothstep(3000, 9000, distance));
}

// Preserve the ordinary occupied-density spacing. Adaptive integration is intentionally unsupported here.
float CloudFarSurfaceDensityStep(float distance, float extinction, float spaceStep)
{
    float step = clamp(0.14 / extinction, 5, 18);
    #ifdef CLOUD_DISTANCE_DENSITY
    step *= CloudDistanceScale(distance, _CloudDistanceDensity);
    #endif
    return min(spaceStep, step);
}

// Reject non-finite surface inputs before they can poison a color/depth output.
bool CloudFarSurfaceFinite(float4 value)
{
    return all(isfinite(value));
}

// Store invalid data for every proxy texel, so camera/weather/origin changes cannot expose stale texels.
void CloudFarSurfaceClear(uint2 pixel)
{
    _CloudFarSurfaceGeometryWrite[pixel] = 0;
    _CloudFarSurfaceNormalWrite[pixel] = 0;
    _CloudFarSurfaceOcclusionWrite[pixel] = 0;
    if (_CloudFarSurfaceDiagnostics != 0)
    {
        [unroll]
        for (uint y = 0u; y < 2u; y++)
        {
            [unroll]
            for (uint x = 0u; x < 2u; x++)
            {
                uint2 fullPixel = pixel * 2u + uint2(x, y);
                if (all(fullPixel < (uint2)_Size.xy))
                {
                    _CloudFarSurfaceUsage[fullPixel] = 0;
                }
            }
        }
    }
}

// Extract the first contiguous body only. A separate thin foreground layer invalidates this candidate.
[numthreads(8, 8, 1)]
void BuildFarSurface(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= (uint2)_CloudFarSurfaceSize.xy))
    {
        return;
    }

    CloudFarSurfaceClear(id.xy);
    CloudFarSurfaceRecord(0u);
    if (_CloudFarSurfaceEnabled == 0 || _StyleMode != 4)
    {
        CloudFarSurfaceRecord(5u);
        return;
    }

    float2 uv = (id.xy + 0.5) * _CloudFarSurfaceSize.zw;
    float3 ray = normalize(_CameraForward.xyz + (uv.x * 2 - 1) * _CameraRight.xyz
        + (1 - uv.y * 2) * _CameraUp.xyz);
    float2 interval = LayerIntersection(ray);
    float end = interval.y;
    uint2 sourcePixel = min((uint2)(uv * _Size.xy), (uint2)_Size.xy - 1u);
    float sceneDepth = _SceneDepth.Load(int4(sourcePixel, 0, 0)).r;
    if (sceneDepth > 0)
    {
        end = min(end, rcp(sceneDepth * _ZParams.x + _ZParams.y)
            / max(0.001, dot(ray, _CameraForward.xyz)));
    }

    float start = interval.x;
    if (end <= start)
    {
        CloudFarSurfaceRecord(4u);
        return;
    }

    float initialDensity = CloudFarSurfaceDensity(_CameraPosition.xyz + ray * start, 6u);
    if (initialDensity > 0.002)
    {
        CloudFarSurfaceRecord(2u);
        return;
    }

    float initialStep = clamp(12 + start * 0.008, 12, 240);
    float distance = start + initialStep * 0.5;
    float previousDistance = start;
    float entry = -1;
    float transmission = 1;
    float weightedDepth = 0;
    float weightedDepthSquared = 0;
    float thresholdDepth = end;
    float extinction = CloudExtinction();
    float3 entryNormal = 0;

    [loop]
    for (int iteration = 0; iteration < 1024 && distance < end; iteration++)
    {
        // The ordinary loop computes its spatial step before entry bisection. Preserve that exact value.
        float spaceStep = min(end - distance, CloudFarSurfaceSpaceStep(distance));
        float density = CloudFarSurfaceDensity(_CameraPosition.xyz + ray * distance, 6u);
        if (!isfinite(density))
        {
            CloudFarSurfaceRecord(5u);
            return;
        }

        if (density <= 0.002)
        {
            if (entry >= 0)
            {
                CloudFarSurfaceRecord(3u);
                return;
            }

            previousDistance = distance;
            distance += spaceStep;
            continue;
        }

        if (entry < 0)
        {
            float low = previousDistance;
            float high = distance;
            [loop]
            for (int refinement = 0; refinement < 5; refinement++)
            {
                float middle = (low + high) * 0.5;
                float middleDensity = CloudFarSurfaceDensity(_CameraPosition.xyz + ray * middle, 6u);
                if (middleDensity > 0.002)
                {
                    high = middle;
                }
                else
                {
                    low = middle;
                }
            }

            entry = high;
            distance = high;
            if (entry < _CloudFarSurfaceLimits.x)
            {
                CloudFarSurfaceRecord(2u);
                return;
            }

            density = CloudFarSurfaceDensity(_CameraPosition.xyz + ray * distance, 6u);
            CloudFarSurfaceRecord(7u);
            entryNormal = CloudBoundaryNormal(_CameraPosition.xyz + ray * distance);
        }

        float step = min(end - distance, CloudFarSurfaceDensityStep(distance, extinction, spaceStep));
        if (transmission > 0.99)
        {
            thresholdDepth = distance;
        }

        float alpha = 1 - exp(-density * step * extinction);
        float contribution = transmission * alpha;
        weightedDepth += contribution * distance;
        weightedDepthSquared += contribution * distance * distance;
        transmission *= 1 - alpha;

        if (transmission <= 0.008)
        {
            float thickness = distance + step - entry;
            if (thickness < _CloudFarSurfaceLimits.y || thickness > _CloudFarSurfaceLimits.z)
            {
                CloudFarSurfaceRecord(3u);
                return;
            }

            float mean = weightedDepth / max(0.0001, 1 - transmission);
            float variance = max(0, weightedDepthSquared / max(0.0001, 1 - transmission) - mean * mean);
            CloudFarSurfaceRecord(8u);
            float2 occlusion = CloudEvaluateOcclusion(_CameraPosition.xyz + ray * mean,
                normalize(_SunDirection.xyz), extinction);
            float4 geometry = float4(entry, distance + step, mean, variance);
            float4 normalHit = float4(entryNormal, thresholdDepth);
            float4 optical = float4(occlusion, transmission, 1);
            if (!CloudFarSurfaceFinite(geometry) || !CloudFarSurfaceFinite(normalHit)
                || !CloudFarSurfaceFinite(optical) || dot(entryNormal, entryNormal) < 0.5)
            {
                CloudFarSurfaceRecord(5u);
                return;
            }

            _CloudFarSurfaceGeometryWrite[id.xy] = geometry;
            _CloudFarSurfaceNormalWrite[id.xy] = normalHit;
            _CloudFarSurfaceOcclusionWrite[id.xy] = optical;
            CloudFarSurfaceRecord(1u);
            return;
        }

        if (distance + step - entry > _CloudFarSurfaceLimits.z)
        {
            CloudFarSurfaceRecord(4u);
            return;
        }

        distance += step;
    }

    CloudFarSurfaceRecord(4u);
}

// Register one mutually exclusive query failure plus the corresponding volume continuation.
bool CloudFarSurfaceFallback(uint reason)
{
    CloudFarSurfaceRecord(reason);
    CloudFarSurfaceRecord(19u);
    return false;
}

// Consume a separately extracted surface only at the first exact volume entry.
// The main-ray density guard remains: this prototype removes repeated interior lighting, not density marching.
bool CloudTryFarSurface(uint3 id, float3 ray, float entry, float end, float entryDensity,
    float initialSpaceStep, int remainingSteps,
    float3 entryNormal, float incomingTransmission, float extinction, float phase, float sunCosine,
    out float4 lighting, out float2 transmissionHit, out float2 moments)
{
    lighting = 0;
    transmissionHit = 0;
    moments = 0;
    CloudFarSurfaceRecord(9u);
    if (entry < _CloudFarSurfaceLimits.x || incomingTransmission < 0.99999)
    {
        return CloudFarSurfaceFallback(16u);
    }

    uint2 pixel = (uint2)((id.xy + 0.5) * _Size.zw * _CloudFarSurfaceSize.xy);
    if (any(pixel < 1u) || any(pixel + 1u >= (uint2)_CloudFarSurfaceSize.xy))
    {
        return CloudFarSurfaceFallback(12u);
    }

    float4 geometry = _CloudFarSurfaceGeometry.Load(int3(pixel, 0));
    float4 normalHit = _CloudFarSurfaceNormal.Load(int3(pixel, 0));
    float4 optical = _CloudFarSurfaceOcclusion.Load(int3(pixel, 0));
    if (optical.w < 0.5 || optical.z > 0.008 || !CloudFarSurfaceFinite(geometry)
        || !CloudFarSurfaceFinite(normalHit) || !CloudFarSurfaceFinite(optical))
    {
        return CloudFarSurfaceFallback(11u);
    }

    float tolerance = _CloudFarSurfaceLimits.w;
    if (abs(entry - geometry.x) > tolerance || dot(entryNormal, normalHit.xyz) < 0.95)
    {
        return CloudFarSurfaceFallback(14u);
    }

    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            int2 neighbor = int2(pixel) + int2(x, y);
            float4 otherGeometry = _CloudFarSurfaceGeometry.Load(int3(neighbor, 0));
            float4 otherNormal = _CloudFarSurfaceNormal.Load(int3(neighbor, 0));
            float4 otherOptical = _CloudFarSurfaceOcclusion.Load(int3(neighbor, 0));
            if (otherOptical.w < 0.5 || !CloudFarSurfaceFinite(otherGeometry)
                || !CloudFarSurfaceFinite(otherNormal) || !CloudFarSurfaceFinite(otherOptical)
                || abs(otherGeometry.x - geometry.x) > tolerance
                || abs(otherGeometry.y - geometry.y) > tolerance * 2
                || dot(otherNormal.xyz, normalHit.xyz) < 0.95)
            {
                return CloudFarSurfaceFallback(12u);
            }
        }
    }

    float shiftedOpaqueDistance = geometry.y + entry - geometry.x;
    if (end < shiftedOpaqueDistance + tolerance)
    {
        return CloudFarSurfaceFallback(13u);
    }

    // Exact main-ray density samples prevent a thin foreground layer or separated body becoming one surface.
    float distance = entry;
    float transmission = 1;
    float weightedDepth = 0;
    float weightedDepthSquared = 0;
    float thresholdDepth = end;
    float guardEnd = min(end, min(entry + _CloudFarSurfaceLimits.z, shiftedOpaqueDistance + tolerance));
    bool opaque = false;
    [loop]
    for (int iteration = 0; iteration < min(384, remainingSteps) && distance < guardEnd; iteration++)
    {
        float density = entryDensity;
        float spaceStep = initialSpaceStep;
        if (iteration > 0)
        {
            spaceStep = min(end - distance, CloudFarSurfaceSpaceStep(distance));
            density = CloudFarSurfaceDensity(_CameraPosition.xyz + ray * distance, 17u);
        }

        if (!isfinite(density) || density <= 0.002)
        {
            return CloudFarSurfaceFallback(15u);
        }

        float step = min(end - distance, CloudFarSurfaceDensityStep(distance, extinction, spaceStep));
        if (transmission > 0.99)
        {
            thresholdDepth = distance;
        }

        float alpha = 1 - exp(-density * step * extinction);
        float contribution = transmission * alpha;
        weightedDepth += contribution * distance;
        weightedDepthSquared += contribution * distance * distance;
        transmission *= 1 - alpha;
        if (transmission <= 0.008)
        {
            float thickness = distance + step - entry;
            if (thickness < _CloudFarSurfaceLimits.y || thickness > _CloudFarSurfaceLimits.z)
            {
                return CloudFarSurfaceFallback(15u);
            }

            opaque = true;
            break;
        }

        distance += step;
    }

    if (!opaque)
    {
        return CloudFarSurfaceFallback(15u);
    }

    float opacity = 1 - transmission;
    float mean = weightedDepth / max(0.0001, opacity);
    float variance = max(0, weightedDepthSquared / max(0.0001, opacity) - mean * mean);
    float shiftedProxyMean = geometry.z + entry - geometry.x;
    if (abs(mean - shiftedProxyMean) > tolerance)
    {
        return CloudFarSurfaceFallback(14u);
    }

    float3 representativePosition = _CameraPosition.xyz + ray * mean;
    float height = saturate((representativePosition.y - _Layer.x) / (_Layer.y - _Layer.x));
    // Current palette/phase/rim and the exact main-ray entry normal remain current-frame inputs.
    // One stored representative occlusion sample approximates an extended scattering volume.
    CloudFarSurfaceRecord(18u);
    float3 light = CloudIncidentLight(height, optical.x * extinction, optical.y,
        extinction, phase, sunCosine, entryNormal);
    lighting = float4(light * opacity, mean);
    transmissionHit = float2(0, thresholdDepth);
    moments = float2(mean, variance);
    if (!CloudFarSurfaceFinite(lighting) || !all(isfinite(moments)))
    {
        return CloudFarSurfaceFallback(11u);
    }

    CloudFarSurfaceRecord(10u);
    if (_CloudFarSurfaceDiagnostics != 0)
    {
        _CloudFarSurfaceUsage[id.xy] = 1;
    }
    return true;
}

#else

// Keep the named kernel available in the OFF variant; production OFF rays reference no proxy resources.
[numthreads(8, 8, 1)]
void BuildFarSurface(uint3 id : SV_DispatchThreadID)
{
}

#endif

#endif
