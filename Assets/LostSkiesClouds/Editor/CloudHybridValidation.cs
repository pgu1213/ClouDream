using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>Play 상태에서 실제 하이브리드 출력·원점·팔레트·자원 수명을 확인합니다.</summary>
    public static class CloudHybridValidation
    {
        [Serializable]
        public sealed class Sample
        {
            public string name;
            public bool actualHybrid;
            public bool finite;
            public float minimumTransmission;
            public float maximumTransmission;
            public float meanTransmission;
            public float meanRadiance;
            public int builds;
            public int failures;
            public long bytes;
            public int targetAllocations;
            public Vector3 position;
            public string capture;
        }

        [Serializable]
        public sealed class Report
        {
            public string createdUtc;
            public bool completed;
            public bool passed;
            public bool allOutputsFinite;
            public bool allSamplesHybrid;
            public bool oceanContinuous;
            public bool withinMemoryBudget;
            public bool lightingPreservesTransmission;
            public float lightingTransmissionDifference;
            public bool originPreserved;
            public float originDifference;
            public bool offReleasesResources;
            public bool referenceRestored;
            public bool twoCamerasSeparated;
            public bool resizeReleasesPreviousTarget;
            public bool foregroundOcclusion;
            public Color foregroundPixel;
            public bool interruptedWeatherContinuous;
            public bool weatherRestored;
            public float weatherRestoreDifference;
            public bool weatherDoesNotRebuildMeshes;
            public bool allGeometryKindsPresent;
            public bool projectionAgreement;
            public float projectionExtraOpaqueFraction;
            public int[] geometryCounts;
            public string error;
            public string visualReview = "Required: automated finite/transmission checks do not establish visual quality.";
            public List<Sample> samples = new List<Sample>();
        }

        public static bool Running { get; private set; }

        public static string Status { get; private set; } = "idle";

        public static string ReportPath { get; private set; }

        private static Report report;

        private static CloudHybridSwitch routing;

        private static CloudTimeOfDayController clock;

        private static Camera camera;

        private static Vector3 originalPosition;

        private static Quaternion originalRotation;

        private static float originalHour;

        private static bool originalAdvance;

        private static bool originalHybrid;

        private static bool originalEditor;

        private static bool originalOverride;

        private static float originalSimulationTime;

        private static float originalTimeScale;

        private static bool originalBackground;

        private static bool flightEnabled;

        private static CloudSeaFlight flight;

        private static int scenario;

        private static int stableFrames;

        private static double started;

        private static Color[] dayOutput;

        private static Color[] beforeOrigin;

        private static Vector3 translation;

        private static bool translated;

        /// <summary>시간과 바람을 고정하고 검증을 Editor tick에 예약합니다.</summary>
        public static string Begin()
        {
            if (!Application.isPlaying || Running)
            {
                throw new InvalidOperationException("Start an idle validation during Play.");
            }

            routing = UnityEngine.Object.FindAnyObjectByType<CloudHybridSwitch>();
            if (routing == null || !routing.Connect())
            {
                throw new InvalidOperationException("Open the hybrid validation scene first.");
            }

            camera = Camera.main;
            clock = routing.GetComponent<CloudTimeOfDayController>();
            flight = camera.GetComponent<CloudSeaFlight>();
            originalPosition = camera.transform.position;
            originalRotation = camera.transform.rotation;
            originalHour = clock.TimeOfDay;
            originalAdvance = clock.autoAdvance;
            originalHybrid = routing.useHybrid;
            originalEditor = routing.Reference.renderInEditor;
            originalOverride = routing.Reference.useSimulationTimeOverride;
            originalSimulationTime = routing.Reference.simulationTimeOverride;
            originalTimeScale = Time.timeScale;
            originalBackground = Application.runInBackground;
            if (flight != null)
            {
                flightEnabled = flight.enabled;
                flight.enabled = false;
            }

            Time.timeScale = 0f;
            Application.runInBackground = true;
            clock.autoAdvance = false;
            routing.Reference.renderInEditor = false;
            routing.Reference.useSimulationTimeOverride = true;
            routing.Reference.simulationTimeOverride = 0f;
            routing.SetHybrid(true);
            report = new Report();
            report.createdUtc = DateTime.UtcNow.ToString("O");
            ReportPath = "Docs/Validation/Hybrid/Play-Validation.json";
            scenario = 0;
            translated = false;
            Running = true;
            ApplyScenario();
            EditorApplication.update += Tick;
            return "queued";
        }

        /// <summary>각 시점의 셀이 완성되면 이미지와 원시 L/T를 확인합니다.</summary>
        private static void Tick()
        {
            if (!Running)
            {
                return;
            }

            try
            {
                if (!Application.isPlaying)
                {
                    throw new InvalidOperationException("Play stopped during validation.");
                }

                EditorApplication.QueuePlayerLoopUpdate();
                if (routing.Hybrid.Failures > 0)
                {
                    throw new InvalidOperationException(routing.Hybrid.Status);
                }

                if (!routing.Hybrid.actual || routing.Hybrid.Pending > 0 || routing.Hybrid.ResidencyMissing > 0)
                {
                    stableFrames = 0;
                    Status = "warming scenario " + scenario + ": " + routing.Hybrid.Status;
                    if (EditorApplication.timeSinceStartup - started > 240)
                    {
                        throw new TimeoutException(Status);
                    }

                    return;
                }

                stableFrames++;
                if (stableFrames < 8)
                {
                    return;
                }

                CaptureScenario();
                scenario++;
                if (scenario < 8)
                {
                    ApplyScenario();
                }
                else
                {
                    report.geometryCounts = routing.Hybrid.ReadGeometryCounts();
                    report.allGeometryKindsPresent = report.geometryCounts[1] > 0
                        && report.geometryCounts[3] > 0 && report.geometryCounts[5] > 0;
                    CheckWeather();
                    CheckCamerasAndForeground();
                    CheckOffAndRestore();
                    Finish(null);
                }
            }
            catch (Exception exception)
            {
                Finish(exception.ToString());
            }
        }

        /// <summary>대표 색·운해 상하·기존 상층 점유 위치를 같은 시각으로 재현합니다.</summary>
        private static void ApplyScenario()
        {
            stableFrames = 0;
            started = EditorApplication.timeSinceStartup;
            Status = "warming scenario " + scenario;
            if (scenario <= 2)
            {
                camera.transform.SetPositionAndRotation(new Vector3(1222f, 3300f, -6800f), Quaternion.Euler(10f, 15f, 0f));
                float hour = 12f;
                if (scenario == 1)
                {
                    hour = 18f;
                }
                else if (scenario == 2)
                {
                    hour = 22f;
                }

                clock.SetTime(hour);
            }
            else if (scenario == 3)
            {
                clock.SetTime(12f);
                camera.transform.SetPositionAndRotation(new Vector3(1222f, 3300f, -6800f), Quaternion.Euler(75f, 15f, 0f));
            }
            else if (scenario == 4)
            {
                camera.transform.SetPositionAndRotation(new Vector3(5031f, 6550f, -5390f), Quaternion.Euler(0f, 20f, 0f));
            }
            else if (scenario == 5)
            {
                camera.transform.SetPositionAndRotation(new Vector3(1222f, -500f, -6800f), Quaternion.Euler(0f, 25f, 0f));
            }
            else if (scenario == 6)
            {
                camera.transform.SetPositionAndRotation(new Vector3(1222f, -3000f, -6800f), Quaternion.Euler(-75f, 15f, 0f));
            }
            else
            {
                beforeOrigin = ReadOutput();
                translation = new Vector3(10000f, 0f, -20000f);
                TranslateRoots(translation);
                routing.Reference.worldOrigin.ApplySceneTranslation(translation);
                translated = true;
            }
        }

        /// <summary>이미지 QA와 수치 검사를 계측에서 분리합니다. 동기 읽기는 이 진단에만 사용합니다.</summary>
        private static void CaptureScenario()
        {
            string[] names = { "Day", "Sunset", "Night", "Ocean-Down", "Hero-Inside", "Ocean-Inside", "Ocean-Below", "Origin-Shift" };
            Color[] output = ReadOutput();
            Sample sample = new Sample();
            sample.name = names[scenario];
            sample.actualHybrid = routing.Hybrid.actual;
            sample.finite = true;
            sample.minimumTransmission = 1f;
            sample.maximumTransmission = 0f;
            sample.builds = routing.Hybrid.Builds;
            sample.failures = routing.Hybrid.Failures;
            sample.bytes = routing.Hybrid.Bytes;
            sample.targetAllocations = routing.Hybrid.TargetAllocations;
            sample.position = camera.transform.position;
            double transmission = 0;
            double radiance = 0;
            foreach (Color pixel in output)
            {
                if (!Finite(pixel.r) || !Finite(pixel.g) || !Finite(pixel.b) || !Finite(pixel.a)
                    || pixel.a < 0f || pixel.a > 1.0001f)
                {
                    sample.finite = false;
                }

                sample.minimumTransmission = Mathf.Min(sample.minimumTransmission, pixel.a);
                sample.maximumTransmission = Mathf.Max(sample.maximumTransmission, pixel.a);
                transmission += pixel.a;
                radiance += (pixel.r + pixel.g + pixel.b) / 3f;
            }

            sample.meanTransmission = (float)(transmission / output.Length);
            sample.meanRadiance = (float)(radiance / output.Length);
            if (scenario == 0)
            {
                dayOutput = output;
                CheckProjectionAgreement(output);
            }

            if (scenario == 1 && dayOutput.Length == output.Length)
            {
                float difference = 0f;
                for (int i = 0; i < output.Length; i++)
                {
                    difference = Mathf.Max(difference, Mathf.Abs(output[i].a - dayOutput[i].a));
                }

                report.lightingTransmissionDifference = difference;
                report.lightingPreservesTransmission = difference <= 0.001f;
            }

            if (scenario == 7 && beforeOrigin.Length == output.Length)
            {
                float difference = 0f;
                for (int i = 0; i < output.Length; i++)
                {
                    difference = Mathf.Max(difference, Mathf.Abs(output[i].a - beforeOrigin[i].a));
                    difference = Mathf.Max(difference, Mathf.Abs(output[i].r - beforeOrigin[i].r));
                    difference = Mathf.Max(difference, Mathf.Abs(output[i].g - beforeOrigin[i].g));
                    difference = Mathf.Max(difference, Mathf.Abs(output[i].b - beforeOrigin[i].b));
                }

                report.originDifference = difference;
                report.originPreserved = difference <= 0.005f;
            }

            sample.capture = "Docs/Validation/Hybrid/" + sample.name + ".png";
            CaptureCamera(sample.capture);
            report.samples.Add(sample);
            SaveReport();
        }

        /// <summary>실제 하이브리드 출력을 읽으며 기존 경로의 표시를 성공으로 취급하지 않습니다.</summary>
        private static Color[] ReadOutput()
        {
            if (!routing.Hybrid.actual || routing.Hybrid.LastOutput == null)
            {
                throw new InvalidOperationException("Hybrid output is not active.");
            }

            var request = AsyncGPUReadback.Request(routing.Hybrid.LastOutput, 0, TextureFormat.RGBAFloat);
            request.WaitForCompletion();
            if (request.hasError)
            {
                throw new InvalidOperationException("Hybrid diagnostic readback failed.");
            }

            return request.GetData<Color>().ToArray();
        }

        /// <summary>같은 FHD 투영에서 UI 없는 카메라 이미지를 저장한 뒤 타깃을 복원합니다.</summary>
        private static void CaptureCamera(string path)
        {
            RenderTexture previous = camera.targetTexture;
            RenderTexture active = RenderTexture.active;
            RenderTexture target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Texture2D image = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0f, 0f, 1920f, 1080f), 0, 0);
                image.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previous;
                RenderTexture.active = active;
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(image);
            }
        }

        /// <summary>명시적 OFF가 자원을 해제하고 기존 패스를 복구하는지 확인합니다.</summary>
        private static void CheckOffAndRestore()
        {
            routing.SetHybrid(false);
            report.offReleasesResources = routing.Hybrid.Bytes == 0 && routing.Hybrid.LastOutput == null;
            camera.Render();
            report.referenceRestored = !routing.Reference.skipForHybrid && !routing.Hybrid.actual;
        }

        /// <summary>메시의 빈 하늘에 뒤집힌 볼륨이 큰 불투명 면을 만드는 회귀를 검사합니다.</summary>
        private static void CheckProjectionAgreement(Color[] volume)
        {
            CloudHybridQualityProfile quality = routing.Hybrid.quality;
            float previousNear = quality.volumeNear;
            float previousFar = quality.volumeFar;
            try
            {
                quality.volumeNear = 100f;
                quality.volumeFar = 200f;
                camera.Render();
                Color[] mesh = ReadOutput();
                int extraOpaque = 0;
                for (int i = 0; i < volume.Length && i < mesh.Length; i++)
                {
                    if (mesh[i].a > 0.999f && volume[i].a < 0.5f)
                    {
                        extraOpaque++;
                    }
                }

                report.projectionExtraOpaqueFraction = (float)extraOpaque / volume.Length;
                report.projectionAgreement = volume.Length == mesh.Length
                    && report.projectionExtraOpaqueFraction <= 0.05f;
            }
            finally
            {
                quality.volumeNear = previousNear;
                quality.volumeFar = previousFar;
                camera.Render();
            }
        }

        /// <summary>새 렌더러에서도 날씨 중단 직후 값과 원래 GPU 출력의 복원을 검사합니다.</summary>
        private static void CheckWeather()
        {
            CloudEnvironmentSource previous = clock.weatherSource;
            GameObject host = new GameObject("Hybrid weather validation");
            host.hideFlags = HideFlags.HideAndDontSave;
            CloudWeatherProfile neutral = ScriptableObject.CreateInstance<CloudWeatherProfile>();
            CloudWeatherProfile overcast = ScriptableObject.CreateInstance<CloudWeatherProfile>();
            CloudWeatherProfile clearing = ScriptableObject.CreateInstance<CloudWeatherProfile>();
            try
            {
                neutral.environment = CloudEnvironment.ClearDay();
                if (previous != null)
                {
                    neutral.environment = previous.Evaluate(camera.transform.position + routing.Reference.worldOrigin.SamplingOffset);
                }

                overcast.environment = neutral.environment;
                overcast.environment.densityMultiplier *= 1.25f;
                overcast.environment.coverageOffset += 0.035f;
                clearing.environment = neutral.environment;
                clearing.environment.densityMultiplier *= 0.85f;
                clearing.environment.coverageOffset -= 0.02f;
                CloudWeatherController weather = host.AddComponent<CloudWeatherController>();
                weather.TransitionTo(neutral, 0f);
                clock.weatherSource = weather;
                camera.Render();
                Color[] before = ReadOutput();
                int builds = routing.Hybrid.Builds;
                weather.TransitionTo(overcast, 10f);
                weather.Advance(4f);
                CloudEnvironment interrupted = weather.Evaluate(Vector3.zero);
                camera.Render();
                weather.TransitionTo(clearing, 6f);
                CloudEnvironment after = weather.Evaluate(Vector3.zero);
                report.interruptedWeatherContinuous = Mathf.Abs(interrupted.densityMultiplier - after.densityMultiplier) < 0.000001f
                    && Mathf.Abs(interrupted.coverageOffset - after.coverageOffset) < 0.000001f;
                weather.Advance(2f);
                camera.Render();
                weather.TransitionTo(neutral, 0f);
                camera.Render();
                Color[] restored = ReadOutput();
                float difference = MaximumDifference(before, restored);
                report.weatherRestoreDifference = difference;
                report.weatherRestored = difference <= 0.005f;
                report.weatherDoesNotRebuildMeshes = builds == routing.Hybrid.Builds;
            }
            finally
            {
                clock.weatherSource = previous;
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(neutral);
                UnityEngine.Object.DestroyImmediate(overcast);
                UnityEngine.Object.DestroyImmediate(clearing);
            }
        }

        /// <summary>전경 깊이, 두 카메라의 독립 출력, 크기 변경과 원래 타깃 재사용을 검사합니다.</summary>
        private static void CheckCamerasAndForeground()
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.hideFlags = HideFlags.HideAndDontSave;
            cube.transform.position = camera.transform.position + camera.transform.forward * 20f;
            cube.transform.localScale = Vector3.one * 8f;
            Material material = new Material(Shader.Find("HDRP/Unlit"));
            material.SetColor("_UnlitColor", new Color(0.2f, 0.8f, 0.3f));
            cube.GetComponent<Renderer>().sharedMaterial = material;
            GameObject host = new GameObject("Hybrid camera validation B");
            host.hideFlags = HideFlags.HideAndDontSave;
            RenderTexture first = new RenderTexture(192, 108, 24);
            RenderTexture second = new RenderTexture(320, 180, 24);
            try
            {
                camera.Render();
                Color[] withForeground = ReadOutput();
                RenderTexture primaryOutput = routing.Hybrid.LastOutput;
                int center = primaryOutput.width * (primaryOutput.height / 2) + primaryOutput.width / 2;
                report.foregroundPixel = withForeground[center];
                report.foregroundOcclusion = withForeground[center].a >= 0.999f
                    && withForeground[center].r <= 0.0001f && withForeground[center].g <= 0.0001f;
                UnityEngine.Object.DestroyImmediate(cube);
                cube = null;
                Camera secondary = host.AddComponent<Camera>();
                secondary.CopyFrom(camera);
                secondary.enabled = false;
                secondary.transform.SetPositionAndRotation(camera.transform.position, camera.transform.rotation);
                secondary.targetTexture = first;
                secondary.Render();
                RenderTexture secondOutput = routing.Hybrid.LastOutput;
                int allocations = routing.Hybrid.TargetAllocations;
                report.twoCamerasSeparated = primaryOutput != secondOutput && routing.Hybrid.actual;
                secondary.targetTexture = second;
                secondary.Render();
                report.resizeReleasesPreviousTarget = routing.Hybrid.actual && !secondOutput.IsCreated()
                    && routing.Hybrid.TargetAllocations == allocations + 1;
                camera.Render();
                report.twoCamerasSeparated &= routing.Hybrid.actual && routing.Hybrid.LastOutput == primaryOutput;
            }
            finally
            {
                if (cube != null)
                {
                    UnityEngine.Object.DestroyImmediate(cube);
                }

                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(material);
                first.Release();
                second.Release();
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }
        }

        /// <summary>서로 같은 크기의 선형 L/T 출력에서 최악 채널 차이를 구합니다.</summary>
        private static float MaximumDifference(Color[] first, Color[] second)
        {
            if (first.Length != second.Length)
            {
                return float.MaxValue;
            }

            float difference = 0f;
            for (int i = 0; i < first.Length; i++)
            {
                difference = Mathf.Max(difference, Mathf.Abs(first[i].r - second[i].r));
                difference = Mathf.Max(difference, Mathf.Abs(first[i].g - second[i].g));
                difference = Mathf.Max(difference, Mathf.Abs(first[i].b - second[i].b));
                difference = Mathf.Max(difference, Mathf.Abs(first[i].a - second[i].a));
            }

            return difference;
        }

        /// <summary>게임 원점 이동과 동일하게 모든 루트에 평행이동을 적용합니다.</summary>
        private static void TranslateRoots(Vector3 delta)
        {
            foreach (GameObject root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                root.transform.position += delta;
            }
        }

        /// <summary>유한한 선형 색과 투과율만 수치 검사 성공으로 간주합니다.</summary>
        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>모든 표본을 보존하고 시각 합격과 구분해 자동 계약 결과를 저장합니다.</summary>
        private static void Finish(string error)
        {
            report.error = error;
            report.completed = error == null;
            report.allOutputsFinite = true;
            report.allSamplesHybrid = true;
            report.withinMemoryBudget = true;
            report.oceanContinuous = true;
            foreach (Sample sample in report.samples)
            {
                report.allOutputsFinite &= sample.finite;
                report.allSamplesHybrid &= sample.actualHybrid;
                report.withinMemoryBudget &= sample.bytes <= routing.Hybrid.quality.memoryMiB * 1048576L;
                if (sample.name == "Ocean-Down" || sample.name == "Ocean-Inside" || sample.name == "Ocean-Below")
                {
                    report.oceanContinuous &= sample.maximumTransmission <= 0.008f;
                }
            }

            report.passed = report.completed && report.samples.Count == 8 && report.allOutputsFinite
                && report.allSamplesHybrid && report.withinMemoryBudget && report.oceanContinuous
                && report.lightingPreservesTransmission && report.originPreserved && report.offReleasesResources
                && report.referenceRestored && report.twoCamerasSeparated && report.resizeReleasesPreviousTarget
                && report.foregroundOcclusion && report.interruptedWeatherContinuous && report.weatherRestored
                && report.weatherDoesNotRebuildMeshes && report.allGeometryKindsPresent && report.projectionAgreement;
            SaveReport();
            Restore();
            Status = "completed";
            if (!report.passed)
            {
                Status = "failed: " + error;
            }
        }

        /// <summary>원시 결과와 실패 상태를 각 단계에서 저장합니다.</summary>
        private static void SaveReport()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
        }

        /// <summary>검증 중단에도 원점·카메라·시각·입력을 원래 상태로 복구합니다.</summary>
        private static void Restore()
        {
            EditorApplication.update -= Tick;
            Running = false;
            if (translated)
            {
                TranslateRoots(-translation);
                routing.Reference.worldOrigin.ApplySceneTranslation(-translation);
                translated = false;
            }

            routing.SetHybrid(originalHybrid);
            routing.Reference.renderInEditor = originalEditor;
            routing.Reference.useSimulationTimeOverride = originalOverride;
            routing.Reference.simulationTimeOverride = originalSimulationTime;
            clock.autoAdvance = originalAdvance;
            clock.SetTime(originalHour);
            camera.transform.SetPositionAndRotation(originalPosition, originalRotation);
            Time.timeScale = originalTimeScale;
            Application.runInBackground = originalBackground;
            if (flight != null)
            {
                flight.enabled = flightEnabled;
            }
        }
    }
}
