using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>공간 차폐의 근사 색 오차와 밀도·원점·환경·메모리 계약을 따로 검사합니다.</summary>
    public static class CloudSpatialLightValidation
    {
        [Serializable]
        public sealed class Result
        {
            public string scenario;
            public float cellSize;
            public CloudRayOptimizationValidation.Comparison image;
            public float cloudyPixelP95;
            public bool withinColorBudget;
        }

        [Serializable]
        public sealed class Report
        {
            public bool completed;
            public bool contractsPassed;
            public bool stableAllocations;
            public bool rebuildEveryRender;
            public bool originPreserved;
            public bool environmentRestored;
            public bool unsupportedFallback;
            public long bytes;
            public float maxDensityError;
            public float maxOutsideOcclusionError;
            public float maxWindowShiftOcclusionError;
            public float maxSnapBoundaryOcclusionError;
            public int nonZeroDensitySamples;
            public int nonZeroOcclusionSamples;
            public float maxGridVertexError;
            public string error;
            public Result[] results;
        }

        /// <summary>16/32/64m 격자를 같은 입력으로 비교합니다. 시각 근사 오차는 구조 검사 통과와 분리합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Validate Spatial Lighting")]
        public static Report Run()
        {
            Report report = new Report();
            List<Result> results = new List<Result>();
            GameObject host = new GameObject("Spatial light validation");
            host.hideFlags = HideFlags.HideAndDontSave;
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.aspect = 16f / 9f;
            camera.fieldOfView = 68;
            camera.farClipPlane = 100000;
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
                    renderer.settings.coverageIntensity = 0.165f;
                    renderer.settings.density = 160000f;
                    renderer.Resize(320, 184);
                    string[] names = { "home", "horizon", "above-down", "inside", "negative-cell", "axis-parallel",
                        "max-displacement", "origin-offset", "sky-disabled", "low-sun", "mixed-light", "bright-rim",
                        "night", "side", "inside-candidate" };
                    float[] sizes = { 16, 32, 64 };
                    for (int scenario = 0; scenario < names.Length; scenario++)
                    {
                        int configuration = scenario;
                        if (configuration > 11)
                        {
                            configuration = 0;
                        }

                        CloudRayOptimizationValidation.Configure(configuration, camera, renderer, sky, style);
                        if (scenario >= 12)
                        {
                            CloudLightingProfile lighting = CloudRayOptimizationValidation.Load<CloudLightingProfile>("Presets/TimeOfDay-ConceptV2.asset");
                            renderer.skyLighting = lighting.Evaluate(23f);
                            if (scenario > 12)
                            {
                                renderer.skyLighting = lighting.Evaluate(12f);
                                CloudStyleCapture.CameraPose pose = CloudStyleCapture.DescribePath("orbit").poses[26];
                                if (scenario == 14)
                                {
                                    pose = CloudStyleCapture.DescribePath("traverse").poses[60];
                                }

                                camera.transform.SetPositionAndRotation(pose.densityPosition, pose.worldRotation);
                            }
                        }

                        renderer.useSpatialLightCache = false;
                        CloudRayOptimizationValidation.Frame expected = CloudRayOptimizationValidation.Draw(renderer, camera);
                        foreach (float size in sizes)
                        {
                            renderer.useSpatialLightCache = true;
                            renderer.spatialLightCellSize = size;
                            CloudRayOptimizationValidation.Frame actual = CloudRayOptimizationValidation.Draw(renderer, camera);
                            Require(renderer.SpatialLightActive, "Spatial texture is unavailable.");
                            Result result = new Result();
                            result.scenario = names[scenario];
                            result.cellSize = size;
                            result.image = CloudRayOptimizationValidation.Compare(expected, actual);
                            result.cloudyPixelP95 = CloudyPixelP95(expected, actual);
                            result.withinColorBudget = result.image.maximumRgbError <= 0.03f
                                && result.image.meanRgbError <= 0.002 && result.cloudyPixelP95 <= 0.01f;
                            results.Add(result);
                            Require(result.image.maximumTransmissionError <= 0.0001f && result.image.maximumDepthError <= 0.1f,
                                "Spatial lighting changed cloud shape/transmittance: " + names[scenario]);
                        }
                    }

                    CheckContracts(report, renderer, camera, style, sky);
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
                UnityEngine.Object.DestroyImmediate(style);
                UnityEngine.Object.DestroyImmediate(sky);
                report.results = results.ToArray();
                report.completed = true;
                Directory.CreateDirectory("Screenshots");
                File.WriteAllText("Screenshots/SpatialLight-Validation.json", JsonUtility.ToJson(report, true));
            }

            return report;
        }

        /// <summary>원점 변경·날씨 중단 복구·프레임 갱신과 OFF의 메모리 반환을 생산 경로로 검사합니다.</summary>
        private static void CheckContracts(Report report, LostSkiesCloudRenderer renderer, Camera camera,
            CloudStyleProfile style, CloudSkyProfile sky)
        {
            CloudRayOptimizationValidation.Configure(0, camera, renderer, sky, style);
            renderer.spatialLightCellSize = 32;
            renderer.useSpatialLightCache = true;
            CloudRayOptimizationValidation.Frame original = CloudRayOptimizationValidation.Draw(renderer, camera);
            report.bytes = renderer.SpatialLightBytes;
            int allocations = renderer.SpatialLightAllocations;
            int builds = renderer.SpatialLightBuilds;
            CloudRayOptimizationValidation.Draw(renderer, camera);
            report.stableAllocations = allocations == renderer.SpatialLightAllocations;
            report.rebuildEveryRender = builds + 1 == renderer.SpatialLightBuilds;
            Require(report.stableAllocations && report.rebuildEveryRender, "Spatial allocation/update contract failed.");

            Vector3[] points = new Vector3[4096];
            System.Random random = new System.Random(1660);
            for (int index = 0; index < points.Length; index++)
            {
                points[index] = new Vector3((float)random.NextDouble() * 32000 - 16000,
                    (float)random.NextDouble() * 14000 - 1600, (float)random.NextDouble() * 32000 - 16000);
            }

            Vector2[] densities = renderer.ProbeDensity(points, true);
            foreach (Vector2 density in densities)
            {
                if (density.x > 0.01f)
                {
                    report.nonZeroDensitySamples++;
                }
            }

            // 모든 캐스케이드에서 정확히 노드에 놓인 점은 보간 오차 없이 직접 계산과 일치해야 합니다.
            Vector3[] vertices = new Vector3[4096];
            for (int index = 0; index < vertices.Length; index++)
            {
                vertices[index] = new Vector3((index % 16 - 8) * 512, (index / 256 - 2) * 512,
                    (index / 16 % 16 - 20) * 512);
            }

            Vector2[] cachedVertices = renderer.ProbeOcclusion(vertices);
            renderer.useSpatialLightCache = false;
            CloudRayOptimizationValidation.Draw(renderer, camera);
            Vector2[] exactVertices = renderer.ProbeOcclusion(vertices);
            foreach (Vector2 value in exactVertices)
            {
                if (value.x > 1 || value.y > 1)
                {
                    report.nonZeroOcclusionSamples++;
                }
            }

            report.maxGridVertexError = MaximumDifference(cachedVertices, exactVertices);
            Require(report.nonZeroDensitySamples > 100 && report.nonZeroOcclusionSamples > 100,
                "Probe must contain occupied cloud samples, not an all-zero field.");
            Require(report.maxGridVertexError < 0.05f, "Grid vertices do not match analytic occlusion.");
            renderer.useSpatialLightCache = true;
            CloudRayOptimizationValidation.Draw(renderer, camera);
            renderer.worldOriginOffset = new Vector3(128000, 0, -128000);
            camera.transform.position -= renderer.worldOriginOffset;
            CloudRayOptimizationValidation.Comparison shifted = CloudRayOptimizationValidation.Compare(original,
                CloudRayOptimizationValidation.Draw(renderer, camera));
            report.originPreserved = shifted.maximumRgbError < 0.001f && shifted.maximumDepthError < 0.1f;
            Require(report.originPreserved, "World-origin correction changed cached lighting.");

            CloudLightingState light = renderer.skyLighting;
            Vector3 wind = renderer.settings.baseOffset;
            float flow = style.regionalFlowDegrees;
            renderer.skyLighting.sunElevation = 2;
            renderer.settings.density = 240000;
            renderer.settings.coverageIntensity = 0.22f;
            renderer.skyDensityMultiplier = 0.3f;
            renderer.settings.baseOffset += new Vector3(0.03f, 0, 0);
            style.regionalFlowDegrees += 17;
            CloudRayOptimizationValidation.Draw(renderer, camera);
            renderer.skyLighting = light;
            renderer.settings.density = 160000;
            renderer.settings.coverageIntensity = 0.165f;
            renderer.skyDensityMultiplier = 1;
            renderer.settings.baseOffset = wind;
            style.regionalFlowDegrees = flow;
            CloudRayOptimizationValidation.Comparison restored = CloudRayOptimizationValidation.Compare(original,
                CloudRayOptimizationValidation.Draw(renderer, camera));
            report.environmentRestored = restored.maximumRgbError < 0.001f && restored.maximumDepthError < 0.1f;
            Require(report.environmentRestored, "Interrupted environment changes left stale lighting.");

            // 운해 안에서 격자 창의 경계를 넘깁니다. 빈 공중의 0값만 비교하는 검사를 피합니다.
            CloudRayOptimizationValidation.Configure(3, camera, renderer, sky, style);
            CloudRayOptimizationValidation.Draw(renderer, camera);
            Vector3[] edgePoints = new Vector3[4096];
            for (int index = 0; index < edgePoints.Length; index++)
            {
                edgePoints[index] = new Vector3(-1017 + index % 16 * 16,
                    -200 + index / 256 * 80, -4100 + index / 16 % 16 * 100);
            }

            Vector2[] before = renderer.ProbeOcclusion(edgePoints);
            camera.transform.position += new Vector3(256, 0, 0);
            CloudRayOptimizationValidation.Draw(renderer, camera);
            Vector2[] after = renderer.ProbeOcclusion(edgePoints);
            report.maxWindowShiftOcclusionError = MaximumDifference(before, after);
            camera.transform.position = new Vector3(255.99f, 900, -3200);
            CloudRayOptimizationValidation.Draw(renderer, camera);
            before = renderer.ProbeOcclusion(edgePoints);
            camera.transform.position = new Vector3(256.01f, 900, -3200);
            CloudRayOptimizationValidation.Draw(renderer, camera);
            after = renderer.ProbeOcclusion(edgePoints);
            report.maxSnapBoundaryOcclusionError = MaximumDifference(before, after);
            Require(report.maxSnapBoundaryOcclusionError < 0.1f, "Grid snap introduced a discontinuous lighting change.");

            Vector3[] outside = new Vector3[64];
            for (int index = 0; index < outside.Length; index++)
            {
                outside[index] = new Vector3(90000 + index * 600, 1000 + index * 80, 80000);
            }

            Vector2[] cachedOutside = renderer.ProbeOcclusion(outside);
            renderer.useSpatialLightCache = false;
            CloudRayOptimizationValidation.Draw(renderer, camera);
            report.maxOutsideOcclusionError = MaximumDifference(cachedOutside, renderer.ProbeOcclusion(outside));
            report.maxDensityError = MaximumDifference(densities, renderer.ProbeDensity(points, true));
            Require(report.maxOutsideOcclusionError < 0.001f && report.maxDensityError < 0.0001f,
                "Outside fallback or density preservation failed.");
            Require(renderer.SpatialLightBytes == 0 && !renderer.SpatialLightActive, "OFF kept the spatial volume allocated.");
            renderer.useSpatialLightCache = true;
            style.method = CloudStyleProfile.ShapeMethod.LayeredBillows;
            CloudRayOptimizationValidation.Draw(renderer, camera);
            report.unsupportedFallback = !renderer.SpatialLightActive && renderer.SpatialLightBytes == 0;
            Require(report.unsupportedFallback, "Unsupported style did not fall back to exact lighting.");
        }

        /// <summary>배경이 평균을 낮추지 않도록 실제 구름 픽셀의 최대 채널 오차 p95를 구합니다.</summary>
        private static float CloudyPixelP95(CloudRayOptimizationValidation.Frame expected, CloudRayOptimizationValidation.Frame actual)
        {
            List<float> errors = new List<float>();
            for (int index = 0; index < expected.lighting.Length; index++)
            {
                if (expected.transmission[index].r < 0.999f)
                {
                    Color difference = expected.lighting[index] - actual.lighting[index];
                    errors.Add(Mathf.Max(Mathf.Abs(difference.r), Mathf.Abs(difference.g), Mathf.Abs(difference.b)));
                }
            }

            if (errors.Count == 0)
            {
                return 0;
            }

            errors.Sort();
            return errors[Mathf.Clamp(Mathf.CeilToInt(errors.Count * 0.95f) - 1, 0, errors.Count - 1)];
        }

        /// <summary>같은 위치의 밀도·차폐량 차이와 유한성을 검사합니다.</summary>
        private static float MaximumDifference(Vector2[] expected, Vector2[] actual)
        {
            float maximum = 0;
            for (int index = 0; index < expected.Length; index++)
            {
                Vector2 difference = expected[index] - actual[index];
                float error = Mathf.Max(Mathf.Abs(difference.x), Mathf.Abs(difference.y));
                Require(!float.IsNaN(error) && !float.IsInfinity(error), "Non-finite probe result.");
                maximum = Mathf.Max(maximum, error);
            }

            return maximum;
        }

        /// <summary>구조적 실패를 근사 품질 수치와 구분하여 보고합니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
