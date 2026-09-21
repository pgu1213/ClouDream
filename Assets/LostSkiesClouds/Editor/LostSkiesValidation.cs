using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies.Editor
{
    public static class LostSkiesValidation
    {

        /// <summary>반복 렌더의 안정성과 운해를 통과하는 광선의 투과율을 검사합니다.</summary>
        public static object CheckGpu()
        {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/LostSkiesClouds/Runtime/CloudRaymarch.compute");
            var noise = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/LostSkiesClouds/SourceShaders/CloudGenerator.asset");
            var preset = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/LostSkiesClouds/Presets/normal.json");
            var go = new GameObject("Volumetric validation camera");
            var c = go.AddComponent<Camera>();
            c.enabled = false;
            c.farClipPlane = 100000;
            c.aspect = 1;
            c.transform.SetPositionAndRotation(new Vector3(0, 1800, 0), Quaternion.Euler(70, 0, 0));
            try
            {
                using (var r = new LostSkiesCloudRenderer(shader, noise, preset))
                {
                    r.includeOcean = true;
                    r.settings.coverageIntensity = .165f;
                    r.Resize(96, 96);
                    float maxDifference = 0, maxTransmission = 0;
                    Color[] first = null;
                    for (int frame = 0; frame < 4; frame++)
                    {
                        using (var cmd = new CommandBuffer())
                        {
                            r.Render(cmd, c, new Vector3(.4f, .8f, .2f), Color.white, 3);
                            Graphics.ExecuteCommandBuffer(cmd);
                        }

                        var read = AsyncGPUReadback.Request(r.transmittance, 0, TextureFormat.RGBAFloat);
                        read.WaitForCompletion();
                        if (read.hasError)
                        {
                            throw new Exception("GPU readback failed");
                        }

                        var data = read.GetData<Color>();
                        if (first == null)
                        {
                            first = data.ToArray();
                        }

                        for (int i = 0; i < data.Length; i++)
                        {
                            if (float.IsNaN(data[i].r) || float.IsInfinity(data[i].r))
                            {
                                throw new Exception("Invalid GPU value");
                            }

                            maxDifference = Mathf.Max(maxDifference, Mathf.Abs(data[i].r - first[i].r));
                            maxTransmission = Mathf.Max(maxTransmission, data[i].r);
                        }
                    }

                    if (maxDifference > .0001f)
                    {
                        throw new Exception("Repeated render changed with frozen inputs");
                    }

                    if (maxTransmission > .03f)
                    {
                        throw new Exception("A downward ray escaped the continuous cloud ocean");
                    }

                    return new
                    {
                    passed = true, repeatedFrames = 4, maxDifference, maxTransmission, noise = r.DiagnoseNoise()}

                    ;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Serializable]
        public class FlightReport
        {
            public bool passed;
            public float movedMetres;
            public bool resetRestoredPosition;
            public string error;
        }

        /// <summary>실제 입력 시스템으로 전진 및 초기 위치 복귀를 검증합니다.</summary>
        public static async void StartFlightCheck()
        {
            var report = new FlightReport();
            var camera = Camera.main;
            var before = camera.transform.position;
            var keyboard = InputSystem.AddDevice<Keyboard>();
            bool background = Application.runInBackground;
            var routing = InputSystem.settings.editorInputBehaviorInPlayMode;
            var focus = InputSystem.settings.backgroundBehavior;
            try
            {
                if (!Application.isPlaying)
                {
                    throw new Exception("Play mode required");
                }

                InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                Application.runInBackground = true;
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W));
                InputSystem.Update();
                for (int i = 0; i < 15; i++)
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                    await Task.Delay(30);
                }

                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                await Task.Delay(100);
                report.movedMetres = Vector3.Distance(before, camera.transform.position);
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.R));
                await Task.Delay(100);
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                await Task.Delay(100);
                report.resetRestoredPosition = Vector3.Distance(before, camera.transform.position) < .1f;
                report.passed = report.movedMetres > 2 && report.resetRestoredPosition;
            }
            catch (Exception e)
            {
                report.error = e.Message;
            }
            finally
            {
                InputSystem.RemoveDevice(keyboard);
                camera.transform.position = before;
                Application.runInBackground = background;
                InputSystem.settings.editorInputBehaviorInPlayMode = routing;
                InputSystem.settings.backgroundBehavior = focus;
                Directory.CreateDirectory("Screenshots");
                File.WriteAllText("Screenshots/LostSkies-FlightValidation.json", JsonUtility.ToJson(report, true));
            }
        }
    }
}
