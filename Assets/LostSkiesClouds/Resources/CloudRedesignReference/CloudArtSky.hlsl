// 팔레트는 노출 전 상대 선형 입사광입니다. 최종 합성에서만 3000과 HDRP 노출을 적용합니다.
float4 _ArtKeyDirection;
float4 _ArtAtmosphereDirection;
float4 _ArtKeyColor;
float4 _ArtCelestial; // x: V2 혼합, y: 디스크 반지름(rad), z: 디스크 세기, w: 별 세기
float4 _ArtGlow; // rgb: 방향성 공기광, w: 광원 주변의 넓은 발광 세기
float4 _ArtAir; // x: 시작 거리, y: 종료 거리, z: 혼합 세기, w: 발광 지수

// 월드 방향에서 안정적인 별 난수를 만듭니다. 시간이나 화면 픽셀에 붙지 않습니다.
float ArtStarHash(float3 cell)
{
    cell = frac(cell * float3(0.1031, 0.11369, 0.13787));
    cell += dot(cell, cell.yzx + 19.19);
    return frac((cell.x + cell.y) * cell.z);
}

// 가까운/먼 하늘에 공통으로 사용할 방향성 색을 반환하며 디스크와 별은 포함하지 않습니다.
float3 ArtAtmosphere(float3 ray)
{
    float upperWeight = pow(saturate((ray.y + 0.025) * 2.7), 0.65);
    float3 legacy = lerp(_ArtSkyHorizon.rgb, _ArtSkyTop.rgb, upperWeight);
    if (ray.y < -0.025)
    {
        legacy = lerp(_ArtSkyHorizon.rgb, _ArtSkyLower.rgb, saturate((-ray.y - 0.025) * 2));
    }

    // 태양 반대편까지 따뜻한 띠가 둘러싸지 않도록 방위별로 지평선 색을 조절합니다.
    float alignment = saturate(dot(ray, normalize(_ArtAtmosphereDirection.xyz)) * 0.5 + 0.5);
    float glow = pow(alignment, max(1, _ArtAir.w));
    float horizon = exp(-abs(ray.y) * 3.0);
    float3 oppositeHorizon = lerp(_ArtSkyTop.rgb, _ArtSkyHorizon.rgb, 0.25);
    float3 directionalHorizon = lerp(oppositeHorizon, _ArtSkyHorizon.rgb, pow(alignment, 2));
    float3 concept = lerp(directionalHorizon, _ArtSkyTop.rgb, upperWeight);
    if (ray.y < -0.025)
    {
        concept = lerp(directionalHorizon, _ArtSkyLower.rgb, saturate((-ray.y - 0.025) * 2));
    }

    concept += _ArtGlow.rgb * _ArtGlow.w * glow * (0.35 + 0.65 * horizon);
    return lerp(legacy, concept, _ArtCelestial.x);
}

// 월드 구면에 작은 별을 분포시키며 기울기 기반 픽셀 폭으로 먼 점의 깜박임을 제한합니다.
float3 ArtStars(float3 ray)
{
    float3 coordinate = ray * 320;
    float3 cell = floor(coordinate);
    float seed = ArtStarHash(cell);
    float distance = length(frac(coordinate) - 0.5);
    float width = clamp(length(fwidth(coordinate)), 0.035, 0.12);
    float star = 1 - smoothstep(0.04, 0.04 + width, distance);
    star *= step(0.9985, seed) * smoothstep(0.02, 0.30, ray.y);
    return float3(0.70, 0.84, 1.0) * star * _ArtCelestial.w;
}

// 아트 하늘의 광원 디스크는 대표 조명 방향을 그대로 사용하며 합성 시 구름 투과율로 가려집니다.
float3 ArtSkyWithCelestial(float3 ray)
{
    float3 color = ArtAtmosphere(ray);
    float cosine = dot(ray, normalize(_ArtKeyDirection.xyz));
    float radius = max(0.0001, _ArtCelestial.y);
    float edge = max(fwidth(cosine) * 1.2, 0.000001);
    float disk = smoothstep(cos(radius) - edge, cos(radius) + edge, cosine);
    float halo = pow(saturate(cosine), 800) * 0.06;
    color += _ArtCelestial.x * _ArtKeyColor.rgb * _ArtCelestial.z * (disk + halo);
    color += _ArtCelestial.x * ArtStars(ray);
    return color;
}
