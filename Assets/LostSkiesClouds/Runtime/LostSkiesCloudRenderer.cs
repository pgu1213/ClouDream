using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    /// <summary>원본 노이즈를 한 번 생성하고 여러 구름 형태를 하나의 밀도장으로 렌더링합니다.</summary>
    public sealed class LostSkiesCloudRenderer : IDisposable
    {
        private const int MaximumCachedCameras = 4;

        private readonly ComputeShader renderer;
        private readonly ComputeShader generator;
        private readonly int kernel;

        private readonly ComputeShader temporalShader;
        private readonly int temporalKernel = -1;

        public bool useTemporalReprojection;

        public CloudDistanceQualitySettings distanceQuality = CloudDistanceQualitySettings.Default;
        public int temporalUpdatePhases = 2;

        /// <summary>진단값은 현재 선택된 카메라의 이력만 나타냅니다.</summary>
        public bool TemporalActive { get; private set; }

        public long TemporalBytes
        {
            get
            {
                long bytes = 0;
                foreach (CloudRenderTargets targets in cameraTargets.Values)
                {
                    bytes += targets.temporal.Bytes;
                }

                return bytes;
            }
        }

        public int TemporalAllocations
        {
            get
            {
                if (activeTargets == null)
                {
                    return 0;
                }
                return activeTargets.temporal.Allocations;
            }
        }

        public int TemporalResets
        {
            get
            {
                if (activeTargets == null)
                {
                    return 0;
                }
                return activeTargets.temporal.Resets;
            }
        }

        public string TemporalStatus
        {
            get
            {
                if (activeTargets == null)
                {
                    return "Off";
                }
                return activeTargets.temporal.Status;
            }
        }

        /// <summary>GPU 검사용 깊이·나이 버퍼이며 OFF에서는 null입니다.</summary>
        public RenderTexture TemporalState
        {
            get
            {
                if (activeTargets == null)
                {
                    return null;
                }
                return activeTargets.temporal.State;
            }
        }

        private readonly bool paletteFastPathSupported;
        private readonly bool distanceQualityKeywordsSupported;

        private readonly CloudShapeCellCache shapeCells = new CloudShapeCellCache();

        private readonly CloudOceanHeightCache oceanHeights = new CloudOceanHeightCache();

        private readonly CloudSpatialLightCache spatialLight = new CloudSpatialLightCache();

        public bool useSpatialLightCache;

        public float spatialLightCellSize = 16f;

        public long SpatialLightBytes
        {
            get
            {
                return spatialLight.Bytes;
            }
        }

        public int SpatialLightBuilds
        {
            get
            {
                return spatialLight.BuildCount;
            }
        }

        public int SpatialLightAllocations
        {
            get
            {
                return spatialLight.AllocationCount;
            }
        }

        public bool SpatialLightActive
        {
            get
            {
                return spatialLight.Active;
            }
        }

        // 독립적인 A/B 스위치입니다. 스타일/환경 에셋을 변경하지 않습니다.
        public bool useShapeCellCache = true;
        public bool useSupportRejection = true;
        public bool useCompactTargets;

        public bool useRayIntervals;

        public bool useShadowTermination;

        // 완전한 팔레트의 불필요한 물리 조명 연산만 생략합니다. 혼합 상태는 매 렌더 다시 판정합니다.
        public bool usePaletteLightingFastPath;

        /// <summary>현재 렌더가 요청 조건을 만족해 실제로 팔레트 전용 변형을 사용하는지 표시합니다.</summary>
        public bool PaletteLightingFastPathActive { get; private set; }

        // 높이 캐시는 오차·성능 검사용 실험 기능입니다. 장면 패스와 사용자 설정에는 연결하지 않습니다.
        // 32m 격자에서 약 3.3m 높이 오차가 확인되어 기본 경로는 절차식을 유지합니다.
        public bool useOceanHeightCache;

        public float oceanHeightTexelSize = 32f;

        public bool oceanHeightHalfPrecision;

        public int OceanHeightBuildCount
        {
            get
            {
                return oceanHeights.BuildCount;
            }
        }

        public long OceanHeightBytes
        {
            get
            {
                return oceanHeights.Bytes;
            }
        }

        public bool OceanHeightActive
        {
            get
            {
                return oceanHeights.Active;
            }
        }

        /// <summary>다음 렌더에서 RT 재할당 없이 높이 필드를 다시 계산하도록 요청합니다.</summary>
        public void InvalidateOceanHeightCache()
        {
            oceanHeights.Invalidate();
        }

        public int ShapeCacheBuildCount
        {
            get
            {
                return shapeCells.BuildCount;
            }
        }

        public bool CompactTargetsActive
        {
            get
            {
                return activeTargets != null && activeTargets.compact;
            }
        }

        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private readonly Dictionary<string, Texture> noise = new Dictionary<string, Texture>();
        private readonly Dictionary<int, CloudRenderTargets> cameraTargets = new Dictionary<int, CloudRenderTargets>();
        private CloudRenderTargets activeTargets;
        private int renderSequence;

        public UniversalCloudLayerRenderSettings settings;
        public bool includeOcean;

        // 서로 다른 구름 형태도 동일한 프로필 계약과 렌더 경로를 사용합니다.
        public CloudFormationProfile oceanProfile;
        public CloudFormationProfile skyProfile;
        public CloudFormationProfile styleProfile;

        public Vector3 worldOriginOffset;
        public float skyDensityMultiplier = 1f;

        public CloudLightingState skyLighting;

        // 선택적인 GPU 계측입니다. 기록하지 않을 때는 기존 dispatch 비용을 유지합니다.
        public ProfilingSampler raymarchSampler;

        public RenderTexture lighting
        {
            get
            {
                return activeTargets.lighting;
            }
        }

        public RenderTexture transmittance
        {
            get
            {
                return activeTargets.transmittance;
            }
        }

        public RenderTexture DepthTarget
        {
            get
            {
                return activeTargets.depth;
            }
        }

        public int Width
        {
            get
            {
                if (activeTargets == null)
                {
                    return 0;
                }

                return activeTargets.width;
            }
        }

        public int Height
        {
            get
            {
                if (activeTargets == null)
                {
                    return 0;
                }

                return activeTargets.height;
            }
        }

        public int NoiseGenerationCount { get; private set; }

        /// <summary>셰이더의 DX12 전용 컴파일 계약을 확인하고 지원하지 않는 API의 실패 원인을 반환합니다.</summary>
        public static string GetGraphicsApiUnsupportedReason()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                return "현재 구름 렌더러는 Windows Direct3D12에서 지원·검증됩니다. 현재 그래픽 API: "
                    + SystemInfo.graphicsDeviceType
                    + ". Player Settings의 Graphics APIs를 Direct3D12로 설정하고 Unity를 재시작해야 합니다.";
            }

            return string.Empty;
        }

        /// <summary>여러 카메라와 날씨 변경에도 유지할 공용 노이즈 및 셰이더를 준비합니다.</summary>
        public LostSkiesCloudRenderer(ComputeShader raymarch, ComputeShader noiseGenerator, TextAsset preset, ComputeShader temporal = null)
        {
            string unsupportedReason = GetGraphicsApiUnsupportedReason();
            if (!string.IsNullOrEmpty(unsupportedReason))
            {
                throw new NotSupportedException(unsupportedReason);
            }

            try
            {
                renderer = UnityEngine.Object.Instantiate(raymarch);
                generator = UnityEngine.Object.Instantiate(noiseGenerator);
                owned.Add(renderer);
                owned.Add(generator);
                kernel = renderer.FindKernel("Raymarch");
                if (temporal != null && renderer.HasKernel("RaymarchTemporal"))
                {
                    temporalShader = UnityEngine.Object.Instantiate(temporal);
                    owned.Add(temporalShader);
                    temporalKernel = renderer.FindKernel("RaymarchTemporal");
                }
                paletteFastPathSupported = renderer.keywordSpace.FindKeyword("CLOUD_PALETTE_LIGHTING_FAST_PATH").isValid;
                distanceQualityKeywordsSupported = renderer.keywordSpace.FindKeyword("CLOUD_DISTANCE_DENSITY").isValid
                    && renderer.keywordSpace.FindKeyword("CLOUD_DISTANCE_LIGHTING").isValid;
                settings = JsonUtility.FromJson<UniversalCloudLayerRenderSettings>(preset.text);
                noise["_BaseNoise"] = GenerateNoise("WORLEY3D", 128, 4f, 6, 0.55f);
                noise["_StructureNoise"] = GenerateNoise("PERLIN3D", 64, 12f, 4, 0.75f);
                noise["_DetailNoise"] = GenerateNoise("WORLEY3D", 64, 12f, 5, 0.75f);
                noise["_WarpNoise"] = GenerateNoise("PERLIN3D", 64, 16f, 4, 0.5f);
                noise["_DetailWarpNoise"] = GenerateNoise("CURL3D", 32, 8f, 3, 0.3f);
                noise["_HeightCurves"] = CreateHeightCurves();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>normal 프리셋의 원본 16개 높이 샘플을 GPU 조회 텍스처로 만듭니다.</summary>
        private Texture2D CreateHeightCurves()
        {
            float[] density = {0.86533356f, 0.63915867f, 0.3359517f, 0.9990258f, 0.980437f, 0.9415723f, 0.8868187f, 0.820563f, 0.74719214f, 0.67109287f, 0.59665215f, 0.5282568f, 0.47027975f, 0.3593751f, 0.17953202f, 0f};
            float[] coverage = {0f, 0.4327357f, 0.9116376f, 0.82701176f, 0.7581364f, 0.7149202f, 0.6895204f, 0.67409396f, 0.66079795f, 0.64178944f, 0.60390514f, 0.55050063f, 0.5014584f, 0.47668558f, 0.49608916f, 0.7604588f};
            Texture2D texture = new Texture2D(16, 1, TextureFormat.RGBAHalf, false, true);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            owned.Add(texture);
            Color[] pixels = new Color[16];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color(density[i], coverage[i], 0f, 1f);
            }

            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        /// <summary>원본 GPU 생성기로 주기적 3D 노이즈를 만들고 수명 내내 재사용합니다.</summary>
        private RenderTexture GenerateNoise(string kernelName, int size, float scale, int octaves, float persistence)
        {
            RenderTexture texture = new RenderTexture(size, size, 0, RenderTextureFormat.ARGBHalf);
            texture.dimension = TextureDimension.Tex3D;
            texture.volumeDepth = size;
            texture.enableRandomWrite = true;
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Trilinear;
            texture.useMipMap = true;
            texture.autoGenerateMips = false;
            texture.name = "Original " + kernelName;
            owned.Add(texture);
            texture.Create();
            int noiseKernel = generator.FindKernel(kernelName);
            generator.SetVector("_res", new Vector4(size, size, size, 0f));
            generator.SetVector("_scale", new Vector4(scale, scale, scale, 0f));
            generator.SetInt("_octaves", octaves);
            generator.SetFloat("_octaveScale", 2f);
            generator.SetFloat("_octaveMultiplier", persistence);
            generator.SetTexture(noiseKernel, "_Noise3D", texture);
            generator.Dispatch(noiseKernel, size / 4, size / 4, size / 4);
            texture.GenerateMips();
            NoiseGenerationCount++;
            return texture;
        }

        /// <summary>카메라별 출력을 선택하고 크기가 바뀔 때만 버퍼를 재할당합니다.</summary>
        public void Resize(int width, int height, int cameraId = 0)
        {
            if (!cameraTargets.TryGetValue(cameraId, out activeTargets))
            {
                if (cameraTargets.Count >= MaximumCachedCameras)
                {
                    EvictOldestCamera();
                }

                activeTargets = new CloudRenderTargets();
                cameraTargets.Add(cameraId, activeTargets);
            }

            activeTargets.lastUsed = ++renderSequence;
            activeTargets.Resize(width, height, useCompactTargets);
        }

        /// <summary>최근 사용하지 않은 카메라의 출력을 해제하여 캐시 메모리 상한을 지킵니다.</summary>
        private void EvictOldestCamera()
        {
            int oldestId = 0;
            int oldestSequence = int.MaxValue;
            foreach (KeyValuePair<int, CloudRenderTargets> entry in cameraTargets)
            {
                if (entry.Value.lastUsed < oldestSequence)
                {
                    oldestId = entry.Key;
                    oldestSequence = entry.Value.lastUsed;
                }
            }

            cameraTargets[oldestId].Dispose();
            cameraTargets.Remove(oldestId);
        }

        /// <summary>성능 검증에 사용할 현재 캐시의 버퍼 생성 횟수를 반환합니다.</summary>
        public int GetTargetAllocationCount()
        {
            int count = 0;
            foreach (CloudRenderTargets targets in cameraTargets.Values)
            {
                count += targets.allocationCount;
            }

            return count;
        }

        /// <summary>운해와 상층 구름을 같은 광선으로 적분하여 서로의 가림과 자기 그림자를 처리합니다.</summary>
        public void Render(CommandBuffer commands, Camera camera, Vector3 sunDirection, Color sunColor, float intensity = 1f)
        {
            bool distanceSupported = styleProfile != null && styleProfile.SupportsDistanceQuality;
            if (distanceQualityKeywordsSupported)
            {
                SetFeatureKeyword("CLOUD_DISTANCE_DENSITY", distanceSupported && distanceQuality.density);
                SetFeatureKeyword("CLOUD_DISTANCE_LIGHTING", distanceSupported && distanceQuality.lighting);
            }
            commands.SetComputeVectorParam(renderer, "_CloudDistanceDensity", distanceQuality.DensityParameters(distanceSupported));
            commands.SetComputeVectorParam(renderer, "_CloudDistanceLighting", distanceQuality.LightingParameters(distanceSupported));
            TemporalActive = useTemporalReprojection && temporalShader != null && temporalKernel >= 0
                && styleProfile != null && styleProfile.SupportsTemporalReprojection && !camera.stereoEnabled
                && camera.cameraType == CameraType.Game
                && SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.RGFloat);
            int traceKernel = kernel;
            if (TemporalActive)
            {
                traceKernel = temporalKernel;
            }
            else if (!useTemporalReprojection)
            {
                // OFF일 때 비활성 카메라에 남은 이력도 해제합니다.
                foreach (CloudRenderTargets targets in cameraTargets.Values)
                {
                    targets.temporal.Dispose();
                }
            }
            else
            {
                activeTargets.temporal.Dispose();
            }

            // 생성과 조회 명령은 같은 셰이더 변형으로 기록합니다.
            bool spatialEnabled = useSpatialLightCache && styleProfile != null && styleProfile.SupportsSpatialLightCache
                && !useOceanHeightCache && SystemInfo.supports3DRenderTextures
                && SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.RGFloat);
            if (spatialEnabled)
            {
                renderer.EnableKeyword("CLOUD_SPATIAL_LIGHT_CACHE");
            }
            else
            {
                renderer.DisableKeyword("CLOUD_SPATIAL_LIGHT_CACHE");
            }

            if (activeTargets.compact)
            {
                renderer.EnableKeyword("CLOUD_COMPACT_TARGETS");
            }
            else
            {
                renderer.DisableKeyword("CLOUD_COMPACT_TARGETS");
            }

            int supportRejection = 0;
            if (useSupportRejection)
            {
                supportRejection = 1;
            }

            commands.SetComputeIntParam(renderer, "_CloudSupportRejection", supportRejection);
            int shadowTermination = 0;
            if (useRayIntervals)
            {
                renderer.EnableKeyword("CLOUD_RAY_INTERVALS");
            }
            else
            {
                renderer.DisableKeyword("CLOUD_RAY_INTERVALS");
            }

            if (useShadowTermination)
            {
                shadowTermination = 1;
            }

            commands.SetComputeIntParam(renderer, "_CloudShadowTermination", shadowTermination);

            // 0.9999도 혼합 경로를 사용합니다. 미래의 미지원 형태는 기본 경로로 복귀합니다.
            bool paletteFastPath = paletteFastPathSupported && usePaletteLightingFastPath && skyLighting.active && skyLighting.cloudPaletteBlend >= 1f
                && styleProfile != null && styleProfile.SupportsPaletteLightingFastPath;
            PaletteLightingFastPathActive = paletteFastPath;
            if (paletteFastPath)
            {
                renderer.EnableKeyword("CLOUD_PALETTE_LIGHTING_FAST_PATH");
            }
            else if (paletteFastPathSupported)
            {
                renderer.DisableKeyword("CLOUD_PALETTE_LIGHTING_FAST_PATH");
            }

            // 중간 크기 구형 굴곡은 단일 옥타브 노이즈를 한 번 생성해 모든 구름에서 재사용합니다.
            if (styleProfile != null && !noise.ContainsKey("_SculptNoise"))
            {
                noise["_SculptNoise"] = GenerateNoise("WORLEY3D", 128, 4f, 1, 0.5f);
            }

            int sharedLighting = 0;
            if (skyLighting.active)
            {
                sharedLighting = 1;
                sunDirection = skyLighting.GetKeyDirection();
                sunColor = skyLighting.GetKeyColor().linear;
                intensity = skyLighting.GetKeyStrength();
            }

            Color ambientSky = skyLighting.ambientSkyColor.linear;
            Color ambientHorizon = skyLighting.ambientHorizonColor.linear;
            commands.SetComputeIntParam(renderer, "_UseSkyLighting", sharedLighting);
            commands.SetComputeVectorParam(renderer, "_AmbientSky", new Vector4(ambientSky.r, ambientSky.g, ambientSky.b, Mathf.Max(0f, skyLighting.ambientIntensity)));
            commands.SetComputeVectorParam(renderer, "_AmbientHorizon", new Vector4(ambientHorizon.r, ambientHorizon.g, ambientHorizon.b, Mathf.Max(0f, skyLighting.silverLining)));

            commands.SetComputeVectorParam(renderer, "_CloudPaletteShadow", skyLighting.cloudShadowColor.linear);
            commands.SetComputeVectorParam(renderer, "_CloudPaletteMid", skyLighting.cloudMidColor.linear);
            commands.SetComputeVectorParam(renderer, "_CloudPaletteLight", skyLighting.cloudLightColor.linear);
            commands.SetComputeFloatParam(renderer, "_CloudPaletteBlend", Mathf.Clamp01(skyLighting.cloudPaletteBlend));

            float fov = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            commands.SetComputeVectorParam(renderer, "_CameraPosition", camera.transform.position + worldOriginOffset);
            commands.SetComputeVectorParam(renderer, "_CameraForward", camera.transform.forward);
            commands.SetComputeVectorParam(renderer, "_CameraRight", camera.transform.right * fov * camera.aspect);
            commands.SetComputeVectorParam(renderer, "_CameraUp", camera.transform.up * fov);
            commands.SetComputeVectorParam(renderer, "_Size", new Vector4(Width, Height, 1f / Width, 1f / Height));
            commands.SetComputeVectorParam(renderer, "_SunDirection", sunDirection);
            commands.SetComputeVectorParam(renderer, "_SunColor", new Vector4(sunColor.r, sunColor.g, sunColor.b, intensity));
            commands.SetComputeVectorParam(renderer, "_Layer", new Vector4(settings.geometryYExtent.x, settings.geometryYExtent.y, settings.density / 400000f, settings.coverageIntensity));
            commands.SetComputeVectorParam(renderer, "_Shape", new Vector4(settings.structureIntensity, settings.detailIntensity, settings.baseWarpIntensity, settings.detailWarpIntensity));
            commands.SetComputeVectorParam(renderer, "_Lighting", new Vector4(settings.anisotropy, settings.shadowPersistence, settings.silverIntensity, settings.silverSpread));
            commands.SetComputeVectorParam(renderer, "_Wind", settings.baseOffset);
            commands.SetComputeVectorParam(renderer, "_ZParams", new Vector4((camera.farClipPlane / camera.nearClipPlane - 1f) / camera.farClipPlane, 1f / camera.farClipPlane, 0f, 0f));
            int oceanMode = 0;
            if (includeOcean)
            {
                oceanMode = 2;
            }

            commands.SetComputeIntParam(renderer, "_Ocean", oceanMode);
            commands.SetComputeVectorParam(renderer, "_OceanBounds", new Vector4(-1600f, 2700f, 0f, 0f));
            commands.SetComputeVectorParam(renderer, "_SkyShape", Vector4.zero);
            commands.SetComputeFloatParam(renderer, "_SkyDensityMultiplier", Mathf.Max(0f, skyDensityMultiplier));
            commands.SetComputeIntParam(renderer, "_StyleMode", 0);
            commands.SetComputeVectorParam(renderer, "_StyleShape", Vector4.zero);
            commands.SetComputeVectorParam(renderer, "_StyleLight", new Vector4(0f, 1f, 0f, 1f));
            commands.SetComputeVectorParam(renderer, "_StyleDetail", new Vector4(4200f, 0f, 1900f, 0f));
            commands.SetComputeVectorParam(renderer, "_ConceptShape", new Vector4(24000f, 1400f, 170f, 25f));
            commands.SetComputeVectorParam(renderer, "_ConceptLighting", new Vector4(0.85f, 150f, 1f, 1f));
            commands.SetComputeVectorParam(renderer, "_ConceptDetail", new Vector4(4200f, 45f, 0.5f, 0.6f));
            ApplyFormation(commands, oceanProfile);
            ApplyFormation(commands, skyProfile);
            ApplyFormation(commands, styleProfile);
            shapeCells.Prepare(commands, renderer, traceKernel, renderer.FindKernel("ProbeDensity"), skyProfile,
                camera.transform.position + worldOriginOffset, useShapeCellCache && styleProfile != null);
            Texture terrainNoise = noise["_BaseNoise"];
            if (noise.ContainsKey("_SculptNoise"))
            {
                terrainNoise = noise["_SculptNoise"];
            }

            oceanHeights.Prepare(commands, renderer, styleProfile, camera.transform.position + worldOriginOffset,
                terrainNoise, useOceanHeightCache, oceanHeightTexelSize, oceanHeightHalfPrecision);
            foreach (KeyValuePair<string, Texture> entry in noise)
            {
                commands.SetComputeTextureParam(renderer, traceKernel, entry.Key, entry.Value);
            }

            if (!noise.ContainsKey("_SculptNoise"))
            {
                commands.SetComputeTextureParam(renderer, traceKernel, "_SculptNoise", noise["_BaseNoise"]);
            }

            commands.SetComputeTextureParam(renderer, traceKernel, "_SceneDepth", DepthTarget);
            commands.SetComputeTextureParam(renderer, traceKernel, "_LightingOut", lighting);
            commands.SetComputeTextureParam(renderer, traceKernel, "_TransmittanceOut", transmittance);
            if (spatialEnabled)
            {
                int build = renderer.FindKernel("BuildSpatialLighting");
                shapeCells.BindKernel(commands, renderer, build);
                foreach (KeyValuePair<string, Texture> entry in noise)
                {
                    commands.SetComputeTextureParam(renderer, build, entry.Key, entry.Value);
                }
            }

            spatialLight.Prepare(commands, renderer, camera.transform.position + worldOriginOffset,
                spatialEnabled, spatialLightCellSize, traceKernel);
            if (TemporalActive)
            {
                int phases = 2;
                if (temporalUpdatePhases >= 4)
                {
                    phases = 4;
                }
                activeTargets.temporal.Prepare(commands, temporalShader, activeTargets, camera, worldOriginOffset,
                    settings.baseOffset, GetTemporalStateHash(sunDirection, sunColor, intensity), phases);
                commands.SetComputeTextureParam(renderer, traceKernel, "_CloudTemporalState", activeTargets.temporal.State);
            }

            if (raymarchSampler != null)
            {
                using (new ProfilingScope(commands, raymarchSampler))
                {
                    commands.DispatchCompute(renderer, traceKernel, Width / 8, Height / 8, 1);
                }
            }
            else
            {
                commands.DispatchCompute(renderer, traceKernel, Width / 8, Height / 8, 1);
            }

            if (TemporalActive)
            {
                activeTargets.temporal.Store(commands, activeTargets);
            }
        }

        /// <summary>조명·날씨·형태·최적화 입력이 바뀐 프레임은 이전 색과 깊이를 전부 폐기합니다.</summary>
        private int GetTemporalStateHash(Vector3 sunDirection, Color sunColor, float intensity)
        {
            HashCode hash = new HashCode();
            hash.Add(sunDirection);
            hash.Add(sunColor);
            hash.Add(intensity);
            hash.Add(skyLighting.active);
            hash.Add(skyLighting.ambientSkyColor);
            hash.Add(skyLighting.ambientHorizonColor);
            hash.Add(skyLighting.ambientIntensity);
            hash.Add(skyLighting.silverLining);
            hash.Add(skyLighting.cloudShadowColor);
            hash.Add(skyLighting.cloudMidColor);
            hash.Add(skyLighting.cloudLightColor);
            hash.Add(skyLighting.cloudPaletteBlend);
            hash.Add(skyDensityMultiplier);
            hash.Add(includeOcean);
            hash.Add(settings.geometryYExtent);
            hash.Add(settings.density);
            hash.Add(settings.coverageIntensity);
            hash.Add(settings.structureIntensity);
            hash.Add(settings.detailIntensity);
            hash.Add(settings.baseWarpIntensity);
            hash.Add(settings.detailWarpIntensity);
            hash.Add(settings.anisotropy);
            hash.Add(settings.shadowPersistence);
            hash.Add(settings.silverIntensity);
            hash.Add(settings.silverSpread);
            hash.Add(useShapeCellCache);
            hash.Add(useSupportRejection);
            hash.Add(useRayIntervals);
            hash.Add(useShadowTermination);
            hash.Add(usePaletteLightingFastPath);
            hash.Add(distanceQuality.DensityParameters(true));
            hash.Add(distanceQuality.LightingParameters(true));
            hash.Add(useSpatialLightCache);
            hash.Add(spatialLightCellSize);
            hash.Add(useOceanHeightCache);
            hash.Add(oceanHeightTexelSize);
            hash.Add(oceanHeightHalfPrecision);
            AddTemporalProfile(ref hash, oceanProfile);
            AddTemporalProfile(ref hash, skyProfile);
            AddTemporalProfile(ref hash, styleProfile);
            return hash.ToHashCode();
        }

        /// <summary>밀도와 조명 옵션의 미사용 코드를 별도 변형에서 제거해 긴 광선 루프의 부담을 줄입니다.</summary>
        private void SetFeatureKeyword(string keyword, bool enabled)
        {
            if (enabled)
            {
                renderer.EnableKeyword(keyword);
            }
            else
            {
                renderer.DisableKeyword(keyword);
            }
        }

        /// <summary>프로필 자산 교체와 실행 중 값 변경을 모두 이력 무효화에 반영합니다.</summary>
        private static void AddTemporalProfile(ref HashCode hash, CloudFormationProfile profile)
        {
            hash.Add(profile);
            if (profile != null)
            {
                hash.Add(profile.GetTemporalStateHash());
            }
        }

        /// <summary>공통 기반 클래스를 통해 구체적인 형태의 GPU 설정을 적용합니다.</summary>
        private void ApplyFormation(CommandBuffer commands, CloudFormationProfile profile)
        {
            if (profile != null)
            {
                profile.Apply(commands, renderer);
            }
        }

        /// <summary>진단 요청 시에만 GPU 노이즈를 읽어 값의 범위와 비정상 값을 확인합니다.</summary>
        public string DiagnoseNoise()
        {
            string result = "";
            foreach (KeyValuePair<string, Texture> entry in noise)
            {
                RenderTexture texture = entry.Value as RenderTexture;
                if (texture == null)
                {
                    continue;
                }

                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBAFloat);
                request.WaitForCompletion();
                if (request.hasError)
                {
                    result += "readback failed; ";
                    continue;
                }

                float minimum = float.MaxValue;
                float maximum = float.MinValue;
                int invalid = 0;
                foreach (Color color in request.GetData<Color>())
                {
                    if (float.IsNaN(color.r) || float.IsInfinity(color.r))
                    {
                        invalid++;
                    }
                    else
                    {
                        minimum = Mathf.Min(minimum, color.r);
                        maximum = Mathf.Max(maximum, color.r);
                    }
                }

                result += entry.Key + ":" + minimum + ".." + maximum + " invalid=" + invalid + "; ";
            }

            return result;
        }

        /// <summary>전체 밀도와 상층 밀도를 읽습니다. 상층은 기본적으로 타워만 포함하며 선택적으로 띠구름을 포함합니다.</summary>
        public Vector2[] ProbeDensity(Vector3[] positions, bool includeRibbons = false)
        {
            return ProbeField(positions, "ProbeDensity", includeRibbons);
        }

        /// <summary>생산용 운해 함수의 높이(m)와 봉우리 마스크를 읽어 캐시 오차를 검사합니다.</summary>
        public Vector2[] ProbeOceanHeights(Vector3[] positions)
        {
            return ProbeField(positions, "ProbeOceanHeight", false);
        }

        /// <summary>주어진 월드 밀도 좌표의 태양·하늘 차폐량을 읽습니다.</summary>
        public Vector2[] ProbeOcclusion(Vector3[] positions)
        {
            return ProbeField(positions, "ProbeSpatialLighting", false);
        }

        /// <summary>동일한 수명·버퍼 계약으로 요청한 생산용 필드를 GPU에서 읽습니다.</summary>
        private Vector2[] ProbeField(Vector3[] positions, string kernelName, bool includeRibbons)
        {
            Vector2[] result = new Vector2[positions.Length];
            if (positions.Length == 0)
            {
                return result;
            }

            using (ComputeBuffer input = new ComputeBuffer(positions.Length, 12))
            {
                using (ComputeBuffer output = new ComputeBuffer(positions.Length, 8))
                {
                    int probeKernel = renderer.FindKernel(kernelName);
                    shapeCells.BindProbe(renderer, probeKernel);
                    oceanHeights.BindProbe(renderer, probeKernel);
                    if (kernelName == "ProbeSpatialLighting")
                    {
                        spatialLight.BindProbe(renderer, probeKernel);
                    }
                    input.SetData(positions);
                    renderer.SetInt("_ProbeCount", positions.Length);
                    int ribbonMode = 0;
                    if (includeRibbons)
                    {
                        ribbonMode = 1;
                    }

                    renderer.SetInt("_ProbeIncludeRibbons", ribbonMode);
                    renderer.SetBuffer(probeKernel, "_ProbePositions", input);
                    renderer.SetBuffer(probeKernel, "_ProbeResults", output);
                    foreach (KeyValuePair<string, Texture> entry in noise)
                    {
                        renderer.SetTexture(probeKernel, entry.Key, entry.Value);
                    }

                    if (!noise.ContainsKey("_SculptNoise"))
                    {
                        renderer.SetTexture(probeKernel, "_SculptNoise", noise["_BaseNoise"]);
                    }

                    renderer.Dispatch(probeKernel, (positions.Length + 63) / 64, 1, 1);
                    output.GetData(result);
                }
            }

            return result;
        }

        /// <summary>모든 카메라 버퍼, 공유 노이즈, 셰이더 복제본을 해제합니다.</summary>
        public void Dispose()
        {
            shapeCells.Dispose();
            oceanHeights.Dispose();
            spatialLight.Dispose();
            foreach (CloudRenderTargets targets in cameraTargets.Values)
            {
                targets.Dispose();
            }

            cameraTargets.Clear();
            foreach (UnityEngine.Object resource in owned)
            {
                CloudRenderTargets.Release(resource);
            }

            owned.Clear();
            noise.Clear();
        }
    }
}
