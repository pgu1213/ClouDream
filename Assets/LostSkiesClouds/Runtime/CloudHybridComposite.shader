Shader "Hidden/ClouDream/HybridComposite"
{
    HLSLINCLUDE
    #pragma target 5.0
    #pragma use_dxc dx12
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
    #include "CloudHybridRendering.hlsl"

    #include "CloudHybridFinalComposite.hlsl"

    // 새로운 이력을 사용하지 않고 현재 픽셀의 실제 밀도만 적분합니다.
    float4 IntegrateObject(Varyings input) : SV_Target
    {
        float2 uv = input.positionCS.xy * _HybridOutputSize.zw;
        float4 surface = _HybridSurfaceLight.SampleLevel(sampler_linear_clamp, uv, 0);
        if (_HybridVolumeAllowed < 0.5)
        {
            return surface;
        }

        float weight = HybridVolumeWeight();
        if (weight <= 0)
        {
            return surface;
        }

        float3 ray = HybridRay(uv);
        float start, end;
        if (!HybridRayBounds(ray, start, end))
        {
            return surface;
        }

        end = min(end, HybridSceneDistance(uv, ray));
        if (end <= start)
        {
            return float4(0, 0, 0, 1);
        }

        int count = (int)_HybridQuality.z;
        float step = (end - start) / max(1, count);
        float transmission = 1;
        float3 light = 0;
        float previous = start;
        float previousDensity = HybridDensity(_HybridCamera.xyz + ray * start);
        bool inside = HybridDensity(_HybridCamera.xyz) > 0.5;
        float3 shade = 0;
        bool shadeReady = false;
        [loop]
        for (int i = 0; i < count && transmission > 0.008; i++)
        {
            float distance = start + (i + 0.5) * step;
            float3 position = _HybridCamera.xyz + ray * distance;
            float density = HybridDensity(position);
            float length = step;
            if (density > 0.001 && previousDensity <= 0.001)
            {
                float lower = previous;
                float upper = distance;
                [unroll]
                for (int refinement = 0; refinement < 5; refinement++)
                {
                    float midpoint = (lower + upper) * 0.5;
                    if (HybridDensity(_HybridCamera.xyz + ray * midpoint) > 0.001)
                    {
                        upper = midpoint;
                    }
                    else
                    {
                        lower = midpoint;
                    }
                }

                position = _HybridCamera.xyz + ray * upper;
                length = min(step, distance + step * 0.5 - upper);
                shadeReady = false;
            }

            if (density > 0.001)
            {
                if (!shadeReady)
                {
                    float3 normal = HybridNormal(position);
                    float3 lightingPosition = position;
                    if (_HybridObject.x < 0.5 && _HybridCamera.y < _OceanBounds.x && ray.y > 0)
                    {
                        // 연속 운해의 아래에서 진입하면 임의 타일 측면 대신 같은 실제 하부 경계로 조명합니다.
                        float lowerDistance = (_OceanBounds.x - _HybridCamera.y) / ray.y;
                        lightingPosition = _HybridCamera.xyz + ray * lowerDistance;
                        normal = float3(0, -1, 0);
                    }

                    shade = HybridShade(lightingPosition, normal, ray, inside);
                    shadeReady = true;
                }

                float attenuation = exp(-density * length * 0.009 * _HybridEnvironment.w);
                light += transmission * (1 - attenuation) * shade;
                transmission *= attenuation;
            }

            previous = distance;
            previousDensity = density;
        }

        // 조밀한 내부를 전환 거리에서 자르지 않습니다. 구간은 실제 몸체 출구 또는 전경 깊이까지입니다.
        if (transmission <= 0.02 && shadeReady)
        {
            // 2% 이하 잔여 에너지는 같은 입사광으로 닫아 경계 타일의 불투명 내부에 틈을 남기지 않습니다.
            light += transmission * shade;
            transmission = 0;
        }

        float4 volume = float4(light, saturate(transmission));
        if (_HybridObject.x < 0.5 && surface.a > 0.999 && volume.a <= 0.02)
        {
            // 표면이 놓친 픽셀도 실제 운해 적분이 불투명하면 그 결과를 사용해 바닥 연속성을 보존합니다.
            weight = 1;
        }

        return lerp(surface, volume, weight);
    }

    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }

        Pass
        {
            Name "IntegrateAndBlend"
            ZWrite Off
            ZTest Always
            Cull Off
            // 기존 누적값 위에 한 구름을 적용합니다. 동일 구름의 두 표현은 이미 보간되어 있습니다.
            Blend One SrcAlpha, Zero SrcAlpha
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment IntegrateObject
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
