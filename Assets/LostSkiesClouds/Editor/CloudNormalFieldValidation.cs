using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>법선 생산 보존·동적 무효화·정확 복귀와 별도의 최종 영상 오차를 기록합니다.</summary>
    public static class CloudNormalFieldValidation
    {
        [Serializable]
        public sealed class Report
        {
            public bool completed;
            public bool contractsPassed;
            public bool imageBudgetPassed;
            public bool geometryStrictPassed;
            public bool stableBuilds;
            public bool lightingDidNotRebuild;
            public bool weatherRebuilt;
            public bool windRebuilt;
            public bool windReversalRebuilt;
            public bool originPreserved;
            public bool windowRebuilt;
            public bool disabledReleased;
            public bool unsupportedFallback;
            public bool otherFieldsBypassed;
            public bool noiseLibraryReady;

            public int noisePreparationsAfterFirstDraw;

            public int noiseGenerations;

            public string noiseSource;
            public bool actualCachedCallsObserved;
            public int validGridNodes;
            public float maximumGridComponentError;
            public float maximumNearNormalError;
            public float maximumOutsideNormalError;
            public float maximumProbeDensityError;
            public float maximumNormalAngle;
            public int normalValidityMismatches;
            public long bytes;
            public int allocations;
            public uint[] sampledCalls;
            public uint[] lastCombinedCalls;
            public ScenarioCalls[] scenarioCalls;
            public string error;
            public string notes = "Normals use the original 25m detailed differences, macro differences and profile blend. "
                + "Grid nodes are compared to exact production inputs. Image acceptance is separate from contracts. "
                + "Mean RGB <=0.005, maximum RGB <=0.15, maximum transmission <=0.0001, weighted depth <=0.1m. "
                + "These budgets may fail; failure keeps this independent experimental feature OFF. Readback time is not GPU timing.";
            public CloudRayOptimizationValidation.Comparison[] images;
        }

        [Serializable]
        public sealed class ScenarioCalls
        {
            public string scenario;
            public uint[] counts;
            public int cumulativeCascadeBuilds;
        }

        public static Report LastReport { get; private set; }

        /// <summary>동일 입력의 실제 렌더와 생산 법선을 비교하고 실패도 원자료에 저장합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Validate Normal Result Cache")]
        public static Report Run()
        {
            Report report = new Report();
            LastReport = report;
            List<CloudRayOptimizationValidation.Comparison> images = new List<CloudRayOptimizationValidation.Comparison>();
            List<ScenarioCalls> calls = new List<ScenarioCalls>();
            report.sampledCalls = new uint[4];
            GameObject host = new GameObject("Cloud normal field validation");
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
                ValidateNoiseLibrary();
                report.noiseLibraryReady = true;
                using (LostSkiesCloudRenderer renderer = Create(style, sky))
                {
                    report.imageBudgetPassed = true;
                    report.geometryStrictPassed = true;
                    int[] scenarios = { 0, 1, 3, 4, 6, 11 };
                    string[] names = { "home", "horizon", "inside", "negative-cell", "max-displacement", "bright-rim" };
                    for (int index = 0; index < scenarios.Length; index++)
                    {
                        CloudRayOptimizationValidation.Configure(scenarios[index], camera, renderer, sky, style);
                        renderer.useNormalFieldCache = false;
                        CloudRayOptimizationValidation.Frame reference = CloudRayOptimizationValidation.Draw(renderer, camera);
                        if (index == 0)
                        {
                            report.noisePreparationsAfterFirstDraw = renderer.NoisePreparationCount;
                            report.noiseGenerations = renderer.NoiseGenerationCount;
                            report.noiseSource = renderer.NoiseSource;
                            Require(report.noisePreparationsAfterFirstDraw == 6 && report.noiseGenerations == 0,
                                "Normal validation did not use exactly six preserved noise resources.");
                            Debug.Log("Cloud normal validation noise: preparations=" + report.noisePreparationsAfterFirstDraw
                                + ", generator dispatches=" + report.noiseGenerations + ", source=" + report.noiseSource);
                        }

                        Vector3[] positions = QueryPoints(camera.transform.position);
                        Vector2[] density = renderer.ProbeDensity(positions, true);
                        renderer.useNormalFieldCache = true;
                        CloudRayOptimizationValidation.Frame candidate = CloudRayOptimizationValidation.Draw(renderer, camera);
                        Require(renderer.NormalFieldActive, "Supported normal field was not active.");
                        uint[] scenarioCounts = renderer.ReadNormalFieldDiagnostics();
                        ValidateCounterPartition(scenarioCounts);
                        calls.Add(new ScenarioCalls
                        {
                            scenario = names[index],
                            counts = scenarioCounts,
                            cumulativeCascadeBuilds = renderer.NormalFieldBuilds
                        });
                        for (int counter = 0; counter < scenarioCounts.Length; counter++)
                        {
                            report.sampledCalls[counter] = checked(report.sampledCalls[counter] + scenarioCounts[counter]);
                        }

                        CloudRayOptimizationValidation.Comparison image = CloudRayOptimizationValidation.Compare(reference, candidate);
                        image.scenario = names[index];
                        image.variant = "normal-result-cache";
                        images.Add(image);
                        report.imageBudgetPassed &= image.meanRgbError <= 0.005 && image.maximumRgbError <= 0.15f;
                        report.geometryStrictPassed &= image.maximumTransmissionError <= 0.0001f && image.maximumDepthError <= 0.1f;
                        Vector2[] actualDensity = renderer.ProbeDensity(positions, true);
                        for (int point = 0; point < positions.Length; point++)
                        {
                            report.maximumProbeDensityError = Mathf.Max(report.maximumProbeDensityError,
                                (actualDensity[point] - density[point]).magnitude);
                        }

                        MeasureNormals(report, renderer, positions);
                        ValidateGrid(report, renderer);
                    }

                    report.actualCachedCallsObserved = report.sampledCalls[1] > 0 || report.sampledCalls[2] > 0;
                    Require(report.sampledCalls[0] > 0 && report.actualCachedCallsObserved,
                        "No actual ray normal lookup used the cache. A fallback-only image match does not validate the feature.");

                    CloudRayOptimizationValidation.Configure(0, camera, renderer, sky, style);
                    CloudRayOptimizationValidation.Draw(renderer, camera);
                    int builds = renderer.NormalFieldBuilds;
                    int allocations = renderer.NormalFieldAllocations;
                    CloudRayOptimizationValidation.Draw(renderer, camera);
                    report.stableBuilds = renderer.NormalFieldBuilds == builds
                        && renderer.NormalFieldAllocations == allocations;
                    Require(report.stableBuilds, "Unchanged inputs rebuilt or reallocated the normal field.");

                    CloudLightingState light = renderer.skyLighting;
                    light.cloudMidColor = Color.red;
                    renderer.skyLighting = light;
                    CloudRayOptimizationValidation.Draw(renderer, camera);
                    report.lightingDidNotRebuild = renderer.NormalFieldBuilds == builds;
                    Require(report.lightingDidNotRebuild, "Light color unnecessarily rebuilt normals.");

                    renderer.settings.coverageIntensity += 0.025f;
                    CloudRayOptimizationValidation.Draw(renderer, camera);
                    report.weatherRebuilt = renderer.NormalFieldBuilds == builds + 3;
                    Require(report.weatherRebuilt, "Weather change retained old normals.");
                    ValidateGrid(report, renderer);

                    Vector3 wind = renderer.settings.baseOffset;
                    builds = renderer.NormalFieldBuilds;
                    renderer.settings.baseOffset += new Vector3(0.002f, 0, -0.003f);
                    CloudRayOptimizationValidation.Draw(renderer, camera);
                    report.windRebuilt = renderer.NormalFieldBuilds == builds + 3;
                    Require(report.windRebuilt, "Wind change retained old normals.");
                    ValidateGrid(report, renderer);
                    builds = renderer.NormalFieldBuilds;
                    renderer.settings.baseOffset = wind;
                    CloudRayOptimizationValidation.Draw(renderer, camera);
                    report.windReversalRebuilt = renderer.NormalFieldBuilds == builds + 3;
                    Require(report.windReversalRebuilt, "Wind reversal retained the wrong prior input.");

                    builds = renderer.NormalFieldBuilds;
                    Vector3 cameraPosition = camera.transform.position;
                    Vector3[] beforePoints = QueryPoints(cameraPosition);
                    Vector4[] beforeNormals = renderer.ProbeNormals(beforePoints);
                    Vector3 offset = new Vector3(10000, 0, -20000);
                    renderer.worldOriginOffset = offset;
                    camera.transform.position = cameraPosition - offset;
                    CloudRayOptimizationValidation.Draw(renderer, camera);
                    Vector4[] afterNormals = renderer.ProbeNormals(beforePoints);
                    report.originPreserved = renderer.NormalFieldBuilds == builds && MaximumVectorError(beforeNormals, afterNormals) <= 0.00001f;
                    Require(report.originPreserved, "World-origin compensation changed the world normal field.");

                    camera.transform.position += Vector3.right * 10000f;
                    CloudRayOptimizationValidation.Draw(renderer, camera);
                    report.windowRebuilt = renderer.NormalFieldBuilds > builds;
                    Require(report.windowRebuilt, "Moved grid window was not rebuilt.");

                    // 다른 근사 필드가 켜져도 노드 생성은 정확한 밀도를 직접 참조해야 합니다.
                    renderer.useShapeFieldCache = true;
                    renderer.useShapeFieldDensity = true;
                    renderer.useOceanShapeFieldCache = true;
                    renderer.useOceanHeightCache = true;
                    renderer.settings.coverageIntensity += 0.001f;
                    CloudRayOptimizationValidation.Draw(renderer, camera);
                    ValidateGrid(report, renderer);
                    report.otherFieldsBypassed = report.maximumGridComponentError <= 0.001f;
                    Require(report.otherFieldsBypassed, "Normal nodes were contaminated by approximate shape/height fields.");

                    report.bytes = renderer.NormalFieldBytes;
                    report.allocations = renderer.NormalFieldAllocations;
                    report.lastCombinedCalls = renderer.ReadNormalFieldDiagnostics();
                    ValidateCounterPartition(report.lastCombinedCalls);

                    renderer.useNormalFieldCache = false;
                    CloudRayOptimizationValidation.Draw(renderer, camera);
                    report.disabledReleased = !renderer.NormalFieldActive && renderer.NormalFieldBytes == 0;
                    Require(report.disabledReleased, "Disabling normals retained the large field texture.");
                    renderer.useNormalFieldCache = true;
                    style.method = CloudStyleProfile.ShapeMethod.SoftNoise;
                    CloudRayOptimizationValidation.Draw(renderer, camera);
                    report.unsupportedFallback = !renderer.NormalFieldActive && renderer.NormalFieldBytes == 0;
                    Require(report.unsupportedFallback, "Unsupported style activated the Concept normal cache.");

                    Require(report.validGridNodes > 0, "All sampled normal grid nodes were invalid.");
                    Require(report.maximumProbeDensityError == 0, "Normal-only option changed fixed-point density.");
                    Require(report.maximumNearNormalError <= 0.00001f && report.maximumOutsideNormalError <= 0.00001f,
                        "Near or outside normal fallback differs from the exact producer.");
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
                report.images = images.ToArray();
                report.scenarioCalls = calls.ToArray();
                report.completed = true;
                Directory.CreateDirectory("Screenshots");
                File.WriteAllText("Screenshots/NormalField-Validation.json", JsonUtility.ToJson(report, true));
            }

            return report;
        }

        /// <summary>직렬화 생성기 대신 실제 렌더 입력인 여섯 공유 텍스처 계약을 검사합니다.</summary>
        private static void ValidateNoiseLibrary()
        {
            CloudNoiseLibrary.LoadAll();
        }

        /// <summary>각 실제 렌더의 query/full/blend/exact가 빠짐없이 분할되는지 검사합니다.</summary>
        private static void ValidateCounterPartition(uint[] counts)
        {
            Require(counts != null && counts.Length == 4, "Normal counter layout is not query/full/blend/exact.");
            ulong outcomes = (ulong)counts[1] + counts[2] + counts[3];
            Require((ulong)counts[0] == outcomes, "Normal counters do not partition actual calls.");
        }

        /// <summary>독립 법선 옵션만 켜고 끌 수 있는 공통 비교 렌더러를 생성합니다.</summary>
        private static LostSkiesCloudRenderer Create(CloudStyleProfile style, CloudSkyProfile sky)
        {
            LostSkiesCloudRenderer renderer = new LostSkiesCloudRenderer(
                CloudRayOptimizationValidation.Load<ComputeShader>("Runtime/CloudRaymarch.compute"),
                null,
                CloudRayOptimizationValidation.Load<TextAsset>("Presets/normal.json"));
            renderer.includeOcean = true;
            renderer.oceanProfile = CloudRayOptimizationValidation.Load<CloudOceanProfile>("Presets/ContinuousOcean.asset");
            renderer.styleProfile = style;
            renderer.skyProfile = sky;
            renderer.useCompactTargets = true;
            renderer.useNormalFieldDiagnostics = true;
            renderer.settings.coverageIntensity = 0.165f;
            renderer.settings.density = 160000f;
            renderer.Resize(160, 96);
            return renderer;
        }

        /// <summary>근접·원경·범위 밖을 포함하는 결정적인 월드 검사 위치를 만듭니다.</summary>
        private static Vector3[] QueryPoints(Vector3 camera)
        {
            List<Vector3> points = new List<Vector3>();
            points.Add(camera + Vector3.right * 100f);
            points.Add(camera + Vector3.forward * 2000f);
            points.Add(camera + Vector3.one * 300000f);
            for (int z = -6; z <= 6; z++)
            {
                for (int x = -6; x <= 6; x++)
                {
                    points.Add(camera + new Vector3(x * 1700f, -700f, z * 1700f));
                }
            }

            return points.ToArray();
        }

        /// <summary>근접/범위 밖의 정확 복귀와 원경의 최대 방향 오차를 따로 기록합니다.</summary>
        private static void MeasureNormals(Report report, LostSkiesCloudRenderer renderer, Vector3[] positions)
        {
            Vector4[] exact = renderer.ProbeNormals(positions, 1);
            Vector4[] cached = renderer.ProbeNormals(positions);
            for (int index = 0; index < positions.Length; index++)
            {
                Vector3 reference = new Vector3(exact[index].x, exact[index].y, exact[index].z);
                Vector3 actual = new Vector3(cached[index].x, cached[index].y, cached[index].z);
                Require(IsFinite(reference.x) && IsFinite(reference.y) && IsFinite(reference.z), "Nonfinite exact normal producer.");
                Require(IsFinite(actual.x) && IsFinite(actual.y) && IsFinite(actual.z), "Nonfinite cached normal.");
                Require(IsFinite(cached[index].w) && cached[index].w >= 0f && cached[index].w <= 1f,
                    "Normal cache weight is not finite or outside [0,1].");
                bool referenceValid = reference.sqrMagnitude > 0.5f;
                bool actualValid = actual.sqrMagnitude > 0.5f;
                if (referenceValid != actualValid)
                {
                    report.normalValidityMismatches++;
                }

                if (referenceValid && actualValid)
                {
                    report.maximumNormalAngle = Mathf.Max(report.maximumNormalAngle, Vector3.Angle(reference, actual));
                }

                if (index < 2)
                {
                    report.maximumNearNormalError = Mathf.Max(report.maximumNearNormalError, (reference - actual).magnitude);
                }
                else if (index == 2)
                {
                    report.maximumOutsideNormalError = Mathf.Max(report.maximumOutsideNormalError, (reference - actual).magnitude);
                }
            }
        }

        /// <summary>각 격자 정점의 저장 법선이 정확 생산 결과의 half 정밀도 오차 이내인지 검사합니다.</summary>
        private static void ValidateGrid(Report report, LostSkiesCloudRenderer renderer)
        {
            Vector4[] regions = renderer.GetNormalFieldRegions();
            for (int cascade = 0; cascade < regions.Length; cascade++)
            {
                Vector4 region = regions[cascade];
                List<Vector3> points = new List<Vector3>();
                for (int z = 4; z <= 28; z += 4)
                {
                    for (int y = 4; y <= 28; y += 4)
                    {
                        for (int x = 4; x <= 28; x += 4)
                        {
                            points.Add(new Vector3(region.x + x * region.w,
                                region.y + y * region.w, region.z + z * region.w));
                        }
                    }
                }

                Vector3[] positions = points.ToArray();
                Vector4[] exact = renderer.ProbeNormals(positions, 1);
                Vector4[] nodes = renderer.ProbeNormals(positions, 2, cascade);
                for (int index = 0; index < positions.Length; index++)
                {
                    Require(IsFinite(nodes[index].x) && IsFinite(nodes[index].y) && IsFinite(nodes[index].z)
                        && IsFinite(nodes[index].w), "Nonfinite stored normal node.");
                    Require(IsFinite(exact[index].x) && IsFinite(exact[index].y) && IsFinite(exact[index].z),
                        "Nonfinite exact normal at a grid vertex.");
                    if (nodes[index].w > 0.5f)
                    {
                        report.validGridNodes++;
                    }

                    float difference = Mathf.Max(Mathf.Abs(nodes[index].x - exact[index].x),
                        Mathf.Max(Mathf.Abs(nodes[index].y - exact[index].y), Mathf.Abs(nodes[index].z - exact[index].z)));
                    report.maximumGridComponentError = Mathf.Max(report.maximumGridComponentError, difference);
                }
            }

            Require(report.maximumGridComponentError <= 0.001f, "Grid producer differs from exact finite differences.");
        }

        /// <summary>원점 보상 전후의 방향과 캐시 가중치를 함께 비교합니다.</summary>
        private static float MaximumVectorError(Vector4[] left, Vector4[] right)
        {
            float maximum = 0f;
            for (int index = 0; index < left.Length; index++)
            {
                maximum = Mathf.Max(maximum, (left[index] - right[index]).magnitude);
            }

            return maximum;
        }

        /// <summary>지원 런타임의 공통 float API로 비유한 법선을 거절합니다.</summary>
        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>계약 실패를 예외로 기록하되 최종 영상의 근사 예산과 구분합니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
