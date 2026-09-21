using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies.Editor
{
    public static class CloudSkyValidation
    {
        [Serializable]
        public sealed class Report
        {
            public bool passed;
            public int sampledPoints;
            public int occupiedCells;
            public float skyVolumeFraction;
            public float occupiedWidthVariation;
            public float occupiedHeightVariation;
            public float repeatDifference;
            public float originShiftDifference;
            public bool emptyCellsHaveNoDensity;
            public bool cellBoundariesClear;
            public bool seedChangesDistribution;
            public int noiseGenerations;
            public int cameraTargetAllocations;
            public bool interruptedTransitionContinuous;
            public bool zeroDurationTransitionImmediate;
            public string error;
        }

        /// <summary>상층 밀도 분포와 재현성, 원점 이동, 버퍼 재사용, 날씨 전환을 검사합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Validate Sky Clouds")]
        public static Report Run()
        {
            Report report = new Report();
            GameObject cameraObject = new GameObject("Upper cloud validation");
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.farClipPlane = 100000f;
            camera.aspect = 1f;
            camera.transform.SetPositionAndRotation(new Vector3(1222f, 3300f, -6800f), Quaternion.Euler(-8f, 15f, 0f));
            CloudSkyProfile savedSky = AssetDatabase.LoadAssetAtPath<CloudSkyProfile>("Assets/LostSkiesClouds/Presets/SparseSky.asset");
            CloudSkyProfile sky = UnityEngine.Object.Instantiate(savedSky);
            try
            {
                ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/LostSkiesClouds/Runtime/CloudRaymarch.compute");
                ComputeShader noise = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/LostSkiesClouds/SourceShaders/CloudGenerator.asset");
                TextAsset preset = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/LostSkiesClouds/Presets/normal.json");
                using (LostSkiesCloudRenderer renderer = new LostSkiesCloudRenderer(shader, noise, preset))
                {
                    renderer.includeOcean = true;
                    renderer.skyProfile = sky;
                    renderer.settings.coverageIntensity = 0.165f;
                    renderer.settings.density = 160000f;
                    renderer.Resize(96, 96, 101);
                    Draw(renderer, camera);
                    List<Vector3> positions = new List<Vector3>();
                    int pointsPerCell = 16 * 16 * 16;
                    for (int cellZ = -2; cellZ < 2; cellZ++)
                    {
                        for (int cellX = -2; cellX < 2; cellX++)
                        {
                            for (int y = 0; y < 16; y++)
                            {
                                for (int z = 0; z < 16; z++)
                                {
                                    for (int x = 0; x < 16; x++)
                                    {
                                        positions.Add(new Vector3((cellX + (x + 0.5f) / 16f) * sky.spacing, Mathf.Lerp(sky.altitude.x - sky.verticalRadius.y, sky.altitude.y + sky.verticalRadius.y, y / 15f), (cellZ + (z + 0.5f) / 16f) * sky.spacing));
                                    }
                                }
                            }
                        }
                    }

                    Vector3[] samples = positions.ToArray();
                    Vector2[] first = renderer.ProbeDensity(samples);
                    Vector2[] repeated = renderer.ProbeDensity(samples);
                    report.sampledPoints = samples.Length;
                    report.repeatDifference = MaximumDifference(first, repeated);
                    int occupiedSamples = 0;
                    float smallestWidth = float.MaxValue;
                    float largestWidth = 0f;
                    float lowestCenter = float.MaxValue;
                    float highestCenter = float.MinValue;
                    for (int cell = 0; cell < 16; cell++)
                    {
                        Bounds bounds = new Bounds();
                        bool found = false;
                        for (int point = cell * pointsPerCell; point < (cell + 1) * pointsPerCell; point++)
                        {
                            Require(!float.IsNaN(first[point].y) && !float.IsInfinity(first[point].y), "Invalid upper density");
                            if (first[point].y <= 0.01f)
                            {
                                continue;
                            }

                            occupiedSamples++;
                            if (!found)
                            {
                                bounds = new Bounds(samples[point], Vector3.zero);
                                found = true;
                            }

                            bounds.Encapsulate(samples[point]);
                        }

                        if (found)
                        {
                            report.occupiedCells++;
                            smallestWidth = Mathf.Min(smallestWidth, bounds.size.x);
                            largestWidth = Mathf.Max(largestWidth, bounds.size.x);
                            lowestCenter = Mathf.Min(lowestCenter, bounds.center.y);
                            highestCenter = Mathf.Max(highestCenter, bounds.center.y);
                        }
                    }

                    report.skyVolumeFraction = occupiedSamples / (float)samples.Length;
                    report.occupiedWidthVariation = largestWidth - smallestWidth;
                    report.occupiedHeightVariation = highestCenter - lowestCenter;
                    Require(report.occupiedCells > 3 && report.occupiedCells < 16, "Sky must contain both filled and empty cells");
                    Require(report.skyVolumeFraction > 0.005f && report.skyVolumeFraction < 0.25f, "Sky distribution is too empty or too dense");
                    Require(report.occupiedWidthVariation > 500f && report.occupiedHeightVariation > 1000f, "Cloud dimensions lack variation");
                    Require(report.repeatDifference == 0f, "Frozen density changed between queries");
                    List<Vector3> edges = new List<Vector3>();
                    for (int cell = -2; cell <= 2; cell++)
                    {
                        for (int offset = -32; offset <= 32; offset++)
                        {
                            for (int y = 3000; y < 10500; y += 500)
                            {
                                edges.Add(new Vector3(cell * sky.spacing + 1f, y, offset * sky.spacing / 16f));
                                edges.Add(new Vector3(cell * sky.spacing - 1f, y, offset * sky.spacing / 16f));
                                edges.Add(new Vector3(offset * sky.spacing / 16f, y, cell * sky.spacing + 1f));
                                edges.Add(new Vector3(offset * sky.spacing / 16f, y, cell * sky.spacing - 1f));
                            }
                        }
                    }

                    report.cellBoundariesClear = MaximumSky(renderer.ProbeDensity(edges.ToArray())) == 0f;
                    Require(report.cellBoundariesClear, "A cloud crosses the single-cell support boundary");
                    Color[] beforeShift = ReadTransmission(renderer);
                    CloudWorldOrigin origin = cameraObject.AddComponent<CloudWorldOrigin>();
                    Vector3 translation = new Vector3(-32000f, -1200f, 17000f);
                    camera.transform.position += translation;
                    origin.ApplySceneTranslation(translation);
                    renderer.worldOriginOffset = origin.SamplingOffset;
                    Draw(renderer, camera);
                    Color[] afterShift = ReadTransmission(renderer);
                    for (int i = 0; i < beforeShift.Length; i++)
                    {
                        report.originShiftDifference = Mathf.Max(report.originShiftDifference, Mathf.Abs(beforeShift[i].r - afterShift[i].r));
                    }

                    Require(report.originShiftDifference < 0.0001f, "Origin shift moved rendered clouds");
                    sky.occupiedCells = 0f;
                    Draw(renderer, camera);
                    report.emptyCellsHaveNoDensity = MaximumSky(renderer.ProbeDensity(samples)) == 0f;
                    Require(report.emptyCellsHaveNoDensity, "Disabled sky still has density");
                    sky.occupiedCells = savedSky.occupiedCells;
                    sky.seed += 11;
                    Draw(renderer, camera);
                    report.seedChangesDistribution = MaximumDifference(first, renderer.ProbeDensity(samples)) > 0.1f;
                    Require(report.seedChangesDistribution, "Seed does not affect distribution");
                    for (int iteration = 0; iteration < 8; iteration++)
                    {
                        renderer.Resize(96, 96, 101);
                        renderer.Resize(160, 88, 202);
                    }

                    report.cameraTargetAllocations = renderer.GetTargetAllocationCount();
                    report.noiseGenerations = renderer.NoiseGenerationCount;
                    Require(report.cameraTargetAllocations == 2, "Camera switching repeatedly allocated targets");
                    Require(report.noiseGenerations == 5, "Runtime changes regenerated shared noise");
                }

                CheckTransitions(cameraObject, report);
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
                UnityEngine.Object.DestroyImmediate(cameraObject);
                Directory.CreateDirectory("Screenshots");
                File.WriteAllText("Screenshots/SkyClouds-Validation.json", JsonUtility.ToJson(report, true));
            }

            return report;
        }

        /// <summary>실제 게임용 렌더 경로로 설정을 적용하고 GPU 실행을 제출합니다.</summary>
        private static void Draw(LostSkiesCloudRenderer renderer, Camera camera)
        {
            using (CommandBuffer commands = new CommandBuffer())
            {
                renderer.Render(commands, camera, new Vector3(0.4f, 0.8f, 0.2f), Color.white, 3f);
                Graphics.ExecuteCommandBuffer(commands);
            }
        }

        /// <summary>GPU 투과율을 읽어 원점 이동 전후의 이미지를 수치 비교합니다.</summary>
        private static Color[] ReadTransmission(LostSkiesCloudRenderer renderer)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(renderer.transmittance, 0, TextureFormat.RGBAFloat);
            request.WaitForCompletion();
            Require(!request.hasError, "GPU readback failed");
            return request.GetData<Color>().ToArray();
        }

        /// <summary>두 밀도 결과의 최대 차이를 계산합니다.</summary>
        private static float MaximumDifference(Vector2[] first, Vector2[] second)
        {
            float difference = 0f;
            for (int i = 0; i < first.Length; i++)
            {
                difference = Mathf.Max(difference, Mathf.Abs(first[i].y - second[i].y));
            }

            return difference;
        }

        /// <summary>상층 전용 밀도의 최대값을 찾습니다.</summary>
        private static float MaximumSky(Vector2[] density)
        {
            float maximum = 0f;
            foreach (Vector2 value in density)
            {
                maximum = Mathf.Max(maximum, value.y);
            }

            return maximum;
        }

        /// <summary>중간 전환 재시작과 0초 즉시 전환이 정상 동작하는지 검사합니다.</summary>
        private static void CheckTransitions(GameObject host, Report report)
        {
            CloudWeatherController weather = host.AddComponent<CloudWeatherController>();
            CloudWeatherProfile dark = ScriptableObject.CreateInstance<CloudWeatherProfile>();
            CloudWeatherProfile bright = ScriptableObject.CreateInstance<CloudWeatherProfile>();
            try
            {
                CloudEnvironment target = dark.environment;
                target.brightness = 0.2f;
                dark.environment = target;
                weather.TransitionTo(dark, 10f);
                weather.Advance(4f);
                float interrupted = weather.Evaluate(Vector3.zero).brightness;
                Require(Mathf.Abs(interrupted - 0.92f) < 0.0001f, "Transition did not advance");
                weather.TransitionTo(bright, 10f);
                report.interruptedTransitionContinuous = Mathf.Abs(weather.Evaluate(Vector3.zero).brightness - interrupted) < 0.0001f;
                Require(report.interruptedTransitionContinuous, "Restarted transition jumped");
                weather.Advance(10f);
                Require(Mathf.Abs(weather.Evaluate(Vector3.zero).brightness - bright.environment.brightness) < 0.0001f, "Transition did not finish");
                weather.TransitionTo(dark, 0f);
                report.zeroDurationTransitionImmediate = Mathf.Abs(weather.Evaluate(Vector3.zero).brightness - 0.2f) < 0.0001f;
                Require(report.zeroDurationTransitionImmediate, "Zero-duration transition did not apply immediately");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(dark);
                UnityEngine.Object.DestroyImmediate(bright);
            }
        }

        /// <summary>검증 조건을 만족하지 않으면 명확한 원인과 함께 중단합니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
