#ifndef CLOUD_SPATIAL_LIGHTING_INCLUDED
#define CLOUD_SPATIAL_LIGHTING_INCLUDED

// 각 64³ 캐스케이드를 Z 방향으로 연결합니다. 색·법선·시선 의존값을 저장하지 않습니다.
Texture3D<float2> _CloudSpatialLight;
RWTexture3D<float2> _CloudSpatialLightWrite;
float4 _CloudSpatialRegions[3];

// 텍스처 범위 밖에서 드리워지는 그림자도 원래 밀도 함수를 직접 평가하여 포함합니다.
[numthreads(4, 4, 4)]
void BuildSpatialLighting(uint3 id : SV_DispatchThreadID)
{
    uint cascade = id.z / 64;
    float3 cell = float3(id.xy, id.z % 64);
    float3 position = _CloudSpatialRegions[cascade].xyz + cell * _CloudSpatialRegions[cascade].w;
    float extinction = CloudExtinction();
    _CloudSpatialLightWrite[id] = CloudEvaluateOcclusion(position, normalize(_SunDirection.xyz), extinction);
}

// 월드 격자의 이동과 별개인 연속 카메라 거리로 레벨을 섞어, 창이 이동하는 프레임의 명암 점프를 피합니다.
float2 CloudSpatialOcclusion(float3 position, out float exactWeight)
{
    float2 result = 0;
    exactWeight = 1;
    [unroll]
    for (int cascade = 0; cascade < 3; cascade++)
    {
        float3 cell = (position - _CloudSpatialRegions[cascade].xyz) / _CloudSpatialRegions[cascade].w;
        float3 cameraDistance = abs(position - _CameraPosition.xyz) / _CloudSpatialRegions[cascade].w;
        // 원점 스냅은 8셀입니다. 반경 22셀은 이동 전후 모두 실제 64³ 텍스처 안에 남습니다.
        float weight = 1 - smoothstep(16, 22, max(cameraDistance.x, max(cameraDistance.y, cameraDistance.z)));
        if (weight > 0 && exactWeight > 0)
        {
            float3 uv = (cell + 0.5) / 64;
            uv.z = (uv.z + cascade) / 3;
            result += _CloudSpatialLight.SampleLevel(sampler_linear_clamp, uv, 0) * (exactWeight * weight);
            exactWeight *= 1 - weight;
        }
    }

    return result;
}

#endif
