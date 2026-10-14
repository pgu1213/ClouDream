#ifndef CLOUD_SHAPE_FIELD_CACHE_INCLUDED
#define CLOUD_SHAPE_FIELD_CACHE_INCLUDED

// Actual call counters from one pixel in 64: body query/full/blend/exact, occlusion query/full/blend/exact.
// Cache-build dispatches never call CloudBeginCacheSample and therefore never contaminate ray statistics.
RWStructuredBuffer<uint> _CloudCacheCounters;
int _CloudCacheDiagnosticsEnabled;
static bool CloudCacheDiagnosticSample = false;

// Detailed normal differences can reuse fields independently of the main ray density quality option.
static bool CloudShapeFieldNormalQuery = false;

// Pixel selection is independent of density, coverage, distance and the eventual cache outcome.
void CloudBeginCacheSample(uint2 pixel)
{
    CloudCacheDiagnosticSample = _CloudCacheDiagnosticsEnabled != 0
        && ((pixel.x * 1973u + pixel.y * 9277u) & 63u) == 0u;
}

// Atomics are disabled by default and are only used by explicitly requested diagnostic renders.
void CloudRecordCacheCall(uint counter)
{
    if (CloudCacheDiagnosticSample)
    {
        InterlockedAdd(_CloudCacheCounters[counter], 1u);
    }
}

// Each 40³ RGHalf block stores the original scalar before displacement and density saturation.
// The two channels remain separate: R contains 14 ellipsoids; G contains the original five macro ellipsoids.
Texture3D<float2> _CloudShapeFieldAtlas;
RWTexture3D<float2> _CloudShapeFieldWrite;
StructuredBuffer<int> _CloudShapeFieldDirectory;
RWStructuredBuffer<float4> _CloudShapeFieldCells;
StructuredBuffer<int> _CloudShapeFieldBuildSlots;
int4 _CloudShapeFieldRegion;
float4 _CloudShapeFieldSettings;

// Converts a bounded slot index to an atlas origin without allowing filtering between independent cells.
uint3 ConceptShapeBlockOrigin(uint slot)
{
    return uint3(slot % 4u, (slot / 4u) % 4u, slot / 16u) * 40u;
}

// Reuses the unchanged procedural body function once per grid vertex, not once per ray/normal/light query.
[numthreads(4, 4, 4)]
void BuildShapeFields(uint3 id : SV_DispatchThreadID)
{
    uint buildIndex = id.z / 40u;
    uint slot = (uint)_CloudShapeFieldBuildSlots[buildIndex];
    uint3 voxel = uint3(id.xy, id.z % 40u);
    int2 cell = (int2)_CloudShapeFieldCells[slot].xy;
    float3 radius = float3(lerp(_SkyShape.x, _SkyShape.y, CellRandom(cell, 4)),
        lerp(_SkyAltitude.z, _SkyAltitude.w, CellRandom(cell, 6)),
        lerp(_SkyShape.x, _SkyShape.y, CellRandom(cell, 5)));
    radius.xz = min(radius.xz, max(4000, _SkyPlacement.x) * 0.38);
    float3 extent = radius + 512;
    float3 position = (float3(voxel) / 39.0 * 2 - 1) * extent;
    float characteristicSize;
    float lightingField;
    float renderField = ConceptSkyBodyFields(position, radius, cell, true, characteristicSize, lightingField);
    _CloudShapeFieldWrite[ConceptShapeBlockOrigin(slot) + voxel] = float2(renderField, lightingField);
    if (all(voxel == 0u))
    {
        _CloudShapeFieldCells[slot] = float4(cell, characteristicSize, 0);
    }
}

// Reads only an available occupied block. Near silhouettes and all missing/outside cells use exact evaluation.
bool ConceptTryShapeField(float3 localPosition, float3 radius, int2 cell, float3 worldPosition,
    bool useDetail, out float field, out float characteristicSize, out float weight)
{
    field = 0;
    characteristicSize = 0;
    weight = 0;
    if (CloudNormalProducerExactOnly)
    {
        return false;
    }

    CloudRecordCacheCall(0u);
    #ifdef CLOUD_SHAPE_FIELD_CACHE
    int2 address = cell - _CloudShapeFieldRegion.xy;
    if (any(address < 0) || any(address >= _CloudShapeFieldRegion.z)
        || (useDetail && _CloudShapeFieldSettings.z < 0.5 && !CloudShapeFieldNormalQuery))
    {
        CloudRecordCacheCall(3u);
        return false;
    }

    int storedSlot = _CloudShapeFieldDirectory[address.y * _CloudShapeFieldRegion.z + address.x];
    float distanceToCamera = length(worldPosition - _CameraPosition.xyz);
    if (storedSlot <= 0 || distanceToCamera <= _CloudShapeFieldSettings.x)
    {
        CloudRecordCacheCall(3u);
        return false;
    }

    float3 normalized = localPosition / (radius + 512) * 0.5 + 0.5;
    if (any(normalized < 0) || any(normalized > 1))
    {
        CloudRecordCacheCall(3u);
        return false;
    }

    uint slot = (uint)(storedSlot - 1);
    float3 texel = ConceptShapeBlockOrigin(slot) + normalized * 39 + 0.5;
    float2 fields = _CloudShapeFieldAtlas.SampleLevel(sampler_linear_clamp,
        texel / float3(160, 160, 320), 0);
    field = fields.y;
    if (useDetail)
    {
        field = fields.x;
    }

    characteristicSize = _CloudShapeFieldCells[slot].z;
    weight = smoothstep(_CloudShapeFieldSettings.x, _CloudShapeFieldSettings.y, distanceToCamera);
    if (weight >= 1)
    {
        CloudRecordCacheCall(1u);
    }
    else
    {
        CloudRecordCacheCall(2u);
    }

    return true;
    #else
    CloudRecordCacheCall(3u);
    return false;
    #endif
}

#endif
