using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>조명 연산 생략을 원래 출력과 비교하며 혼합·비지원 경로와 토글 복귀를 검사합니다.</summary>
    public static class CloudPaletteLightingValidation
    {
        [Serializable]
        public sealed class Report
        {
            public bool passed;
            public string referenceShader;
            public bool stableAllocations;
            public float maximumFirstHitError;
            public int cloudPixels;
            public string error;
            public CloudRayOptimizationValidation.Comparison[] comparisons;
        }

        /// <summary>현재 셰이더의 OFF를 기준으로 ON과 다시 OFF의 출력을 검사합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Validate Palette Lighting")]
        public static Report Run()
        {
            return RunAgainst(null);
        }

        /// <summary>도입 시 보관한 변경 전 셰이더가 있으면 그것을 독립 기준으로 사용합니다.</summary>
        public static Report RunAgainst(ComputeShader referenceShader)
        {
            Report report = new Report();
            List<CloudRayOptimizationValidation.Comparison> comparisons = new List<CloudRayOptimizationValidation.Comparison>();
            ComputeShader production = CloudRayOptimizationValidation.Load<ComputeShader>("Runtime/CloudRaymarch.compute");
            if (referenceShader == null)
            {
                referenceShader = production;
            }

            report.referenceShader = AssetDatabase.GetAssetPath(referenceShader);
            GameObject host = new GameObject("Palette lighting validation");
            host.hideFlags = HideFlags.HideAndDontSave;
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.aspect = 16f / 9f;
            camera.fieldOfView = 68f;
            camera.farClipPlane = 100000f;
            CloudSkyProfile sky = UnityEngine.Object.Instantiate(CloudRayOptimizationValidation.Load<CloudSkyProfile>("Presets/Sky-ConceptV2.asset"));
            CloudStyleProfile style = UnityEngine.Object.Instantiate(CloudRayOptimizationValidation.Load<CloudStyleProfile>("Presets/Style-ConceptV2.asset"));
            try
            {
                using (LostSkiesCloudRenderer baseline = CreateRenderer(referenceShader, sky, style))
                using (LostSkiesCloudRenderer candidate = CreateRenderer(production, sky, style))
                {
                    string[] names = { "home", "horizon", "above-down", "inside", "negative-cell", "axis-parallel",
                        "max-displacement", "origin-offset", "sky-disabled", "low-sun", "mixed-half", "bright-rim",
                        "physical-only", "almost-palette", "inactive-lighting", "night", "soft-noise", "layered-billows",
                        "clamped-palette", "shadow-and-spatial" };
                    for (int format = 0; format < 2; format++)
                    {
                        baseline.useCompactTargets = format == 1;
                        candidate.useCompactTargets = format == 1;
                        baseline.Resize(320, 184);
                        candidate.Resize(320, 184);
                        for (int index = 0; index < names.Length; index++)
                        {
                            Configure(index, camera, baseline, sky, style);
                            Configure(index, camera, candidate, sky, style);
                            baseline.usePaletteLightingFastPath = false;
                            CloudRayOptimizationValidation.Frame reference = CloudRayOptimizationValidation.Draw(baseline, camera);
                            for (int pixel = 0; pixel < reference.transmission.Length; pixel++)
                            {
                                if (reference.transmission[pixel].r < 0.99f)
                                {
                                    report.cloudPixels++;
                                }
                            }

                            // OFF / ON / OFF로 생략 경로뿐 아니라 동일 렌더러의 복귀도 검사합니다.
                            int allocations = -1;
                            for (int state = 0; state < 3; state++)
                            {
                                candidate.usePaletteLightingFastPath = state == 1;
                                CloudRayOptimizationValidation.Frame actual = CloudRayOptimizationValidation.Draw(candidate, camera);
                                CloudRayOptimizationValidation.Comparison result = CloudRayOptimizationValidation.Compare(reference, actual);
                                result.scenario = names[index];
                                result.variant = "format=" + format + "; state=" + state;
                                comparisons.Add(result);
                                Require(result.maximumRgbError <= 0.001f, "Radiance changed: " + result.scenario);
                                Require(result.maximumTransmissionError == 0, "Transmission changed: " + result.scenario);
                                Require(result.maximumDepthError == 0, "Depth moment changed: " + result.scenario);
                                for (int pixel = 0; pixel < reference.transmission.Length; pixel++)
                                {
                                    float expectedHit = reference.transmission[pixel].a;
                                    float actualHit = actual.transmission[pixel].a;
                                    if (format == 1)
                                    {
                                        expectedHit = reference.transmission[pixel].g;
                                        actualHit = actual.transmission[pixel].g;
                                    }

                                    report.maximumFirstHitError = Mathf.Max(report.maximumFirstHitError, Mathf.Abs(expectedHit - actualHit));
                                }

                                if (allocations >= 0)
                                {
                                    Require(candidate.GetTargetAllocationCount() == allocations, "Lighting toggle reallocated render targets.");
                                }

                                allocations = candidate.GetTargetAllocationCount();
                            }
                        }
                    }

                    Require(report.cloudPixels > 1000, "No meaningful cloud samples were compared.");
                    Require(report.maximumFirstHitError == 0, "First cloud intersection changed.");
                    report.stableAllocations = true;
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
                File.WriteAllText("Screenshots/PaletteLighting-Validation.json", JsonUtility.ToJson(report, true));
            }

            return report;
        }

        /// <summary>기준과 후보에 같은 프로필·밀도·노이즈 입력을 설정합니다.</summary>
        private static LostSkiesCloudRenderer CreateRenderer(ComputeShader shader, CloudSkyProfile sky, CloudStyleProfile style)
        {
            LostSkiesCloudRenderer renderer = new LostSkiesCloudRenderer(shader,
                CloudRayOptimizationValidation.Load<ComputeShader>("SourceShaders/CloudGenerator.asset"),
                CloudRayOptimizationValidation.Load<TextAsset>("Presets/normal.json"));
            renderer.includeOcean = true;
            renderer.oceanProfile = CloudRayOptimizationValidation.Load<CloudOceanProfile>("Presets/ContinuousOcean.asset");
            renderer.skyProfile = sky;
            renderer.styleProfile = style;
            renderer.settings.coverageIntensity = 0.165f;
            renderer.settings.density = 160000f;
            return renderer;
        }

        /// <summary>기초 조건을 매번 초기화하고 혼합 경계와 비지원 모드를 추가로 검사합니다.</summary>
        private static void Configure(int index, Camera camera, LostSkiesCloudRenderer renderer, CloudSkyProfile sky, CloudStyleProfile style)
        {
            int baseCase = index;
            if (index >= 12)
            {
                baseCase = 0;
            }

            CloudRayOptimizationValidation.Configure(baseCase, camera, renderer, sky, style);
            renderer.useRayIntervals = false;
            renderer.useShadowTermination = index == 19;
            renderer.useSpatialLightCache = index == 19;
            CloudLightingState light = renderer.skyLighting;
            if (index == 12)
            {
                light.cloudPaletteBlend = 0;
            }
            else if (index == 13)
            {
                light.cloudPaletteBlend = 0.9999f;
            }
            else if (index == 14)
            {
                light.active = false;
            }
            else if (index == 15)
            {
                light = CloudLightingState.MoonlitNight();
            }
            else if (index == 16)
            {
                style.method = CloudStyleProfile.ShapeMethod.SoftNoise;
            }
            else if (index == 17)
            {
                style.method = CloudStyleProfile.ShapeMethod.LayeredBillows;
            }
            else if (index == 18)
            {
                light.cloudPaletteBlend = 1.1f;
            }

            renderer.skyLighting = light;
        }

        /// <summary>검사 실패 원인을 보고서에 남길 수 있도록 예외로 전달합니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
