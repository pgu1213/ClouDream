using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>원래 GPU 생성기의 결과만 저장합니다. 노이즈 생성 원리를 추측하거나 새 식으로 교체하지 않습니다.</summary>
    public static class CloudNoiseBake
    {
        [Serializable]
        public sealed class Entry
        {
            public string resource;
            public int size;
            public int mipCount;
            public long bytes;
            public string[] sha256PerMip;
            public bool gpuRoundtripIdentical;

            public bool containsSpatialVariation;
        }

        [Serializable]
        public sealed class Report
        {
            public bool passed;
            public string graphicsApi;
            public string unityVersion;
            public string source = "Existing serialized CloudGenerator.asset GPU output; all RGBAHalf mip levels preserved.";
            public string error;
            public Entry[] entries;
        }

        private struct Request
        {
            public string kernel;

            public int size;

            public float scale;

            public int octaves;

            public float persistence;

            /// <summary>기존 렌더러의 생성 호출 한 개를 그대로 기술합니다.</summary>
            public Request(string kernelName, int resolution, float frequency, int octaveCount, float amplitude)
            {
                kernel = kernelName;
                size = resolution;
                scale = frequency;
                octaves = octaveCount;
                persistence = amplitude;
            }
        }

        /// <summary>여섯 원본 생성 설정의 모든 mip을 저장하고 다시 GPU에서 읽어 바이트 단위로 검증합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Bake Player Noise")]
        public static Report Run()
        {
            Report report = new Report();
            report.graphicsApi = SystemInfo.graphicsDeviceType.ToString();
            report.unityVersion = Application.unityVersion;
            List<Entry> entries = new List<Entry>();
            ComputeShader generator = null;
            try
            {
                ComputeShader original = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/LostSkiesClouds/SourceShaders/CloudGenerator.asset");
                Require(original != null, "Original serialized noise generator is unavailable.");
                generator = UnityEngine.Object.Instantiate(original);
                Request[] requests =
                {
                    new Request("WORLEY3D", 128, 4f, 6, 0.55f),
                    new Request("PERLIN3D", 64, 12f, 4, 0.75f),
                    new Request("WORLEY3D", 64, 12f, 5, 0.75f),
                    new Request("PERLIN3D", 64, 16f, 4, 0.5f),
                    new Request("CURL3D", 32, 8f, 3, 0.3f),
                    new Request("WORLEY3D", 128, 4f, 1, 0.5f)
                };
                Directory.CreateDirectory("Assets/LostSkiesClouds/Resources/CloudNoise");
                AssetDatabase.Refresh();
                foreach (Request request in requests)
                {
                    entries.Add(Bake(generator, request));
                }

                AssetDatabase.SaveAssets();
                report.passed = true;
            }
            catch (Exception exception)
            {
                report.error = exception.ToString();
                Debug.LogException(exception);
            }
            finally
            {
                if (generator != null)
                {
                    UnityEngine.Object.DestroyImmediate(generator);
                }

                report.entries = entries.ToArray();
                Directory.CreateDirectory("Docs/Validation");
                File.WriteAllText("Docs/Validation/CloudNoise-Bake.json", JsonUtility.ToJson(report, true));
            }

            return report;
        }

        /// <summary>원래 렌더 텍스처 설정과 dispatch를 그대로 실행해 새 텍스처 에셋에 옮깁니다.</summary>
        private static Entry Bake(ComputeShader generator, Request request)
        {
            Require(generator.HasKernel(request.kernel), "Original GPU kernel is unavailable: " + request.kernel);
            RenderTexture source = new RenderTexture(request.size, request.size, 0, RenderTextureFormat.ARGBHalf);
            source.dimension = TextureDimension.Tex3D;
            source.volumeDepth = request.size;
            source.enableRandomWrite = true;
            source.wrapMode = TextureWrapMode.Repeat;
            source.filterMode = FilterMode.Trilinear;
            source.useMipMap = true;
            source.autoGenerateMips = false;
            source.Create();
            Texture3D baked = null;
            try
            {
                int kernel = generator.FindKernel(request.kernel);
                generator.SetVector("_res", new Vector4(request.size, request.size, request.size, 0f));
                generator.SetVector("_scale", new Vector4(request.scale, request.scale, request.scale, 0f));
                generator.SetInt("_octaves", request.octaves);
                generator.SetFloat("_octaveScale", 2f);
                generator.SetFloat("_octaveMultiplier", request.persistence);
                generator.SetTexture(kernel, "_Noise3D", source);
                generator.Dispatch(kernel, request.size / 4, request.size / 4, request.size / 4);
                source.GenerateMips();

                Entry entry = new Entry();
                entry.resource = CloudNoiseLibrary.ResourceKey(request.kernel, request.size,
                    request.scale, request.octaves, request.persistence);
                entry.size = request.size;
                entry.mipCount = source.mipmapCount;
                entry.sha256PerMip = new string[entry.mipCount];
                baked = new Texture3D(request.size, request.size, request.size, TextureFormat.RGBAHalf, true);
                baked.name = Path.GetFileName(entry.resource);
                baked.wrapMode = TextureWrapMode.Repeat;
                baked.filterMode = FilterMode.Trilinear;
                Require(baked.mipmapCount == entry.mipCount, "Baked mip count differs from generated noise.");
                for (int mip = 0; mip < entry.mipCount; mip++)
                {
                    byte[] data = ReadMip(source, mip, request.size);
                    if (mip == 0)
                    {
                        entry.containsSpatialVariation = HasVariation(data);
                        Require(entry.containsSpatialVariation, "Noise generator returned a constant volume: " + entry.resource);
                    }

                    entry.sha256PerMip[mip] = Hash(data);
                    entry.bytes += data.Length;
                    baked.SetPixelData(data, mip);
                }

                baked.Apply(false, false);
                for (int mip = 0; mip < entry.mipCount; mip++)
                {
                    string actual = Hash(ReadMip(baked, mip, request.size));
                    Require(actual == entry.sha256PerMip[mip], "GPU noise roundtrip changed bytes: " + entry.resource + " mip " + mip);
                }

                entry.gpuRoundtripIdentical = true;
                string path = "Assets/LostSkiesClouds/Resources/" + entry.resource + ".asset";
                Texture3D existing = AssetDatabase.LoadAssetAtPath<Texture3D>(path);
                if (existing == null)
                {
                    AssetDatabase.CreateAsset(baked, path);
                    baked = null;
                }
                else
                {
                    EditorUtility.CopySerialized(baked, existing);
                    EditorUtility.SetDirty(existing);
                }

                return entry;
            }
            finally
            {
                source.Release();
                UnityEngine.Object.DestroyImmediate(source);
                if (baked != null)
                {
                    UnityEngine.Object.DestroyImmediate(baked);
                }
            }
        }

        /// <summary>3D 텍스처의 전체 mip 부피를 읽고 누락된 슬라이스 또는 형식 변환을 검사합니다.</summary>
        private static byte[] ReadMip(Texture texture, int mip, int baseSize)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(texture, mip, TextureFormat.RGBAHalf);
            request.WaitForCompletion();
            Require(!request.hasError, "Noise GPU readback failed at mip " + mip);
            int size = Mathf.Max(1, baseSize >> mip);
            int expected = size * size * size * 8;
            byte[] data = request.GetData<byte>().ToArray();
            if (data.Length != expected && request.layerCount > 1)
            {
                data = new byte[expected];
                int offset = 0;
                for (int layer = 0; layer < request.layerCount; layer++)
                {
                    byte[] values = request.GetData<byte>(layer).ToArray();
                    Require(offset + values.Length <= expected, "Readback returned too many noise bytes.");
                    Array.Copy(values, 0, data, offset, values.Length);
                    offset += values.Length;
                }

                Require(offset == expected, "Readback omitted noise slices.");
            }

            Require(data.Length == expected, "Noise mip byte count differs from RGBAHalf volume size.");
            return data;
        }

        /// <summary>출력 바이트의 SHA-256을 기록하여 Player 입력이 기존 Editor 입력과 같은지 감사할 수 있게 합니다.</summary>
        private static string Hash(byte[] data)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] hash = algorithm.ComputeHash(data);
                StringBuilder text = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash)
                {
                    text.Append(value.ToString("x2"));
                }

                return text.ToString();
            }
        }

        /// <summary>잘못된 빈 compute가 만든 전부 같은 픽셀을 정상 생성 결과로 저장하지 않습니다.</summary>
        private static bool HasVariation(byte[] data)
        {
            for (int index = 8; index < data.Length; index++)
            {
                if (data[index] != data[index % 8])
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>생성기 또는 데이터 계약 위반을 보고서에 명시적으로 기록합니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
