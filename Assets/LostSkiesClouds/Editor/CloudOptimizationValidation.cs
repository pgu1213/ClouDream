using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>최적화 유무만 바꾸고 같은 입력의 밀도와 HDR 출력을 비교하는 GPU 회귀 검사입니다.</summary>
    public static class CloudOptimizationValidation
    {
        [Serializable]
        public sealed class Report
        {
            public bool passed;
            public int cases;
            public int densitySamples;
            public float maximumDensityError;
            public float maximumRadianceError;
            public float maximumTransmissionError;
            public float maximumWeightedDepthError;
            public bool cacheReused;
            public bool compactTargetsSupported;
            public int formatSwitchAllocations;
            public string error;
        }

        private sealed class Frame
        {
            public Color[] lighting;
            public Color[] transmission;
        }

        /// <summary>음수 좌표·캐시 범위 밖·시드/크기 변경·최대 변위를 기존 절차식과 비교합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Validate A-B")]
        public static Report Run()
        {
            Report report = new Report();
            GameObject host = new GameObject("Cloud optimization validation");
            host.hideFlags = HideFlags.HideAndDontSave;
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.aspect = 1.5f;
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
                    renderer.settings.coverageIntensity = 0.165f;
                    renderer.settings.density = 160000f;
                    renderer.skyLighting = CloudLightingState.Day();
                    renderer.Resize(144, 96);

                    for (int index = 0; index < 8; index++)
                    {
                        camera.transform.SetPositionAndRotation(new Vector3(1222f, 3300f, -6800f), Quaternion.Euler(10f, 15f, 0f));
                        if (index == 1)
                        {
                            camera.transform.SetPositionAndRotation(new Vector3(-19000f, 8700f, -12000f), Quaternion.Euler(20f, 130f, 0f));
                        }
                        else if (index == 2)
                        {
                            camera.transform.SetPositionAndRotation(new Vector3(0f, 920f, -3200f), Quaternion.identity);
                        }
                        else if (index == 3)
                        {
                            camera.transform.SetPositionAndRotation(new Vector3(0f, 9000f, -3200f), Quaternion.Euler(75f, 15f, 0f));
                        }
                        else if (index == 4)
                        {
                            sky.seed = 912;
                            sky.spacing = 4000f;
                            sky.horizontalRadius = new Vector2(600f, 5000f);
                            sky.verticalRadius = new Vector2(300f, 2000f);
                        }
                        else if (index == 5)
                        {
                            style.viewBillowDisplacement = 600f;
                            style.fineBillowDisplacement = 160f;
                            camera.transform.position = new Vector3(180000f, 7400f, -190000f);
                        }
                        else if (index == 6)
                        {
                            renderer.worldOriginOffset = new Vector3(180000f, 0f, -190000f);
                        }
                        else if (index == 7)
                        {
                            renderer.skyDensityMultiplier = 0f;
                        }

                        Vector3[] points = SamplePoints(sky.spacing, renderer.worldOriginOffset);
                        renderer.useShapeCellCache = false;
                        renderer.useSupportRejection = false;
                        Frame reference = Draw(renderer, camera);
                        Vector2[] expected = renderer.ProbeDensity(points, true);

                        renderer.useShapeCellCache = true;
                        renderer.useSupportRejection = true;
                        Frame optimized = Draw(renderer, camera);
                        Vector2[] actual = renderer.ProbeDensity(points, true);
                        Compare(reference, optimized, report);
                        for (int point = 0; point < points.Length; point++)
                        {
                            float error = Mathf.Max(Mathf.Abs(expected[point].x - actual[point].x),
                                Mathf.Abs(expected[point].y - actual[point].y));
                            Require(IsFinite(error), "Non-finite density comparison.");
                            report.maximumDensityError = Mathf.Max(report.maximumDensityError, error);
                        }

                        int builds = renderer.ShapeCacheBuildCount;
                        Draw(renderer, camera);
                        Require(renderer.ShapeCacheBuildCount == builds, "Unchanged inputs rebuilt the shape cache.");
                        report.cases++;
                        report.densitySamples += points.Length;
                    }

                    report.cacheReused = true;
                    report.compactTargetsSupported = SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.RGHalf);
                    renderer.skyDensityMultiplier = 1f;
                    renderer.worldOriginOffset = Vector3.zero;
                    Frame regular = Draw(renderer, camera);
                    renderer.useCompactTargets = true;
                    renderer.Resize(144, 96);
                    Frame compact = Draw(renderer, camera);
                    Compare(regular, compact, report);
                    int allocations = renderer.GetTargetAllocationCount();
                    renderer.Resize(144, 96);
                    Require(allocations == renderer.GetTargetAllocationCount(), "Unchanged compact targets reallocated.");
                    renderer.useCompactTargets = false;
                    renderer.Resize(144, 96);
                    Compare(regular, Draw(renderer, camera), report);
                    report.formatSwitchAllocations = renderer.GetTargetAllocationCount();
                    Require(report.formatSwitchAllocations == 3, "Unexpected render-target format lifecycle.");
                    Require(report.maximumDensityError < 0.0002f, "Density changed beyond float roundoff tolerance.");
                    Require(report.maximumTransmissionError < 0.002f, "Transmission changed beyond half precision tolerance.");
                    Require(report.maximumRadianceError < 0.005f, "Cloud radiance changed beyond tolerance.");
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
                Directory.CreateDirectory("Screenshots");
                File.WriteAllText("Screenshots/Optimization-Validation.json", JsonUtility.ToJson(report, true));
            }

            return report;
        }

        /// <summary>현재 프로젝트의 생산용 에셋을 읽습니다.</summary>
        private static T Load<T>(string relativePath) where T : UnityEngine.Object
        {
            return AssetDatabase.LoadAssetAtPath<T>("Assets/LostSkiesClouds/" + relativePath);
        }

        /// <summary>경계·음수 좌표·캐시 밖 월드 영역을 포함하는 재현 가능한 3D 표본을 만듭니다.</summary>
        private static Vector3[] SamplePoints(float spacing, Vector3 offset)
        {
            Vector3[] points = new Vector3[32768];
            System.Random random = new System.Random(1660);
            for (int index = 0; index < points.Length; index++)
            {
                float span = spacing * 5f;
                if (index % 4 == 0)
                {
                    span = spacing * 42f;
                }

                points[index] = offset + new Vector3(((float)random.NextDouble() - 0.5f) * span,
                    Mathf.Lerp(-1600f, 15000f, (float)random.NextDouble()),
                    ((float)random.NextDouble() - 0.5f) * span);
            }

            return points;
        }

        /// <summary>검증 전용 동기 readback으로 HDR 결과를 읽습니다. 이 시간은 성능 수치가 아닙니다.</summary>
        private static Frame Draw(LostSkiesCloudRenderer renderer, Camera camera)
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

        /// <summary>형식에 관계없이 RGBA float로 변환하여 검사합니다.</summary>
        private static Color[] Read(RenderTexture texture)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBAFloat);
            request.WaitForCompletion();
            Require(!request.hasError, "GPU readback failed.");
            return request.GetData<Color>().ToArray();
        }

        /// <summary>투과율 R, premultiplied RGB, opacity 가중 깊이를 서로 독립적으로 비교합니다.</summary>
        private static void Compare(Frame expected, Frame actual, Report report)
        {
            for (int index = 0; index < expected.lighting.Length; index++)
            {
                Color first = expected.lighting[index];
                Color second = actual.lighting[index];
                Require(IsFinite(second.r) && IsFinite(second.g) && IsFinite(second.b) && IsFinite(second.a), "Non-finite radiance.");
                float transmission = actual.transmission[index].r;
                Require(IsFinite(transmission) && transmission >= 0f && transmission <= 1f, "Invalid transmission.");
                report.maximumRadianceError = Mathf.Max(report.maximumRadianceError,
                    Mathf.Abs(first.r - second.r), Mathf.Abs(first.g - second.g), Mathf.Abs(first.b - second.b));
                report.maximumWeightedDepthError = Mathf.Max(report.maximumWeightedDepthError, Mathf.Abs(first.a - second.a));
                report.maximumTransmissionError = Mathf.Max(report.maximumTransmissionError,
                    Mathf.Abs(expected.transmission[index].r - transmission));
            }
        }

        /// <summary>GPU에서 돌아온 NaN과 무한대를 거부합니다.</summary>
        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>검수 실패 이유를 보고서에 남길 수 있도록 예외를 발생시킵니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
