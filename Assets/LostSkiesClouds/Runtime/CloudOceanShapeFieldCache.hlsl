#ifndef CLOUD_OCEAN_SHAPE_FIELD_CACHE_INCLUDED
#define CLOUD_OCEAN_SHAPE_FIELD_CACHE_INCLUDED

// The definition follows the upper-body include. Both caches share the same sampled actual-call buffer.
void CloudRecordCacheCall(uint counter);

// Four 96×48×96 grids use a thin, fixed ocean Y domain. RGB stores pre-weather field, size and detail mask.
Texture3D<float4> _CloudOceanShapeField;
RWTexture3D<float4> _CloudOceanShapeFieldWrite;
float4 _CloudOceanShapeRegions[4];
float4 _CloudOceanShapeSettings;
int _CloudOceanShapeFieldEnabled;
int _CloudOceanShapeBuildCascade;

// Builds only the immutable ocean body. Weather expansion, bottom clipping and dynamic noise remain queries.
[numthreads(4, 4, 4)]
void BuildOceanShapeFields(uint3 id : SV_DispatchThreadID)
{
    uint cascade = (uint)_CloudOceanShapeBuildCascade;
    float4 region = _CloudOceanShapeRegions[cascade];
    float3 position = region.xyz + float3(id.x * region.w,
        id.y * _CloudOceanShapeSettings.z, id.z * region.w);
    ConceptShapeSample sample = ConceptSampleOceanBody(position, true);
    uint3 address = uint3(id.xy, id.z + cascade * 96u);
    _CloudOceanShapeFieldWrite[address] = float4(sample.distance,
        sample.characteristicSize, sample.detailMask, 0);
}

// Continuous camera-distance weights blend overlapping world-fixed grids; uncovered points retain exact fields.
bool ConceptTryOceanShapeField(float3 position, out ConceptShapeSample sample, out float weight)
{
    sample = ConceptEmptySample();
    weight = 0;
    if (CloudNormalProducerExactOnly)
    {
        return false;
    }

    CloudRecordCacheCall(8u);
    #ifdef CLOUD_SHAPE_FIELD_CACHE
    if (_CloudOceanShapeFieldEnabled == 0)
    {
        CloudRecordCacheCall(11u);
        return false;
    }

    float distanceToCamera = length(position - _CameraPosition.xyz);
    if (distanceToCamera <= _CloudOceanShapeSettings.x)
    {
        CloudRecordCacheCall(11u);
        return false;
    }

    float4 combined = 0;
    float remaining = 1;
    [unroll]
    for (int cascade = 0; cascade < 4; cascade++)
    {
        float4 region = _CloudOceanShapeRegions[cascade];
        float3 cell = (position - region.xyz) / float3(region.w, _CloudOceanShapeSettings.z, region.w);
        float2 relative = abs(position.xz - _CameraPosition.xz) / region.w;
        float cascadeWeight = 1 - smoothstep(30, 38, max(relative.x, relative.y));
        if (remaining > 0 && cascadeWeight > 0 && all(cell >= 0)
            && cell.x <= 95 && cell.y <= 47 && cell.z <= 95)
        {
            float3 texel = cell + 0.5;
            texel.z += cascade * 96;
            float4 field = _CloudOceanShapeField.SampleLevel(sampler_linear_clamp,
                texel / float3(96, 48, 384), 0);
            combined += field * (remaining * cascadeWeight);
            remaining *= 1 - cascadeWeight;
        }
    }

    float available = 1 - remaining;
    if (available <= 0.00001)
    {
        CloudRecordCacheCall(11u);
        return false;
    }

    combined /= available;
    sample.distance = combined.x;
    sample.characteristicSize = max(700, combined.y);
    sample.detailMask = saturate(combined.z);
    sample.densityMultiplier = 1;
    weight = available * smoothstep(_CloudOceanShapeSettings.x, _CloudOceanShapeSettings.y, distanceToCamera);
    if (weight >= 1)
    {
        CloudRecordCacheCall(9u);
    }
    else
    {
        CloudRecordCacheCall(10u);
    }

    return true;
    #else
    CloudRecordCacheCall(11u);
    return false;
    #endif
}

#endif
