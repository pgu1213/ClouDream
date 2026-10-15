#ifndef CLOUD_CONCEPT_LIGHTING_INCLUDED
#define CLOUD_CONCEPT_LIGHTING_INCLUDED

// 기존 Concept V2의 팔레트 응답을 동일하게 공유합니다. 차폐 입력의 생산은 렌더 방식별입니다.
float3 ConceptIncidentLight(float opticalDepth, float skyVisibility, float sunCosine, float3 surfaceNormal)
{
    float facing = 0.45;
    if (dot(surfaceNormal, surfaceNormal) > 0.1)
    {
        facing = saturate(dot(surfaceNormal, normalize(_SunDirection.xyz)) * 0.65 + 0.35);
    }

    float visibility = exp(-opticalDepth * 0.35);
    float coordinate = smoothstep(0.08, 0.92, facing) * smoothstep(0.02, 0.95, visibility);
    coordinate = saturate((coordinate - 0.5) * _ConceptLighting.z + 0.5);
    float3 illuminated = lerp(_CloudPaletteMid.rgb, _CloudPaletteLight.rgb, smoothstep(0.18, 0.80, coordinate));
    float3 ambient = _CloudPaletteShadow.rgb * _AmbientSky.w * lerp(0.58, 1.0, skyVisibility);
    float rim = pow(saturate(sunCosine), 18) * exp(-opticalDepth * 0.7) * _AmbientHorizon.w * 0.18;
    float3 key = (illuminated * coordinate + _CloudPaletteLight.rgb * rim) * _SunColor.w;
    return ambient + key;
}

#endif
