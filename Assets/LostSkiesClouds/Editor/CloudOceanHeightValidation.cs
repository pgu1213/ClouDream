using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>운해 높이 캐시의 정밀도와 수명·원점·경계·환경 재전환을 검사합니다.</summary>
    public static class CloudOceanHeightValidation
    {
        [Serializable]
        public sealed class Result
        {
            public float texelSize;
            public bool halfPrecision;
            public float maxHeightError;
            public double meanHeightError;
            public float maxMaskError;
            public float maxDensityError;
            public float maxWindowShiftHeightError;
            public long bytes;
            public bool cacheReused;
            public bool originPreserved;
            public bool parametersInvalidated;
            public bool environmentRestored;
            public CloudRayOptimizationValidation.Comparison image;
        }

        [Serializable]
        public sealed class Report
        {
            public bool completed;
            public bool contractsPassed;
            public string error;
            public Result[] results;
        }

        /// <summary>16/32/64m의 float 필드와 32m half 필드를 원래 절차식과 비교합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Validate Ocean Height Cache")]
        public static Report Run()
        {
            Report report = new Report();
            List<Result> results = new List<Result>();
            GameObject host = new GameObject("Ocean cache validation");
            host.hideFlags = HideFlags.HideAndDontSave;
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.aspect = 16f / 9f;
            camera.farClipPlane = 100000;
            CloudStyleProfile style = UnityEngine.Object.Instantiate(CloudRayOptimizationValidation.Load<CloudStyleProfile>("Presets/Style-ConceptV2.asset"));
            try
            {
                using (LostSkiesCloudRenderer renderer = new LostSkiesCloudRenderer(
                    CloudRayOptimizationValidation.Load<ComputeShader>("Runtime/CloudRaymarch.compute"),
                    CloudRayOptimizationValidation.Load<ComputeShader>("SourceShaders/CloudGenerator.asset"),
                    CloudRayOptimizationValidation.Load<TextAsset>("Presets/normal.json")))
                {
                    renderer.includeOcean = true;
                    renderer.oceanProfile = CloudRayOptimizationValidation.Load<CloudOceanProfile>("Presets/ContinuousOcean.asset");
                    renderer.skyProfile = CloudRayOptimizationValidation.Load<CloudSkyProfile>("Presets/Sky-ConceptV2.asset");
                    renderer.styleProfile = style;
                    renderer.useCompactTargets = true;
                    renderer.settings.coverageIntensity = 0.165f;
                    renderer.settings.density = 160000f;
                    renderer.skyLighting = CloudRayOptimizationValidation.Load<CloudLightingProfile>("Presets/TimeOfDay-ConceptV2.asset").Evaluate(12f);
                    renderer.Resize(320, 184);
                    float[] sizes = { 16, 32, 64, 32 };
                    for (int index = 0; index < sizes.Length; index++)
                    {
                        Result result = new Result();
                        result.texelSize = sizes[index];
                        result.halfPrecision = index == 3;
                        results.Add(result);
                        camera.transform.SetPositionAndRotation(new Vector3(1222, 3300, -6800), Quaternion.Euler(10, 15, 0));
                        renderer.worldOriginOffset = Vector3.zero;
                        renderer.useOceanHeightCache = false;
                        CloudRayOptimizationValidation.Frame reference = CloudRayOptimizationValidation.Draw(renderer, camera);
                        Vector3[] points = MakePoints(camera.transform.position, result.texelSize);
                        Vector2[] heights = renderer.ProbeOceanHeights(points);
                        for (int point = 0; point < points.Length; point++)
                        {
                            points[point].y = heights[point].x + (point % 33 - 16) * 12f;
                        }

                        Vector2[] expectedDensity = renderer.ProbeDensity(points);
                        renderer.useOceanHeightCache = true;
                        renderer.oceanHeightTexelSize = result.texelSize;
                        renderer.oceanHeightHalfPrecision = result.halfPrecision;
                        result.image = CloudRayOptimizationValidation.Compare(reference, CloudRayOptimizationValidation.Draw(renderer, camera));
                        result.image.scenario = "home";
                        result.bytes = renderer.OceanHeightBytes;
                        Require(renderer.OceanHeightActive, "Requested cache format was not supported.");
                        Vector2[] actual = renderer.ProbeOceanHeights(points);
                        Vector2[] density = renderer.ProbeDensity(points);
                        for (int point = 0; point < points.Length; point++)
                        {
                            float error = Mathf.Abs(heights[point].x - actual[point].x);
                            Require(!float.IsNaN(error) && !float.IsInfinity(error), "Non-finite height.");
                            result.maxHeightError = Mathf.Max(result.maxHeightError, error);
                            result.meanHeightError += error / points.Length;
                            result.maxMaskError = Mathf.Max(result.maxMaskError, Mathf.Abs(heights[point].y - actual[point].y));
                            result.maxDensityError = Mathf.Max(result.maxDensityError, Mathf.Abs(expectedDensity[point].x - density[point].x));
                        }

                        int builds = renderer.OceanHeightBuildCount;
                        CloudRayOptimizationValidation.Draw(renderer, camera);
                        result.cacheReused = builds == renderer.OceanHeightBuildCount;
                        Require(result.cacheReused, "Stable field rebuilt.");
                        renderer.worldOriginOffset = new Vector3(128000, 0, -128000);
                        camera.transform.position -= renderer.worldOriginOffset;
                        CloudRayOptimizationValidation.Draw(renderer, camera);
                        Vector2[] shiftedOrigin = renderer.ProbeOceanHeights(points);
                        result.originPreserved = builds == renderer.OceanHeightBuildCount;
                        for (int point = 0; point < points.Length; point++)
                        {
                            result.originPreserved &= Mathf.Abs(shiftedOrigin[point].x - actual[point].x) < 0.001f;
                        }
                        Require(result.originPreserved, "World-origin shift changed cached heights.");

                        // 상태가 달라졌다가 중간에 되돌아와도 정규화 필드는 현재 환경 값으로 복원되어야 합니다.
                        renderer.settings.coverageIntensity = 0.21f;
                        renderer.settings.density = 210000f;
                        style.oceanRelief += 250;
                        CloudRayOptimizationValidation.Draw(renderer, camera);
                        style.oceanRelief -= 250;
                        renderer.settings.coverageIntensity = 0.165f;
                        renderer.settings.density = 160000f;
                        CloudRayOptimizationValidation.Draw(renderer, camera);
                        Vector2[] restored = renderer.ProbeDensity(points);
                        result.environmentRestored = builds == renderer.OceanHeightBuildCount;
                        for (int point = 0; point < points.Length; point++)
                        {
                            result.environmentRestored &= Mathf.Abs(restored[point].x - density[point].x) < 0.0001f;
                        }
                        Require(result.environmentRestored, "Environment restoration changed the density contract.");

                        float direction = style.regionalFlowDegrees;
                        style.regionalFlowDegrees += 7;
                        CloudRayOptimizationValidation.Draw(renderer, camera);
                        style.regionalFlowDegrees = direction;
                        CloudRayOptimizationValidation.Draw(renderer, camera);
                        result.parametersInvalidated = renderer.OceanHeightBuildCount == builds + 2;
                        Require(result.parametersInvalidated, "Terrain parameters did not invalidate the cache.");
                        camera.transform.position += new Vector3(result.texelSize * 64, 0, 0);
                        CloudRayOptimizationValidation.Draw(renderer, camera);
                        Vector2[] moved = renderer.ProbeOceanHeights(points);
                        for (int point = 0; point < points.Length; point++)
                        {
                            result.maxWindowShiftHeightError = Mathf.Max(result.maxWindowShiftHeightError, Mathf.Abs(actual[point].x - moved[point].x));
                        }
                    }

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
                report.completed = true;
                report.results = results.ToArray();
                Directory.CreateDirectory("Screenshots");
                File.WriteAllText("Screenshots/OceanHeight-Validation.json", JsonUtility.ToJson(report, true));
            }

            return report;
        }

        /// <summary>음수 좌표와 창 경계·범위 밖을 포함하는 재현 가능한 검사 점을 만듭니다.</summary>
        private static Vector3[] MakePoints(Vector3 camera, float texelSize)
        {
            Vector3[] points = new Vector3[32768];
            System.Random random = new System.Random(1660);
            for (int index = 0; index < points.Length; index++)
            {
                points[index] = camera + new Vector3(((float)random.NextDouble() - 0.5f) * texelSize * 600,
                    0, ((float)random.NextDouble() - 0.5f) * texelSize * 600);
            }

            return points;
        }

        /// <summary>구조적 실패와 측정된 근사 오차를 구분해 기록합니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
