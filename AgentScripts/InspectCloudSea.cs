using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEditor;
using UnityEditor.SceneManagement;
using ClouDream;

public static class InspectCloudSea
{
    public static object Diagnose()
    {
        var w = Object.FindAnyObjectByType<CloudSeaWorld>(); var v = w.GetComponent<Volume>();
        var active = HDCamera.GetOrCreate(Camera.main).volumeStack.GetComponent<VolumetricClouds>();
        v.sharedProfile.TryGet<VolumetricClouds>(out var shared);
        return new { w.shaping, w.shapeScale, w.erosion, instantiated = v.HasInstantiatedProfile(), sharedShape = shared.shapeFactor.value, activeShape = active.shapeFactor.value, activeScale = active.shapeScale.value, activeErosion = active.erosionFactor.value, activeMap = active.cloudMap.value.name, activeLut = active.cloudLut.value.name, sharedMap = shared.cloudMap.value.name, mapPixel = w.weatherMap.GetPixel(256,256).ToString(), lutPixel = w.heightLut.GetPixel(5,30).ToString() };
    }
    public static async Task<string> Capture(string name = "CloudSea-Day", int view = 0)
    {
        var camera = Camera.main;
        var oldPosition = camera.transform.position; var oldRotation = camera.transform.rotation;
        var oldTarget = camera.targetTexture;
        var oldActive = RenderTexture.active;
        var target = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var result = new Texture2D(1600, 900, TextureFormat.RGB24, false);
        try
        {
            if (view == 1) camera.transform.SetPositionAndRotation(new Vector3(0, 3500, -3200), Quaternion.Euler(48, 15, 0));
            if (view == 2) camera.transform.SetPositionAndRotation(new Vector3(0, 920, -3200), Quaternion.Euler(0, 0, 0));
            if (view == 3) camera.transform.SetPositionAndRotation(new Vector3(0, 1650, -3200), Quaternion.Euler(8, 100, 0));
            camera.targetTexture = target;
            // Let the frame index advance between renders so HDRP's temporal reconstruction converges.
            for (int i = 0; i < 40; i++) { camera.Render(); EditorApplication.QueuePlayerLoopUpdate(); await Task.Delay(20); }
            RenderTexture.active = target;
            result.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0); result.Apply();
            Directory.CreateDirectory("Screenshots");
            File.WriteAllBytes("Screenshots/" + name + ".png", result.EncodeToPNG());
            return Path.GetFullPath("Screenshots/" + name + ".png");
        }
        finally
        {
            camera.targetTexture = oldTarget; camera.transform.SetPositionAndRotation(oldPosition, oldRotation);
            RenderTexture.active = oldActive;
            target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(result);
        }
    }
}
