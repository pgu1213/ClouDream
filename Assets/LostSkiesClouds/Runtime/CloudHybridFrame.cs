using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    /// <summary>한 카메라의 같은 시점에서 메시·볼륨·하늘이 공유하는 읽기 전용 입력입니다.</summary>
    internal sealed class CloudHybridFrame
    {
        public CloudHybridGeometry ocean;

        public CloudHybridGeometry sky;

        public CloudHybridGeometry style;

        public CloudEnvironment environment;

        public Vector3 origin;

        public Vector3 cameraPosition;

        public Vector3 wind;

        public int shapeHash;

        /// <summary>미지원 형태와 추출값 전용 모드는 명시적으로 기존 렌더 경로에 남깁니다.</summary>
        public bool Update(LostSkiesCloudPass source, Camera camera)
        {
            if (source == null || source.useExtractedValues || source.oceanProfile == null
                || source.skyProfile == null || source.styleProfile == null
                || !source.oceanProfile.SupportsHybridRendering || !source.skyProfile.SupportsHybridRendering
                || !source.styleProfile.SupportsHybridRendering)
            {
                return false;
            }

            if (!source.oceanProfile.TryGetHybridGeometry(out ocean)
                || !source.skyProfile.TryGetHybridGeometry(out sky)
                || !source.styleProfile.TryGetHybridGeometry(out style))
            {
                return false;
            }

            origin = Vector3.zero;
            if (source.worldOrigin != null)
            {
                origin = source.worldOrigin.SamplingOffset;
            }

            cameraPosition = camera.transform.position + origin;
            environment = source.ResolveEnvironment(cameraPosition);
            environment.densityMultiplier *= Mathf.Max(0f, source.cloudDensity) / 160000f;
            environment.coverageOffset += source.towerCoverage - 0.165f;
            wind = source.GetSamplingWind();
            // 색·밀도·바람·조명은 이 키에 넣지 않아 매 프레임 메시 재생성을 방지합니다.
            shapeHash = HashCode.Combine(ocean.bounds, sky.placement, sky.bounds, sky.shape,
                style.shape.x, style.shape.y, style.detail.z,
                HashCode.Combine(style.detail.w, style.edge.x));
            return true;
        }

        /// <summary>생성기는 불변 형태만 받습니다. 원본 소유 텍스처를 복제하거나 해제하지 않습니다.</summary>
        public void BindGeneration(CommandBuffer commands, ComputeShader shader, int kernel, Texture noise)
        {
            commands.SetComputeTextureParam(shader, kernel, "_SculptNoise", noise);
            commands.SetComputeVectorParam(shader, "_OceanBounds", ocean.bounds);
            commands.SetComputeVectorParam(shader, "_SkyPlacement", sky.placement);
            commands.SetComputeVectorParam(shader, "_SkyAltitude", sky.bounds);
            commands.SetComputeVectorParam(shader, "_SkyShape", sky.shape);
            commands.SetComputeVectorParam(shader, "_ConceptShape", style.shape);
            commands.SetComputeVectorParam(shader, "_ConceptDetail", style.detail);
            commands.SetComputeVectorParam(shader, "_StyleShape", style.edge);
            commands.SetComputeFloatParam(shader, "_SkyDensityMultiplier", 1f);
        }

        /// <summary>공유 환경의 팔레트를 선형 RGB로 변환하고 카메라별 자료만 바인딩합니다.</summary>
        public void BindRendering(MaterialPropertyBlock properties, Camera camera,
            CloudHybridQualityProfile quality, Texture noise, int width, int height)
        {
            CloudLightingState lighting = environment.lighting;
            if (!lighting.active)
            {
                lighting = CloudLightingState.ConceptDay();
            }

            float tangent = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            properties.SetTexture("_SculptNoise", noise);
            properties.SetVector("_OceanBounds", ocean.bounds);
            properties.SetVector("_SkyPlacement", sky.placement);
            properties.SetVector("_SkyAltitude", sky.bounds);
            properties.SetVector("_SkyShape", sky.shape);
            properties.SetVector("_ConceptShape", style.shape);
            properties.SetVector("_ConceptDetail", style.detail);
            properties.SetVector("_ConceptLighting", style.lighting);
            properties.SetVector("_StyleShape", style.edge);
            properties.SetVector("_HybridCamera", cameraPosition);
            properties.SetVector("_HybridOrigin", origin);
            properties.SetVector("_HybridForward", camera.transform.forward);
            properties.SetVector("_HybridRight", camera.transform.right * tangent * camera.aspect);
            properties.SetVector("_HybridUp", camera.transform.up * tangent);
            properties.SetVector("_HybridOutputSize", new Vector4(width, height, 1f / width, 1f / height));
            properties.SetVector("_HybridQuality", new Vector4(quality.volumeNear, quality.volumeFar,
                quality.primarySamples, 0f));
            properties.SetVector("_HybridEnvironment", new Vector4(Mathf.Max(0f, environment.densityMultiplier),
                Mathf.Max(0f, environment.skyDensityMultiplier), environment.coverageOffset, Mathf.Max(0.25f, style.edge.w)));
            properties.SetVector("_HybridWind", wind);
            properties.SetFloat("_SkyDensityMultiplier", 1f);
            properties.SetVector("_HybridLightDirection", lighting.GetKeyDirection());
            properties.SetColor("_HybridShadow", lighting.cloudShadowColor.linear);
            properties.SetColor("_HybridMid", lighting.cloudMidColor.linear);
            properties.SetColor("_HybridHighlight", lighting.cloudLightColor.linear);
            properties.SetVector("_HybridLightSettings", new Vector4(lighting.GetKeyStrength(),
                quality.lightSamples, lighting.silverLining, lighting.ambientIntensity));
            properties.SetVector("_HybridAmbientSky", new Vector4(0f, 0f, 0f, lighting.ambientIntensity));
            properties.SetVector("_HybridAmbientHorizon", new Vector4(0f, 0f, 0f, lighting.silverLining));
            properties.SetVector("_HybridSunColor", new Vector4(0f, 0f, 0f, lighting.GetKeyStrength()));
            properties.SetVector("_HybridAir", new Vector4(lighting.aerialStart, lighting.aerialEnd,
                lighting.aerialStrength * style.lighting.w, 0f));
            properties.SetColor("_HybridAirColor", lighting.skyHorizonColor.linear);
            properties.SetFloat("_HybridBrightness", environment.brightness);
            properties.SetFloat("_HybridSkyBlend", lighting.skyBlend);
            properties.SetColor("_HybridSkyTop", lighting.skyTopColor.linear);
            properties.SetColor("_HybridSkyHorizon", lighting.skyHorizonColor.linear);
            properties.SetColor("_HybridSkyLower", lighting.skyLowerColor.linear);
            properties.SetVector("_ArtSkyTop", lighting.skyTopColor.linear);
            properties.SetVector("_ArtSkyHorizon", lighting.skyHorizonColor.linear);
            properties.SetVector("_ArtSkyLower", lighting.skyLowerColor.linear);
            properties.SetVector("_ArtKeyDirection", lighting.GetKeyDirection());
            properties.SetVector("_ArtAtmosphereDirection", lighting.GetAtmosphereDirection());
            properties.SetVector("_ArtKeyColor", lighting.GetKeyColor().linear);
            properties.SetVector("_ArtCelestial", new Vector4(lighting.conceptSky,
                lighting.celestialDiskDegrees * Mathf.Deg2Rad * 0.5f, lighting.celestialDiskIntensity, lighting.starsIntensity));
            Color glow = lighting.cloudLightColor.linear;
            properties.SetVector("_ArtGlow", new Vector4(glow.r, glow.g, glow.b, lighting.skyGlowStrength));
            properties.SetVector("_ArtAir", new Vector4(lighting.aerialStart, lighting.aerialEnd,
                lighting.aerialStrength * style.lighting.w, lighting.skyGlowPower));
        }
    }
}
