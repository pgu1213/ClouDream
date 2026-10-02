#ifndef CLOUD_RECONSTRUCTION_INCLUDED
#define CLOUD_RECONSTRUCTION_INCLUDED

Texture2DArray<float4> _CloudLighting;
Texture2DArray<float4> _CloudTransmittance;
Texture2DArray<float4> _CloudLowSceneDepth;

float4 _CloudOutputSize;
float4 _CloudDepthParams;
float _CloudCompact;
float _CloudDepthUpsampling;

// Concept V2의 조명 RGB와 깊이 모멘트는 opacity가 곱해진 값입니다. 동일한 가중치로 투과율과 함께 복원합니다.
struct CloudReconstructionSample
{
    float4 lighting;
    float transmission;
};

// 렌더러와 같은 역 Z 변환을 사용합니다. 0은 배경이며 유한 깊이와 별도로 비교합니다.
float CloudEyeDepth(float rawDepth)
{
    return rcp(rawDepth * _CloudDepthParams.x + _CloudDepthParams.y);
}

// 장면의 같은 표면에서 얻은 저해상도 광선에 가중치를 주고 전경/배경 혼합을 막습니다.
float CloudDepthWeight(float highDepth, float lowDepth)
{
    if (highDepth == 0 && lowDepth == 0)
    {
        return 1;
    }

    if (highDepth == 0 || lowDepth == 0)
    {
        return 0;
    }

    float highEye = CloudEyeDepth(highDepth);
    float difference = abs(highEye - CloudEyeDepth(lowDepth)) / max(2, highEye * 0.02);
    if (difference > 8)
    {
        return 0;
    }

    return rcp(1 + difference * difference);
}

// 저해상도에서 놓친 얇은 전경도 전체 해상도 깊이보다 뒤에 있는 구름으로 덮지 않습니다.
CloudReconstructionSample CloudLoadReconstruction(int2 pixel, float sceneDepth, float viewCosine)
{
    pixel = clamp(pixel, int2(0, 0), (int2)_CloudOutputSize.xy - 1);
    CloudReconstructionSample result;
    result.lighting = _CloudLighting.Load(int4(pixel, 0, 0));
    float4 transmission = _CloudTransmittance.Load(int4(pixel, 0, 0));
    result.transmission = saturate(transmission.r);
    float firstHit = transmission.a;
    if (_CloudCompact > 0.5)
    {
        firstHit = transmission.g;
    }

    if (sceneDepth > 0)
    {
        float eyeDepth = CloudEyeDepth(sceneDepth);
        float tolerance = max(1, eyeDepth * 0.001);
        if (firstHit * viewCosine > eyeDepth + tolerance)
        {
            result.lighting = 0;
            result.transmission = 1;
        }
    }

    return result;
}

// 네 이웃의 깊이 가중 보간을 수행합니다. 대응 표면이 없을 때만 3×3에서 가까운 표면을 찾습니다.
CloudReconstructionSample CloudReconstruct(float2 uv, float sceneDepth, float viewCosine)
{
    CloudReconstructionSample result;
    if (_CloudDepthUpsampling < 0.5)
    {
        result.lighting = _CloudLighting.SampleLevel(s_linear_clamp_sampler, float3(uv, 0), 0);
        result.transmission = saturate(_CloudTransmittance.SampleLevel(s_linear_clamp_sampler, float3(uv, 0), 0).r);
        return result;
    }

    float2 location = uv * _CloudOutputSize.xy - 0.5;
    int2 basePixel = (int2)floor(location);
    float2 fraction = frac(location);
    float totalWeight = 0;
    result.lighting = 0;
    result.transmission = 0;
    [unroll]
    for (int y = 0; y < 2; y++)
    {
        [unroll]
        for (int x = 0; x < 2; x++)
        {
            int2 pixel = clamp(basePixel + int2(x, y), int2(0, 0), (int2)_CloudOutputSize.xy - 1);
            float2 spatial = lerp(1 - fraction, fraction, float2(x, y));
            float depth = _CloudLowSceneDepth.Load(int4(pixel, 0, 0)).r;
            float weight = spatial.x * spatial.y * CloudDepthWeight(sceneDepth, depth);
            CloudReconstructionSample sample = CloudLoadReconstruction(pixel, sceneDepth, viewCosine);
            result.lighting += sample.lighting * weight;
            result.transmission += sample.transmission * weight;
            totalWeight += weight;
        }
    }

    if (totalWeight > 0.00001)
    {
        result.lighting /= totalWeight;
        result.transmission = saturate(result.transmission / totalWeight);
        return result;
    }

    int2 nearest = (int2)floor(location + 0.5);
    int2 bestPixel = nearest;
    float bestScore = -1;
    [unroll]
    for (int dy = -1; dy <= 1; dy++)
    {
        [unroll]
        for (int dx = -1; dx <= 1; dx++)
        {
            int2 pixel = clamp(nearest + int2(dx, dy), int2(0, 0), (int2)_CloudOutputSize.xy - 1);
            float depth = _CloudLowSceneDepth.Load(int4(pixel, 0, 0)).r;
            float2 delta = pixel - location;
            float score = CloudDepthWeight(sceneDepth, depth) / (1 + dot(delta, delta));
            if (score > bestScore)
            {
                bestScore = score;
                bestPixel = pixel;
            }
        }
    }

    // 표면이 어떤 이웃에도 없으면 가장 가까운 광선을 사용하며 뒤쪽 구름 제거는 여전히 적용합니다.
    if (bestScore <= 0)
    {
        bestPixel = nearest;
    }

    return CloudLoadReconstruction(bestPixel, sceneDepth, viewCosine);
}

#endif
