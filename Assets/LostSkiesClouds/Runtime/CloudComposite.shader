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

    #include "CloudArtSky.hlsl"

    // 카메라의 투영에서 월드 방향을 복원합니다. 천체·공기광·구름은 같은 좌표계를 사용합니다.
    float3 CloudWorldRay(float2 screenUv)
    {
        return normalize(_CloudViewForward.xyz + (screenUv.x * 2 - 1) * _CloudViewRight.xyz
            + (screenUv.y * 2 - 1) * _CloudViewUp.xyz);
    }

    // 적분 결과의 투과율을 유지하며 구름 표면의 명암과 색을 합성합니다.
    float4 Composite(Varyings input) : SV_Target
    {
        float2 uv = input.positionCS.xy * _ScreenSize.zw;
        uv.y = 1 - uv.y;

        float4 cloudSample = SAMPLE_TEXTURE2D_ARRAY_LOD(_CloudLighting, s_linear_clamp_sampler, uv, 0, 0);
        float3 light = cloudSample.rgb;
        float3 transmittance = SAMPLE_TEXTURE2D_ARRAY_LOD(_CloudTransmittance, s_linear_clamp_sampler, uv, 0, 0).rgb;
        float transmission = saturate(dot(transmittance, 1.0 / 3.0));
        float opacity = 1 - transmission;

        if (_UseSkyLighting > 0.5)
        {
            // 노이즈 렌더러의 상대 방사량을 HDRP 휘도 범위로 보정한 뒤 노출을 한 번만 적용합니다.
            // EV12에서의 기준을 3000으로 정했으며, 시간대가 바뀌어도 이 기준은 고정합니다.
            float3 ray = CloudWorldRay(input.positionCS.xy * _ScreenSize.zw);
            float3 atmosphere = ArtAtmosphere(ray);
            float representativeDepth = cloudSample.a / max(opacity, 0.0001);
            float air = smoothstep(_ArtAir.x, max(_ArtAir.x + 1, _ArtAir.y), representativeDepth);
            air *= _ArtAir.z * _ArtCelestial.x;
            // RGB는 이미 premultiplied입니다. 투과율을 바꾸거나 빈 공간에 구름색을 만들지 않습니다.
            light = lerp(light, opacity * atmosphere, saturate(air));
            float3 exposedRadiance = light * (3000.0 * _CloudBrightness) * GetCurrentExposureMultiplier();
            float skyBlend = 0;
            float sceneDepth = LoadCameraDepth((uint2)input.positionCS.xy);
            if (sceneDepth == 0)
            {
                skyBlend = _ArtSkyBlend;
                exposedRadiance += transmission * skyBlend * ArtSkyWithCelestial(ray) * 3000.0 * GetCurrentExposureMultiplier();
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
