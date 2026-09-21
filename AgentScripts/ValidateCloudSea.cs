using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEditor;
using UnityEditor.SceneManagement;
using ClouDream;

public static class ValidateCloudSea
{
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static object Validate()
    {
        var world = UnityEngine.Object.FindAnyObjectByType<CloudSeaWorld>();
        Check(world != null, "CloudSeaWorld missing");
        Check(Camera.main != null && Camera.main.GetComponent<CloudSeaFlight>() != null, "Flight camera missing");
        Check(RenderSettings.sun != null, "Sun missing");
        Check(((HDRenderPipelineAsset)GraphicsSettings.currentRenderPipeline).currentPlatformRenderPipelineSettings.supportVolumetricClouds, "Pipeline cloud support disabled");
        var a = world.GenerateWeatherMap(64); var b = world.GenerateWeatherMap(64);
        float minCoverage = 1, maxType = 0, minType = 1, seam = 0;
        var pixelsA = a.GetPixels32(); var pixelsB = b.GetPixels32();
        Check(pixelsA.SequenceEqual(pixelsB), "Same seed produced different weather");
        foreach (var p in a.GetPixels()) { minCoverage = Mathf.Min(minCoverage, p.r); maxType = Mathf.Max(maxType, p.b); minType = Mathf.Min(minType, p.b); Check(p.a == 1, "Height clipping mask invalid"); }
        for (int i = 0; i < 64; i++) { seam = Mathf.Max(seam, Mathf.Abs(a.GetPixel(0,i).b - a.GetPixel(63,i).b)); seam = Mathf.Max(seam, Mathf.Abs(a.GetPixel(i,0).b - a.GetPixel(i,63).b)); }
        Check(seam < 0.005f, "Weather map edges do not tile seamlessly");
        Check(minCoverage > 0.9f, "Cloud sea weather contains low coverage gaps");
        Check(maxType > 0.8f && minType < 0.2f, "Insufficient variation in tower heights");
        var lut = world.GenerateHeightLut();
        for (int x = 0; x < lut.width; x++) { Check(lut.GetPixel(x, 0).r < 0.001f, "Cloud base edge not faded"); Check(lut.GetPixel(x,lut.height-1).r < 0.001f, "Cloud top edge not faded"); Check(lut.GetPixel(x,10).r > 0.99f, "Lower sea is not continuous across cloud types"); }
        int originalSeed = world.seed; world.seed++;
        var different = world.GenerateWeatherMap(64); world.seed = originalSeed;
        Check(!pixelsA.SequenceEqual(different.GetPixels32()), "Changing seed has no effect");
        UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b); UnityEngine.Object.DestroyImmediate(lut); UnityEngine.Object.DestroyImmediate(different);
        var deps = AssetDatabase.GetDependencies("Assets/CloudSea/Scenes/CloudSea.unity");
        Check(!deps.Any(p => p.Contains("Lost_Skies") || p.Contains("Expanse")), "Reference resource dependency present");
        foreach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            Check(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go) == 0, "Missing scene script");
        return new { passed = true, deterministicSeed = true, differentSeedsDiffer = true, minCoverage, minType, maxType, coarseMapSeamDifference = seam, continuousLowerProfile = true, sceneDependencies = deps.Length };
    }

    public static string FinalizeAssets()
    {
        var world = UnityEngine.Object.FindAnyObjectByType<CloudSeaWorld>();
        ClouDream.Editor.CloudSeaWorldEditor.Bake(world);
        world.Apply();
        EditorSceneManager.SaveOpenScenes(); AssetDatabase.SaveAssets();
        return "Generated textures baked and scene saved.";
    }

    public static async Task<object> FlightTest()
    {
        Check(Application.isPlaying, "Flight test needs Play Mode");
        var camera = Camera.main; var keyboard = InputSystem.AddDevice<Keyboard>();
        Check(keyboard != null, "Keyboard unavailable");
        var before = camera.transform.position;
        bool previousBackground = Application.runInBackground;
        var previousInputRouting = InputSystem.settings.editorInputBehaviorInPlayMode;
        var previousFocusBehavior = InputSystem.settings.backgroundBehavior;
        InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
        InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
        Application.runInBackground = true;
        int beforeFrame = Time.frameCount;
        try
        {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W)); InputSystem.Update();
            for (int i = 0; i < 15; i++) { EditorApplication.QueuePlayerLoopUpdate(); await Task.Delay(30); }
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            await Task.Delay(100);
            float moved = Vector3.Distance(before, camera.transform.position);
            Check(moved > 2, "Forward key did not move flight camera; frames=" + (Time.frameCount - beforeFrame) + ", mouse=" + (Mouse.current != null));
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.R));
            await Task.Delay(100); InputSystem.QueueStateEvent(keyboard, new KeyboardState()); await Task.Delay(100);
            Check(Vector3.Distance(before, camera.transform.position) < 0.1f, "Reset did not restore camera");
            return new { passed = true, movedMetres = moved, resetRestoredPosition = true };
        }
        finally
        {
            InputSystem.RemoveDevice(keyboard); camera.transform.position = before; Application.runInBackground = previousBackground;
            InputSystem.settings.editorInputBehaviorInPlayMode = previousInputRouting;
            InputSystem.settings.backgroundBehavior = previousFocusBehavior;
        }
    }
}
