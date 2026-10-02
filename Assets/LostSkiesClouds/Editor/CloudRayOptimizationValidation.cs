using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>구간·그림자 최적화를 같은 광선 입력으로 비교하고 형태·색·깊이의 오차를 저장합니다.</summary>
    public static class CloudRayOptimizationValidation
    {
        [Serializable]
        public sealed class Comparison
        {
            public string scenario;
            public string variant;
            public float maximumRgbError;
            public float maximumTransmissionError;
            public float maximumDepthError;
            public double meanRgbError;
            public int pixels;
        }

        [Serializable]
        public sealed class Report
        {
            public bool passed;
            public string error;
            public Comparison[] comparisons;
        }

        internal sealed class Frame
        {
            public Color[] lighting;
            public Color[] transmission;
        }

        /// <summary>하늘·수평선·내부·음수 셀·최대 변위·원점·빛 방향과 혼합 모드를 검사합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Validate Ray Intervals A-B")]
        public static Report Run()
        {
            Report report = new Report();
            List<Comparison> comparisons = new List<Comparison>();
            GameObject host = new GameObject("Cloud ray validation");
            host.hideFlags = HideFlags.HideAndDontSave;
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.aspect = 16f / 9f;
            camera.farClipPlane = 100000f;
            CloudSkyProfile sky = UnityEngine.Object.Instantiate(Load<CloudSkyProfile>("Presets/Sky-ConceptV2.asset"));
            CloudStyleProfile style = UnityEngine.Object.Instantiate(Load<CloudStyleProfile>("Presets/Style-ConceptV2.asset"));
            try
            {
                using (LostSkiesCloudRenderer renderer = new LostSkiesCloudRenderer(
                    Load<ComputeShader>("Runtime/CloudRaymarch.compute"),
                    Load<ComputeShader>("SourceShaders/CloudGenerator.asset"), Load<TextAsset>("Presets/normal.json")))
                {
                    renderer.includeOcean = true;
                    renderer.oceanProfile = Load<CloudOceanProfile>("Presets/ContinuousOcean.asset");
                    renderer.skyProfile = sky;
                    renderer.styleProfile = style;
                    renderer.useCompactTargets = true;
                    renderer.settings.coverageIntensity = 0.165f;
                    renderer.settings.density = 160000f;
                    renderer.Resize(320, 184);
                    string[] names = { "home", "horizon", "above-down", "inside", "negative-cell", "axis-parallel",
                        "max-displacement", "origin-offset", "sky-disabled", "low-sun", "mixed-light", "bright-rim" };
                    for (int index = 0; index < names.Length; index++)
                    {
                        Configure(index, camera, renderer, sky, style);
                        renderer.useRayIntervals = false;
                        renderer.useShadowTermination = false;
                        Frame reference = Draw(renderer, camera);
                        for (int variant = 1; variant <= 3; variant++)
                        {
                            renderer.useRayIntervals = variant == 1 || variant == 3;
                            renderer.useShadowTermination = variant >= 2;
                            Comparison comparison = Compare(reference, Draw(renderer, camera));
                            comparison.scenario = names[index];
                            comparison.variant = variant.ToString();
                            comparisons.Add(comparison);
                            Require(comparison.maximumTransmissionError <= 0.0001f, "Transmission changed: " + names[index]);
                            Require(comparison.maximumDepthError <= 0.1f, "Weighted depth changed: " + names[index]);
                            Require(comparison.maximumRgbError <= 0.001f, "Radiance changed: " + names[index]);
                        }
                    }

                    report.passed = true;
                }
            }
            catch (Exception exception)
            {
                report.error = exception.ToString();
                Debug.LogException(exception);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(sky);
                UnityEngine.Object.DestroyImmediate(style);
                report.comparisons = comparisons.ToArray();
                Directory.CreateDirectory("Screenshots");
                File.WriteAllText("Screenshots/RayOptimization-Validation.json", JsonUtility.ToJson(report, true));
            }

            return report;
        }

        /// <summary>원본 에셋을 변경하지 않고 각 조건을 독립적으로 재현합니다.</summary>
        internal static void Configure(int index, Camera camera, LostSkiesCloudRenderer renderer, CloudSkyProfile sky, CloudStyleProfile style)
        {
            EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(Load<CloudSkyProfile>("Presets/Sky-ConceptV2.asset")), sky);
            EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(Load<CloudStyleProfile>("Presets/Style-ConceptV2.asset")), style);
            renderer.worldOriginOffset = Vector3.zero;
            renderer.skyDensityMultiplier = 1f;
            camera.transform.SetPositionAndRotation(new Vector3(1222, 3300, -6800), Quaternion.Euler(10, 15, 0));
            CloudLightingState light = CloudLightingState.Day();
            light.cloudPaletteBlend = 1;
            light.cloudShadowColor = new Color(0.25f, 0.45f, 0.6f);
            light.cloudMidColor = new Color(0.7f, 0.85f, 0.9f);
            light.cloudLightColor = Color.white;
            if (index == 1)
            {
                camera.transform.SetPositionAndRotation(new Vector3(1222, 6500, -6800), Quaternion.Euler(0, 15, 0));
            }
            else if (index == 2)
            {
                camera.transform.SetPositionAndRotation(new Vector3(0, 16000, -3200), Quaternion.Euler(65, 15, 0));
            }
            else if (index == 3)
            {
                camera.transform.position = new Vector3(0, 900, -3200);
            }
            else if (index == 4)
            {
                camera.transform.SetPositionAndRotation(new Vector3(-19000, 8700, -12000), Quaternion.Euler(20, 130, 0));
            }
            else if (index == 5)
            {
                camera.transform.SetPositionAndRotation(new Vector3(0, 11000, 0), Quaternion.identity);
            }
            else if (index == 6)
            {
                style.viewBillowDisplacement = 600;
                style.fineBillowDisplacement = 160;
                sky.spacing = 4000;
                sky.horizontalRadius = new Vector2(600, 5000);
                sky.verticalRadius = new Vector2(300, 2000);
                camera.transform.position = new Vector3(180000, 7400, -190000);
            }
            else if (index == 7)
            {
                renderer.worldOriginOffset = new Vector3(180000, 0, -190000);
            }
            else if (index == 8)
            {
                renderer.skyDensityMultiplier = 0;
                camera.transform.rotation = Quaternion.Euler(-25, 15, 0);
            }
            else if (index == 9)
            {
                light.sunElevation = 2;
                camera.transform.rotation = Quaternion.Euler(-2, light.sunAzimuth, 0);
            }
            else if (index == 10)
            {
                light.cloudPaletteBlend = 0.5f;
            }
            else if (index == 11)
            {
                light.cloudLightColor = new Color(4, 3, 2);
                light.directMultiplier = 10;
                light.silverLining = 2;
                camera.transform.rotation = Quaternion.Euler(-light.sunElevation, light.sunAzimuth, 0);
            }

            renderer.skyLighting = light;
        }

        /// <summary>생산용 에셋 경로를 한곳에서 해석합니다.</summary>
        internal static T Load<T>(string relative) where T : UnityEngine.Object
        {
            return AssetDatabase.LoadAssetAtPath<T>("Assets/LostSkiesClouds/" + relative);
        }

        /// <summary>GPU 출력만 동기 비교하며 이 대기 시간은 성능 수치로 사용하지 않습니다.</summary>
        internal static Frame Draw(LostSkiesCloudRenderer renderer, Camera camera)
        {
            using (CommandBuffer commands = new CommandBuffer())
            {
                renderer.Render(commands, camera, Vector3.up, Color.white);
                Graphics.ExecuteCommandBuffer(commands);
            }

            Frame frame = new Frame();
            frame.lighting = Read(renderer.lighting);
            frame.transmission = Read(renderer.transmittance);
            return frame;
        }

        /// <summary>압축 RT도 공통 float 비교 형식으로 읽습니다.</summary>
        private static Color[] Read(RenderTexture texture)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBAFloat);
            request.WaitForCompletion();
            Require(!request.hasError, "GPU readback failed.");
            return request.GetData<Color>().ToArray();
        }

        /// <summary>RGB·투과율·대표 깊이 오차와 모든 채널의 유한성을 검사합니다.</summary>
        internal static Comparison Compare(Frame expected, Frame actual)
        {
            Comparison result = new Comparison();
            result.pixels = actual.lighting.Length;
            for (int index = 0; index < result.pixels; index++)
            {
                Color difference = expected.lighting[index] - actual.lighting[index];
                float rgb = Mathf.Max(Mathf.Abs(difference.r), Mathf.Abs(difference.g), Mathf.Abs(difference.b));
                float depth = Mathf.Abs(difference.a);
                float transmission = Mathf.Abs(expected.transmission[index].r - actual.transmission[index].r);
                Require(!float.IsNaN(rgb + depth + transmission) && !float.IsInfinity(rgb + depth + transmission), "Non-finite output.");
                result.maximumRgbError = Mathf.Max(result.maximumRgbError, rgb);
                result.maximumDepthError = Mathf.Max(result.maximumDepthError, depth);
                result.maximumTransmissionError = Mathf.Max(result.maximumTransmissionError, transmission);
                result.meanRgbError += rgb / result.pixels;
            }

            return result;
        }

        /// <summary>실패 조건을 보고서에 보존합니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
