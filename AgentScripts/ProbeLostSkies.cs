using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using System.Linq;

public static class ProbeLostSkies
{
    public static object Probe()
    {
        AssetDatabase.Refresh();
        var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/LostSkiesClouds/SourceShaders/CloudGenerator.asset");
        if (shader == null) return new { loaded = false };
        int kernel = shader.FindKernel("CONSTANT3D");
        var texture = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGBFloat) { dimension = TextureDimension.Tex3D, volumeDepth = 4, enableRandomWrite = true };
        texture.Create();
        try
        {
            shader.SetTexture(kernel, "_Noise3D", texture);
            shader.Dispatch(kernel, 1, 1, 1);
            var readback = AsyncGPUReadback.Request(texture); readback.WaitForCompletion();
            if (readback.hasError) return new { loaded = true, gpuReadback = false };
            var data = readback.GetData<float>();
            return new { loaded = true, graphics = SystemInfo.graphicsDeviceType.ToString(), shaderSupported = shader.IsSupported(kernel), first = data[0], min = data.ToArray().Min(), max = data.ToArray().Max(), sampleCount = data.Length, kernels = new [] { "PERLIN3D", "WORLEY3D", "PERLINWORLEY3D", "CURL3D" }.Select(s => new { name = s, present = shader.HasKernel(s) }).ToArray() };
        }
        finally { texture.Release(); Object.DestroyImmediate(texture); }
    }
}
