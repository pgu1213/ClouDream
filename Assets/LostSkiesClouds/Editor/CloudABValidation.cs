using System;
using System.IO;
using UnityEngine;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>A의 전체 복귀 제거와 B의 픽셀 깊이 순서를 실제 출력으로 검사합니다.</summary>
    public static class CloudABValidation
    {
        [Serializable]
        public sealed class Report
        {
            public string createdUtc;
            public bool coldAUsesProxy;
            public bool teleportAUsesProxy;
            public bool referenceNeverExecuted;
            public bool coldOceanContinuous;
            public bool finite;
            public bool bOrderIndependent;
            public float bOrderDifference;
            public int coldProxyCells;
            public int teleportProxyCells;
            public bool zeroDensityEmpty;
            public bool passed;
        }

        /// <summary>캐시가 빈 카메라와 순간이동을 동기 진단하고 원시 L/T를 읽습니다. 일반 비행에는 readback을 추가하지 않습니다.</summary>
        public static string Run()
        {
            CloudHybridSwitch routing = UnityEngine.Object.FindAnyObjectByType<CloudHybridSwitch>();
            Camera camera = Camera.main;
            Vector3 savedPosition = camera.transform.position;
            Quaternion savedRotation = camera.transform.rotation;
            CloudComparisonVariant savedVariant = routing.variant;
            CloudComparisonView savedView = routing.view;
            bool savedOverride = routing.Reference.useSimulationTimeOverride;
            float savedTime = routing.Reference.simulationTimeOverride;
            float savedDensity = routing.Reference.cloudDensity;
            routing.Reference.simulationTimeOverride = routing.Reference.GetSimulationTime();
            routing.Reference.useSimulationTimeOverride = true;
            Report report = new Report();
            report.createdUtc = DateTime.UtcNow.ToString("O");
            report.finite = true;
            try
            {
                routing.SetComparison(CloudComparisonVariant.HybridA, CloudComparisonView.Full);
                routing.Hybrid.ReleaseRenderer();
                camera.transform.SetPositionAndRotation(new Vector3(1222, 3300, -6800), Quaternion.Euler(85, 25, 0));
                int executions = routing.Reference.executions;
                camera.Render();
                report.coldProxyCells = routing.Hybrid.Missing;
                report.coldAUsesProxy = routing.Hybrid.actual && report.coldProxyCells > 0;
                report.coldOceanContinuous = true;
                foreach (Color pixel in Read(routing.Hybrid.LastOutput))
                {
                    report.finite &= Finite(pixel);
                    report.coldOceanContinuous &= pixel.a <= 0.008f;
                }
                camera.transform.position += new Vector3(90000, 0, -70000);
                camera.Render();
                report.teleportProxyCells = routing.Hybrid.Missing;
                report.teleportAUsesProxy = routing.Hybrid.actual && report.teleportProxyCells > 0;
                foreach (Color pixel in Read(routing.Hybrid.LastOutput))
                {
                    report.finite &= Finite(pixel);
                }
                report.referenceNeverExecuted = routing.Reference.executions == executions;
                camera.transform.SetPositionAndRotation(new Vector3(1222, 3300, -6800), Quaternion.Euler(8, 25, 0));
                routing.SetComparison(CloudComparisonVariant.SurfaceB, CloudComparisonView.Full);
                camera.Render();
                Color[] forward = Read(routing.Hybrid.LastOutput);
                routing.Hybrid.reverseSurfaceOrder = true;
                camera.Render();
                Color[] reverse = Read(routing.Hybrid.LastOutput);
                for (int index = 0; index < forward.Length; index++)
                {
                    report.finite &= Finite(forward[index]) && Finite(reverse[index]);
                    Color difference = forward[index] - reverse[index];
                    report.bOrderDifference = Mathf.Max(report.bOrderDifference,
                        Mathf.Max(Mathf.Abs(difference.r), Mathf.Max(Mathf.Abs(difference.g), Mathf.Abs(difference.b))));
                }
                report.bOrderIndependent = report.bOrderDifference <= 0.005f;
                routing.Reference.cloudDensity = 0f;
                camera.Render();
                report.zeroDensityEmpty = true;
                foreach (Color pixel in Read(routing.Hybrid.LastOutput))
                {
                    report.zeroDensityEmpty &= pixel.a >= 0.999f && Mathf.Abs(pixel.r) < 0.001f
                        && Mathf.Abs(pixel.g) < 0.001f && Mathf.Abs(pixel.b) < 0.001f;
                }
                report.passed = report.coldAUsesProxy && report.teleportAUsesProxy && report.referenceNeverExecuted
                    && report.coldOceanContinuous && report.finite && report.bOrderIndependent && report.zeroDensityEmpty;
            }
            finally
            {
                routing.Hybrid.reverseSurfaceOrder = false;
                routing.Reference.cloudDensity = savedDensity;
                routing.Reference.useSimulationTimeOverride = savedOverride;
                routing.Reference.simulationTimeOverride = savedTime;
                camera.transform.SetPositionAndRotation(savedPosition, savedRotation);
                routing.SetComparison(savedVariant, savedView);
            }
            string path = "Docs/Validation/CloudAB/Streaming-And-Depth.json";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(report, true));
            return JsonUtility.ToJson(report);
        }

        /// <summary>원시 선형 L/T를 진단용으로만 동기 복사합니다.</summary>
        private static Color[] Read(RenderTexture output)
        {
            RenderTexture previous = RenderTexture.active;
            Texture2D copy = new Texture2D(output.width, output.height, TextureFormat.RGBAFloat, false, true);
            RenderTexture.active = output;
            copy.ReadPixels(new Rect(0, 0, output.width, output.height), 0, 0);
            copy.Apply();
            Color[] pixels = copy.GetPixels();
            RenderTexture.active = previous;
            UnityEngine.Object.DestroyImmediate(copy);
            return pixels;
        }

        /// <summary>색과 투과율의 NaN·무한대·범위 이탈을 확인합니다.</summary>
        private static bool Finite(Color value)
        {
            return !float.IsNaN(value.r) && !float.IsInfinity(value.r)
                && !float.IsNaN(value.g) && !float.IsInfinity(value.g)
                && !float.IsNaN(value.b) && !float.IsInfinity(value.b)
                && !float.IsNaN(value.a) && !float.IsInfinity(value.a) && value.a >= 0f && value.a <= 1.001f;
        }
    }
}
