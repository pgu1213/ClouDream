using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEditor.SceneManagement;

namespace ClouDream.LostSkies.Editor
{
    public static class LostSkiesCloudTools
    {

        /// <summary>볼류메트릭 데모 장면을 열고 없으면 기존 장면에서 구성합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Create or Open Cloud Scene")]
        public static void OpenScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            const string path = "Assets/LostSkiesClouds/Scenes/LostSkiesCloudSea.unity";
            if (File.Exists(path))
            {
                EditorSceneManager.OpenScene(path);
                return;
            }

            Directory.CreateDirectory("Assets/LostSkiesClouds/Scenes");
            var scene = EditorSceneManager.OpenScene("Assets/CloudSea/Scenes/CloudSea.unity");
            EditorSceneManager.SaveScene(scene, path, true);
            scene = EditorSceneManager.OpenScene(path);
            foreach (var world in UnityEngine.Object.FindObjectsByType<ClouDream.CloudSeaWorld>(FindObjectsSortMode.None))
            {
                world.enabled = false;
            }

            foreach (var volume in UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
            {
                if (volume.sharedProfile == null)
                {
                    continue;
                }

                var profile = ScriptableObject.CreateInstance<VolumeProfile>();
                string profilePath = AssetDatabase.GenerateUniqueAssetPath("Assets/LostSkiesClouds/Presets/CloudSky.asset");
                AssetDatabase.CreateAsset(profile, profilePath);
                foreach (var component in volume.sharedProfile.components)
                {
                    var copy = UnityEngine.Object.Instantiate(component);
                    copy.name = component.name;
                    profile.components.Add(copy);
                    AssetDatabase.AddObjectToAsset(copy, profile);
                }

                volume.sharedProfile = profile;
                if (profile.TryGet<VolumetricClouds>(out var clouds))
                {
                    clouds.enable.Override(false);
                }

                EditorUtility.SetDirty(profile);
            }

            var go = new GameObject("Lost Skies • Cloud Reconstruction");
            var passVolume = go.AddComponent<CustomPassVolume>();
            passVolume.isGlobal = true;
            passVolume.injectionPoint = CustomPassInjectionPoint.BeforeTransparent;
            var pass = new LostSkiesCloudPass
            {
                name = "Lost Skies / volumetric reconstruction",
                raymarchShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/LostSkiesClouds/Runtime/CloudRaymarch.compute"),
                originalGenerator = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/LostSkiesClouds/SourceShaders/CloudGenerator.asset"),
                originalPreset = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/LostSkiesClouds/Presets/normal.json"),
                compositeShader = Shader.Find("Hidden/ClouDream/LostSkiesComposite"),
                sun = RenderSettings.sun
            };
            passVolume.customPasses.Add(pass);
            Camera.main.transform.SetPositionAndRotation(new Vector3(1222, 3300, -6800), Quaternion.Euler(10, 15, 0));
            Camera.main.farClipPlane = 100000;
            Camera.main.GetComponent<HDAdditionalCameraData>().antialiasing = HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            EditorUtility.SetDirty(passVolume);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>{new EditorBuildSettingsScene(path, true)};
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (s.path != path)
                {
                    scenes.Add(s);
                }
            }

            EditorBuildSettings.scenes = scenes.ToArray();
            Selection.activeGameObject = go;
            CloudSkySetup.Configure();
            CloudLightingSetup.Configure();
        }

        /// <summary>카메라 렌더 결과를 PNG로 저장하고 임시 렌더 상태를 복원합니다.</summary>
        public static string Capture(string filename = "LostSkies-CloudSea", int width = 1280, int height = 720)
        {
            var camera = Camera.main;
            var oldTarget = camera.targetTexture;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.Create();
            var previous = RenderTexture.active;
            try
            {
                camera.targetTexture = rt;
                for (int i = 0; i < 4; i++)
                {
                    camera.Render();
                }

                RenderTexture.active = rt;
                var t = new Texture2D(width, height, TextureFormat.RGB24, false);
                t.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                t.Apply();
                Directory.CreateDirectory("Screenshots");
                string path = "Screenshots/" + filename + ".png";
                File.WriteAllBytes(path, t.EncodeToPNG());
                Object.DestroyImmediate(t);
                return Path.GetFullPath(path);
            }
            finally
            {
                camera.targetTexture = oldTarget;
                RenderTexture.active = previous;
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        /// <summary>임시 카메라로 GPU 출력을 렌더하고 노이즈와 투과율을 진단합니다.</summary>
        public static string Probe(float height = 5200, float pitch = 20, float density = 400000, float coverage = .248f)
        {
            var r = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/LostSkiesClouds/Runtime/CloudRaymarch.compute");
            var g = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/LostSkiesClouds/SourceShaders/CloudGenerator.asset");
            var p = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/LostSkiesClouds/Presets/normal.json");
            var go = new GameObject("Cloud GPU probe");
            var camera = go.AddComponent<Camera>();
            camera.enabled = false;
            camera.transform.position = new Vector3(1222, height, 248);
            camera.transform.rotation = Quaternion.Euler(pitch, 0, 0);
            camera.farClipPlane = 100000;
            camera.fieldOfView = 65;
            camera.aspect = 16f / 9;
            try
            {
                using (var renderer = new LostSkiesCloudRenderer(r, g, p))
                {
                    renderer.settings.density = density;
                    renderer.settings.coverageIntensity = coverage;
                    renderer.Resize(384, 216);
                    using (var cmd = new CommandBuffer())
                    {
                        renderer.Render(cmd, camera, new Vector3(.4f, .8f, .2f).normalized, new Color(1, .94f, .85f), 5);
                        Graphics.ExecuteCommandBuffer(cmd);
                    }

                    string result = Save(renderer.lighting, "Screenshots/LostSkies-Lighting.png", false) + "; " + Save(renderer.transmittance, "Screenshots/LostSkies-Transmittance.png", true);
                    return result + "; " + renderer.DiagnoseNoise();
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>GPU 출력을 읽어 값의 범위를 확인하고 진단 이미지를 저장합니다.</summary>
        public static string Save(RenderTexture rt, string path, bool alpha)
        {
            var req = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBAFloat);
            req.WaitForCompletion();
            if (req.hasError)
            {
                return "GPU readback error";
            }

            var data = req.GetData<Color>();
            var pixels = new Color[data.Length];
            float min = 1e30f, max = -1e30f;
            int invalid = 0;
            for (int i = 0; i < data.Length; i++)
            {
                var c = data[i];
                if (float.IsNaN(c.r) || float.IsInfinity(c.r))
                {
                    invalid++;
                }
                else
                {
                    min = Mathf.Min(min, c.r);
                    max = Mathf.Max(max, c.r);
                }

                float gain = 0.65f;
                if (alpha)
                {
                    gain = 1f;
                }

                pixels[i] = new Color(c.r * gain, c.g * gain, c.b * gain, 1);
            }

            var t = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
            t.SetPixels(pixels);
            t.Apply();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, t.EncodeToPNG());
            Object.DestroyImmediate(t);
            return path + " range=" + min + ".." + max + " invalid=" + invalid;
        }
    }
}
