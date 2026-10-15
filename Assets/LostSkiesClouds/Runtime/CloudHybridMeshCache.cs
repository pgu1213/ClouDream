using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    /// <summary>절대 셀의 불변 필드와 간접 메시를 보관합니다. 출력 카메라와 원점 이동은 키에 넣지 않습니다.</summary>
    internal sealed class CloudHybridMeshCache : IDisposable
    {
        /// <summary>동일 셀의 LOD 교체가 끝날 때까지 이전 버퍼를 보존합니다.</summary>
        internal sealed class Entry : IDisposable
        {
            public Vector3Int key;

            public Bounds bounds;

            public int grid;

            public int capacity;

            public RenderTexture field;

            public ComputeBuffer triangles;

            public ComputeBuffer counts;

            public ComputeBuffer arguments;

            public int stage;

            public int slice;

            public int lastUsed;

            public bool ready;

            public bool failed;

            public bool disposed;

            public uint triangleCount;

            public long Bytes { get; private set; }

            /// <summary>동일한 미터 격자를 밀도 조회와 등가면 생성에서 공유합니다.</summary>
            public Entry(Vector3Int cell, Bounds region, int resolution)
            {
                key = cell;
                bounds = region;
                grid = resolution;
                capacity = grid * grid * 24;
                if (key.z == 0)
                {
                    capacity = grid * grid * 4;
                }
                int side = grid + 1;
                field = new RenderTexture(side, side, 0, RenderTextureFormat.ARGBHalf);
                field.dimension = TextureDimension.Tex3D;
                field.volumeDepth = side;
                field.enableRandomWrite = true;
                field.wrapMode = TextureWrapMode.Clamp;
                field.filterMode = FilterMode.Bilinear;
                field.name = "Hybrid field " + key;
                field.Create();
                triangles = new ComputeBuffer(capacity, 72);
                counts = new ComputeBuffer(2, sizeof(uint));
                arguments = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
                counts.SetData(new uint[2]);
                arguments.SetData(new uint[] { 0, 1, 0, 0 });
                Bytes = (long)side * side * side * 8 + (long)capacity * 72 + 24;
            }

            /// <summary>버퍼 완성과 오버플로를 비동기 확인하며 파괴 이후 콜백은 무시합니다.</summary>
            public void OnCountsReady(AsyncGPUReadbackRequest request)
            {
                if (disposed)
                {
                    return;
                }

                if (request.hasError)
                {
                    failed = true;
                    return;
                }

                var data = request.GetData<uint>();
                triangleCount = data[0];
                failed = data[1] != 0 || triangleCount > capacity;
                ready = !failed;
            }

            /// <summary>생성 및 그리기에서 동일한 개별 셀 좌표를 바인딩합니다.</summary>
            public void Bind(CommandBuffer commands, ComputeShader shader, int kernel)
            {
                commands.SetComputeVectorParam(shader, "_HybridObject", new Vector4(key.z, key.x, key.y, grid));
                commands.SetComputeVectorParam(shader, "_HybridGridMin", bounds.min);
                commands.SetComputeVectorParam(shader, "_HybridGridSize", bounds.size);
                commands.SetComputeIntParam(shader, "_HybridTriangleCapacity", capacity);
                commands.SetComputeTextureParam(shader, kernel, "_HybridFieldWrite", field);
                commands.SetComputeTextureParam(shader, kernel, "_HybridField", field);
                commands.SetComputeBufferParam(shader, kernel, "_HybridTriangles", triangles);
                commands.SetComputeBufferParam(shader, kernel, "_HybridCounts", counts);
                commands.SetComputeBufferParam(shader, kernel, "_HybridArgs", arguments);
            }

            /// <summary>프레임 공유 입력 위에 개별 형태의 자료만 덮어씁니다.</summary>
            public void Bind(MaterialPropertyBlock properties)
            {
                properties.SetVector("_HybridObject", new Vector4(key.z, key.x, key.y, grid));
                properties.SetVector("_HybridGridMin", bounds.min);
                properties.SetVector("_HybridGridSize", bounds.size);
                properties.SetTexture("_HybridField", field);
                properties.SetBuffer("_HybridTriangles", triangles);
            }

            /// <summary>이 항목이 소유한 GPU 자원만 한 번 해제합니다.</summary>
            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                CloudRenderTargets.Release(field);
                triangles?.Dispose();
                counts?.Dispose();
                arguments?.Dispose();
                field = null;
                triangles = null;
                counts = null;
                arguments = null;
            }

            /// <summary>빈 셀은 논리적인 점유 결과만 보관하고 생성용 GPU 버퍼를 반환합니다.</summary>
            public void ReleaseEmptyData()
            {
                if (!ready || triangleCount != 0)
                {
                    return;
                }

                CloudRenderTargets.Release(field);
                triangles.Dispose();
                counts.Dispose();
                arguments.Dispose();
                field = null;
                triangles = null;
                counts = null;
                arguments = null;
                Bytes = 0;
            }
        }

        internal struct Candidate
        {
            public Vector3Int key;

            public Bounds bounds;

            public float distance;

            public int grid;

            public bool visible;
        }

        private readonly Dictionary<Vector3Int, Entry> entries = new Dictionary<Vector3Int, Entry>();

        private readonly List<Entry> pending = new List<Entry>();

        private readonly List<Candidate> candidates = new List<Candidate>();

        private readonly List<Vector3Int> removal = new List<Vector3Int>();

        public readonly List<Entry> Visible = new List<Entry>();

        public long Bytes { get; private set; }

        public int Builds { get; private set; }

        public int Failures { get; private set; }

        public int Missing { get; private set; }

        public int ResidencyMissing { get; private set; }

        public int PendingCount { get { return pending.Count; } }

        /// <summary>종류별 논리 셀과 실제 비어 있지 않은 메시 수를 진단에 제공합니다.</summary>
        public int[] PopulationCounts()
        {
            int[] counts = new int[6];
            foreach (Entry entry in entries.Values)
            {
                int offset = entry.key.z * 2;
                counts[offset]++;
                if (entry.triangleCount > 0)
                {
                    counts[offset + 1]++;
                }
            }

            return counts;
        }

        private int shapeHash;

        private int fieldKernel;

        private int meshKernel;

        private int finishKernel;

        private int oceanKernel;

        private int lastBuildFrame = -1;

        /// <summary>실제 실행할 세 생성 커널을 한 번 확인합니다.</summary>
        public CloudHybridMeshCache(ComputeShader shader)
        {
            fieldKernel = shader.FindKernel("BuildField");
            meshKernel = shader.FindKernel("EmitSurface");
            finishKernel = shader.FindKernel("FinishMesh");
            oceanKernel = shader.FindKernel("EmitOceanSurface");
        }

        /// <summary>원식이 바뀔 때만 무효화하고 가까운 후보부터 제한된 생성을 예약합니다.</summary>
        public void Prepare(CommandBuffer commands, ComputeShader shader, CloudHybridFrame frame,
            CloudHybridQualityProfile quality, Camera camera, Texture noise, long targetBytes)
        {
            if (shapeHash != frame.shapeHash)
            {
                Clear();
                shapeHash = frame.shapeHash;
            }

            CompleteJobs();
            CollectCandidates(frame, quality, camera);
            long budget = (long)quality.memoryMiB * 1048576 - targetBytes;
            PlanGrids(Math.Max(0, budget * 3 / 4));
            Trim(budget, quality.maximumCells);
            for (int i = 0; i < candidates.Count && pending.Count < quality.buildsInFlight; i++)
            {
                Candidate candidate = candidates[i];
                if (entries.TryGetValue(candidate.key, out Entry existing))
                {
                    existing.lastUsed = Time.frameCount;
                    if (existing.grid == candidate.grid)
                    {
                        continue;
                    }
                }

                if (IsPending(candidate.key) || entries.Count + pending.Count >= quality.maximumCells)
                {
                    continue;
                }

                long expected = ExpectedBytes(candidate.grid, candidate.key.z);
                if (Bytes + expected > budget)
                {
                    continue;
                }

                Entry entry = new Entry(candidate.key, candidate.bounds, candidate.grid);
                entry.lastUsed = Time.frameCount;
                pending.Add(entry);
                Bytes += entry.Bytes;
            }

            if (lastBuildFrame != Time.frameCount)
            {
                AdvanceJobs(commands, shader, frame, quality, noise);
                lastBuildFrame = Time.frameCount;
            }
            Visible.Clear();
            Missing = 0;
            ResidencyMissing = 0;
            foreach (Candidate candidate in candidates)
            {
                if (entries.TryGetValue(candidate.key, out Entry entry) && entry.ready)
                {
                    entry.lastUsed = Time.frameCount;
                    if (candidate.visible)
                    {
                        Visible.Add(entry);
                    }
                }
                else
                {
                    ResidencyMissing++;
                    if (candidate.visible)
                    {
                        Missing++;
                    }
                }
            }

            Visible.Sort(new BackToFront(frame.cameraPosition));
        }

        /// <summary>완료된 새 메시만 교체하고 오버플로 시 이전 메시를 그대로 보존합니다.</summary>
        private void CompleteJobs()
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Entry entry = pending[i];
                if (!entry.ready && !entry.failed)
                {
                    continue;
                }

                pending.RemoveAt(i);
                if (entry.failed)
                {
                    Bytes -= entry.Bytes;
                    entry.Dispose();
                    Failures++;
                    continue;
                }

                if (entries.TryGetValue(entry.key, out Entry previous))
                {
                    Bytes -= previous.Bytes;
                    previous.Dispose();
                }

                if (entry.triangleCount == 0)
                {
                    long previousBytes = entry.Bytes;
                    entry.ReleaseEmptyData();
                    Bytes -= previousBytes;
                }

                entries[entry.key] = entry;
                Builds++;
            }
        }

        /// <summary>한 프레임의 생성량을 Z 슬랩 수로 제한하며 동기 읽기를 하지 않습니다.</summary>
        private void AdvanceJobs(CommandBuffer commands, ComputeShader shader, CloudHybridFrame frame,
            CloudHybridQualityProfile quality, Texture noise)
        {
            int remaining = quality.buildSlicesPerFrame;
            foreach (Entry entry in pending)
            {
                if (remaining <= 0 || entry.stage >= 2)
                {
                    continue;
                }

                int kernel = fieldKernel;
                int totalSlices = entry.grid + 1;
                if (entry.stage == 1)
                {
                    kernel = meshKernel;
                    if (entry.key.z == 0)
                    {
                        kernel = oceanKernel;
                    }
                    totalSlices = entry.grid;
                }

                int slices = Mathf.Min(remaining, totalSlices - entry.slice);
                frame.BindGeneration(commands, shader, kernel, noise);
                entry.Bind(commands, shader, kernel);
                commands.SetComputeIntParam(shader, "_HybridSliceStart", entry.slice);
                commands.SetComputeIntParam(shader, "_HybridSliceCount", slices);
                int groupsY = (entry.grid + 4) / 4;
                if (kernel == oceanKernel)
                {
                    groupsY = 1;
                }

                commands.DispatchCompute(shader, kernel, (entry.grid + 4) / 4,
                    groupsY, (slices + 3) / 4);
                entry.slice += slices;
                remaining -= slices;
                if (entry.slice >= totalSlices)
                {
                    entry.slice = 0;
                    entry.stage++;
                    if (entry.stage == 2)
                    {
                        entry.Bind(commands, shader, finishKernel);
                        commands.DispatchCompute(shader, finishKernel, 1, 1, 1);
                        commands.RequestAsyncReadback(entry.counts, entry.OnCountsReady);
                    }
                }
            }
        }

        /// <summary>CPU는 난수를 복제하지 않고 보수적 셀 경계만 계산합니다. 실제 점유/형태는 GPU가 결정합니다.</summary>
        private void CollectCandidates(CloudHybridFrame frame, CloudHybridQualityProfile quality, Camera camera)
        {
            candidates.Clear();
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
            CollectKind(0, 8192f, frame.ocean.bounds.x, frame.ocean.bounds.x + frame.ocean.bounds.y * 0.48f
                + frame.style.shape.y + Mathf.Max(3200f, frame.style.shape.x * 0.22f) * 0.54f
                + frame.style.shape.z + frame.style.detail.y + 800f, frame, quality, planes);
            CollectKind(1, frame.sky.placement.x, frame.sky.bounds.x - frame.sky.bounds.w - 600f,
                frame.sky.bounds.y + frame.sky.bounds.w + 600f, frame, quality, planes);
            CollectKind(2, 14000f, 3200f, 11600f, frame, quality, planes);
            candidates.Sort(new NearestFirst());
        }

        /// <summary>교체용 여유 메모리를 남기고 먼 셀부터 LOD를 낮춰 필수 셀 전체가 예산 안에 들어오게 합니다.</summary>
        private void PlanGrids(long budget)
        {
            long expected = 0;
            foreach (Candidate candidate in candidates)
            {
                expected += ExpectedBytes(candidate.grid, candidate.key.z);
            }

            for (int pass = 0; pass < 2 && expected > budget; pass++)
            {
                for (int i = candidates.Count - 1; i >= 0 && expected > budget; i--)
                {
                    Candidate candidate = candidates[i];
                    if (candidate.grid > 16)
                    {
                        expected -= ExpectedBytes(candidate.grid, candidate.key.z);
                        candidate.grid /= 2;
                        expected += ExpectedBytes(candidate.grid, candidate.key.z);
                        candidates[i] = candidate;
                    }
                }
            }
        }

        /// <summary>풍경 범위와 프러스텀에 필요한 절대 셀만 고릅니다. 운해 바닥은 수직 범위를 유지합니다.</summary>
        private void CollectKind(int kind, float spacing, float bottom, float top,
            CloudHybridFrame frame, CloudHybridQualityProfile quality, Plane[] planes)
        {
            float residencyDistance = quality.viewDistance + quality.prefetchDistance;
            int radius = Mathf.CeilToInt(residencyDistance / spacing);
            int centerX = Mathf.FloorToInt(frame.cameraPosition.x / spacing);
            int centerZ = Mathf.FloorToInt(frame.cameraPosition.z / spacing);
            for (int z = centerZ - radius; z <= centerZ + radius; z++)
            {
                for (int x = centerX - radius; x <= centerX + radius; x++)
                {
                    Bounds bounds = new Bounds(new Vector3((x + 0.5f) * spacing,
                        (bottom + top) * 0.5f, (z + 0.5f) * spacing), new Vector3(spacing, top - bottom, spacing));
                    float distance = Vector3.Distance(frame.cameraPosition, bounds.ClosestPoint(frame.cameraPosition));
                    Bounds sceneBounds = bounds;
                    sceneBounds.center -= frame.origin;
                    if (distance > residencyDistance)
                    {
                        continue;
                    }

                    Candidate candidate = new Candidate();
                    candidate.key = new Vector3Int(x, z, kind);
                    candidate.bounds = bounds;
                    candidate.distance = distance;
                    candidate.grid = quality.GridForDistance(distance);
                    candidate.visible = distance <= quality.viewDistance
                        && GeometryUtility.TestPlanesAABB(planes, sceneBounds);
                    candidates.Add(candidate);
                }
            }
        }

        /// <summary>같은 셀의 중복 생성과 일시적인 두 LOD 작업을 막습니다.</summary>
        private bool IsPending(Vector3Int key)
        {
            foreach (Entry entry in pending)
            {
                if (entry.key == key)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>새로운 카메라 창 밖의 자원을 우선 해제합니다. 현재 프레임에 필요한 셀은 유지합니다.</summary>
        private void Trim(long budget, int maximum)
        {
            removal.Clear();
            foreach (KeyValuePair<Vector3Int, Entry> pair in entries)
            {
                bool visible = false;
                foreach (Candidate candidate in candidates)
                {
                    if (candidate.key == pair.Key)
                    {
                        visible = true;
                        break;
                    }
                }

                if (!visible && (Bytes > budget * 0.7f || entries.Count >= maximum
                    || Time.frameCount - pair.Value.lastUsed > 300))
                {
                    removal.Add(pair.Key);
                }
            }

            foreach (Vector3Int key in removal)
            {
                Entry entry = entries[key];
                Bytes -= entry.Bytes;
                entry.Dispose();
                entries.Remove(key);
            }

            // 해상도나 두 번째 카메라가 예산을 줄이면 먼 자료를 해제하고 준비 중 기존 경로로 복귀합니다.
            while (Bytes > budget && entries.Count > 0)
            {
                Entry selected = null;
                float selectedDistance = -1f;
                foreach (Entry entry in entries.Values)
                {
                    float distance = float.MaxValue;
                    foreach (Candidate candidate in candidates)
                    {
                        if (candidate.key == entry.key)
                        {
                            distance = candidate.distance;
                            break;
                        }
                    }

                    if (entry.Bytes > 0 && distance > selectedDistance)
                    {
                        selected = entry;
                        selectedDistance = distance;
                    }
                }

                if (selected == null)
                {
                    break;
                }

                Bytes -= selected.Bytes;
                entries.Remove(selected.key);
                selected.Dispose();
            }

            for (int i = pending.Count - 1; i >= 0 && Bytes > budget; i--)
            {
                Entry entry = pending[i];
                pending.RemoveAt(i);
                Bytes -= entry.Bytes;
                entry.Dispose();
            }
        }

        /// <summary>할당 전에 교체 중인 이전 자료를 포함한 상한을 검사합니다.</summary>
        private static long ExpectedBytes(int grid, int kind)
        {
            long side = grid + 1;
            int trianglesPerColumn = 24;
            if (kind == 0)
            {
                trianglesPerColumn = 4;
            }

            return side * side * side * 8 + (long)grid * grid * trianglesPerColumn * 72 + 24;
        }

        /// <summary>형태 변경과 패스 종료가 같은 소유 자원 해제 경로를 사용합니다.</summary>
        private void Clear()
        {
            foreach (Entry entry in entries.Values)
            {
                entry.Dispose();
            }

            foreach (Entry entry in pending)
            {
                entry.Dispose();
            }

            entries.Clear();
            pending.Clear();
            Visible.Clear();
            Bytes = 0;
        }

        /// <summary>공유 노이즈는 소유하지 않으며 생성한 필드와 버퍼만 해제합니다.</summary>
        public void Dispose()
        {
            Clear();
        }

        /// <summary>카메라 가까운 경계부터 생성 우선순위를 고릅니다.</summary>
        private sealed class NearestFirst : IComparer<Candidate>
        {
            /// <summary>동일 거리에서도 셀 ID로 결정적인 순서를 유지합니다.</summary>
            public int Compare(Candidate first, Candidate second)
            {
                int order = first.distance.CompareTo(second.distance);
                if (order == 0)
                {
                    order = first.key.z.CompareTo(second.key.z);
                }

                if (order == 0)
                {
                    order = first.key.x.CompareTo(second.key.x);
                }

                if (order == 0)
                {
                    order = first.key.y.CompareTo(second.key.y);
                }

                return order;
            }
        }

        /// <summary>프리멀티플라이 합성을 위해 먼 몸체부터 적용합니다.</summary>
        private sealed class BackToFront : IComparer<Entry>
        {
            private readonly Vector3 camera;

            /// <summary>현재 절대 카메라 위치를 고정합니다.</summary>
            public BackToFront(Vector3 position)
            {
                camera = position;
            }

            /// <summary>셀 중앙의 거리로 서로 분리된 몸체의 합성 순서를 정합니다.</summary>
            public int Compare(Entry first, Entry second)
            {
                return (second.bounds.center - camera).sqrMagnitude.CompareTo((first.bounds.center - camera).sqrMagnitude);
            }
        }
    }
}
