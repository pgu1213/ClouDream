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
                return activeTargets.width;
            }
        }

        public int Height
        {
            get
            {
                return activeTargets.height;
            }
        }

        public int NoiseGenerationCount { get; private set; }

        /// <summary>여러 카메라와 날씨 변경에도 유지할 공용 노이즈 및 셰이더를 준비합니다.</summary>
        public LostSkiesCloudRenderer(ComputeShader raymarch, ComputeShader noiseGenerator, TextAsset preset)
        {
            try
            {
                renderer = UnityEngine.Object.Instantiate(raymarch);
                generator = UnityEngine.Object.Instantiate(noiseGenerator);
                owned.Add(renderer);
                owned.Add(generator);
                kernel = renderer.FindKernel("Raymarch");
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
            activeTargets.Resize(width, height);
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
            // 중간 크기 구형 굴곡은 단일 옥타브 노이즈를 한 번 생성해 모든 구름에서 재사용합니다.
            if (styleProfile != null && !noise.ContainsKey("_SculptNoise"))
            {
                noise["_SculptNoise"] = GenerateNoise("WORLEY3D", 128, 4f, 1, 0.5f);
            }

            int sharedLighting = 0;
            if (skyLighting.active)
            {
                sharedLighting = 1;
                sunDirection = skyLighting.GetSunDirection();
                sunColor = skyLighting.sunColor.linear;
                intensity = skyLighting.GetDirectStrength();
            }

            Color ambientSky = skyLighting.ambientSkyColor.linear;
            Color ambientHorizon = skyLighting.ambientHorizonColor.linear;
            commands.SetComputeIntParam(renderer, "_UseSkyLighting", sharedLighting);
            commands.SetComputeVectorParam(renderer, "_AmbientSky", new Vector4(ambientSky.r, ambientSky.g, ambientSky.b, Mathf.Max(0f, skyLighting.ambientIntensity)));
            commands.SetComputeVectorParam(renderer, "_AmbientHorizon", new Vector4(ambientHorizon.r, ambientHorizon.g, ambientHorizon.b, Mathf.Max(0f, skyLighting.silverLining)));

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
            ApplyFormation(commands, oceanProfile);
            ApplyFormation(commands, skyProfile);
            ApplyFormation(commands, styleProfile);
            foreach (KeyValuePair<string, Texture> entry in noise)
            {
                commands.SetComputeTextureParam(renderer, kernel, entry.Key, entry.Value);
            }

            if (!noise.ContainsKey("_SculptNoise"))
            {
                commands.SetComputeTextureParam(renderer, kernel, "_SculptNoise", noise["_BaseNoise"]);
            }

            commands.SetComputeTextureParam(renderer, kernel, "_SceneDepth", DepthTarget);
            commands.SetComputeTextureParam(renderer, kernel, "_LightingOut", lighting);
            commands.SetComputeTextureParam(renderer, kernel, "_TransmittanceOut", transmittance);
            commands.DispatchCompute(renderer, kernel, Width / 8, Height / 8, 1);
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
            Vector2[] result = new Vector2[positions.Length];
            if (positions.Length == 0)
            {
                return result;
            }

            using (ComputeBuffer input = new ComputeBuffer(positions.Length, 12))
            {
                using (ComputeBuffer output = new ComputeBuffer(positions.Length, 8))
                {
                    int probeKernel = renderer.FindKernel("ProbeDensity");
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
