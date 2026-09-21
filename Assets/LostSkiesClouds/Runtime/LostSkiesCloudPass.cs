using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Serialization;

namespace ClouDream.LostSkies
{
    [Serializable]
    public sealed class LostSkiesCloudPass : CustomPass
    {
        [Header("렌더링 리소스")]
        [FormerlySerializedAs("originalRenderer")]
        public ComputeShader raymarchShader;
        public ComputeShader originalGenerator;
        public TextAsset originalPreset;
        public Shader compositeShader;
        public Light sun;

        [Header("독립적인 구름 형태")]
        public CloudOceanProfile oceanProfile;
        public CloudSkyProfile skyProfile;
        public CloudStyleProfile styleProfile;

        [Header("날씨 / 바이옴 / 월드 원점 확장")]
        public CloudEnvironmentSource environmentSource;
        public CloudWorldOrigin worldOrigin;

        [Header("기본 형태와 품질")]
        [Tooltip("추출 수치를 사용하고 추가 운해와 상층 구름을 끕니다.")]
        [FormerlySerializedAs("originalPresetOnly")]
        public bool useExtractedValues;
        public bool renderInEditor = true;
        [Range(0.25f, 1f)]
        public float resolutionScale = 0.75f;
        [Range(0.1f, 0.25f)]
        public float towerCoverage = 0.165f;

        [Min(100f)]
        public float cloudDensity = 160000f;
        [Min(0f)]
        public float windSpeed = 5f;

        [Header("환경 공급자가 없을 때 사용할 색상")]
        [Range(0.1f, 5f)]
        public float brightness = 1.4f;
        public Color sunlight = new Color(1f, 0.93f, 0.8f);
        public Color shadowTint = new Color(0.15f, 0.37f, 0.48f);
        public Color highlightTint = new Color(1.12f, 1.08f, 0.97f);

        // 패스가 소유하고 종료 시 해제하는 렌더 상태입니다.
        private LostSkiesCloudRenderer clouds;
        private UniversalCloudLayerRenderSettings source;
        private Material composite;

        [NonSerialized]
        public string lastCamera;
        [NonSerialized]
        public int executions;

        /// <summary>패스의 수명 동안 사용할 노이즈 생성기와 합성 머티리얼을 준비합니다.</summary>
        protected override void Setup(ScriptableRenderContext context, CommandBuffer commands)
        {
            if (raymarchShader == null || !raymarchShader.HasKernel("Raymarch") || originalGenerator == null || originalPreset == null || compositeShader == null)
            {
                Debug.LogError("Cloud pass is missing required rendering resources.");
                return;
            }

            clouds = new LostSkiesCloudRenderer(raymarchShader, originalGenerator, originalPreset);
            source = clouds.settings;
            composite = CoreUtils.CreateEngineMaterial(compositeShader);
        }

        /// <summary>카메라와 환경 설정을 모아 공통 볼류메트릭 렌더 경로를 실행합니다.</summary>
        protected override void Execute(CustomPassContext context)
        {
            Camera camera = context.hdCamera.camera;
            if (clouds == null || (!Application.isPlaying && !renderInEditor) || camera.cameraType == CameraType.Reflection || camera.orthographic)
            {
                return;
            }

            lastCamera = camera.name;
            executions++;
            Vector3 offset = Vector3.zero;
            if (worldOrigin != null)
            {
                offset = worldOrigin.SamplingOffset;
            }

            CloudEnvironment environment = ResolveEnvironment(camera.transform.position + offset);
            UniversalCloudLayerRenderSettings current = source;
            clouds.skyProfile = null;
            clouds.styleProfile = null;
            if (!useExtractedValues)
            {
                current.coverageIntensity = towerCoverage + environment.coverageOffset;
                current.density = cloudDensity * Mathf.Max(0f, environment.densityMultiplier);
                clouds.skyProfile = skyProfile;
                clouds.styleProfile = styleProfile;
            }

            float wind = 0f;
            if (Application.isPlaying)
            {
                wind = Time.time * windSpeed;
            }

            current.baseOffset += new Vector3(wind * current.baseTile / (current.geometryXExtent.y - current.geometryXExtent.x), 0f, 0f);
            clouds.includeOcean = !useExtractedValues;
            clouds.oceanProfile = oceanProfile;
            clouds.worldOriginOffset = offset;
            clouds.skyDensityMultiplier = environment.skyDensityMultiplier;
            clouds.skyLighting = environment.lighting;
            clouds.settings = current;
            int width = Mathf.Max(64, Mathf.RoundToInt(context.hdCamera.actualWidth * resolutionScale));
            int height = Mathf.Max(64, Mathf.RoundToInt(context.hdCamera.actualHeight * resolutionScale));
            clouds.Resize(width, height, camera.GetEntityId().GetHashCode());
            Draw(context, environment);
        }

        /// <summary>다형적 환경 공급자 또는 기존 Inspector 색상을 사용합니다.</summary>
        private CloudEnvironment ResolveEnvironment(Vector3 position)
        {
            if (environmentSource != null && environmentSource.isActiveAndEnabled)
            {
                return environmentSource.Evaluate(position);
            }

            CloudEnvironment environment = CloudEnvironment.ClearDay();
            environment.brightness = brightness;
            environment.sunlight = sunlight;
            environment.shadowTint = shadowTint;
            environment.highlightTint = highlightTint;
            return environment;
        }

        /// <summary>HDRP 깊이를 복사하고 구름을 적분한 뒤 카메라 색 버퍼에 합성합니다.</summary>
        private void Draw(CustomPassContext context, CloudEnvironment environment)
        {
            context.propertyBlock.SetVector("_CloudOutputSize", new Vector4(clouds.Width, clouds.Height, 0f, 0f));
            context.cmd.SetRenderTarget(clouds.DepthTarget, 0, CubemapFace.Unknown, 0);
            context.cmd.SetViewport(new Rect(0f, 0f, clouds.Width, clouds.Height));
            CoreUtils.DrawFullScreen(context.cmd, composite, context.propertyBlock, shaderPassId: 1);
            Vector3 direction = new Vector3(0.4f, 0.8f, 0.2f).normalized;
            if (sun != null)
            {
                direction = -sun.transform.forward;
            }

            clouds.Render(context.cmd, context.hdCamera.camera, direction, environment.sunlight, 3f);
            CoreUtils.SetRenderTarget(context.cmd, context.cameraColorBuffer);
            context.cmd.SetViewport(new Rect(0f, 0f, context.hdCamera.actualWidth, context.hdCamera.actualHeight));
            context.propertyBlock.SetTexture("_CloudLighting", clouds.lighting);
            context.propertyBlock.SetTexture("_CloudTransmittance", clouds.transmittance);
            context.propertyBlock.SetFloat("_CloudBrightness", environment.brightness);
            context.propertyBlock.SetColor("_CloudShadowTint", environment.shadowTint);
            context.propertyBlock.SetColor("_CloudHighlightTint", environment.highlightTint);
            float sharedLighting = 0f;
            if (environment.lighting.active)
            {
                sharedLighting = 1f;
            }

            context.propertyBlock.SetFloat("_UseSkyLighting", sharedLighting);
            BindSkyPalette(context, environment.lighting);
            CoreUtils.DrawFullScreen(context.cmd, composite, context.propertyBlock, shaderPassId: 0);
        }

        /// <summary>공유 하늘 팔레트와 시선 방향을 전달해 원경 배경에만 색 보정을 적용합니다.</summary>
        private void BindSkyPalette(CustomPassContext context, CloudLightingState lighting)
        {
            Camera camera = context.hdCamera.camera;
            float tangent = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            context.propertyBlock.SetVector("_CloudViewForward", camera.transform.forward);
            context.propertyBlock.SetVector("_CloudViewRight", camera.transform.right * tangent * camera.aspect);
            context.propertyBlock.SetVector("_CloudViewUp", camera.transform.up * tangent);

            // SetVector로 전달하므로 작가가 고른 sRGB 팔레트를 여기서 선형 RGB로 변환합니다.
            context.propertyBlock.SetVector("_ArtSkyTop", lighting.skyTopColor.linear);
            context.propertyBlock.SetVector("_ArtSkyHorizon", lighting.skyHorizonColor.linear);
            context.propertyBlock.SetVector("_ArtSkyLower", lighting.skyLowerColor.linear);
            context.propertyBlock.SetFloat("_ArtSkyBlend", Mathf.Clamp01(lighting.skyBlend));
        }

        /// <summary>패스 해제 또는 도메인 재로드 때 모든 GPU 리소스를 돌려줍니다.</summary>
        protected override void Cleanup()
        {
            if (clouds != null)
            {
                clouds.Dispose();
                clouds = null;
            }

            CoreUtils.Destroy(composite);
        }
    }
}
