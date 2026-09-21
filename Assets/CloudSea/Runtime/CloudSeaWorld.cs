using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream
{
    /// <summary>Art controls for a continuous cloud ocean with isolated convection towers.</summary>
    [ExecuteAlways, RequireComponent(typeof(Volume))]
    public sealed class CloudSeaWorld : MonoBehaviour
    {
        [Header("Geometry (metres)")]
        [Min(1)]
        public float bottomAltitude = 650;
        [Min(100)]
        public float thickness = 3200;
        [Range(0.15f, 0.6f)]
        public float oceanDepth = 0.25f;
        [Min(4000)]
        public float weatherMapSize = 16000;

        public int seed = 73;
        [Range(0.65f, 1)]
        public float coverage = 0.96f;
        [Range(0, 1)]
        public float towerAmount = 0.72f;

        [Header("Surface and light")]
        [Range(0, 1)]
        public float density = 0.6f;
        [Range(0, 1)]
        public float shaping = 0.76f;
        [Min(0.1f)]
        public float shapeScale = 18;
        [Range(0, 1)]
        public float erosion = 0.42f;

        [Min(1)]
        public float erosionScale = 95;
        [Range(32, 256)]
        public int primarySteps = 128;
        [Range(1, 16)]
        public int lightSteps = 8;

        [Header("Wind")]
        [Min(0)]
        public float windSpeedKmh = 18;
        [Range(0, 360)]
        public float windDirection = 25;

        [Header("Generated data (Rebuild maps after changing geometry/seed)")]
        public Texture2D weatherMap;
        public Texture2D heightLut;
        Volume volume;
        VolumeProfile runtimeProfile;
        Texture2D liveMap, liveLut;

        /// <summary>컴포넌트 활성화 시 참조와 실행용 프로필을 준비합니다.</summary>
        void OnEnable()
        {
            volume = GetComponent<Volume>();
            if (Application.isPlaying && volume.sharedProfile != null)
            {
                runtimeProfile = volume.profile;
            }

            Apply();
        }

        /// <summary>Inspector 변경 사항을 활성 구름에 반영합니다.</summary>
        void OnValidate()
        {
            if (isActiveAndEnabled)
            {
                Apply();
            }
        }

        /// <summary>구름 형태와 조명 설정을 HDRP 볼륨에 적용합니다.</summary>
        public void Apply()
        {
            if (volume == null)
            {
                volume = GetComponent<Volume>();
            }

            var profile = volume.sharedProfile;
            if (Application.isPlaying)
            {
                profile = volume.profile;
            }

            if (profile == null || !profile.TryGet<VolumetricClouds>(out var clouds))
            {
                return;
            }

            clouds.enable.Override(true);
            clouds.cloudControl.Override(VolumetricClouds.CloudControl.Manual);
            Texture2D selectedMap = weatherMap;
            if (liveMap != null)
            {
                selectedMap = liveMap;
            }

            clouds.cloudMap.Override(selectedMap);
            Texture2D selectedLut = heightLut;
            if (liveLut != null)
            {
                selectedLut = liveLut;
            }

            clouds.cloudLut.Override(selectedLut);
            clouds.bottomAltitude.Override(bottomAltitude);
            clouds.altitudeRange.Override(thickness);
            // HDRP normalizes weather UVs by its horizon distance (Earth radius = 6,378,100 m).
            double radius = 6378100.0, altitude = bottomAltitude + thickness * 0.5;
            float horizon = (float)System.Math.Sqrt((radius + altitude) * (radius + altitude) - radius * radius);
            clouds.cloudTiling.Override(Vector2.one * (horizon / weatherMapSize));
            clouds.densityMultiplier.Override(density);
            clouds.shapeFactor.Override(shaping);
            clouds.shapeScale.Override(shapeScale);
            clouds.erosionFactor.Override(erosion);
            clouds.erosionScale.Override(erosionScale);
            clouds.erosionNoiseType.Override(VolumetricClouds.CloudErosionNoise.Worley32);
            clouds.microErosion.Override(false);
            clouds.numPrimarySteps.Override(primarySteps);
            clouds.numLightSteps.Override(lightSteps);
            clouds.fadeInMode.Override(VolumetricClouds.CloudFadeInMode.Manual);
            clouds.fadeInStart.Override(0);
            clouds.fadeInDistance.Override(25);
            clouds.multiScattering.Override(0.65f);
            clouds.powderEffectIntensity.Override(0.35f);
            clouds.erosionOcclusion.Override(0.15f);
            clouds.temporalAccumulationFactor.Override(0.92f);
            clouds.ghostingReduction.Override(true);
            clouds.altitudeDistortion.Override(0.12f);
            clouds.globalWindSpeed.Override(new WindParameter.WindParamaterValue{mode = WindParameter.WindOverrideMode.Custom, customValue = windSpeedKmh, multiplyValue = 1});
            clouds.orientation.Override(new WindParameter.WindParamaterValue{mode = WindParameter.WindOverrideMode.Custom, customValue = windDirection, multiplyValue = 1});
            clouds.shapeSpeedMultiplier.Override(0.45f);
            clouds.erosionSpeedMultiplier.Override(0.18f);
            clouds.verticalShapeWindSpeed.Override(0.5f);
            clouds.shadows.Override(true);
            clouds.shadowDistance.Override(12000);
        }

        /// <summary>연속 운해와 타워 분포를 나타내는 주기적 날씨 맵을 만듭니다.</summary>
        public Texture2D GenerateWeatherMap(int size = 512)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
            {name = "WeatherMap", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear};
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (float)x / (size - 1), v = (float)y / (size - 1);
                    float broad = TileNoise(u, v, 9, seed * 0.17f);
                    float detail = TileNoise(u, v, 23, seed * 0.31f);
                    float tower = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.68f - towerAmount * 0.25f, 0.76f, broad * 0.8f + detail * 0.2f));
                    // R coverage never drops to zero: the base remains a cloud sea.
                    // B selects a column in the height LUT, A leaves all heights available.
                    pixels[y * size + x] = new Color(Mathf.Clamp01(coverage + (detail - 0.5f) * 0.055f), 0, Mathf.Clamp01(tower + detail * 0.16f), 1);
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        /// <summary>높이별 밀도와 침식, 주변광을 조회할 LUT를 만듭니다.</summary>
        public Texture2D GenerateHeightLut(int size = 128)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBAHalf, false, true)
            {name = "HeightLUT", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear};
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float h = (float)y / (size - 1), type = (float)x / (size - 1);
                    float top = Mathf.Lerp(oceanDepth, 0.98f, Mathf.Pow(type, 0.8f));
                    float lower = Mathf.SmoothStep(0, 1, h / 0.055f);
                    float upper = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(top * 0.88f, top, h));
                    float profile = lower * upper;
                    // A low erosion weight deep in the sea prevents full-depth holes.
                    float edge = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.06f, 0.18f, h));
                    float erosionWeight = Mathf.Lerp(0.22f, 1.0f, edge);
                    float ambient = Mathf.Lerp(0.38f, 1, Mathf.Clamp01(h / Mathf.Max(top, 0.01f)));
                    pixels[y * size + x] = new Color(profile, erosionWeight, ambient, 1);
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        /// <summary>맵 가장자리에서 이어지는 주기적 Perlin 노이즈를 계산합니다.</summary>
        static float TileNoise(float u, float v, float frequency, float offset)
        {
            float a = Mathf.PerlinNoise(u * frequency + offset, v * frequency + offset);
            float b = Mathf.PerlinNoise((u - 1) * frequency + offset, v * frequency + offset);
            float c = Mathf.PerlinNoise(u * frequency + offset, (v - 1) * frequency + offset);
            float d = Mathf.PerlinNoise((u - 1) * frequency + offset, (v - 1) * frequency + offset);
            return Mathf.Lerp(Mathf.Lerp(a, b, Mathf.SmoothStep(0, 1, u)), Mathf.Lerp(c, d, Mathf.SmoothStep(0, 1, u)), Mathf.SmoothStep(0, 1, v));
        }

        /// <summary>이전 미리보기 리소스를 해제하고 현재 설정으로 맵을 다시 만듭니다.</summary>
        [ContextMenu("Preview maps (use Inspector Bake to save)")]
        public void RebuildMaps()
        {
            Release(liveMap);
            Release(liveLut);
            liveMap = GenerateWeatherMap();
            liveLut = GenerateHeightLut();
            Apply();
        }

        /// <summary>컴포넌트 소유의 임시 텍스처와 실행용 프로필을 해제합니다.</summary>
        void OnDisable()
        {
            Release(liveMap);
            Release(liveLut);
            liveMap = liveLut = null;
            if (runtimeProfile != null)
            {
                foreach (var component in runtimeProfile.components)
                {
                    Release(component);
                }

                Release(runtimeProfile);
                runtimeProfile = null;
            }
        }

        /// <summary>편집 또는 실행 모드에 맞게 Unity 오브젝트를 해제합니다.</summary>
        static void Release(Object obj)
        {
            if (obj == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(obj);
            }
            else
            {
                DestroyImmediate(obj);
            }
        }
    }
}
