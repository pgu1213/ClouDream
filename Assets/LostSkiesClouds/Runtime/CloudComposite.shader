Shader "Hidden/ClouDream/LostSkiesComposite"
{
    HLSLINCLUDE
    #pragma target 4.5
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"

    TEXTURE2D_ARRAY(_CloudLighting);
    TEXTURE2D_ARRAY(_CloudTransmittance);

    float _CloudBrightness;
    float _UseSkyLighting;
    float4 _CloudShadowTint;
    float4 _CloudHighlightTint;
    float4 _CloudOutputSize;

    float4 _CloudViewForward, _CloudViewRight, _CloudViewUp;
    float4 _ArtSkyTop, _ArtSkyHorizon, _ArtSkyLower;
    float _ArtSkyBlend;

    // 태양 디스크가 있는 HDRP 배경에 컨셉의 청색 천정과 따뜻한 지평선 색을 섞습니다.
    float3 EvaluateArtSky(float2 screenUv)
    {
        float3 ray = normalize(_CloudViewForward.xyz + (screenUv.x * 2 - 1) * _CloudViewRight.xyz
            + (screenUv.y * 2 - 1) * _CloudViewUp.xyz);
        float upperWeight = pow(saturate((ray.y + 0.025) * 2.7), 0.65);
        float3 color = lerp(_ArtSkyHorizon.rgb, _ArtSkyTop.rgb, upperWeight);
        if (ray.y < -0.025)
        {
            color = lerp(_ArtSkyHorizon.rgb, _ArtSkyLower.rgb, saturate((-ray.y - 0.025) * 2));
        }

        return color * 3000.0 * GetCurrentExposureMultiplier();
    }

    // 적분 결과의 투과율을 유지하며 구름 표면의 명암과 색을 합성합니다.
    float4 Composite(Varyings input) : SV_Target
    {
        float2 uv = input.positionCS.xy * _ScreenSize.zw;
        uv.y = 1 - uv.y;

        float3 light = SAMPLE_TEXTURE2D_ARRAY_LOD(_CloudLighting, s_linear_clamp_sampler, uv, 0, 0).rgb;
        float3 transmittance = SAMPLE_TEXTURE2D_ARRAY_LOD(_CloudTransmittance, s_linear_clamp_sampler, uv, 0, 0).rgb;
        float transmission = saturate(dot(transmittance, 1.0 / 3.0));
        float opacity = 1 - transmission;

        if (_UseSkyLighting > 0.5)
        {
            // 노이즈 렌더러의 상대 방사량을 HDRP 휘도 범위로 보정한 뒤 노출을 한 번만 적용합니다.
            // EV12에서의 기준을 3000으로 정했으며, 시간대가 바뀌어도 이 기준은 고정합니다.
            float3 exposedRadiance = light * (3000.0 * _CloudBrightness) * GetCurrentExposureMultiplier();
            float skyBlend = 0;
            float sceneDepth = LoadCameraDepth((uint2)input.positionCS.xy);
            if (sceneDepth == 0)
            {
                skyBlend = _ArtSkyBlend;
                exposedRadiance += transmission * skyBlend * EvaluateArtSky(input.positionCS.xy * _ScreenSize.zw);
            }

            return float4(exposedRadiance, transmission * (1 - skyBlend));
        }

        float3 normalizedLight = light / max(opacity, 0.0001);
        float shade = smoothstep(0.22, 1.12, dot(normalizedLight, float3(0.2126, 0.7152, 0.0722)));
        float3 color = lerp(_CloudShadowTint.rgb, _CloudHighlightTint.rgb, shade) * _CloudBrightness;
        return float4(color * opacity, transmission);
    }

    // HDRP 장면 깊이를 컴퓨트의 출력 해상도 및 UV 방향에 맞게 복사합니다.
    float4 CopyDepth(Varyings input) : SV_Target
    {
        uint2 pixel = (uint2)(input.positionCS.xy / _CloudOutputSize.xy * _ScreenSize.xy);
        pixel.y = (uint)_ScreenSize.y - 1 - pixel.y;
        float depth = LoadCameraDepth(pixel);
        return depth.xxxx;
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }

        Pass
        {
            Name "Composite"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One SrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Composite
            ENDHLSL
        }

        Pass
        {
            Name "Depth"
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment CopyDepth
            ENDHLSL
        }
    }
}
