Shader "Hidden/ClouDream/LobeComposite"
{
    HLSLINCLUDE
    #pragma target 5.0
    #pragma use_dxc dx12
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
    #include "CloudHybridRendering.hlsl"
    #include "CloudLobeGeometry.hlsl"
    #include "CloudHybridFinalComposite.hlsl"
    #include "CloudLobePrepared.hlsl"
    StructuredBuffer<uint> _LobeTileCounts;
    StructuredBuffer<uint> _LobeTileIds;
    Texture2D<float4> _LobeFarLight;
    Texture2D<float2> _LobeFarDepth;
    float4 _LobeTileSize;
    int _LobeObjectCount;
    float _LobeVolumeEnabled;

    // 타일 목록의 포화는 밀도를 잃지 않고 작은 전체 국소 목록으로 대체합니다.
    uint LobeObjectId(uint tile, uint index, uint tileCount)
    {
        if (tileCount > 32)
        {
            return index;
        }
        return _LobeTileIds[tile * 32 + index];
    }

    // 겹치는 로브·운해는 더 진한 밀도를 사용해 같은 구름의 중복 감쇠를 막습니다.
    float LobeUnionDensity(float3 position, uint tile, uint count, uint tileCount)
    {
        float density = 0;
        [loop]
        for (uint index = 0; index < count; index++)
        {
            uint objectIndex = LobeObjectId(tile, index, tileCount);
            NearObject cloud = _LobeObjects[objectIndex];
            int kind = (int)cloud.cell.x;
            int2 cell = (int2)cloud.cell.yz;
            if (cloud.cell.w > 0)
            {
                density = max(density, LobePreparedDensity(position, objectIndex));
            }
        }
        return density;
    }

    // 하나의 전방 적분이 모든 가까운 구름을 처리하고 먼 표면/불투명 장면 깊이에서 멈춥니다.
    float4 LobeIntegrate(Varyings input) : SV_Target
    {
        float2 uv = input.positionCS.xy * _HybridOutputSize.zw;
        float4 surface = _HybridSurfaceLight.SampleLevel(sampler_linear_clamp, uv, 0);
        if (_LobeVolumeEnabled < 0.5 || _LobeObjectCount == 0)
        {
            return surface;
        }
        uint2 tileAddress = min((uint2)(input.positionCS.xy / 16), (uint2)_LobeTileSize.xy - 1);
        uint tile = tileAddress.x + tileAddress.y * (uint)_LobeTileSize.x;
        uint tileCount = _LobeTileCounts[tile];
        uint count = tileCount;
        if (tileCount > 32)
        {
            count = (uint)_LobeObjectCount;
        }
        if (count == 0)
        {
            return surface;
        }
        float3 ray = HybridRay(uv);
        float weight = 0;
        float start = 1000000;
        float end = 0;
        [loop]
        for (uint objectIndex = 0; objectIndex < count; objectIndex++)
        {
            NearObject cloud = _LobeObjects[LobeObjectId(tile, objectIndex, tileCount)];
            float first, last;
            if (cloud.cell.w > 0 && HybridBoundsInterval(ray, cloud.minimum.xyz, cloud.size.xyz, first, last))
            {
                weight = max(weight, cloud.cell.w);
                start = min(start, first);
                end = max(end, last);
            }
        }
        if (weight <= 0 || end <= start)
        {
            return surface;
        }
        float4 farSurface = _LobeFarLight.SampleLevel(sampler_linear_clamp, uv, 0);
        float farDistance = _LobeFarDepth.SampleLevel(sampler_linear_clamp, uv, 0).x;
        end = min(end, HybridSceneDistance(uv, ray));
        bool inside = LobeUnionDensity(_HybridCamera.xyz, tile, count, tileCount) > 0.5;
        if (!inside)
        {
            end = min(end, _HybridQuality.y + 500);
        }
        if (farSurface.a < 0.5)
        {
            end = min(end, farDistance);
        }
        if (end <= start)
        {
            return surface;
        }
        float step = (end - start) / max(1, _HybridQuality.z);
        float3 light = 0;
        float transmission = 1;
        float previousDistance = start;
        float previousDensity = LobeUnionDensity(_HybridCamera.xyz + ray * start, tile, count, tileCount);
        float3 shade = 0;
        bool shaded = false;
        [loop]
        for (int sampleIndex = 0; sampleIndex < (int)_HybridQuality.z && transmission > 0.008; sampleIndex++)
        {
            float distance = start + (sampleIndex + 0.5) * step;
            float3 position = _HybridCamera.xyz + ray * distance;
            float density = LobeUnionDensity(position, tile, count, tileCount);
            float segment = step;
            if (density > 0.001 && previousDensity <= 0.001)
            {
                float lower = previousDistance;
                float upper = distance;
                [unroll]
                for (int refinement = 0; refinement < 4; refinement++)
                {
                    float midpoint = (lower + upper) * 0.5;
                    if (LobeUnionDensity(_HybridCamera.xyz + ray * midpoint, tile, count, tileCount) > 0.001)
                    {
                        upper = midpoint;
                    }
                    else
                    {
                        lower = midpoint;
                    }
                }
                position = _HybridCamera.xyz + ray * upper;
                segment = min(step, distance + step * 0.5 - upper);
                shaded = false;
            }
            if (density > 0.001)
            {
                if (!shaded)
                {
                    float depth = 0;
                    [loop]
                    for (int lightIndex = 0; lightIndex < (int)_HybridLightSettings.y; lightIndex++)
                    {
                        depth += LobeUnionDensity(position + _HybridLightDirection.xyz * (lightIndex + 1) * 160,
                            tile, count, tileCount) * 160 * 0.009 * _HybridEnvironment.w;
                    }
                    float3 normal = -ray;
                    if (inside)
                    {
                        normal = 0;
                    }
                    shade = ConceptIncidentLight(depth, saturate(1 - depth * 0.08), dot(ray, _HybridLightDirection.xyz), normal);
                    shaded = true;
                }
                float attenuation = exp(-density * segment * 0.009 * _HybridEnvironment.w);
                light += transmission * (1 - attenuation) * shade;
                transmission *= attenuation;
            }
            previousDensity = density;
            previousDistance = distance;
        }
        if (transmission < 0.02 && shaded)
        {
            light += transmission * shade;
            transmission = 0;
        }
        float2 nearestDepth = _HybridSurfaceDepth.SampleLevel(sampler_linear_clamp, uv, 0);
        float4 nearSurface = float4(0, 0, 0, 1);
        if (nearestDepth.y > 0.0001)
        {
            nearSurface = surface;
        }
        if (!shaded)
        {
            return surface;
        }
        if (nearestDepth.y > 0.0001)
        {
            weight = nearestDepth.y;
        }
        float4 local = lerp(nearSurface, float4(light, transmission), weight);
        return float4(local.rgb + local.a * farSurface.rgb, local.a * farSurface.a);
    }
    ENDHLSL
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Pass
        {
            Name "SingleLocalVolume"
            ZWrite Off
            ZTest Always
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment LobeIntegrate
            ENDHLSL
        }
        Pass
        {
            Name "Composite"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One SrcAlpha
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment CompositeClouds
            ENDHLSL
        }
        Pass
        {
            Name "CopySceneDepth"
            ZWrite Off
            ZTest Always
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment CopyHybridSceneDepth
            ENDHLSL
        }
    }
}
