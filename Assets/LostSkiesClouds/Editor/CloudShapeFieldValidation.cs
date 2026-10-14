using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>새 희소 필드의 근사 오차를 기록하고 정확한 fallback·동적 밀도·원점 계약을 검사합니다.</summary>
    public static class CloudShapeFieldValidation
    {
        [Serializable]
        public sealed class Report
        {
            public bool completed;
            public bool contractsPassed;
            public bool stableBuilds;
            public bool weatherDidNotRebuild;
            public bool environmentRestored;
            public bool originPreserved;
            public bool withinSuggestedImageBudget;
            public bool lightingOnlyGeometryStrictPassed = true;
            public float maximumLightingOnlyProbeDifference;
            public float maximumNearDensityError;
            public float maximumOutsideDensityError;
            public float maximumProbeDensityError;
            public float meanProbeDensityError;
            public int nonzeroUpperSamples;
            public int cachedCells;
            public long bytes;
            public uint[] sampledActualCalls;
            public string error;
            public CloudRayOptimizationValidation.Comparison[] comparisons;
            public GeometryDifference[] geometry;
        }

        [Serializable]
        public sealed class GeometryDifference
        {
            public string scenario;
            public bool singlePrecision;
            public int changedTransmissionPixels;
            public int changedFirstHitPixels;
            public int changedWeightedDepthPixels;
            public int transmissionBeyondOneHalfStep;
            public int firstHitBeyondOneHalfStep;
            public int weightedDepthBeyondOneHalfStep;
            public float maximumTransmissionError;
            public float maximumFirstHitError;
            public float maximumWeightedDepthError;
            public int worstTransmissionPixel;
            public int worstFirstHitPixel;
            public int worstWeightedDepthPixel;
            public Vector4 exactWorstTransmission;
            public Vector4 cachedWorstTransmission;
            public Vector4 exactWorstFirstHit;
            public Vector4 cachedWorstFirstHit;
            public Vector4 exactWorstWeightedDepth;
            public Vector4 cachedWorstWeightedDepth;
        }

        /// <summary>동일한 입력으로 상세 필드와 조명 전용 필드를 비교합니다. readback 대기는 성능 수치가 아닙니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Validate Shape Field Cache")]
        public static Report Run()
        {
            Report report = new Report();
            List<CloudRayOptimizationValidation.Comparison> comparisons = new List<CloudRayOptimizationValidation.Comparison>();
            List<GeometryDifference> geometry = new List<GeometryDifference>();
            GameObject host = new GameObject("Cloud shape field validation");
            host.hideFlags = HideFlags.HideAndDontSave;
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.aspect = 16f / 9f;
            camera.fieldOfView = 68f;
            camera.farClipPlane = 100000f;
            CloudStyleProfile style = UnityEngine.Object.Instantiate(CloudRayOptimizationValidation.Load<CloudStyleProfile>("Presets/Style-ConceptV2.asset"));
            CloudSkyProfile sky = UnityEngine.Object.Instantiate(CloudRayOptimizationValidation.Load<CloudSkyProfile>("Presets/Sky-ConceptV2.asset"));
            try
            {
                using (LostSkiesCloudRenderer renderer = new LostSkiesCloudRenderer(
                    CloudRayOptimizationValidation.Load<ComputeShader>("Runtime/CloudRaymarch.compute"),
                    CloudRayOptimizationValidation.Load<ComputeShader>("SourceShaders/CloudGenerator.asset"),
                    CloudRayOptimizationValidation.Load<TextAsset>("Presets/normal.json")))
                {
                    renderer.includeOcean = true;
                    renderer.oceanProfile = CloudRayOptimizationValidation.Load<CloudOceanProfile>("Presets/ContinuousOcean.asset");
                    renderer.skyProfile = sky;
                    renderer.styleProfile = style;
                    renderer.useCompactTargets = true;
                    renderer.useCacheDiagnostics = true;
                    renderer.settings.coverageIntensity = 0.165f;
                    renderer.settings.density = 160000f;
                    renderer.shapeFieldExactNearDistance = 2500f;
                    renderer.Resize(320, 184);
                    int[] scenarios = { 0, 1, 3, 4, 6, 9 };
                    string[] names = { "home", "horizon", "inside", "negative-cell", "max-displacement", "low-sun" };
                    report.withinSuggestedImageBudget = true;
                    for (int index = 0; index < scenarios.Length; index++)
                    {
                        CloudRayOptimizationValidation.Configure(scenarios[index], camera, renderer, sky, style);
                        renderer.useShapeFieldCache = false;
                        CloudRayOptimizationValidation.Frame exact = CloudRayOptimizationValidation.Draw(renderer, camera);
                        CloudRayOptimizationValidation.Frame exactFloat = DrawFloat(renderer, camera);
                        Vector3[] fixedProbes = CreateProbes(camera.transform.position);
                        Vector2[] exactProbes = renderer.ProbeDensity(fixedProbes, true);
                        renderer.useShapeFieldCache = true;
                        for (int variant = 0; variant < 2; variant++)
                        {
                            renderer.useShapeFieldDensity = variant == 1;
                            CloudRayOptimizationValidation.Frame cached = CloudRayOptimizationValidation.Draw(renderer, camera);
                            Require(renderer.ShapeFieldActive, "Shape field cache is unavailable.");
                            CloudRayOptimizationValidation.Comparison comparison = CloudRayOptimizationValidation.Compare(exact, cached);
                            comparison.scenario = names[index];
                            comparison.variant = "lighting-only";
                            if (variant == 1)
                            {
                                comparison.variant = "render-and-lighting";
                            }

                            comparisons.Add(comparison);
                            if (comparison.meanRgbError > 0.005 || comparison.maximumRgbError > 0.15f
                                || comparison.maximumTransmissionError > 0.1f)
                            {
                                report.withinSuggestedImageBudget = false;
                            }

                            if (variant == 0)
                            {
                                GeometryDifference halfDifference = CompareGeometry(exact, cached, names[index], false);
                                geometry.Add(halfDifference);
                                geometry.Add(CompareGeometry(exactFloat, DrawFloat(renderer, camera), names[index], true));
                                Vector2[] actualProbes = renderer.ProbeDensity(fixedProbes, true);
                                for (int probe = 0; probe < actualProbes.Length; probe++)
                                {
                                    float difference = Mathf.Max(Mathf.Abs(actualProbes[probe].x - exactProbes[probe].x),
                                        Mathf.Abs(actualProbes[probe].y - exactProbes[probe].y));
                                    report.maximumLightingOnlyProbeDifference = Mathf.Max(report.maximumLightingOnlyProbeDifference, difference);
                                }

                                if (comparison.maximumTransmissionError >= 0.0001f || comparison.maximumDepthError >= 0.1f
                                    || halfDifference.maximumFirstHitError >= 0.1f)
                                {
                                    report.lightingOnlyGeometryStrictPassed = false;
                                }
                            }
                        }
                    }

                    CheckContracts(report, renderer, camera, sky, style);
                    report.contractsPassed = true;
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
                report.completed = true;
                report.comparisons = comparisons.ToArray();
                report.geometry = geometry.ToArray();
                Directory.CreateDirectory("Screenshots/Redesign");
                File.WriteAllText("Screenshots/Redesign/ShapeField-Validation.json", JsonUtility.ToJson(report, true));
            }

            return report;
        }

        /// <summary>같은 생산 셰이더를 float32 출력으로 다시 실행해 half 반올림과 실제 적분 오차를 구분합니다.</summary>
        private static CloudRayOptimizationValidation.Frame DrawFloat(LostSkiesCloudRenderer renderer, Camera camera)
        {
            CloudRayOptimizationValidation.Draw(renderer, camera);
            FieldInfo shaderField = typeof(LostSkiesCloudRenderer).GetField("renderer", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(shaderField != null, "Renderer shader field is unavailable for diagnostic dispatch.");
            ComputeShader shader = (ComputeShader)shaderField.GetValue(renderer);
            int kernel = shader.FindKernel("Raymarch");
            RenderTexture lighting = CreateFloatTarget(renderer.Width, renderer.Height, RenderTextureFormat.ARGBFloat);
            RenderTexture transmission = CreateFloatTarget(renderer.Width, renderer.Height, RenderTextureFormat.RGFloat);
            try
            {
                shader.SetTexture(kernel, "_LightingOut", lighting);
                shader.SetTexture(kernel, "_TransmittanceOut", transmission);
                shader.Dispatch(kernel, renderer.Width / 8, renderer.Height / 8, 1);
                CloudRayOptimizationValidation.Frame result = new CloudRayOptimizationValidation.Frame();
                result.lighting = ReadFloatTarget(lighting);
                result.transmission = ReadFloatTarget(transmission);
                return result;
            }
            finally
            {
                shader.SetTexture(kernel, "_LightingOut", renderer.lighting);
                shader.SetTexture(kernel, "_TransmittanceOut", renderer.transmittance);
                lighting.Release();
                transmission.Release();
                UnityEngine.Object.DestroyImmediate(lighting);
                UnityEngine.Object.DestroyImmediate(transmission);
            }
        }

        /// <summary>생산 출력과 같은 차원을 갖는 임시 단정밀도 진단 타깃을 만듭니다.</summary>
        private static RenderTexture CreateFloatTarget(int width, int height, RenderTextureFormat format)
        {
            RenderTexture target = new RenderTexture(width, height, 0, format);
            target.dimension = TextureDimension.Tex2DArray;
            target.volumeDepth = 1;
            target.enableRandomWrite = true;
            target.Create();
            return target;
        }

        /// <summary>진단용 출력만 동기 읽으며 이 시간은 성능 보고에 사용하지 않습니다.</summary>
        private static Color[] ReadFloatTarget(RenderTexture target)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(target, 0, TextureFormat.RGBAFloat);
            request.WaitForCompletion();
            Require(!request.hasError, "Float geometry diagnostic readback failed.");
            return request.GetData<Color>().ToArray();
        }

        /// <summary>투과율·첫 교차·opacity 가중 깊이를 따로 비교하고 가장 큰 오차의 원시 값을 남깁니다.</summary>
        private static GeometryDifference CompareGeometry(CloudRayOptimizationValidation.Frame exact,
            CloudRayOptimizationValidation.Frame cached, string scenario, bool singlePrecision)
        {
            GeometryDifference result = new GeometryDifference();
            result.scenario = scenario;
            result.singlePrecision = singlePrecision;
            for (int index = 0; index < exact.lighting.Length; index++)
            {
                float expectedTransmission = exact.transmission[index].r;
                float actualTransmission = cached.transmission[index].r;
                float expectedHit = exact.transmission[index].g;
                float actualHit = cached.transmission[index].g;
                float expectedWeighted = exact.lighting[index].a;
                float actualWeighted = cached.lighting[index].a;
                float transmissionError = Mathf.Abs(expectedTransmission - actualTransmission);
                float hitError = Mathf.Abs(expectedHit - actualHit);
                float weightedError = Mathf.Abs(expectedWeighted - actualWeighted);
                if (transmissionError > 0)
                {
                    result.changedTransmissionPixels++;
                }

                if (hitError > 0)
                {
                    result.changedFirstHitPixels++;
                }

                if (weightedError > 0)
                {
                    result.changedWeightedDepthPixels++;
                }

                if (transmissionError > HalfStep(expectedTransmission, actualTransmission))
                {
                    result.transmissionBeyondOneHalfStep++;
                }

                if (hitError > HalfStep(expectedHit, actualHit))
                {
                    result.firstHitBeyondOneHalfStep++;
                }

                if (weightedError > HalfStep(expectedWeighted, actualWeighted))
                {
                    result.weightedDepthBeyondOneHalfStep++;
                }

                if (transmissionError > result.maximumTransmissionError)
                {
                    result.maximumTransmissionError = transmissionError;
                    result.worstTransmissionPixel = index;
                    result.exactWorstTransmission = new Vector4(expectedTransmission, expectedHit, expectedWeighted, 0);
                    result.cachedWorstTransmission = new Vector4(actualTransmission, actualHit, actualWeighted, 0);
                }

                if (hitError > result.maximumFirstHitError)
                {
                    result.maximumFirstHitError = hitError;
                    result.worstFirstHitPixel = index;
                    result.exactWorstFirstHit = new Vector4(expectedTransmission, expectedHit, expectedWeighted, 0);
                    result.cachedWorstFirstHit = new Vector4(actualTransmission, actualHit, actualWeighted, 0);
                }

                if (weightedError > result.maximumWeightedDepthError)
                {
                    result.maximumWeightedDepthError = weightedError;
                    result.worstWeightedDepthPixel = index;
                    result.exactWorstWeightedDepth = new Vector4(expectedTransmission, expectedHit, expectedWeighted, 0);
                    result.cachedWorstWeightedDepth = new Vector4(actualTransmission, actualHit, actualWeighted, 0);
                }
            }

            return result;
        }

        /// <summary>두 값의 크기에서 binary16 한 단계 크기를 계산합니다. 허용 기준 변경 대신 오차 분류에만 씁니다.</summary>
        private static float HalfStep(float first, float second)
        {
            float magnitude = Mathf.Max(Mathf.Abs(first), Mathf.Abs(second));
            if (magnitude < 0.00006103515625f)
            {
                return 0.000000059604644775390625f;
            }

            return Mathf.Pow(2f, Mathf.Floor(Mathf.Log(magnitude, 2f)) - 10f);
        }

        /// <summary>실제 형태 조회와 가까운·외부 영역의 정확성, 바람·날씨와 독립적인 재사용을 검사합니다.</summary>
        private static void CheckContracts(Report report, LostSkiesCloudRenderer renderer, Camera camera,
            CloudSkyProfile sky, CloudStyleProfile style)
        {
            CloudRayOptimizationValidation.Configure(0, camera, renderer, sky, style);
            renderer.useShapeFieldCache = true;
            renderer.useShapeFieldDensity = true;
            CloudRayOptimizationValidation.Frame original = CloudRayOptimizationValidation.Draw(renderer, camera);
            report.sampledActualCalls = renderer.ReadCacheDiagnostics();
            report.bytes = renderer.ShapeFieldBytes;
            report.cachedCells = renderer.ShapeFieldCachedCells;
            int builds = renderer.ShapeFieldBuilds;
            CloudRayOptimizationValidation.Draw(renderer, camera);
            report.stableBuilds = renderer.ShapeFieldBuilds == builds;
            Require(report.stableBuilds, "Stationary fields rebuilt without a shape change.");
            Require(report.sampledActualCalls[1] + report.sampledActualCalls[2] > 0,
                "Actual rays did not read cached fields; an all-fallback test is invalid.");

            Vector3 wind = renderer.settings.baseOffset;
            renderer.settings.baseOffset += new Vector3(0.08f, 0f, -0.06f);
            renderer.settings.coverageIntensity = 0.24f;
            renderer.settings.density = 220000f;
            renderer.skyDensityMultiplier = 0.4f;
            CloudRayOptimizationValidation.Draw(renderer, camera);
            report.weatherDidNotRebuild = renderer.ShapeFieldBuilds == builds;
            renderer.settings.baseOffset = wind;
            renderer.settings.coverageIntensity = 0.165f;
            renderer.settings.density = 160000f;
            renderer.skyDensityMultiplier = 1f;
            CloudRayOptimizationValidation.Comparison restored = CloudRayOptimizationValidation.Compare(original,
                CloudRayOptimizationValidation.Draw(renderer, camera));
            report.environmentRestored = restored.maximumRgbError < 0.001f
                && restored.maximumTransmissionError < 0.0001f && restored.maximumDepthError < 0.1f;
            Require(report.weatherDidNotRebuild && report.environmentRestored,
                "Interrupted weather/wind changes invalidated immutable fields or left stale density.");

            renderer.worldOriginOffset = new Vector3(128000f, 0f, -128000f);
            camera.transform.position -= renderer.worldOriginOffset;
            CloudRayOptimizationValidation.Comparison shifted = CloudRayOptimizationValidation.Compare(original,
                CloudRayOptimizationValidation.Draw(renderer, camera));
            report.originPreserved = shifted.maximumRgbError < 0.001f && shifted.maximumDepthError < 0.1f;
            Require(report.originPreserved, "Floating-origin correction changed field lookup.");
            camera.transform.position += renderer.worldOriginOffset;
            renderer.worldOriginOffset = Vector3.zero;
            CloudRayOptimizationValidation.Draw(renderer, camera);

            Vector3[] probes = CreateProbes(camera.transform.position);
            Vector2[] cached = renderer.ProbeDensity(probes, true);
            renderer.useShapeFieldCache = false;
            CloudRayOptimizationValidation.Draw(renderer, camera);
            Vector2[] exact = renderer.ProbeDensity(probes, true);
            double totalError = 0;
            for (int index = 0; index < probes.Length; index++)
            {
                float difference = Mathf.Max(Mathf.Abs(cached[index].x - exact[index].x),
                    Mathf.Abs(cached[index].y - exact[index].y));
                Require(!float.IsNaN(difference) && !float.IsInfinity(difference), "Non-finite field density.");
                report.maximumProbeDensityError = Mathf.Max(report.maximumProbeDensityError, difference);
                totalError += difference;
                if (index < 256)
                {
                    report.maximumNearDensityError = Mathf.Max(report.maximumNearDensityError, difference);
                }
                else if (index < 512)
                {
                    report.maximumOutsideDensityError = Mathf.Max(report.maximumOutsideDensityError, difference);
                }

                if (exact[index].y > 0.01f)
                {
                    report.nonzeroUpperSamples++;
                }
            }

            report.meanProbeDensityError = (float)(totalError / probes.Length);
            Require(report.maximumNearDensityError < 0.00001f && report.maximumOutsideDensityError < 0.00001f,
                "Near or uncovered field lookup did not retain the exact fallback.");
            Require(report.nonzeroUpperSamples > 16, "Field probes did not contain enough occupied upper clouds.");
        }

        /// <summary>가까운 원본 영역·캐시 밖·점유 상층을 같은 고정 난수로 검사합니다.</summary>
        private static Vector3[] CreateProbes(Vector3 cameraPosition)
        {
            Vector3[] probes = new Vector3[8704];
            System.Random random = new System.Random(1973);
            for (int index = 0; index < probes.Length; index++)
            {
                float x = (float)random.NextDouble() * 2f - 1f;
                float z = (float)random.NextDouble() * 2f - 1f;
                float y = (float)random.NextDouble();
                if (index < 256)
                {
                    probes[index] = cameraPosition + new Vector3(x * 1000f, (y - 0.5f) * 1000f, z * 1000f);
                }
                else if (index < 512)
                {
                    probes[index] = cameraPosition + new Vector3(250000f + x * 8000f, y * 6000f, 250000f + z * 8000f);
                }
                else
                {
                    probes[index] = new Vector3(cameraPosition.x + x * 42000f, 3000f + y * 7000f,
                        cameraPosition.z + z * 42000f);
                }
            }

            return probes;
        }

        /// <summary>계약 위반을 정확한 원인과 함께 결과 파일에 남깁니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
