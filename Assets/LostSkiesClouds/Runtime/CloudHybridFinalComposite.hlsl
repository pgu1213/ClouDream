#ifndef CLOUD_HYBRID_FINAL_COMPOSITE_INCLUDED
#define CLOUD_HYBRID_FINAL_COMPOSITE_INCLUDED

    Texture2D<float4> _HybridSurfaceLight;
    Texture2D<float2> _HybridSurfaceDepth;
    Texture2D<float4> _HybridAccumulation;
    Texture2D<float> _HybridLowSceneDepth;
    float _HybridBrightness;
    float _HybridSkyBlend;
    float _HybridVolumeAllowed;
    float4 _HybridSkyTop, _HybridSkyHorizon, _HybridSkyLower;
    float4 _ArtSkyTop, _ArtSkyHorizon, _ArtSkyLower;

    #include "CloudArtSky.hlsl"

    // 전경과 배경을 섞지 않는 4표본 업샘플링입니다. 대응 표본이 없으면 전경을 보존합니다.
    float4 ReconstructHybrid(float2 uv, float sceneDepth)
    {
        float2 position = uv * _HybridOutputSize.xy - 0.5;
        int2 first = (int2)floor(position);
        float2 fraction = frac(position);
        float4 result = 0;
        float total = 0;
        [unroll]
        for (int y = 0; y < 2; y++)
        {
            [unroll]
            for (int x = 0; x < 2; x++)
            {
                int2 address = clamp(first + int2(x, y), 0, (int2)_HybridOutputSize.xy - 1);
                float lowDepth = _HybridLowSceneDepth.Load(int3(address, 0));
                float weight = lerp(1 - fraction.x, fraction.x, (float)x)
                    * lerp(1 - fraction.y, fraction.y, (float)y);
                if ((sceneDepth == 0) != (lowDepth == 0))
                {
                    weight = 0;
                }
                else if (sceneDepth != 0)
                {
                    float current = LinearEyeDepth(sceneDepth, _ZBufferParams);
                    float difference = abs(LinearEyeDepth(lowDepth, _ZBufferParams) - current);
                    weight *= 1 - smoothstep(max(1, current * 0.01), max(2, current * 0.03), difference);
                }

                result += _HybridAccumulation.Load(int3(address, 0)) * weight;
                total += weight;
            }
        }

        if (total <= 0.0001)
        {
            return float4(0, 0, 0, 1);
        }

        return result / total;
    }

    // HDRP 노출을 최종 단계에서 한 번 적용하고 배경 깊이에만 공유 하늘색을 합성합니다.
    float4 CompositeClouds(Varyings input) : SV_Target
    {
        float2 uv = input.positionCS.xy * _ScreenSize.zw;
        float sceneDepth = LoadCameraDepth((uint2)input.positionCS.xy);
        float4 clouds = ReconstructHybrid(uv, sceneDepth);
        float3 light = clouds.rgb * 3000 * _HybridBrightness * GetCurrentExposureMultiplier();
        float skyBlend = 0;
        if (sceneDepth == 0)
        {
            skyBlend = _HybridSkyBlend;
            float3 ray = HybridRay(uv);
            float3 sky = ArtSkyWithCelestial(ray);

            light += clouds.a * skyBlend * sky * 3000 * GetCurrentExposureMultiplier();
        }

        return float4(light, clouds.a * (1 - skyBlend));
    }

    // 재구성용 장면 깊이는 구름의 대표 깊이와 분리해서 보존합니다.
    float CopyHybridSceneDepth(Varyings input) : SV_Target
    {
        float2 uv = input.positionCS.xy * _HybridOutputSize.zw;
        return LoadCameraDepth((uint2)(uv * _ScreenSize.xy));
    }

#endif
