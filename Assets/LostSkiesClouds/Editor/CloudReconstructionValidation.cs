using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>합성의 GPU 계약과 실제 카메라의 해상도별 오차를 구분하여 검사합니다.</summary>
    public static class CloudReconstructionValidation
    {
        private static int captureWidth = 640;
        private static int captureHeight = 360;

        private static string outputDirectory;

        private static string reportPath;

        [Serializable]
        public sealed class Measurement
        {
            public string scenario;
            public float scale;
            public bool depthAware;
            public double meanRgbError;
            public float maximumRgbError;
            public float pixelP95;
            public double edgeMeanError;
            public int edgePixels;
            public double foregroundMeanError;
            public int foregroundPixels;
        }

        [Serializable]
        public sealed class Report
        {
            public bool completed;
            public bool contractsPassed;
            public int width;
            public int height;
            public float maximumContractError;
            public float foregroundLinearMeanError;
            public float foregroundDepthMeanError;
            public string scope = "Linear HDR camera output, postprocess and antialiasing OFF, 100% reference; absolute RGB values include HDRP exposure. Shape and subpixel temporal equivalence are not asserted.";
            public Measurement[] measurements;
            public string error;
        }

        /// <summary>GPU의 알려진 입력 계약과 실제 장면의 근사 오차를 독립적으로 검사합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Validate Reconstruction")]
        public static Report Run()
        {
            return RunResolution(640, 360, "Screenshots/Reconstruction");
        }

        /// <summary>목표 해상도에서 동일한 품질 비교를 실행하고 축소 검사와 별도 결과를 보관합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Validate FHD Reconstruction")]
        public static Report RunFHD()
        {
            return RunResolution(1920, 1080, "Screenshots/Reconstruction-FHD");
        }

        /// <summary>동기 Editor 검사 동안 동일한 해상도로 렌더·집계·PNG 저장을 수행합니다.</summary>
        private static Report RunResolution(int width, int height, string directory)
        {
            captureWidth = width;
            captureHeight = height;
            outputDirectory = directory;
            reportPath = directory + "-Validation.json";
            Report report = new Report();
            report.width = width;
            report.height = height;
            List<Measurement> measurements = new List<Measurement>();
            try
            {
                CheckSynthetic(report);
                CheckScene(report, measurements);
                report.contractsPassed = true;
            }
            catch (Exception exception)
            {
                report.error = exception.ToString();
                Debug.LogException(exception);
            }
            finally
            {
                report.completed = true;
                report.measurements = measurements.ToArray();
                Directory.CreateDirectory("Screenshots");
                File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
            }

            return report;
        }

        /// <summary>상수 보존·전경 경계·저해상도에 없는 얇은 전경·전경 앞의 구름을 두 버퍼 계약으로 검사합니다.</summary>
        private static void CheckSynthetic(Report report)
        {
            const int lowWidth = 20;
            const int lowHeight = 12;
            Material material = new Material(AssetDatabase.LoadAssetAtPath<Shader>(
                "Assets/LostSkiesClouds/Editor/CloudReconstructionValidation.shader"));
            Texture2DArray lighting = new Texture2DArray(lowWidth, lowHeight, 1, TextureFormat.RGBAFloat, false, true);
            Texture2DArray transmission = new Texture2DArray(lowWidth, lowHeight, 1, TextureFormat.RGBAFloat, false, true);
            Texture2DArray lowDepth = new Texture2DArray(lowWidth, lowHeight, 1, TextureFormat.RGBAFloat, false, true);
            Texture2D highDepth = new Texture2D(40, 24, TextureFormat.RGBAFloat, false, true);
            RenderTexture target = new RenderTexture(40, 24, 0, RenderTextureFormat.ARGBFloat);
            target.Create();
            try
            {
                material.SetTexture("_CloudLighting", lighting);
                material.SetTexture("_CloudTransmittance", transmission);
                material.SetTexture("_CloudLowSceneDepth", lowDepth);
                material.SetTexture("_ValidationDepth", highDepth);
                material.SetVector("_ValidationSize", new Vector4(40, 24, 0, 0));
                material.SetVector("_CloudOutputSize", new Vector4(lowWidth, lowHeight, 0, 0));
                material.SetVector("_CloudDepthParams", new Vector4(1, 0, 0, 0));
                material.SetFloat("_CloudDepthUpsampling", 1);
                for (int compact = 0; compact < 2; compact++)
                {
                    material.SetFloat("_CloudCompact", compact);
                    for (int scenario = 0; scenario < 4; scenario++)
                    {
                        Color[] light = new Color[lowWidth * lowHeight];
                        Color[] trans = new Color[light.Length];
                        Color[] low = new Color[light.Length];
                        Color[] high = new Color[40 * 24];
                        Color[] expected = new Color[high.Length];
                        for (int index = 0; index < light.Length; index++)
                        {
                            light[index] = new Color(0.2f, 0.3f, 0.4f, 500);
                            trans[index] = new Color(0.5f, 1000, 0, 1000);
                            if (scenario == 3)
                            {
                                trans[index] = new Color(0.5f, 20, 0, 20);
                            }

                            if (scenario == 1 && index % lowWidth < 10)
                            {
                                low[index] = new Color(0.01f, 0, 0, 0);
                                light[index] = Color.clear;
                                trans[index] = new Color(1, 0, 0, 0);
                            }
                        }

                        for (int index = 0; index < high.Length; index++)
                        {
                            expected[index] = new Color(0.2f, 0.3f, 0.4f, 0.5f);
                            bool foreground = scenario == 1 && index % 40 < 21;
                            foreground |= scenario >= 2 && index % 40 == 17;
                            if (foreground)
                            {
                                high[index] = new Color(0.01f, 0, 0, 0);
                                if (scenario != 3)
                                {
                                    expected[index] = new Color(0, 0, 0, 1);
                                }
                            }
                        }

                        lighting.SetPixels(light, 0);
                        transmission.SetPixels(trans, 0);
                        lowDepth.SetPixels(low, 0);
                        highDepth.SetPixels(high);
                        lighting.Apply();
                        transmission.Apply();
                        lowDepth.Apply();
                        highDepth.Apply();
                        using (CommandBuffer commands = new CommandBuffer())
                        {
                            commands.SetRenderTarget(target);
                            commands.SetViewport(new Rect(0, 0, 40, 24));
                            CoreUtils.DrawFullScreen(commands, material);
                            Graphics.ExecuteCommandBuffer(commands);
                        }

                        Color[] actual = Read(target);
                        for (int index = 0; index < actual.Length; index++)
                        {
                            Color delta = actual[index] - expected[index];
                            float error = Mathf.Max(Mathf.Abs(delta.r), Mathf.Abs(delta.g), Mathf.Abs(delta.b), Mathf.Abs(delta.a));
                            Require(float.IsFinite(error), "Non-finite reconstruction result.");
                            report.maximumContractError = Mathf.Max(report.maximumContractError, error);
                        }
                    }
                }

                Require(report.maximumContractError < 0.001f, "GPU reconstruction contract failed.");
            }
            finally
            {
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(lighting);
                UnityEngine.Object.DestroyImmediate(transmission);
                UnityEngine.Object.DestroyImmediate(lowDepth);
                UnityEngine.Object.DestroyImmediate(highDepth);
            }
        }

        /// <summary>시각·비행·카메라·품질과 임시 전경을 복원하면서 실제 HDRP 해상도 오차를 측정합니다.</summary>
        private static void CheckScene(Report report, List<Measurement> measurements)
        {
            Camera camera = Camera.main;
            CloudOptimizationPanel panel = UnityEngine.Object.FindFirstObjectByType<CloudOptimizationPanel>();
            CustomPassVolume volume = panel.GetComponent<CustomPassVolume>();
            LostSkiesCloudPass pass = null;
            foreach (CustomPass candidate in volume.customPasses)
            {
                if (candidate is LostSkiesCloudPass cloudPass)
                {
                    pass = cloudPass;
                    break;
                }
            }

            CloudTimeOfDayController clock = UnityEngine.Object.FindFirstObjectByType<CloudTimeOfDayController>();
            HDAdditionalCameraData data = camera.GetComponent<HDAdditionalCameraData>();
            var antialiasing = data.antialiasing;
            bool customSettings = data.customRenderingSettings;
            FrameSettings settings = data.renderingPathCustomFrameSettings;
            FrameSettingsOverrideMask mask = data.renderingPathCustomFrameSettingsOverrideMask;
            float hour = clock.TimeOfDay;
            bool advance = clock.autoAdvance;
            float wind = pass.windSpeed;
            float scale = pass.resolutionScale;
            bool reconstruction = pass.useDepthAwareUpsampling;
            bool spatial = pass.useSpatialLightCache;
            bool intervals = pass.useRayIntervals;
            bool shadows = pass.useShadowTermination;
            Vector3 position = camera.transform.position;
            Quaternion rotation = camera.transform.rotation;
            float aspect = camera.aspect;
            float fov = camera.fieldOfView;
            RenderTexture previous = camera.targetTexture;
            Camera targetCamera = volume.targetCamera;
            RenderTexture target = new RenderTexture(captureWidth, captureHeight, 24, RenderTextureFormat.ARGBFloat);
            List<GameObject> foreground = new List<GameObject>();
            List<Rect> rectangles = new List<Rect>();
            Material material = new Material(Shader.Find("HDRP/Unlit"));
            material.SetColor("_UnlitColor", new Color(0.03f, 1f, 0.1f));
            target.Create();
            try
            {
                clock.autoAdvance = false;
                pass.windSpeed = 0;
                pass.useSpatialLightCache = false;
                pass.useRayIntervals = false;
                pass.useShadowTermination = false;
                camera.aspect = (float)captureWidth / captureHeight;
                camera.fieldOfView = 68;
                camera.targetTexture = target;
                volume.targetCamera = camera;
                data.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
                data.customRenderingSettings = true;
                data.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.Postprocess, false);
                data.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.Postprocess] = true;
                string[] names = { "home", "side", "inside", "horizon", "quick-turn", "sunset", "foreground" };
                float[] scales = { 0.75f, 0.5f, 0.5f };
                for (int scenario = 0; scenario < names.Length; scenario++)
                {
                    clock.SetTime(12f);
                    Vector3 densityPosition = new Vector3(1222, 3300, -6800);
                    Quaternion direction = Quaternion.Euler(10, 15, 0);
                    if (scenario == 1 || scenario == 2)
                    {
                        CloudStyleCapture.CameraPose pose = CloudStyleCapture.DescribePath("orbit").poses[26];
                        if (scenario == 2)
                        {
                            pose = CloudStyleCapture.DescribePath("traverse").poses[60];
                        }

                        densityPosition = pose.densityPosition;
                        direction = pose.worldRotation;
                    }

                    if (scenario == 3)
                    {
                        densityPosition.y = 6500;
                        direction = Quaternion.Euler(0, 15, 0);
                    }

                    if (scenario == 4)
                    {
                        direction = Quaternion.Euler(12, 60, 0);
                    }

                    if (scenario == 5)
                    {
                        clock.SetTime(18f);
                    }

                    Vector3 origin = Vector3.zero;
                    if (pass.worldOrigin != null)
                    {
                        origin = pass.worldOrigin.SamplingOffset;
                    }

                    camera.transform.SetPositionAndRotation(densityPosition - origin, direction);
                    if (scenario == 6)
                    {
                        AddForeground(camera, material, foreground, rectangles);
                    }

                    pass.resolutionScale = 1;
                    pass.useDepthAwareUpsampling = false;
                    Color[] reference = Capture(camera, target);
                    SavePreview(reference, names[scenario] + "-100");
                    for (int variant = 0; variant < scales.Length; variant++)
                    {
                        pass.resolutionScale = scales[variant];
                        pass.useDepthAwareUpsampling = variant == 2;
                        Color[] actual = Capture(camera, target);
                        Measurement result = Compare(reference, actual, rectangles);
                        result.scenario = names[scenario];
                        result.scale = scales[variant];
                        result.depthAware = pass.useDepthAwareUpsampling;
                        measurements.Add(result);
                        SavePreview(actual, names[scenario] + "-" + variant);
                        if (scenario == 6 && variant == 1)
                        {
                            report.foregroundLinearMeanError = (float)result.foregroundMeanError;
                        }

                        if (scenario == 6 && variant == 2)
                        {
                            report.foregroundDepthMeanError = (float)result.foregroundMeanError;
                            if (result.foregroundPixels < 1000)
                            {
                                throw new InvalidOperationException("Foreground comparison did not contain enough visible test pixels.");
                            }

                            if (report.foregroundDepthMeanError > report.foregroundLinearMeanError * 0.1f + 0.00001f)
                            {
                                throw new InvalidOperationException("Depth reconstruction did not sufficiently reduce foreground leakage.");
                            }
                        }
                    }
                }
            }
            finally
            {
                foreach (GameObject item in foreground)
                {
                    UnityEngine.Object.DestroyImmediate(item);
                }

                UnityEngine.Object.DestroyImmediate(material);
                camera.targetTexture = previous;
                camera.transform.SetPositionAndRotation(position, rotation);
                camera.aspect = aspect;
                camera.fieldOfView = fov;
                data.antialiasing = antialiasing;
                data.customRenderingSettings = customSettings;
                data.renderingPathCustomFrameSettings = settings;
                data.renderingPathCustomFrameSettingsOverrideMask = mask;
                volume.targetCamera = targetCamera;
                pass.windSpeed = wind;
                pass.resolutionScale = scale;
                pass.useDepthAwareUpsampling = reconstruction;
                pass.useSpatialLightCache = spatial;
                pass.useRayIntervals = intervals;
                pass.useShadowTermination = shadows;
                clock.SetTime(hour);
                clock.autoAdvance = advance;
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        /// <summary>화면의 알려진 영역에 넓은 전경과 1~3픽셀 폭의 얇은 전경을 배치합니다.</summary>
        private static void AddForeground(Camera camera, Material material, List<GameObject> objects, List<Rect> rectangles)
        {
            Rect[] areas = { new Rect(0.22f, 0.17f, 0.19f, 0.46f), new Rect(0.55f, 0.14f, 1.4f / captureWidth, 0.65f),
                new Rect(0.68f, 0.22f, 3f / captureWidth, 0.53f) };
            float vertical = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 600f;
            foreach (Rect area in areas)
            {
                GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = "Temporary reconstruction foreground";
                quad.hideFlags = HideFlags.HideAndDontSave;
                quad.transform.SetPositionAndRotation(camera.ViewportToWorldPoint(new Vector3(area.center.x, area.center.y, 300)), camera.transform.rotation);
                quad.transform.localScale = new Vector3(area.width * vertical * camera.aspect, area.height * vertical, 1);
                quad.GetComponent<Renderer>().sharedMaterial = material;
                objects.Add(quad);
                rectangles.Add(area);
            }
        }

        /// <summary>고정 입력으로 4회 렌더하여 HDRP의 기존 이력을 안정시킨 뒤 float 출력을 읽습니다.</summary>
        private static Color[] Capture(Camera camera, RenderTexture target)
        {
            for (int frame = 0; frame < 4; frame++)
            {
                camera.Render();
            }

            return Read(target);
        }

        /// <summary>GPU 원시 값을 동기적으로 읽고 읽기 실패를 수치 비교에 사용하지 않습니다.</summary>
        private static Color[] Read(RenderTexture texture)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBAFloat);
            request.WaitForCompletion();
            Require(!request.hasError, "GPU readback failed.");
            return request.GetData<Color>().ToArray();
        }

        /// <summary>전체 픽셀·강한 색 경계·알려진 전경 내부를 따로 집계합니다. 값은 노출 후 선형 HDR입니다.</summary>
        private static Measurement Compare(Color[] expected, Color[] actual, List<Rect> rectangles)
        {
            Measurement result = new Measurement();
            List<float> errors = new List<float>();
            for (int index = 0; index < expected.Length; index++)
            {
                float error = RgbDifference(expected[index], actual[index]);
                Require(float.IsFinite(error), "Non-finite camera output.");
                errors.Add(error);
                result.meanRgbError += error / expected.Length;
                result.maximumRgbError = Mathf.Max(result.maximumRgbError, error);
                int x = index % captureWidth;
                int y = index / captureWidth;
                if (x > 0 && y > 0 && (RgbDifference(expected[index], expected[index - 1]) > 0.015f
                    || RgbDifference(expected[index], expected[index - captureWidth]) > 0.015f))
                {
                    result.edgeMeanError += error;
                    result.edgePixels++;
                }

                foreach (Rect rectangle in rectangles)
                {
                    // 읽기 이미지의 상하 방향에 의존하지 않고 기준 영상의 녹색 전경으로도 확인합니다.
                    bool horizontal = (x + 0.5f) / captureWidth > rectangle.xMin && (x + 0.5f) / captureWidth < rectangle.xMax;
                    Color reference = expected[index];
                    if (horizontal && reference.g > reference.r * 3 && reference.g > reference.b * 3)
                    {
                        result.foregroundMeanError += error;
                        result.foregroundPixels++;
                        break;
                    }
                }
            }

            result.edgeMeanError /= Math.Max(1, result.edgePixels);
            result.foregroundMeanError /= Math.Max(1, result.foregroundPixels);
            errors.Sort();
            result.pixelP95 = errors[Mathf.CeilToInt(errors.Count * 0.95f) - 1];
            return result;
        }

        /// <summary>최대 RGB 차이를 동일한 기준으로 반환합니다.</summary>
        private static float RgbDifference(Color first, Color second)
        {
            Color delta = first - second;
            return Mathf.Max(Mathf.Abs(delta.r), Mathf.Abs(delta.g), Mathf.Abs(delta.b));
        }

        /// <summary>수치 비교와 별도로 고정 Reinhard 표시 변환을 적용한 진단 PNG를 저장합니다.</summary>
        private static void SavePreview(Color[] linear, string name)
        {
            Color[] colors = new Color[linear.Length];
            for (int index = 0; index < colors.Length; index++)
            {
                Color value = linear[index];
                colors[index] = new Color(value.r / (1 + value.r), value.g / (1 + value.g), value.b / (1 + value.b), 1).gamma;
            }

            Texture2D texture = new Texture2D(captureWidth, captureHeight, TextureFormat.RGBA32, false);
            texture.SetPixels(colors);
            texture.Apply();
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllBytes(outputDirectory + "/" + name + ".png", texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
        }

        /// <summary>계약 위반을 잡을 수 있는 예외로 반환합니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
