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
        public ComputeShader temporalShader;
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

        [Tooltip("전체 해상도 장면 깊이로 전경 경계를 구분하여 구름을 업샘플링합니다. 손실된 구름 세부 형태를 재생성하지는 않습니다.")]
        public bool useDepthAwareUpsampling = true;

        [Header("개별 최적화 / Play 모드 A/B")]
        public bool useShapeCellCache = true;
        public bool useSupportRejection = true;
        public bool useCompactTargets = true;

        [Tooltip("빈 구간의 밀도 계산을 생략합니다. 일부 시점에서는 더 느려 기본 OFF이며 Play에서 비교합니다.")]
        public bool useRayIntervals;

        [Tooltip("팔레트 조명의 남은 rim 기여가 작을 때 그림자 조회를 종료합니다.")]
        public bool useShadowTermination;

        [Tooltip("실험 옵션: 완전한 Concept V2 팔레트에서 물리 조명 연산을 생략합니다. 시점별 이득이 달라 기본 OFF입니다.")]
        public bool usePaletteLightingFastPath;

        [Tooltip("이전 프레임을 재투영하고 8x8 타일을 순환 갱신합니다. 잔상과 추가 메모리를 비교하는 실험 옵션입니다.")]
        public bool useTemporalReprojection;

        [Header("거리별 밀도와 조명 품질")]
        public CloudDistanceQualitySettings distanceQuality = CloudDistanceQualitySettings.Default;

        public bool DistanceQualitySupported
        {
            get
            {
                return !useExtractedValues && styleProfile != null && styleProfile.SupportsDistanceQuality;
            }
        }

        [Tooltip("2는 매 프레임 절반, 4는 1/4 타일을 새로 계산합니다. 무효 이력은 추가로 즉시 계산합니다.")]
        public int temporalUpdatePhases = 2;

        public long TemporalBytes
        {
            get
            {
                if (clouds == null)
                {
                    return 0;
                }
                return clouds.TemporalBytes;
            }
        }

        public int TemporalResets
        {
            get
            {
                if (clouds == null)
                {
                    return 0;
                }
                return clouds.TemporalResets;
            }
        }

        public int TemporalAllocations
        {
            get
            {
                if (clouds == null)
                {
                    return 0;
                }
                return clouds.TemporalAllocations;
            }
        }

        public bool TemporalActive
        {
            get
            {
                return clouds != null && clouds.TemporalActive;
            }
        }

        public string TemporalStatus
        {
            get
            {
                if (clouds == null)
                {
                    return "Off";
                }
                return clouds.TemporalStatus;
            }
        }

        /// <summary>설정 요청과 실제 지원 조건을 구분하여 Play 설정창에 표시합니다.</summary>
        public bool PaletteLightingFastPathActive
        {
            get
            {
                if (clouds == null)
                {
                    return false;
                }

                return clouds.PaletteLightingFastPathActive;
            }
        }

        [Tooltip("실험용 근사 조명입니다. 시점에 따라 비용과 명암이 달라지며 원경에서 느려질 수 있어 기본 OFF입니다.")]
        public bool useSpatialLightCache;

        [Range(16f, 64f)]
        public float spatialLightCellSize = 16f;

        public int SpatialLightAllocations
        {
            get
            {
                if (clouds == null)
                {
                    return 0;
                }

                return clouds.SpatialLightAllocations;
            }
        }

        public int SpatialLightBuilds
        {
            get
            {
                if (clouds == null)
                {
                    return 0;
                }

                return clouds.SpatialLightBuilds;
            }
        }

        public long SpatialLightBytes
        {
            get
            {
                if (clouds == null)
                {
                    return 0;
                }

                return clouds.SpatialLightBytes;
            }
        }

        // 상시 marker만 기록합니다. GPU recorder는 진단 요청 시에만 켭니다.
        private readonly ProfilingSampler depthSampler = new ProfilingSampler("Cloud.DepthCopy");
        private readonly ProfilingSampler raySampler = new ProfilingSampler("Cloud.Raymarch");
        private readonly ProfilingSampler compositeSampler = new ProfilingSampler("Cloud.Composite");
        private readonly ProfilingSampler totalSampler = new ProfilingSampler("Cloud.Total");

        public int OutputWidth
        {
            get
            {
                if (clouds == null)
                {
                    return 0;
                }

                return clouds.Width;
            }
        }

        public int OutputHeight
        {
            get
            {
                if (clouds == null)
                {
                    return 0;
                }

                return clouds.Height;
            }
        }

        public int ShapeCacheBuilds
        {
            get
            {
                if (clouds == null)
                {
                    return 0;
                }

                return clouds.ShapeCacheBuildCount;
            }
        }

        public bool CompactTargetsActive
        {
            get
            {
                return clouds != null && clouds.CompactTargetsActive;
            }
        }

        public int TargetAllocations
        {
            get
            {
                if (clouds == null)
                {
                    return 0;
                }

                return clouds.GetTargetAllocationCount();
            }
        }

        /// <summary>현재 카메라의 세 RT 저장량을 반환합니다. 노이즈·HDRP·다른 카메라 메모리는 제외합니다.</summary>
        public long GetActiveTargetBytes()
        {
            int bytesPerPixel = 24;
            if (CompactTargetsActive)
            {
                bytesPerPixel = 16;
            }

            return (long)OutputWidth * OutputHeight * bytesPerPixel;
        }
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
        private ComputeShader activeRaymarchShader;
        private UniversalCloudLayerRenderSettings source;
        private Material composite;

        [NonSerialized]
        public string lastCamera;
        [NonSerialized]
        public int executions;

        /// <summary>패스의 수명 동안 사용할 노이즈 생성기와 합성 머티리얼을 준비합니다.</summary>
        protected override void Setup(ScriptableRenderContext context, CommandBuffer commands)
        {
            string unsupportedReason = LostSkiesCloudRenderer.GetGraphicsApiUnsupportedReason();
            if (!string.IsNullOrEmpty(unsupportedReason))
            {
                Debug.LogError(unsupportedReason);
                return;
            }

            if (raymarchShader == null || !raymarchShader.HasKernel("Raymarch") || originalGenerator == null || originalPreset == null || compositeShader == null)
            {
                Debug.LogError("Cloud pass is missing required rendering resources.");
                return;
            }

            CreateRenderer();
            composite = CoreUtils.CreateEngineMaterial(compositeShader);
        }

        /// <summary>벤치마크의 기준 셰이더 교체 시 이전 GPU 자원을 해제하고 새 렌더러를 워밍업에 제공합니다.</summary>
        private void CreateRenderer()
        {
            if (clouds != null)
            {
                clouds.Dispose();
            }

            clouds = new LostSkiesCloudRenderer(raymarchShader, originalGenerator, originalPreset, temporalShader);
            activeRaymarchShader = raymarchShader;
            source = clouds.settings;
            clouds.raymarchSampler = raySampler;
        }

        /// <summary>카메라와 환경 설정을 모아 공통 볼류메트릭 렌더 경로를 실행합니다.</summary>
        protected override void Execute(CustomPassContext context)
        {
            if (clouds != null && raymarchShader != activeRaymarchShader && raymarchShader != null)
            {
                CreateRenderer();
            }

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
            clouds.useShapeCellCache = useShapeCellCache;
            clouds.useSupportRejection = useSupportRejection;
            clouds.useRayIntervals = useRayIntervals;
            clouds.useShadowTermination = useShadowTermination;
            clouds.usePaletteLightingFastPath = usePaletteLightingFastPath;
            // 카메라 종류별 허용은 렌더러가 판단하여 Scene 뷰가 Game 이력을 지우지 않게 합니다.
            clouds.useTemporalReprojection = useTemporalReprojection && Application.isPlaying;
            clouds.temporalUpdatePhases = temporalUpdatePhases;
            clouds.distanceQuality = distanceQuality;
            clouds.useSpatialLightCache = useSpatialLightCache;
            clouds.spatialLightCellSize = spatialLightCellSize;
            clouds.useCompactTargets = useCompactTargets;
            int width = Mathf.Max(64, Mathf.RoundToInt(context.hdCamera.actualWidth * resolutionScale));
            int height = Mathf.Max(64, Mathf.RoundToInt(context.hdCamera.actualHeight * resolutionScale));
            clouds.Resize(width, height, camera.GetEntityId().GetHashCode());
            using (new ProfilingScope(context.cmd, totalSampler))
            {
                Draw(context, environment);
            }
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
            using (new ProfilingScope(context.cmd, depthSampler))
            {
                CoreUtils.DrawFullScreen(context.cmd, composite, context.propertyBlock, shaderPassId: 1);
            }
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
            context.propertyBlock.SetTexture("_CloudLowSceneDepth", clouds.DepthTarget);
            float reconstruction = 0f;
            if (useDepthAwareUpsampling)
            {
                reconstruction = 1f;
            }

            float compact = 0f;
            if (clouds.CompactTargetsActive)
            {
                compact = 1f;
            }

            Camera camera = context.hdCamera.camera;
            context.propertyBlock.SetFloat("_CloudDepthUpsampling", reconstruction);
            context.propertyBlock.SetFloat("_CloudCompact", compact);
            context.propertyBlock.SetVector("_CloudDepthParams", new Vector4(
                (camera.farClipPlane / camera.nearClipPlane - 1f) / camera.farClipPlane,
                1f / camera.farClipPlane, 0f, 0f));
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
            using (new ProfilingScope(context.cmd, compositeSampler))
            {
                CoreUtils.DrawFullScreen(context.cmd, composite, context.propertyBlock, shaderPassId: 0);
            }
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
            context.propertyBlock.SetVector("_ArtKeyDirection", lighting.GetKeyDirection());
            context.propertyBlock.SetVector("_ArtAtmosphereDirection", lighting.GetAtmosphereDirection());
            context.propertyBlock.SetVector("_ArtKeyColor", lighting.GetKeyColor().linear);
            context.propertyBlock.SetVector("_ArtCelestial", new Vector4(lighting.conceptSky,
                lighting.celestialDiskDegrees * Mathf.Deg2Rad * 0.5f, lighting.celestialDiskIntensity, lighting.starsIntensity));
            Color glow = lighting.cloudLightColor.linear;
            context.propertyBlock.SetVector("_ArtGlow", new Vector4(glow.r, glow.g, glow.b, lighting.skyGlowStrength));
            float aerialScale = 0f;
            if (!useExtractedValues && styleProfile != null && styleProfile.method == CloudStyleProfile.ShapeMethod.ConceptV2)
            {
                aerialScale = styleProfile.aerialScale;
            }

            context.propertyBlock.SetVector("_ArtAir", new Vector4(lighting.aerialStart, lighting.aerialEnd,
                lighting.aerialStrength * aerialScale, lighting.skyGlowPower));
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
