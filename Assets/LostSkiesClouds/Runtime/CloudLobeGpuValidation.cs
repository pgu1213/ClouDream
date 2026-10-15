using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    /// <summary>생산용 B 밀도 함수를 명시적 진단 호출에서만 GPU readback으로 검사합니다.</summary>
    public static class CloudLobeGpuValidation
    {
        private struct ObjectData
        {
            public Vector4 cell;

            public Vector4 minimum;

            public Vector4 size;
        }

        [Serializable]
        public sealed class Report
        {
            public int samples;
            public int occupiedCells;
            public int occupiedSamples;
            public bool finite = true;
            public bool boundariesClear = true;
            public float repeatDifference;
            public float preparedDensityDifference;
            public bool preparedMatches;
            public bool passed;
        }

        /// <summary>16개 셀의 65,536개 점에서 유한값·점유·경계·반복 입력을 검사합니다.</summary>
        public static Report Run(LostSkiesCloudPass source, CloudFormationProfile lobes, ComputeShader validationShader, Camera camera, ComputeShader preparingShader)
        {
            CloudHybridFrame frame = new CloudHybridFrame();
            if (!frame.Update(source, camera))
            {
                throw new InvalidOperationException("No supported shared formation.");
            }
            Report report = new Report();
            const int count = 16 * 16 * 16 * 16;
            Vector4[] positions = new Vector4[count];
            float spacing = frame.sky.placement.x;
            int cursor = 0;
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
                                positions[cursor++] = new Vector4((cellX + x / 15f) * spacing,
                                    Mathf.Lerp(2500f, 14500f, y / 15f), (cellZ + z / 15f) * spacing, 0f);
                            }
                        }
                    }
                }
            }
            for (int index = 0; index < count; index++)
            {
                int cellX = Mathf.FloorToInt(positions[index].x / spacing);
                int cellZ = Mathf.FloorToInt(positions[index].z / spacing);
                positions[index].w = cellX + 2 + (cellZ + 2) * 5;
            }
            ObjectData[] cells = new ObjectData[25];
            for (int z = -2; z <= 2; z++)
            {
                for (int x = -2; x <= 2; x++)
                {
                    cells[x + 2 + (z + 2) * 5].cell = new Vector4(1, x, z, 0);
                }
            }
            ComputeShader shader = UnityEngine.Object.Instantiate(validationShader);
            ComputeShader prepare = UnityEngine.Object.Instantiate(preparingShader);
            ComputeBuffer objects = new ComputeBuffer(25, 48);
            ComputeBuffer prepared = new ComputeBuffer(25 * 18, 16);
            ComputeBuffer input = new ComputeBuffer(count, 16);
            ComputeBuffer output = new ComputeBuffer(count, 16);
            CommandBuffer commands = new CommandBuffer();
            try
            {
                input.SetData(positions);
                Texture noise = CloudNoiseLibrary.Load("WORLEY3D", 128, 4f, 1, 0.5f);
                frame.BindGeneration(commands, shader, 0, noise);
                lobes.Apply(commands, shader);
                commands.SetComputeVectorParam(shader, "_HybridEnvironment", new Vector4(1f, 1f, 0f, frame.style.edge.w));
                commands.SetComputeVectorParam(shader, "_HybridWind", Vector4.zero);
                commands.SetComputeBufferParam(shader, 0, "_ValidationPositions", input);
                commands.SetComputeBufferParam(shader, 0, "_ValidationResults", output);
                commands.SetComputeIntParam(shader, "_ValidationCount", count);
                commands.DispatchCompute(shader, 0, (count + 63) / 64, 1, 1);
                Graphics.ExecuteCommandBuffer(commands);
                Vector4[] first = new Vector4[count];
                output.GetData(first);
                Graphics.ExecuteCommandBuffer(commands);
                Vector4[] second = new Vector4[count];
                output.GetData(second);
                bool occupied = false;
                for (int index = 0; index < count; index++)
                {
                    Vector4 value = first[index];
                    for (int component = 0; component < 4; component++)
                    {
                        report.finite &= !float.IsNaN(value[component]) && !float.IsInfinity(value[component]);
                        report.repeatDifference = Mathf.Max(report.repeatDifference, Mathf.Abs(value[component] - second[index][component]));
                    }
                    if (value.x > 0.001f)
                    {
                        report.occupiedSamples++;
                        occupied = true;
                    }
                    int x = index % 16;
                    int z = index / 16 % 16;
                    if (x == 0 || x == 15 || z == 0 || z == 15)
                    {
                        report.boundariesClear &= value.x <= 0.001f;
                    }
                    if ((index + 1) % 4096 == 0)
                    {
                        if (occupied)
                        {
                            report.occupiedCells++;
                        }
                        occupied = false;
                    }
                }
                commands.Clear();
                objects.SetData(cells);
                frame.BindGeneration(commands, prepare, 1, noise);
                lobes.Apply(commands, prepare);
                commands.SetComputeVectorParam(prepare, "_HybridCamera", frame.cameraPosition);
                commands.SetComputeVectorParam(prepare, "_HybridQuality", new Vector4(1000, 2000, 48, 0));
                commands.SetComputeBufferParam(prepare, 1, "_LobeObjects", objects);
                commands.SetComputeBufferParam(prepare, 1, "_LobePrepared", prepared);
                commands.SetComputeIntParam(prepare, "_LobeObjectCount", 25);
                commands.DispatchCompute(prepare, 1, 1, 1, 1);
                frame.BindGeneration(commands, shader, 1, noise);
                lobes.Apply(commands, shader);
                commands.SetComputeVectorParam(shader, "_HybridEnvironment", new Vector4(1, 1, 0, frame.style.edge.w));
                commands.SetComputeVectorParam(shader, "_HybridWind", Vector4.zero);
                commands.SetComputeBufferParam(shader, 1, "_LobeObjects", objects);
                commands.SetComputeBufferParam(shader, 1, "_LobePrepared", prepared);
                commands.SetComputeBufferParam(shader, 1, "_ValidationPositions", input);
                commands.SetComputeBufferParam(shader, 1, "_ValidationResults", output);
                commands.SetComputeIntParam(shader, "_ValidationCount", count);
                commands.DispatchCompute(shader, 1, (count + 63) / 64, 1, 1);
                Graphics.ExecuteCommandBuffer(commands);
                output.GetData(second);
                foreach (Vector4 value in second)
                {
                    report.finite &= !float.IsNaN(value.x) && !float.IsInfinity(value.x);
                    report.preparedDensityDifference = Mathf.Max(report.preparedDensityDifference, value.z);
                }
                report.preparedMatches = report.preparedDensityDifference < 0.001f;
                report.samples = count;
                report.passed = report.finite && report.boundariesClear && report.repeatDifference == 0f
                    && report.occupiedCells > 0 && report.occupiedSamples > 0 && report.preparedMatches;
            }
            finally
            {
                commands.Dispose();
                input.Dispose();
                output.Dispose();
                objects.Dispose();
                prepared.Dispose();
                CoreUtils.Destroy(shader);
                CoreUtils.Destroy(prepare);
            }
            return report;
        }
    }
}
