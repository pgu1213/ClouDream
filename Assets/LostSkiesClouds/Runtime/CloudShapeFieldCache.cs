using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    /// <summary>필요한 상층 셀의 변위 전 렌더·조명 몸체 필드를 작은 독립 블록으로 공유합니다.</summary>
    internal sealed class CloudShapeFieldCache : IDisposable
    {
        // HLSL 계약: 40³ 블록 128개를 4×4×8로 배치합니다. R은 상세 몸체, G는 큰 몸체입니다.
        // 날씨 밀도·바람·렌더/조명 변위·지지 경계는 텍스처에 굽지 않고 매 조회에 적용합니다.
        private const int BlockSide = 40;
        private const int MaximumCells = 128;
        private const int DirectorySide = 32;
        private const float QueryRadius = 44000f;
        private const int DiagnosticCount = 12;

        private readonly Dictionary<Vector2Int, int> slots = new Dictionary<Vector2Int, int>();
        private readonly List<Candidate> candidates = new List<Candidate>(DirectorySide * DirectorySide);
        private readonly HashSet<Vector2Int> selected = new HashSet<Vector2Int>();
        private readonly List<Vector2Int> removals = new List<Vector2Int>();
        private readonly bool[] usedSlots = new bool[MaximumCells];
        private readonly int[] directoryData = new int[DirectorySide * DirectorySide];
        private readonly int[] changedSlots = new int[MaximumCells];
        private readonly Vector4[] cellData = new Vector4[MaximumCells];
        private readonly uint[] zeroDiagnostics = new uint[DiagnosticCount];
        private readonly int[] region = new int[4];
        private readonly ProfilingSampler buildSampler = new ProfilingSampler("Cloud.ShapeFieldBuild");

        private RenderTexture atlas;
        private ComputeBuffer directory;
        private ComputeBuffer cells;
        private ComputeBuffer buildSlots;
        private ComputeBuffer diagnostics;
        private bool valid;
        private bool diagnosticsEnabled;
        private Vector2Int origin;
        private Vector4 shapeKey;
        private Vector4 placementKey;
        private Vector4 heightKey;

        public bool Active { get; private set; }

        public int BuildCount { get; private set; }

        public int CachedCellCount { get; private set; }

        public long Bytes
        {
            get
            {
                if (atlas == null)
                {
                    return 0;
                }

                return (long)BlockSide * BlockSide * BlockSide * MaximumCells * 4
                    + DirectorySide * DirectorySide * 4 + MaximumCells * 20 + DiagnosticCount * 4;
            }
        }

        /// <summary>동일 셀의 슬롯을 보존하고 새로 들어온 몸체만 생성합니다. 밀도·바람 변화는 재생성하지 않습니다.</summary>
        public void Prepare(CommandBuffer commands, ComputeShader shader, CloudFormationProfile profile,
            Vector3 cameraPosition, bool enabled, bool renderDensity, float exactNearDistance, bool enableDiagnostics = false)
        {
            Active = false;
            shader.DisableKeyword("CLOUD_SHAPE_FIELD_CACHE");
            PrepareDiagnostics(commands, shader, enableDiagnostics);
            if (!enabled || profile == null || !SystemInfo.supports3DRenderTextures
                || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.RGHalf)
                || !profile.TryGetSkyCellParameters(out Vector4 placement, out Vector4 heights, out Vector4 shape))
            {
                ReleaseFields();
                return;
            }

            // RGHalf는 극단적으로 큰 사용자 반경의 비포화 필드를 표현할 수 없습니다.
            if (Mathf.Max(shape.y, heights.w) > 20000f)
            {
                ReleaseFields();
                return;
            }

            AllocateFields();
            float spacing = Mathf.Max(4000f, placement.x);
            Vector2Int center = new Vector2Int(Mathf.FloorToInt(cameraPosition.x / spacing),
                Mathf.FloorToInt(cameraPosition.z / spacing));
            Vector2Int nextOrigin = center - new Vector2Int(DirectorySide / 2, DirectorySide / 2);
            Vector4 nextPlacementKey = new Vector4(spacing, placement.y, placement.z, 0f);
            Vector4 nextHeightKey = new Vector4(0f, 0f, heights.z, heights.w);
            Vector4 nextShapeKey = new Vector4(shape.x, shape.y, 0f, 0f);
            bool shapeChanged = !valid || !shapeKey.Equals(nextShapeKey) || !heightKey.Equals(nextHeightKey)
                || placementKey.x != spacing || placementKey.z != placement.z;
            int changedCount = 0;
            if (shapeChanged || origin != nextOrigin || !placementKey.Equals(nextPlacementKey))
            {
                changedCount = UpdateDirectory(center, nextOrigin, placement, shapeChanged);
                commands.SetBufferData(directory, directoryData);
                // Only changed metadata is uploaded. Unchanged .z values are GPU-generated characteristic sizes.
                for (int index = 0; index < changedCount; index++)
                {
                    int slot = changedSlots[index];
                    commands.SetBufferData(cells, cellData, slot, slot, 1);
                }

                origin = nextOrigin;
                placementKey = nextPlacementKey;
                heightKey = nextHeightKey;
                shapeKey = nextShapeKey;
                valid = true;
            }

            region[0] = origin.x;
            region[1] = origin.y;
            region[2] = DirectorySide;
            region[3] = MaximumCells;
            commands.SetComputeIntParams(shader, "_CloudShapeFieldRegion", region);
            float nearDistance = Mathf.Max(0f, exactNearDistance);
            float detailEnabled = 0f;
            if (renderDensity)
            {
                detailEnabled = 1f;
            }

            commands.SetComputeVectorParam(shader, "_CloudShapeFieldSettings",
                new Vector4(nearDistance, nearDistance + Mathf.Max(500f, nearDistance * 0.5f), detailEnabled, BlockSide));
            shader.EnableKeyword("CLOUD_SHAPE_FIELD_CACHE");
            if (changedCount > 0)
            {
                commands.SetBufferData(buildSlots, changedSlots);
                int build = shader.FindKernel("BuildShapeFields");
                commands.SetComputeBufferParam(shader, build, "_CloudShapeFieldCells", cells);
                commands.SetComputeBufferParam(shader, build, "_CloudShapeFieldBuildSlots", buildSlots);
                commands.SetComputeTextureParam(shader, build, "_CloudShapeFieldWrite", atlas);
                using (new ProfilingScope(commands, buildSampler))
                {
                    commands.DispatchCompute(shader, build, BlockSide / 4, BlockSide / 4, BlockSide * changedCount / 4);
                }

                BuildCount++;
            }

            Active = true;
            CachedCellCount = slots.Count;
            BindKnownKernels(commands, shader);
        }

        /// <summary>계측은 실제 광선 64개 중 하나의 조회를 세며 생성 커널은 포함하지 않습니다.</summary>
        private void PrepareDiagnostics(CommandBuffer commands, ComputeShader shader, bool enabled)
        {
            if (diagnostics == null)
            {
                diagnostics = new ComputeBuffer(DiagnosticCount, sizeof(uint));
                diagnostics.SetData(zeroDiagnostics);
            }

            diagnosticsEnabled = enabled;
            int value = 0;
            if (enabled)
            {
                value = 1;
                commands.SetBufferData(diagnostics, zeroDiagnostics);
            }

            commands.SetComputeIntParam(shader, "_CloudCacheDiagnosticsEnabled", value);
            BindKnownKernels(commands, shader);
        }

        /// <summary>정해진 메모리 상한 안에서 atlas와 희소 주소 표를 한 번 할당합니다.</summary>
        private void AllocateFields()
        {
            if (atlas != null)
            {
                return;
            }

            atlas = new RenderTexture(BlockSide * 4, BlockSide * 4, 0, RenderTextureFormat.RGHalf);
            atlas.name = "Cloud sparse render and lighting body fields";
            atlas.dimension = TextureDimension.Tex3D;
            atlas.volumeDepth = BlockSide * 8;
            atlas.enableRandomWrite = true;
            atlas.filterMode = FilterMode.Bilinear;
            atlas.wrapMode = TextureWrapMode.Clamp;
            atlas.Create();
            directory = new ComputeBuffer(DirectorySide * DirectorySide, sizeof(int));
            cells = new ComputeBuffer(MaximumCells, 16);
            buildSlots = new ComputeBuffer(MaximumCells, sizeof(int));
        }

        /// <summary>42km 광선과 최대 약 1.1km 차폐 조회를 덮는 구역에서 가까운 점유 셀을 우선 보관합니다.</summary>
        private int UpdateDirectory(Vector2Int center, Vector2Int nextOrigin, Vector4 placement, bool reset)
        {
            candidates.Clear();
            selected.Clear();
            removals.Clear();
            float spacing = Mathf.Max(4000f, placement.x);
            float reach = QueryRadius + spacing * 1.5f;
            for (int z = 0; z < DirectorySide; z++)
            {
                for (int x = 0; x < DirectorySide; x++)
                {
                    Vector2Int cell = nextOrigin + new Vector2Int(x, z);
                    float distance = (cell - center).sqrMagnitude * spacing * spacing;
                    if (distance <= reach * reach && CellRandom(cell, placement.z, 0u) < placement.y)
                    {
                        Candidate candidate = new Candidate();
                        candidate.cell = cell;
                        candidate.distance = distance;
                        candidates.Add(candidate);
                    }
                }
            }

            candidates.Sort(CandidateComparer.Instance);
            int count = Mathf.Min(MaximumCells, candidates.Count);
            for (int index = 0; index < count; index++)
            {
                selected.Add(candidates[index].cell);
            }

            if (reset)
            {
                slots.Clear();
            }

            foreach (KeyValuePair<Vector2Int, int> entry in slots)
            {
                if (!selected.Contains(entry.Key))
                {
                    removals.Add(entry.Key);
                }
            }

            foreach (Vector2Int cell in removals)
            {
                slots.Remove(cell);
            }

            Array.Clear(usedSlots, 0, usedSlots.Length);
            Array.Clear(directoryData, 0, directoryData.Length);
            foreach (KeyValuePair<Vector2Int, int> entry in slots)
            {
                usedSlots[entry.Value] = true;
            }

            int changedCount = 0;
            int freeSlot = 0;
            for (int index = 0; index < count; index++)
            {
                Vector2Int cell = candidates[index].cell;
                if (!slots.TryGetValue(cell, out int slot))
                {
                    while (usedSlots[freeSlot])
                    {
                        freeSlot++;
                    }

                    slot = freeSlot;
                    usedSlots[slot] = true;
                    slots.Add(cell, slot);
                    cellData[slot] = new Vector4(cell.x, cell.y, 0f, 0f);
                    changedSlots[changedCount] = slot;
                    changedCount++;
                }

                Vector2Int local = cell - nextOrigin;
                directoryData[local.y * DirectorySide + local.x] = slot + 1;
            }

            return changedCount;
        }

        /// <summary>HLSL의 24비트 셀 난수와 동일한 정수 연산으로 비어 있는 셀에 GPU 블록을 낭비하지 않습니다.</summary>
        private static float CellRandom(Vector2Int cell, float seed, uint salt)
        {
            unchecked
            {
                uint value = (uint)cell.x * 747796405u + (uint)cell.y * 2891336453u;
                value += (uint)seed * 277803737u + salt * 1597334677u;
                value = (value ^ (value >> 16)) * 2246822519u;
                value = (value ^ (value >> 13)) * 3266489917u;
                value ^= value >> 16;
                return (value & 0x00ffffffu) / 16777216f;
            }
        }

        /// <summary>보통 광선·시간 광선·직접 프로브·차폐 생성에 동일한 공유 데이터를 연결합니다.</summary>
        private void BindKnownKernels(CommandBuffer commands, ComputeShader shader)
        {
            BindNamedKernel(commands, shader, "Raymarch");
            BindNamedKernel(commands, shader, "RaymarchTemporal");
            BindNamedKernel(commands, shader, "ProbeDensity");
            BindNamedKernel(commands, shader, "ProbeSpatialLighting");
            BindNamedKernel(commands, shader, "BuildSpatialLighting");
        }

        /// <summary>이전 셰이더 또는 선택적 커널도 안전하게 지원합니다.</summary>
        private void BindNamedKernel(CommandBuffer commands, ComputeShader shader, string name)
        {
            if (shader.HasKernel(name))
            {
                BindKernel(commands, shader, shader.FindKernel(name));
            }
        }

        /// <summary>추가 추적 커널에도 리소스를 연결할 수 있는 렌더러 통합 지점입니다.</summary>
        public void BindKernel(CommandBuffer commands, ComputeShader shader, int kernel)
        {
            if (diagnostics != null)
            {
                commands.SetComputeBufferParam(shader, kernel, "_CloudCacheCounters", diagnostics);
            }

            if (Active && atlas != null)
            {
                commands.SetComputeTextureParam(shader, kernel, "_CloudShapeFieldAtlas", atlas);
                commands.SetComputeBufferParam(shader, kernel, "_CloudShapeFieldDirectory", directory);
                commands.SetComputeBufferParam(shader, kernel, "_CloudShapeFieldCells", cells);
            }
        }

        /// <summary>명시적인 검사 프로브에도 실제 렌더와 같은 atlas와 주소 표를 제공합니다.</summary>
        public void BindProbe(ComputeShader shader, int kernel)
        {
            if (diagnostics == null)
            {
                diagnostics = new ComputeBuffer(DiagnosticCount, sizeof(uint));
                diagnostics.SetData(zeroDiagnostics);
                shader.SetInt("_CloudCacheDiagnosticsEnabled", 0);
            }

            shader.SetBuffer(kernel, "_CloudCacheCounters", diagnostics);

            if (Active && atlas != null)
            {
                shader.SetTexture(kernel, "_CloudShapeFieldAtlas", atlas);
                shader.SetBuffer(kernel, "_CloudShapeFieldDirectory", directory);
                shader.SetBuffer(kernel, "_CloudShapeFieldCells", cells);
            }
        }

        /// <summary>명시적인 진단 요청 때만 GPU 결과를 읽습니다. 일반 렌더에 동기 readback을 넣지 않습니다.</summary>
        public uint[] ReadDiagnostics()
        {
            uint[] result = new uint[DiagnosticCount];
            if (diagnosticsEnabled && diagnostics != null)
            {
                diagnostics.GetData(result);
            }

            return result;
        }

        /// <summary>기능을 끌 때 큰 GPU 메모리를 반환하고 다음 활성화에서 다시 채웁니다.</summary>
        private void ReleaseFields()
        {
            CloudRenderTargets.Release(atlas);
            atlas = null;
            directory?.Release();
            directory = null;
            cells?.Release();
            cells = null;
            buildSlots?.Release();
            buildSlots = null;
            slots.Clear();
            valid = false;
            Active = false;
            CachedCellCount = 0;
        }

        /// <summary>렌더러 수명이 끝나면 필드와 선택적 계측 리소스를 모두 반환합니다.</summary>
        public void Dispose()
        {
            ReleaseFields();
            diagnostics?.Release();
            diagnostics = null;
            buildSampler.Dispose();
        }

        private struct Candidate
        {
            public Vector2Int cell;

            public float distance;
        }

        private sealed class CandidateComparer : IComparer<Candidate>
        {
            public static readonly CandidateComparer Instance = new CandidateComparer();

            /// <summary>거리가 같은 셀도 고정 순서로 정렬해 슬롯 선택이 프레임 사이에서 흔들리지 않게 합니다.</summary>
            public int Compare(Candidate left, Candidate right)
            {
                int distance = left.distance.CompareTo(right.distance);
                if (distance != 0)
                {
                    return distance;
                }

                int z = left.cell.y.CompareTo(right.cell.y);
                if (z != 0)
                {
                    return z;
                }

                return left.cell.x.CompareTo(right.cell.x);
            }
        }
    }
}
