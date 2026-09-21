using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>스타일 밀도장의 GPU 안정성과 기존 원점·조명·버퍼 계약을 검사합니다. 시각적 유사성을 증명하지는 않습니다.</summary>
    public static class CloudStyleValidation
    {
        private const string StyleAssetPath = "Assets/LostSkiesClouds/Presets/Style-Layered.asset";
        private const string SkyAssetPath = "Assets/LostSkiesClouds/Presets/Sky-Concept.asset";
        private const float Tolerance = 0.0001f;
        private const int GridResolution = 16;

        [Serializable]
        public sealed class CellMeasurement
        {
            public Vector2Int cell;
            public int occupiedSamples;
            public Vector3 center;
            public Vector3 measuredSize;
        }

        [Serializable]
        public sealed class Report
        {
            public bool passed;
            public string scope = "GPU density, lighting independence, world-origin and resource contracts. Visual similarity and motion quality require separate visual review.";
            public string styleAsset = StyleAssetPath;
            public string skyAsset = SkyAssetPath;

            public bool finiteGpuValues;
            public bool continuousOcean;
            public int downwardViewCount;
            public float maximumOceanTransmission;
            public float fixedInputRenderDifference;
            public float[] downwardMaximumTransmission;

            public int sampledPoints;
            public int occupiedCells;
            public int emptyCells;
            public float skyVolumeFraction;
            public float occupiedWidthVariation;
            public float occupiedHeightVariation;
            public float occupiedAltitudeVariation;
            public CellMeasurement[] cells;
            public float repeatedDensityDifference;
            public bool disabledSkyHasNoDensity;
            public bool zeroSkyDensityHasNoDensity;
            public bool ribbonsIncludedInDisableCheck;
            public float maximumRibbonContribution;
            public int boundarySampleCount;
            public float maximumBoundarySkyDensity;
            public bool cellBoundariesClear;

            public bool coverageChangesDensity;
            public float coverageDensityDifference;
            public float lowCoverageMaximumTransmission;
            public float highCoverageMaximumTransmission;
            public bool coveragePreservesOcean;

            public float originCoordinateDifference;
            public float originDensityDifference;
            public float originRenderDifference;
            public bool worldOriginPreserved;

            public float lightingTransmissionDifference;
            public float lightingRadianceDifference;
            public bool lightingPreservesGeometry;

            public int cameraTargetAllocations;
            public int initialNoiseGenerations;
            public int firstStyledNoiseGenerations;
            public int noiseGenerations;
            public bool styleNoiseCreatedOnce;
            public bool cameraTargetsReused;
            public string error;
        }

        private sealed class GpuFrame
        {
            public Color[] lighting;
            public Color[] transmission;
        }

        /// <summary>최종 스타일 에셋을 실제 GPU로 검사하고 수치와 실패 원인을 JSON에 기록합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Validate Cloud Surface Style")]
        public static Report Run()
        {
            Report report = new Report();
            GameObject firstHost = new GameObject("Cloud style validation camera A");
            GameObject secondHost = new GameObject("Cloud style validation camera B");
            firstHost.hideFlags = HideFlags.HideAndDontSave;
            secondHost.hideFlags = HideFlags.HideAndDontSave;
            CloudSkyProfile sky = null;

            try
            {
                Camera firstCamera = PrepareCamera(firstHost);
                Camera secondCamera = PrepareCamera(secondHost);
                ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/LostSkiesClouds/Runtime/CloudRaymarch.compute");
                ComputeShader noise = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/LostSkiesClouds/SourceShaders/CloudGenerator.asset");
                TextAsset preset = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/LostSkiesClouds/Presets/normal.json");
                CloudStyleProfile style = AssetDatabase.LoadAssetAtPath<CloudStyleProfile>(StyleAssetPath);
                CloudSkyProfile savedSky = AssetDatabase.LoadAssetAtPath<CloudSkyProfile>(SkyAssetPath);
                CloudOceanProfile ocean = AssetDatabase.LoadAssetAtPath<CloudOceanProfile>("Assets/LostSkiesClouds/Presets/ContinuousOcean.asset");
                Require(shader != null && noise != null && preset != null && style != null && savedSky != null && ocean != null,
                    "최종 스타일 GPU 검사에 필요한 에셋이 없습니다.");
                Require(style.method == CloudStyleProfile.ShapeMethod.LayeredBillows, "최종 에셋이 LayeredBillows 스타일을 사용하지 않습니다.");
                sky = UnityEngine.Object.Instantiate(savedSky);
                sky.hideFlags = HideFlags.HideAndDontSave;

                using (LostSkiesCloudRenderer renderer = new LostSkiesCloudRenderer(shader, noise, preset))
                {
                    renderer.includeOcean = true;
                    renderer.oceanProfile = ocean;
                    renderer.skyProfile = sky;
                    renderer.styleProfile = style;
                    renderer.settings.coverageIntensity = 0.165f;
                    renderer.settings.density = 160000f;
                    renderer.skyLighting = CloudLightingState.Day();
                    renderer.Resize(96, 96, 4101);
                    report.initialNoiseGenerations = renderer.NoiseGenerationCount;
                    Require(report.initialNoiseGenerations == 5, "스타일을 렌더하기 전에 기존 공용 노이즈 다섯 개가 준비되어야 합니다.");

                    CheckOcean(renderer, firstCamera, ocean, report);
                    report.firstStyledNoiseGenerations = renderer.NoiseGenerationCount;
                    Require(report.firstStyledNoiseGenerations == 6, "스타일 최초 렌더에서 조형 노이즈 하나만 추가되어야 합니다.");
                    firstCamera.transform.SetPositionAndRotation(new Vector3(1222f, 3300f, -6800f), Quaternion.Euler(10f, 15f, 0f));
                    Draw(renderer, firstCamera);

                    Vector3[] positions = BuildDensitySamples(sky);
                    Vector2[] originalDensity = renderer.ProbeDensity(positions);
                    CheckDensityValues(originalDensity);
                    CheckDistribution(renderer, firstCamera, sky, positions, originalDensity, report);
                    CheckBoundaries(renderer, firstCamera, sky, report);
                    CheckCoverage(renderer, firstCamera, ocean, style, report);
                    CheckOrigin(renderer, firstCamera, firstHost, positions, originalDensity, report);
                    CheckLighting(renderer, firstCamera, report);
                    CheckCameraReuse(renderer, firstCamera, secondCamera, report);

                    report.noiseGenerations = renderer.NoiseGenerationCount;
                    report.styleNoiseCreatedOnce = report.firstStyledNoiseGenerations == 6 && report.noiseGenerations == 6;
                    Require(report.styleNoiseCreatedOnce, "시각·프로필·원점·카메라 변경 중 스타일 공용 노이즈가 다시 생성되었습니다.");
                    report.finiteGpuValues = true;
                }

                report.passed = true;
            }
            catch (Exception exception)
            {
                report.error = exception.ToString();
                Debug.LogException(exception);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sky);
                UnityEngine.Object.DestroyImmediate(firstHost);
                UnityEngine.Object.DestroyImmediate(secondHost);
                Directory.CreateDirectory("Screenshots");
                File.WriteAllText("Screenshots/Style-Validation.json", JsonUtility.ToJson(report, true));
            }

            return report;
        }

        /// <summary>사용자 카메라를 변경하지 않고 생산용 GPU 광선에 필요한 독립 카메라를 준비합니다.</summary>
        private static Camera PrepareCamera(GameObject host)
        {
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 100000f;
            camera.fieldOfView = 68f;
            camera.aspect = 1f;
            return camera;
        }

        /// <summary>서로 떨어진 네 영역을 수직으로 내려다보며 빈 운해 픽셀과 동일 입력의 변화를 검사합니다.</summary>
        private static void CheckOcean(LostSkiesCloudRenderer renderer, Camera camera, CloudOceanProfile ocean, Report report)
        {
            float height = ocean.bottom + Mathf.Max(100f, ocean.thickness) + 2400f;
            Vector2[] locations =
            {
                new Vector2(1222f, -6800f),
                new Vector2(-17000f, 9000f),
                new Vector2(23500f, 17000f),
                new Vector2(-24000f, -19500f)
            };

            report.downwardMaximumTransmission = new float[locations.Length];
            for (int view = 0; view < locations.Length; view++)
            {
                camera.transform.SetPositionAndRotation(new Vector3(locations[view].x, height, locations[view].y), Quaternion.Euler(90f, 0f, 0f));
                GpuFrame first = Draw(renderer, camera);
                GpuFrame repeat = Draw(renderer, camera);
                float maximum = MaximumTransmission(first.transmission);
                report.downwardMaximumTransmission[view] = maximum;
                report.maximumOceanTransmission = Mathf.Max(report.maximumOceanTransmission, maximum);
                report.fixedInputRenderDifference = Mathf.Max(report.fixedInputRenderDifference, MaximumFrameDifference(first, repeat));
                report.downwardViewCount++;
            }

            report.continuousOcean = report.maximumOceanTransmission < 0.03f;
            Require(report.continuousOcean, "네 하향 화면 중 운해를 3% 이상 투과하는 빈 영역이 있습니다.");
            Require(report.fixedInputRenderDifference == 0f, "고정 입력의 생산용 GPU 출력이 반복 렌더에서 달라졌습니다.");
        }

        /// <summary>카메라와 무관한 4×4 셀 전체를 일정 간격으로 탐색할 밀도장 좌표를 만듭니다.</summary>
        private static Vector3[] BuildDensitySamples(CloudSkyProfile sky)
        {
            float spacing = Mathf.Max(4000f, sky.spacing);
            Vector2 altitude = CloudSkyProfile.OrderedRange(sky.altitude, -100000f);
            Vector2 verticalRadius = CloudSkyProfile.OrderedRange(sky.verticalRadius, 100f);
            float minimumHeight = altitude.x - verticalRadius.y;
            float maximumHeight = altitude.y + verticalRadius.y;
            List<Vector3> positions = new List<Vector3>(16 * GridResolution * GridResolution * GridResolution);
            for (int cellZ = -2; cellZ < 2; cellZ++)
            {
                for (int cellX = -2; cellX < 2; cellX++)
                {
                    for (int y = 0; y < GridResolution; y++)
                    {
                        for (int z = 0; z < GridResolution; z++)
                        {
                            for (int x = 0; x < GridResolution; x++)
                            {
                                positions.Add(new Vector3((cellX + (x + 0.5f) / GridResolution) * spacing,
                                    Mathf.Lerp(minimumHeight, maximumHeight, y / (float)(GridResolution - 1)),
                                    (cellZ + (z + 0.5f) / GridResolution) * spacing));
                            }
                        }
                    }
                }
            }

            return positions.ToArray();
        }

        /// <summary>실측 밀도의 상층 유무와 크기·중심 높이 변화를 확인하고 상층 비활성화도 검사합니다.</summary>
        private static void CheckDistribution(LostSkiesCloudRenderer renderer, Camera camera, CloudSkyProfile sky, Vector3[] positions, Vector2[] original, Report report)
        {
            Vector2[] repeated = renderer.ProbeDensity(positions);
            CheckDensityValues(repeated);
            report.sampledPoints = positions.Length;
            report.repeatedDensityDifference = MaximumDensityDifference(original, repeated);
            Require(report.repeatedDensityDifference == 0f, "동일 좌표의 밀도가 반복 조회에서 달라졌습니다.");

            int pointsPerCell = GridResolution * GridResolution * GridResolution;
            int occupiedSamples = 0;
            float smallestWidth = float.PositiveInfinity;
            float largestWidth = 0f;
            float smallestHeight = float.PositiveInfinity;
            float largestHeight = 0f;
            float lowestCenter = float.PositiveInfinity;
            float highestCenter = float.NegativeInfinity;
            report.cells = new CellMeasurement[16];
            for (int cell = 0; cell < report.cells.Length; cell++)
            {
                CellMeasurement measurement = new CellMeasurement();
                measurement.cell = new Vector2Int(cell % 4 - 2, cell / 4 - 2);
                Bounds bounds = new Bounds();
                bool found = false;
                for (int point = cell * pointsPerCell; point < (cell + 1) * pointsPerCell; point++)
                {
                    // 프로브 x는 운해를 포함합니다. 상층 분포는 별도 출력인 y만 사용합니다.
                    if (original[point].y <= 0.01f)
                    {
                        continue;
                    }

                    measurement.occupiedSamples++;
                    occupiedSamples++;
                    if (!found)
                    {
                        bounds = new Bounds(positions[point], Vector3.zero);
                        found = true;
                    }

                    bounds.Encapsulate(positions[point]);
                }

                if (found)
                {
                    report.occupiedCells++;
                    measurement.center = bounds.center;
                    measurement.measuredSize = bounds.size;
                    smallestWidth = Mathf.Min(smallestWidth, bounds.size.x);
                    largestWidth = Mathf.Max(largestWidth, bounds.size.x);
                    smallestHeight = Mathf.Min(smallestHeight, bounds.size.y);
                    largestHeight = Mathf.Max(largestHeight, bounds.size.y);
                    lowestCenter = Mathf.Min(lowestCenter, bounds.center.y);
                    highestCenter = Mathf.Max(highestCenter, bounds.center.y);
                }
                else
                {
                    report.emptyCells++;
                }

                report.cells[cell] = measurement;
            }

            Require(report.occupiedCells >= 2 && report.emptyCells >= 1, "검사 영역에 비교할 상층 구름 두 개와 빈 셀이 함께 있어야 합니다.");
            report.skyVolumeFraction = occupiedSamples / (float)positions.Length;
            report.occupiedWidthVariation = largestWidth - smallestWidth;
            report.occupiedHeightVariation = largestHeight - smallestHeight;
            report.occupiedAltitudeVariation = highestCenter - lowestCenter;

            // 샘플 격자의 한 칸 이상 차이를 요구합니다. 미세 부동소수점 차이를 형태 변화로 세지 않습니다.
            float horizontalStep = Mathf.Max(4000f, sky.spacing) / GridResolution;
            Vector2 altitude = CloudSkyProfile.OrderedRange(sky.altitude, -100000f);
            Vector2 radius = CloudSkyProfile.OrderedRange(sky.verticalRadius, 100f);
            float verticalStep = (altitude.y - altitude.x + radius.y * 2f) / (GridResolution - 1);
            Require(report.occupiedWidthVariation + Tolerance >= horizontalStep, "상층 구름의 가로 크기 변화가 검사 격자에서 관찰되지 않았습니다.");
            Require(report.occupiedHeightVariation + Tolerance >= verticalStep, "상층 구름의 세로 크기 변화가 검사 격자에서 관찰되지 않았습니다.");
            Require(report.occupiedAltitudeVariation + Tolerance >= verticalStep, "상층 구름의 중심 고도 변화가 검사 격자에서 관찰되지 않았습니다.");

            Vector2[] upperWithRibbons = renderer.ProbeDensity(positions, true);
            CheckDensityValues(upperWithRibbons);
            for (int point = 0; point < original.Length; point++)
            {
                report.maximumRibbonContribution = Mathf.Max(report.maximumRibbonContribution, upperWithRibbons[point].y - original[point].y);
            }

            report.ribbonsIncludedInDisableCheck = report.maximumRibbonContribution > 0.01f;
            Require(report.ribbonsIncludedInDisableCheck, "비활성화 검사 좌표에서 실제 띠구름 밀도를 관측하지 못했습니다.");

            float savedOccupancy = sky.occupiedCells;
            float savedDensity = sky.density;
            try
            {
                sky.occupiedCells = 0f;
                // 프로필 변경을 Render를 통해 적용하여 실제 게임과 동일한 GPU 계약을 검사합니다.
                Draw(renderer, camera);

                Vector2[] disabled = renderer.ProbeDensity(positions, true);
                CheckDensityValues(disabled);
                report.disabledSkyHasNoDensity = MaximumSkyDensity(disabled) == 0f;
                Require(report.disabledSkyHasNoDensity, "상층 생성 확률이 0인데 타워 또는 띠구름 밀도가 남았습니다.");

                sky.occupiedCells = savedOccupancy;
                sky.density = 0f;
                Draw(renderer, camera);
                Vector2[] zeroDensity = renderer.ProbeDensity(positions, true);
                CheckDensityValues(zeroDensity);
                report.zeroSkyDensityHasNoDensity = MaximumSkyDensity(zeroDensity) == 0f;
                Require(report.zeroSkyDensityHasNoDensity, "상층 밀도 설정이 0인데 타워 또는 띠구름 밀도가 남았습니다.");
            }
            finally
            {
                sky.occupiedCells = savedOccupancy;
                sky.density = savedDensity;
            }
        }

        /// <summary>날씨의 coverage 변화가 조형 운해에 반영되면서 낮은 생성량에서도 연속 바닥이 유지되는지 검사합니다.</summary>
        private static void CheckCoverage(LostSkiesCloudRenderer renderer, Camera camera, CloudOceanProfile ocean, CloudStyleProfile style, Report report)
        {
            CloudFormationProfile savedSky = renderer.skyProfile;
            float savedCoverage = renderer.settings.coverageIntensity;
            Vector3 savedPosition = camera.transform.position;
            Quaternion savedRotation = camera.transform.rotation;
            List<Vector3> samples = new List<Vector3>();
            float spacing = Mathf.Max(200f, style.oceanLobeSize);
            float thickness = Mathf.Max(100f, ocean.thickness);
            for (int y = 0; y < GridResolution; y++)
            {
                for (int z = 0; z < GridResolution; z++)
                {
                    for (int x = 0; x < GridResolution; x++)
                    {
                        samples.Add(new Vector3(Mathf.Lerp(-spacing * 2f, spacing * 2f, x / (float)(GridResolution - 1)),
                            Mathf.Lerp(ocean.bottom + thickness * 0.1f, ocean.bottom + thickness + 1800f, y / (float)(GridResolution - 1)),
                            Mathf.Lerp(-spacing * 2f, spacing * 2f, z / (float)(GridResolution - 1))));
                    }
                }
            }

            Vector3[] positions = samples.ToArray();
            try
            {
                // 상층이 운해의 변화를 가리지 않도록 동일 생산용 경로에서 상층 프로필만 잠시 제외합니다.
                renderer.skyProfile = null;
                camera.transform.SetPositionAndRotation(new Vector3(1222f, ocean.bottom + thickness + 2400f, -6800f), Quaternion.Euler(90f, 0f, 0f));
                renderer.settings.coverageIntensity = 0.065f;
                GpuFrame lowFrame = Draw(renderer, camera);
                Vector2[] lowDensity = renderer.ProbeDensity(positions);
                CheckDensityValues(lowDensity);

                renderer.settings.coverageIntensity = 0.265f;
                GpuFrame highFrame = Draw(renderer, camera);
                Vector2[] highDensity = renderer.ProbeDensity(positions);
                CheckDensityValues(highDensity);
                report.coverageDensityDifference = MaximumDensityDifference(lowDensity, highDensity);
                report.coverageChangesDensity = report.coverageDensityDifference > 0.01f;
                Require(report.coverageChangesDensity, "날씨의 coverage 값을 변경해도 조형 운해 밀도가 변하지 않았습니다.");

                report.lowCoverageMaximumTransmission = MaximumTransmission(lowFrame.transmission);
                report.highCoverageMaximumTransmission = MaximumTransmission(highFrame.transmission);
                report.coveragePreservesOcean = report.lowCoverageMaximumTransmission < 0.03f
                    && report.highCoverageMaximumTransmission < 0.03f;
                Require(report.coveragePreservesOcean, "coverage 변경으로 연속 운해 바닥에 빈 영역이 생겼습니다.");
            }
            finally
            {
                renderer.skyProfile = savedSky;
                renderer.settings.coverageIntensity = savedCoverage;
                camera.transform.SetPositionAndRotation(savedPosition, savedRotation);
                Draw(renderer, camera);
            }
        }

        /// <summary>상층 높이 전 구간의 셀 경계 양쪽에서 상층 전용 밀도만 검사합니다.</summary>
        private static void CheckBoundaries(LostSkiesCloudRenderer renderer, Camera camera, CloudSkyProfile sky, Report report)
        {
            Draw(renderer, camera);

            float spacing = Mathf.Max(4000f, sky.spacing);
            Vector2 altitude = CloudSkyProfile.OrderedRange(sky.altitude, -100000f);
            Vector2 radius = CloudSkyProfile.OrderedRange(sky.verticalRadius, 100f);
            List<Vector3> edges = new List<Vector3>();
            for (int boundary = -2; boundary <= 2; boundary++)
            {
                for (int along = -32; along <= 32; along++)
                {
                    for (int y = 0; y < GridResolution; y++)
                    {
                        float height = Mathf.Lerp(altitude.x - radius.y, altitude.y + radius.y, y / (float)(GridResolution - 1));
                        float otherAxis = along * spacing / GridResolution;
                        edges.Add(new Vector3(boundary * spacing + 1f, height, otherAxis));
                        edges.Add(new Vector3(boundary * spacing - 1f, height, otherAxis));
                        edges.Add(new Vector3(otherAxis, height, boundary * spacing + 1f));
                        edges.Add(new Vector3(otherAxis, height, boundary * spacing - 1f));
                    }
                }
            }

            Vector2[] density = renderer.ProbeDensity(edges.ToArray());
            CheckDensityValues(density);
            report.boundarySampleCount = edges.Count;
            report.maximumBoundarySkyDensity = MaximumSkyDensity(density);
            report.cellBoundariesClear = report.maximumBoundarySkyDensity == 0f;
            Require(report.cellBoundariesClear, "셀 경계에서 상층 구름이 잘릴 수 있는 밀도가 검출되었습니다.");
        }

        /// <summary>씬 이동과 역방향 샘플 오프셋을 함께 적용하여 밀도와 렌더 출력을 비교합니다.</summary>
        private static void CheckOrigin(LostSkiesCloudRenderer renderer, Camera camera, GameObject host, Vector3[] positions, Vector2[] original, Report report)
        {
            GpuFrame before = Draw(renderer, camera);
            CloudWorldOrigin origin = host.AddComponent<CloudWorldOrigin>();
            Vector3 originalPosition = camera.transform.position;
            Vector3 translation = new Vector3(-32000f, -1200f, 17000f);
            camera.transform.position += translation;
            origin.ApplySceneTranslation(translation);
            renderer.worldOriginOffset = origin.SamplingOffset;
            try
            {
                GpuFrame after = Draw(renderer, camera);
                report.originRenderDifference = MaximumFrameDifference(before, after);
                Vector3[] shiftedSamples = new Vector3[positions.Length];
                for (int point = 0; point < positions.Length; point++)
                {
                    shiftedSamples[point] = origin.ToCloudPosition(positions[point] + translation);
                    report.originCoordinateDifference = Mathf.Max(report.originCoordinateDifference, Vector3.Distance(positions[point], shiftedSamples[point]));
                }

                Vector2[] shiftedDensity = renderer.ProbeDensity(shiftedSamples);
                CheckDensityValues(shiftedDensity);
                report.originDensityDifference = MaximumDensityDifference(original, shiftedDensity);
                report.worldOriginPreserved = report.originRenderDifference <= Tolerance && report.originDensityDifference <= Tolerance;
                Require(report.worldOriginPreserved, "씬 원점 이동이 실제 구름 밀도 또는 렌더 결과를 변경했습니다.");
            }
            finally
            {
                camera.transform.position = originalPosition;
                renderer.worldOriginOffset = Vector3.zero;
            }
        }

        /// <summary>같은 스타일 밀도장에서 낮과 일몰의 색만 바뀌고 투과율과 깊이는 유지되는지 확인합니다.</summary>
        private static void CheckLighting(LostSkiesCloudRenderer renderer, Camera camera, Report report)
        {
            renderer.skyLighting = CloudLightingState.Day();
            GpuFrame day = Draw(renderer, camera);
            renderer.skyLighting = CloudLightingState.Sunset();
            GpuFrame sunset = Draw(renderer, camera);
            report.lightingTransmissionDifference = MaximumColorDifference(day.transmission, sunset.transmission);
            report.lightingRadianceDifference = MaximumColorDifference(day.lighting, sunset.lighting);
            report.lightingPreservesGeometry = report.lightingTransmissionDifference == 0f;
            Require(report.lightingPreservesGeometry, "조명 시각만 변경했는데 투과율 또는 구름 깊이가 달라졌습니다.");
            Require(report.lightingRadianceDifference > Tolerance, "낮과 일몰의 입사광 변화가 구름 조명에 반영되지 않았습니다.");
            renderer.skyLighting = CloudLightingState.Day();
        }

        /// <summary>실제 카메라 두 개를 고정 해상도로 번갈아 렌더하여 할당 수와 텍스처 객체 재사용을 확인합니다.</summary>
        private static void CheckCameraReuse(LostSkiesCloudRenderer renderer, Camera first, Camera second, Report report)
        {
            renderer.Resize(96, 96, 4101);
            RenderTexture firstLighting = renderer.lighting;
            RenderTexture firstTransmission = renderer.transmittance;
            second.transform.SetPositionAndRotation(new Vector3(-4800f, 6500f, 8100f), Quaternion.Euler(17f, 195f, 0f));
            second.aspect = 160f / 88f;
            renderer.Resize(160, 88, 4102);
            RenderTexture secondLighting = renderer.lighting;
            RenderTexture secondTransmission = renderer.transmittance;
            bool reused = true;
            for (int iteration = 0; iteration < 4; iteration++)
            {
                renderer.Resize(96, 96, 4101);
                reused &= renderer.lighting == firstLighting && renderer.transmittance == firstTransmission;
                Draw(renderer, first);
                renderer.Resize(160, 88, 4102);
                reused &= renderer.lighting == secondLighting && renderer.transmittance == secondTransmission;
                Draw(renderer, second);
            }

            report.cameraTargetAllocations = renderer.GetTargetAllocationCount();
            report.cameraTargetsReused = reused && report.cameraTargetAllocations == 2;
            Require(report.cameraTargetsReused, "카메라 전환에서 출력 텍스처가 재생성되거나 다른 카메라와 공유되었습니다.");
            Require(firstLighting != secondLighting && firstTransmission != secondTransmission, "서로 다른 카메라가 같은 출력 텍스처를 사용합니다.");
        }

        /// <summary>생산용 컴퓨트 렌더와 동기 읽기를 수행하고 방사량, 투과율, 깊이의 유효성을 검사합니다.</summary>
        private static GpuFrame Draw(LostSkiesCloudRenderer renderer, Camera camera)
        {
            using (CommandBuffer commands = new CommandBuffer())
            {
                renderer.Render(commands, camera, renderer.skyLighting.GetSunDirection(), renderer.skyLighting.sunColor, 1f);
                Graphics.ExecuteCommandBuffer(commands);
            }

            GpuFrame frame = new GpuFrame();
            frame.lighting = Read(renderer.lighting);
            frame.transmission = Read(renderer.transmittance);
            for (int pixel = 0; pixel < frame.lighting.Length; pixel++)
            {
                CheckFinite(frame.lighting[pixel]);
                CheckFinite(frame.transmission[pixel]);
                Color light = frame.lighting[pixel];
                Color transmission = frame.transmission[pixel];
                Require(light.r >= 0f && light.g >= 0f && light.b >= 0f && light.a >= 0f, "음수 구름 방사량 또는 깊이가 검출되었습니다.");
                Require(transmission.r >= 0f && transmission.r <= 1f && transmission.g >= 0f && transmission.g <= 1f
                    && transmission.b >= 0f && transmission.b <= 1f && transmission.a >= 0f, "구름 투과율 또는 깊이가 유효 범위를 벗어났습니다.");
            }

            return frame;
        }

        /// <summary>검증에만 사용할 GPU 버퍼 읽기를 수행합니다.</summary>
        private static Color[] Read(RenderTexture texture)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBAFloat);
            request.WaitForCompletion();
            Require(!request.hasError, "스타일 검사 중 GPU 읽기가 실패했습니다.");
            return request.GetData<Color>().ToArray();
        }

        /// <summary>RGBA 네 채널의 NaN과 무한대를 검사합니다.</summary>
        private static void CheckFinite(Color value)
        {
            Require(!float.IsNaN(value.r) && !float.IsInfinity(value.r)
                && !float.IsNaN(value.g) && !float.IsInfinity(value.g)
                && !float.IsNaN(value.b) && !float.IsInfinity(value.b)
                && !float.IsNaN(value.a) && !float.IsInfinity(value.a), "GPU 출력에 NaN 또는 무한대가 있습니다.");
        }

        /// <summary>전체 구름과 상층 구름 밀도가 모두 유한한 0~1 값인지 검사합니다.</summary>
        private static void CheckDensityValues(Vector2[] values)
        {
            foreach (Vector2 value in values)
            {
                Require(!float.IsNaN(value.x) && !float.IsInfinity(value.x) && value.x >= 0f && value.x <= 1f,
                    "전체 구름 밀도에 유효하지 않은 값이 있습니다.");
                Require(!float.IsNaN(value.y) && !float.IsInfinity(value.y) && value.y >= 0f && value.y <= 1f,
                    "상층 구름 밀도에 유효하지 않은 값이 있습니다.");
            }
        }

        /// <summary>전체와 상층 밀도 두 채널에서 최대 차이를 구합니다.</summary>
        private static float MaximumDensityDifference(Vector2[] first, Vector2[] second)
        {
            Require(first.Length == second.Length, "밀도 비교 배열의 길이가 다릅니다.");
            float maximum = 0f;
            for (int index = 0; index < first.Length; index++)
            {
                maximum = Mathf.Max(maximum, Mathf.Abs(first[index].x - second[index].x), Mathf.Abs(first[index].y - second[index].y));
            }

            return maximum;
        }

        /// <summary>운해가 섞이지 않은 상층 전용 채널의 최대 밀도를 구합니다.</summary>
        private static float MaximumSkyDensity(Vector2[] values)
        {
            float maximum = 0f;
            foreach (Vector2 value in values)
            {
                maximum = Mathf.Max(maximum, value.y);
            }

            return maximum;
        }

        /// <summary>하향 화면에서 가장 많이 배경을 통과시키는 픽셀을 찾습니다.</summary>
        private static float MaximumTransmission(Color[] values)
        {
            float maximum = 0f;
            foreach (Color value in values)
            {
                maximum = Mathf.Max(maximum, value.r, value.g, value.b);
            }

            return maximum;
        }

        /// <summary>구름 방사량, 투과율, 깊이를 모두 포함한 두 프레임의 최대 차이를 계산합니다.</summary>
        private static float MaximumFrameDifference(GpuFrame first, GpuFrame second)
        {
            return Mathf.Max(MaximumColorDifference(first.lighting, second.lighting), MaximumColorDifference(first.transmission, second.transmission));
        }

        /// <summary>RGBA 네 채널을 비교하여 RGB 뒤에 저장한 깊이 변화도 놓치지 않습니다.</summary>
        private static float MaximumColorDifference(Color[] first, Color[] second)
        {
            Require(first.Length == second.Length, "GPU 프레임 비교 배열의 길이가 다릅니다.");
            float maximum = 0f;
            for (int pixel = 0; pixel < first.Length; pixel++)
            {
                Color difference = first[pixel] - second[pixel];
                maximum = Mathf.Max(maximum, Mathf.Abs(difference.r), Mathf.Abs(difference.g), Mathf.Abs(difference.b), Mathf.Abs(difference.a));
            }

            return maximum;
        }

        /// <summary>실패한 계약의 원인을 보고서와 콘솔에 남깁니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
