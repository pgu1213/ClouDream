using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>운해 몸체 필드의 근사 오차와 연속 하부·동적 날씨·정확한 복귀 영역을 검증합니다.</summary>
    public static class CloudOceanShapeFieldValidation
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
            public bool continuousLowerOcean;
            public bool withinSuggestedImageBudget;
            public long bytes;
            public int initialCascadeBuilds;
            public float maximumNearDensityError;
            public float maximumOutsideDensityError;
            public float maximumDensityError;
            public double meanDensityError;
            public int originalDeepOccupied;
            public int lostDeepOccupied;
            public float maximumSnapBoundaryError;
            public uint[] sampledActualCalls;
            public string error;
            public CloudRayOptimizationValidation.Comparison[] comparisons;
        }

        /// <summary>상층 필드 상태를 고정하고 운해 캐시 하나만 바꿔 같은 카메라에서 비교합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Validate Ocean Shape Field Cache")]
        public static Report Run()
        {
            Report report = new Report();
            List<CloudRayOptimizationValidation.Comparison> comparisons = new List<CloudRayOptimizationValidation.Comparison>();
            GameObject host = new GameObject("Ocean shape field validation");
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
                    renderer.useShapeFieldCache = true;
                    renderer.useShapeFieldDensity = false;
                    renderer.useCacheDiagnostics = true;
                    renderer.useCompactTargets = true;
                    renderer.settings.coverageIntensity = 0.165f;
                    renderer.settings.density = 160000f;
                    renderer.shapeFieldExactNearDistance = 2500f;
                    renderer.Resize(320, 184);
                    report.withinSuggestedImageBudget = true;
                    int[] scenarios = { 0, 1, 3, 4, 6, 9 };
                    string[] names = { "home", "horizon", "inside", "negative-cell", "max-displacement", "low-sun" };
                    for (int index = 0; index < scenarios.Length; index++)
                    {
                        CloudRayOptimizationValidation.Configure(scenarios[index], camera, renderer, sky, style);
                        renderer.useOceanShapeFieldCache = false;
                        CloudRayOptimizationValidation.Frame reference = CloudRayOptimizationValidation.Draw(renderer, camera);
                        renderer.useOceanShapeFieldCache = true;
                        CloudRayOptimizationValidation.Frame cached = CloudRayOptimizationValidation.Draw(renderer, camera);
                        Require(renderer.OceanShapeFieldActive, "Ocean body field is unavailable.");
                        CloudRayOptimizationValidation.Comparison comparison = CloudRayOptimizationValidation.Compare(reference, cached);
                        comparison.scenario = names[index];
                        comparison.variant = "ocean-body-field";
                        comparisons.Add(comparison);
                        if (comparison.meanRgbError > 0.005 || comparison.maximumRgbError > 0.15f
                            || comparison.maximumTransmissionError > 0.1f)
                        {
                            report.withinSuggestedImageBudget = false;
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
                Directory.CreateDirectory("Screenshots/Redesign");
                File.WriteAllText("Screenshots/Redesign/OceanShapeField-Validation.json", JsonUtility.ToJson(report, true));
            }

            return report;
        }

        /// <summary>근거리·캐시 밖·운해 하부·원점 이동·중단된 날씨 전환의 실제 생산 밀도를 검사합니다.</summary>
        private static void CheckContracts(Report report, LostSkiesCloudRenderer renderer, Camera camera,
            CloudSkyProfile sky, CloudStyleProfile style)
        {
            CloudRayOptimizationValidation.Configure(0, camera, renderer, sky, style);
            renderer.skyDensityMultiplier = 0f;
            renderer.useOceanShapeFieldCache = true;
            CloudRayOptimizationValidation.Frame original = CloudRayOptimizationValidation.Draw(renderer, camera);
            report.bytes = renderer.OceanShapeFieldBytes;
            report.initialCascadeBuilds = renderer.OceanShapeFieldBuilds;
            report.sampledActualCalls = renderer.ReadCacheDiagnostics();
            Require(report.sampledActualCalls.Length >= 12 && report.sampledActualCalls[9] + report.sampledActualCalls[10] > 0,
                "Actual rays did not query the cached ocean body.");
            int builds = renderer.OceanShapeFieldBuilds;
            CloudRayOptimizationValidation.Draw(renderer, camera);
            report.stableBuilds = renderer.OceanShapeFieldBuilds == builds;
            Require(report.stableBuilds, "Unchanged ocean body rebuilt.");

            Vector3 wind = renderer.settings.baseOffset;
            renderer.settings.baseOffset += new Vector3(0.08f, 0f, -0.04f);
            renderer.settings.coverageIntensity = 0.24f;
            renderer.settings.density = 230000f;
            CloudRayOptimizationValidation.Draw(renderer, camera);
            report.weatherDidNotRebuild = renderer.OceanShapeFieldBuilds == builds;
            renderer.settings.baseOffset = wind;
            renderer.settings.coverageIntensity = 0.165f;
            renderer.settings.density = 160000f;
            CloudRayOptimizationValidation.Comparison restored = CloudRayOptimizationValidation.Compare(original,
                CloudRayOptimizationValidation.Draw(renderer, camera));
            report.environmentRestored = restored.maximumRgbError < 0.001f
                && restored.maximumTransmissionError < 0.0001f && restored.maximumDepthError < 0.1f;
            Require(report.weatherDidNotRebuild && report.environmentRestored, "Ocean weather restoration contract failed.");

            renderer.worldOriginOffset = new Vector3(128000f, 0f, -128000f);
            camera.transform.position -= renderer.worldOriginOffset;
            CloudRayOptimizationValidation.Comparison shifted = CloudRayOptimizationValidation.Compare(original,
                CloudRayOptimizationValidation.Draw(renderer, camera));
            report.originPreserved = shifted.maximumRgbError < 0.001f && shifted.maximumDepthError < 0.1f;
            Require(report.originPreserved, "Floating origin changed ocean fields.");
            camera.transform.position += renderer.worldOriginOffset;
            renderer.worldOriginOffset = Vector3.zero;
            camera.transform.position = new Vector3(0f, 900f, -3200f);
            CloudRayOptimizationValidation.Draw(renderer, camera);

            Vector3[] probes = CreateProbes(camera.transform.position);
            Vector2[] cached = renderer.ProbeDensity(probes, true);
            renderer.useOceanShapeFieldCache = false;
            CloudRayOptimizationValidation.Draw(renderer, camera);
            Vector2[] exact = renderer.ProbeDensity(probes, true);
            double total = 0;
            for (int index = 0; index < probes.Length; index++)
            {
                float error = Mathf.Abs(cached[index].x - exact[index].x);
                Require(!float.IsNaN(error) && !float.IsInfinity(error), "Non-finite ocean density.");
                report.maximumDensityError = Mathf.Max(report.maximumDensityError, error);
                total += error;
                if (index < 256)
                {
                    report.maximumNearDensityError = Mathf.Max(report.maximumNearDensityError, error);
                }
                else if (index < 512)
                {
                    report.maximumOutsideDensityError = Mathf.Max(report.maximumOutsideDensityError, error);
                }
                else if (index < 1601 && exact[index].x >= 0.99f)
                {
                    report.originalDeepOccupied++;
                    if (cached[index].x < 0.99f)
                    {
                        report.lostDeepOccupied++;
                    }
                }
            }

            report.meanDensityError = total / probes.Length;
            report.continuousLowerOcean = report.originalDeepOccupied >= 1000 && report.lostDeepOccupied == 0;
            Require(report.continuousLowerOcean, "Cached ocean introduced holes in the dense continuous lower layer.");
            Require(report.maximumNearDensityError < 0.00001f && report.maximumOutsideDensityError < 0.00001f,
                "Near or uncovered ocean field did not use the exact fallback.");

            renderer.useOceanShapeFieldCache = true;
            camera.transform.position = new Vector3(511.99f, 900f, -3200f);
            CloudRayOptimizationValidation.Draw(renderer, camera);
            Vector2[] before = renderer.ProbeDensity(probes, true);
            camera.transform.position = new Vector3(512.01f, 900f, -3200f);
            CloudRayOptimizationValidation.Draw(renderer, camera);
            Vector2[] after = renderer.ProbeDensity(probes, true);
            for (int index = 0; index < probes.Length; index++)
            {
                report.maximumSnapBoundaryError = Mathf.Max(report.maximumSnapBoundaryError, Mathf.Abs(before[index].x - after[index].x));
            }

            Require(report.maximumSnapBoundaryError < 0.01f, "Ocean density jumped at a clipmap window snap.");
        }

        /// <summary>근거리와 외부 복귀 영역, 33×33의 연속 하부와 전체 높이 표본을 함께 만듭니다.</summary>
        private static Vector3[] CreateProbes(Vector3 camera)
        {
            Vector3[] probes = new Vector3[9793];
            System.Random random = new System.Random(9751);
            for (int index = 0; index < probes.Length; index++)
            {
                float x = (float)random.NextDouble() * 2f - 1f;
                float z = (float)random.NextDouble() * 2f - 1f;
                float y = (float)random.NextDouble();
                if (index < 256)
                {
                    probes[index] = camera + new Vector3(x * 1000f, (y - 0.5f) * 1000f, z * 1000f);
                }
                else if (index < 512)
                {
                    probes[index] = new Vector3(250000f + x * 8000f, -1500f + y * 5000f, 250000f + z * 8000f);
                }
                else if (index < 1601)
                {
                    int grid = index - 512;
                    probes[index] = new Vector3((grid % 33 - 16) * 2600f, -1100f, (grid / 33 - 16) * 2600f);
                }
                else
                {
                    probes[index] = new Vector3(camera.x + x * 44000f, -1900f + y * 8500f, camera.z + z * 44000f);
                }
            }

            return probes;
        }

        /// <summary>구조 계약 실패를 시각 근사 예산과 별도로 기록합니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
