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
    /// <summary>저장된 원본 GPU 노이즈를 읽기만 하며 기존 bake 증거와 실제 GPU 입력을 비교합니다.</summary>
    public static class CloudPreservedNoiseValidation
    {
        [Serializable]
        public sealed class Entry
        {
            public string resource;

            public int size;

            public int mipCount;

            public long bytes;

            public string[] expectedSha256PerMip;

            public string[] actualSha256PerMip;

            public bool allMipHashesMatch;

            public bool baseVolumeHasSpatialVariation;

            public int baseSlicesWithSpatialVariation;

            public long checkedHalfChannels;

            public long nonFiniteHalfChannels;

            public bool passed;
        }

        [Serializable]
        public sealed class Report
        {
            public bool passed;

            public string source = CloudNoiseLibrary.SourceDescription;

            public string expectedManifest = "Docs/Validation/CloudNoise-Bake.json";

            public string expectedManifestSha256;

            public string originalSource;

            public string graphicsApi;

            public string unityVersion;

            public int resourcesChecked;

            public int mipsChecked;

            public long bytesChecked;

            public string error;

            public string notes = "Read-only GPU validation of the six preserved Resources Texture3D assets. "
                + "Every RGBAHalf channel of all 43 mip levels is checked for NaN/Infinity. "
                + "Spatial variation is required for each base volume; constant auxiliary channels are allowed. "
                + "Per-slice variation is recorded separately. No original generator dispatch, asset write, or re-bake occurs. "
                + "This diagnostic readback must not run during performance measurement.";

            public Entry[] entries;
        }

        private static readonly string[] ResourceKeys =
        {
            "CloudNoise/WORLEY3D_128_4_6_0.55",
            "CloudNoise/PERLIN3D_64_12_4_0.75",
            "CloudNoise/WORLEY3D_64_12_5_0.75",
            "CloudNoise/PERLIN3D_64_16_4_0.5",
            "CloudNoise/CURL3D_32_8_3_0.3",
            "CloudNoise/WORLEY3D_128_4_1_0.5"
        };

        public static Report LastReport { get; private set; }

        /// <summary>원본 bake의 SHA-256과 현재 GPU 텍스처를 비교하고 별도 파일에만 결과를 기록합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Optimization/Validate Preserved Noise")]
        public static Report Run()
        {
            Report report = new Report();
            LastReport = report;
            report.graphicsApi = SystemInfo.graphicsDeviceType.ToString();
            report.unityVersion = Application.unityVersion;
            List<Entry> entries = new List<Entry>();
            try
            {
                Require(SystemInfo.supportsAsyncGPUReadback, "GPU readback is unavailable.");
                byte[] evidenceBytes = File.ReadAllBytes(report.expectedManifest);
                report.expectedManifestSha256 = Hash(evidenceBytes);
                CloudNoiseBake.Report evidence = JsonUtility.FromJson<CloudNoiseBake.Report>(
                    File.ReadAllText(report.expectedManifest));
                Require(evidence != null && evidence.passed && evidence.entries != null
                    && evidence.entries.Length == ResourceKeys.Length, "Original six-volume bake evidence is incomplete.");
                report.originalSource = evidence.source;
                Dictionary<string, CloudNoiseBake.Entry> expected = new Dictionary<string, CloudNoiseBake.Entry>();
                foreach (CloudNoiseBake.Entry original in evidence.entries)
                {
                    Require(original != null && !string.IsNullOrEmpty(original.resource)
                        && !expected.ContainsKey(original.resource), "Original bake contains an invalid or duplicate resource.");
                    Require(original.gpuRoundtripIdentical && original.containsSpatialVariation,
                        "Original bake did not validate source variation and GPU roundtrip: " + original.resource);
                    expected.Add(original.resource, original);
                }

                // LoadAll은 실제 렌더러와 같은 여섯 입력 설정 및 샘플러 계약을 사용합니다.
                Texture3D[] textures = CloudNoiseLibrary.LoadAll();
                Require(textures.Length == ResourceKeys.Length, "Preserved noise resource count changed.");
                bool allPassed = true;
                for (int index = 0; index < textures.Length; index++)
                {
                    string key = ResourceKeys[index];
                    Require(expected.ContainsKey(key), "Original bake is missing " + key);
                    Texture3D texture = textures[index];
                    Require(Resources.Load<Texture3D>(key) == texture, "Resource order differs from renderer inputs: " + key);
                    CloudNoiseBake.Entry original = expected[key];
                    Require(original.size == texture.width && original.mipCount == texture.mipmapCount
                        && original.sha256PerMip != null && original.sha256PerMip.Length == texture.mipmapCount,
                        "Original dimensions or mip hashes differ from the preserved resource: " + key);

                    Entry entry = new Entry();
                    entry.resource = key;
                    entry.size = texture.width;
                    entry.mipCount = texture.mipmapCount;
                    entry.expectedSha256PerMip = (string[])original.sha256PerMip.Clone();
                    entry.actualSha256PerMip = new string[entry.mipCount];
                    entry.allMipHashesMatch = true;
                    entries.Add(entry);
                    for (int mip = 0; mip < entry.mipCount; mip++)
                    {
                        byte[] data = ReadMip(texture, mip, entry.size);
                        string actual = Hash(data);
                        entry.actualSha256PerMip[mip] = actual;
                        entry.allMipHashesMatch &= actual == entry.expectedSha256PerMip[mip];
                        entry.bytes += data.Length;
                        entry.checkedHalfChannels += data.Length / 2;
                        entry.nonFiniteHalfChannels += CountNonFiniteHalfChannels(data);
                        if (mip == 0)
                        {
                            entry.baseVolumeHasSpatialVariation = HasSpatialVariation(data, 0, data.Length);
                            int sliceBytes = entry.size * entry.size * 8;
                            for (int slice = 0; slice < entry.size; slice++)
                            {
                                if (HasSpatialVariation(data, slice * sliceBytes, sliceBytes))
                                {
                                    entry.baseSlicesWithSpatialVariation++;
                                }
                            }
                        }

                        report.mipsChecked++;
                        report.bytesChecked += data.Length;
                    }

                    entry.passed = entry.allMipHashesMatch && entry.baseVolumeHasSpatialVariation
                        && entry.nonFiniteHalfChannels == 0 && entry.bytes == original.bytes;
                    allPassed &= entry.passed;
                    report.resourcesChecked++;
                }

                Require(allPassed && report.resourcesChecked == 6 && report.mipsChecked == 43,
                    "Preserved noise differs from the original bake or has invalid half values. Inspect entries.");
                report.passed = true;
            }
            catch (Exception exception)
            {
                report.error = exception.ToString();
                Debug.LogException(exception);
            }
            finally
            {
                report.entries = entries.ToArray();
                Directory.CreateDirectory("Docs/Validation");
                File.WriteAllText("Docs/Validation/CloudNoise-PreservedValidation.json", JsonUtility.ToJson(report, true));
            }

            return report;
        }

        /// <summary>전체 mip 부피를 RGBAHalf 그대로 읽고 GPU readback의 슬라이스 누락을 거부합니다.</summary>
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
                    Require(offset + values.Length <= expected, "GPU readback returned too many noise bytes.");
                    Array.Copy(values, 0, data, offset, values.Length);
                    offset += values.Length;
                }

                Require(offset == expected, "GPU readback omitted noise slices.");
            }

            Require(data.Length == expected, "Noise mip byte count differs from the RGBAHalf volume.");
            return data;
        }

        /// <summary>little-endian binary16의 지수가 모두 1인 모든 채널을 NaN 또는 무한대로 계산합니다.</summary>
        private static long CountNonFiniteHalfChannels(byte[] data)
        {
            Require(data.Length % 8 == 0, "RGBAHalf readback is not a whole number of texels.");
            long invalid = 0;
            for (int index = 0; index < data.Length; index += 2)
            {
                int bits = data[index] | (data[index + 1] << 8);
                if ((bits & 0x7c00) == 0x7c00)
                {
                    invalid++;
                }
            }

            return invalid;
        }

        /// <summary>적어도 한 공간 위치의 RGBA 값이 첫 texel과 다른지 원시 바이트로 검사합니다.</summary>
        private static bool HasSpatialVariation(byte[] data, int offset, int count)
        {
            for (int index = 8; index < count; index++)
            {
                if (data[offset + index] != data[offset + index % 8])
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>원본 기록과 같은 소문자 SHA-256 형식으로 정확한 texel 및 mip 바이트를 비교합니다.</summary>
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

        /// <summary>필수 데이터 계약 위반을 성공으로 처리하지 않고 보고서에 보존합니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}

