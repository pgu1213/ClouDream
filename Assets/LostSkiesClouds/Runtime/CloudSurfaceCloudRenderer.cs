using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies
{
    /// <summary>B안: 3D 필드 없이 로브 표면을 깊이로 가리고 타일 목록의 근거리 밀도만 한 번 적분합니다.</summary>
    internal sealed class CloudSurfaceCloudRenderer : CloudComparisonRenderer
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct NearObject
        {
            public Vector4 cell;

            public Vector4 minimum;

            public Vector4 size;
        }

        /// <summary>동일 카메라의 전체 표면·먼 표면·최종 L/T와 국소 타일 목록만 보관합니다.</summary>
        private sealed class Targets : IDisposable
        {
            public readonly int width;

            public readonly int height;

            public readonly int tilesX;

            public readonly int tilesY;

            public readonly RenderTexture surface;

            public readonly RenderTexture distance;

            public readonly RenderTexture farSurface;

            public readonly RenderTexture farDistance;

            public readonly RenderTexture accumulated;

            public readonly RenderTexture sceneDepth;

            public readonly ComputeBuffer objects, counts, ids, prepared;

            public int used;

            public long Bytes { get { return (long)width * height * 52 + 64 * (48 + 18 * 16) + (long)tilesX * tilesY * 33 * 4; } }

            /// <summary>각 표면의 깊이와 카메라별 타깃 소유권을 분리합니다.</summary>
            public Targets(int width, int height)
            {
                this.width = width;
                this.height = height;
                tilesX = (width + 15) / 16;
                tilesY = (height + 15) / 16;
                surface = Create(width, height, RenderTextureFormat.ARGBHalf, 24, "B all surfaces");
                distance = Create(width, height, RenderTextureFormat.RGFloat, 0, "B all distances");
                farSurface = Create(width, height, RenderTextureFormat.ARGBHalf, 24, "B far surfaces");
                farDistance = Create(width, height, RenderTextureFormat.RGFloat, 0, "B far distances");
                accumulated = Create(width, height, RenderTextureFormat.ARGBHalf, 0, "B linear L T");
                sceneDepth = Create(width, height, RenderTextureFormat.RFloat, 0, "B scene depth");
                objects = new ComputeBuffer(64, 48);
                prepared = new ComputeBuffer(64 * 18, 16);
                counts = new ComputeBuffer(tilesX * tilesY, 4);
                ids = new ComputeBuffer(tilesX * tilesY * 32, 4);
            }

            /// <summary>선형 렌더 타깃을 실제 출력 크기로 할당합니다.</summary>
            private static RenderTexture Create(int width, int height, RenderTextureFormat format, int depth, string name)
            {
                RenderTexture result = new RenderTexture(width, height, depth, format, RenderTextureReadWrite.Linear);
                result.name = name;
                result.filterMode = FilterMode.Bilinear;
                result.wrapMode = TextureWrapMode.Clamp;
                result.Create();
                return result;
            }

            /// <summary>resize·카메라 퇴출·패스 종료가 같은 자원 해제 경로를 사용합니다.</summary>
            public void Dispose()
            {
                CloudRenderTargets.Release(surface);
                CloudRenderTargets.Release(distance);
                CloudRenderTargets.Release(farSurface);
                CloudRenderTargets.Release(farDistance);
                CloudRenderTargets.Release(accumulated);
                CloudRenderTargets.Release(sceneDepth);
                objects.Dispose();
                prepared.Dispose();
                counts.Dispose();
                ids.Dispose();
            }
        }

        private readonly Dictionary<int, Targets> cameras = new Dictionary<int, Targets>();

        private readonly CloudCellView cells = new CloudCellView();

        private readonly CloudHybridFrame frame = new CloudHybridFrame();

        private readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();

        private readonly RenderTargetIdentifier[] mrt = new RenderTargetIdentifier[2];

        private readonly NearObject[] near = new NearObject[64];

        private readonly Material surface, composite;

        private readonly ComputeShader tiles;

        private readonly ComputeShader oceanGenerator;

        private readonly Dictionary<Vector3Int, ComputeBuffer> oceanSurfaces = new Dictionary<Vector3Int, ComputeBuffer>();

        private readonly List<Vector3Int> staleOcean = new List<Vector3Int>();

        private int oceanHash;

        private readonly Texture3D noise;

        private readonly CloudFormationProfile profile;

        private readonly int[] population = new int[6];

        private int nearCount;

        public override bool Ready { get; protected set; }

        public override string Status { get; protected set; } = "B preparing";

        public override int DrawnObjects { get; protected set; }

        public override int Allocations { get; protected set; }

        public override RenderTexture LastOutput { get; protected set; }

        public override int Builds { get { return 0; } }

        public override int Failures { get { return 0; } }

        public override int Missing { get { return 0; } }

        public override int ResidencyMissing { get { return 0; } }

        public override int Pending { get { return 0; } }

        public override long Bytes
        {
            get
            {
                long result = (long)oceanSurfaces.Count * 17 * 17 * 32;
                foreach (Targets target in cameras.Values)
                {
                    result += target.Bytes;
                }
                return result;
            }
        }

        /// <summary>기존 환경·노이즈만 재사용하고 B 전용 표면/적분/타일 커널을 소유합니다.</summary>
        public CloudSurfaceCloudRenderer(Shader surfaceShader, Shader compositeShader, ComputeShader tileShader,
            CloudFormationProfile lobeProfile, ComputeShader oceanShader) : base("Surface")
        {
            surface = CoreUtils.CreateEngineMaterial(surfaceShader);
            composite = CoreUtils.CreateEngineMaterial(compositeShader);
            tiles = UnityEngine.Object.Instantiate(tileShader);
            oceanGenerator = UnityEngine.Object.Instantiate(oceanShader);
            profile = lobeProfile;
            noise = CloudNoiseLibrary.Load("WORLEY3D", 128, 4f, 1, 0.5f);
        }

        /// <summary>같은 카메라·환경을 준비하고 근거리 후보의 ID·경계만 전송합니다.</summary>
        public override bool Prepare(CustomPassContext context, LostSkiesCloudPass source, CloudHybridQualityProfile quality)
        {
            Camera camera = context.hdCamera.camera;
            Ready = frame.Update(source, camera) && profile != null;
            if (!Ready || !profile.TryGetSurfaceLobes(out Vector4 body, out Vector4 detail))
            {
                Status = "B / unsupported profile";
                return false;
            }
            quality.Sanitize();
            int width = Mathf.Max(64, Mathf.RoundToInt(context.hdCamera.actualWidth * quality.resolutionScale));
            int height = Mathf.Max(64, Mathf.RoundToInt(context.hdCamera.actualHeight * quality.resolutionScale));
            Targets target = GetTargets(camera, width, height);
            frame.BindRendering(properties, camera, quality, noise, width, height);
            properties.SetVector("_LobeBody", body);
            properties.SetVector("_LobeDetail", detail);
            properties.SetFloat("_SurfaceRepresentation", 1f);
            properties.SetFloat("_HybridAnalytic", 1f);
            nearCount = 0;
            Array.Clear(population, 0, population.Length);
            if (View != CloudComparisonView.Off)
            {
                cells.Update(frame, quality, camera);
                PrepareOcean(context.cmd);
                bool hasOcean = false;
                foreach (CloudHybridMeshCache.Candidate cell in cells.candidates)
                {
                    if (cell.visible)
                    {
                        population[cell.key.z * 2]++;
                        population[cell.key.z * 2 + 1]++;
                    }
                    if (cell.distance > quality.volumeFar + 400f || nearCount >= near.Length)
                    {
                        continue;
                    }
                    if (cell.key.z == 0 && hasOcean)
                    {
                        continue;
                    }
                    NearObject cloud = new NearObject();
                    cloud.cell = new Vector4(cell.key.z, cell.key.x, cell.key.y, 0f);
                    cloud.minimum = cell.bounds.min;
                    cloud.size = cell.bounds.size;
                    if (cell.key.z == 0)
                    {
                        hasOcean = true;
                        float radius = quality.volumeFar + 2000f;
                        cloud.minimum.x = frame.cameraPosition.x - radius;
                        cloud.minimum.z = frame.cameraPosition.z - radius;
                        cloud.size.x = radius * 2f;
                        cloud.size.z = radius * 2f;
                    }
                    near[nearCount++] = cloud;
                }
            }
            bool volume = View == CloudComparisonView.Full || View == CloudComparisonView.SurfaceAndVolume;
            if (volume && nearCount > 0)
            {
                using (new ProfilingScope(context.cmd, buildSampler))
                {
                    context.cmd.SetBufferData(target.objects, near, 0, 0, nearCount);
                    frame.BindGeneration(context.cmd, tiles, 1, noise);
                    profile.Apply(context.cmd, tiles);
                    context.cmd.SetComputeVectorParam(tiles, "_HybridCamera", frame.cameraPosition);
                    context.cmd.SetComputeVectorParam(tiles, "_HybridQuality", new Vector4(quality.volumeNear, quality.volumeFar, quality.primarySamples, 0));
                    context.cmd.SetComputeBufferParam(tiles, 1, "_LobeObjects", target.objects);
                    context.cmd.SetComputeBufferParam(tiles, 1, "_LobePrepared", target.prepared);
                    context.cmd.SetComputeIntParam(tiles, "_LobeObjectCount", nearCount);
                    context.cmd.DispatchCompute(tiles, 1, 1, 1, 1);
                    context.cmd.SetComputeBufferParam(tiles, 0, "_LobeObjects", target.objects);
                    context.cmd.SetComputeBufferParam(tiles, 0, "_LobeTileCounts", target.counts);
                    context.cmd.SetComputeBufferParam(tiles, 0, "_LobeTileIds", target.ids);
                    context.cmd.SetComputeVectorParam(tiles, "_LobeTileSize", new Vector4(target.tilesX, target.tilesY, 0f, 0f));
                    context.cmd.SetComputeVectorParam(tiles, "_LobeOrigin", frame.origin);
                    Matrix4x4 projection = GL.GetGPUProjectionMatrix(camera.nonJitteredProjectionMatrix, true) * camera.worldToCameraMatrix;
                    context.cmd.SetComputeMatrixParam(tiles, "_LobeViewProjection", projection);
                    context.cmd.SetComputeIntParam(tiles, "_LobeObjectCount", nearCount);
                    context.cmd.DispatchCompute(tiles, 0, (target.tilesX + 7) / 8, (target.tilesY + 7) / 8, 1);
                }
            }
            properties.SetInt("_LobeObjectCount", nearCount);
            properties.SetFloat("_LobeVolumeEnabled", 0f);
            if (volume)
            {
                properties.SetFloat("_LobeVolumeEnabled", 1f);
            }
            properties.SetBuffer("_LobeObjects", target.objects);
            properties.SetBuffer("_LobePrepared", target.prepared);
            properties.SetBuffer("_LobeTileCounts", target.counts);
            properties.SetBuffer("_LobeTileIds", target.ids);
            properties.SetVector("_LobeTileSize", new Vector4(target.tilesX, target.tilesY, 0f, 0f));
            Status = "B / lobe surfaces + one local pass / near " + nearCount;
            LastOutput = target.accumulated;
            return true;
        }

        /// <summary>전체 셀을 공통 깊이로 그리고 단일 국소 적분과 기존 노출 합성을 적용합니다.</summary>
        public override void Render(CustomPassContext context, CloudHybridQualityProfile quality)
        {
            Targets target = cameras[context.hdCamera.camera.GetEntityId().GetHashCode()];
            DrawnObjects = 0;
            context.cmd.DisableScissorRect();
            context.cmd.SetRenderTarget(target.sceneDepth);
            context.cmd.SetViewport(new Rect(0f, 0f, target.width, target.height));
            CoreUtils.DrawFullScreen(context.cmd, composite, properties, 2);
            if (View != CloudComparisonView.Off)
            {
                using (new ProfilingScope(context.cmd, surfaceSampler))
                {
                    DrawSurfaces(context.cmd, target, false);
                    if (View != CloudComparisonView.SurfaceOnly)
                    {
                        DrawSurfaces(context.cmd, target, true);
                    }
                }
            }
            context.cmd.SetRenderTarget(target.accumulated);
            context.cmd.ClearRenderTarget(false, true, new Color(0f, 0f, 0f, 1f));
            if (View != CloudComparisonView.Off)
            {
                properties.SetTexture("_HybridSurfaceLight", target.surface);
                properties.SetTexture("_HybridSurfaceDepth", target.distance);
                properties.SetTexture("_LobeFarLight", target.farSurface);
                properties.SetTexture("_LobeFarDepth", target.farDistance);
                using (new ProfilingScope(context.cmd, volumeSampler))
                {
                    CoreUtils.DrawFullScreen(context.cmd, composite, properties, 0);
                }
            }
            properties.SetTexture("_HybridAccumulation", target.accumulated);
            properties.SetTexture("_HybridLowSceneDepth", target.sceneDepth);
            CoreUtils.SetRenderTarget(context.cmd, context.cameraColorBuffer);
            context.cmd.SetViewport(new Rect(0f, 0f, context.hdCamera.actualWidth, context.hdCamera.actualHeight));
            using (new ProfilingScope(context.cmd, compositeSampler))
            {
                CoreUtils.DrawFullScreen(context.cmd, composite, properties, 1);
            }
        }

        /// <summary>셀별 clear나 볼륨 패스 없이 모든 로브가 하나의 깊이 버퍼를 공유합니다.</summary>
        private void DrawSurfaces(CommandBuffer commands, Targets target, bool farOnly)
        {
            RenderTexture color = target.surface;
            RenderTexture distance = target.distance;
            properties.SetFloat("_LobeFarOnly", 0f);
            if (farOnly)
            {
                color = target.farSurface;
                distance = target.farDistance;
                properties.SetFloat("_LobeFarOnly", 1f);
            }
            mrt[0] = color;
            mrt[1] = distance;
            commands.SetRenderTarget(mrt, color);
            commands.ClearRenderTarget(true, true, new Color(0f, 0f, 0f, 1f));
            for (int item = 0; item < cells.candidates.Count; item++)
            {
                int index = item;
                if (ReverseOrder)
                {
                    index = cells.candidates.Count - 1 - item;
                }
                CloudHybridMeshCache.Candidate cell = cells.candidates[index];
                if (!cell.visible)
                {
                    continue;
                }
                int segments = 6;
                if (cell.distance < 8000f)
                {
                    segments = 12;
                }
                properties.SetVector("_HybridObject", new Vector4(cell.key.z, cell.key.x, cell.key.y, 16));
                properties.SetVector("_HybridGridMin", cell.bounds.min);
                properties.SetVector("_HybridGridSize", cell.bounds.size);
                properties.SetFloat("_SurfaceSegments", segments);
                properties.SetFloat("_SurfaceOceanCached", 0f);
                if (cell.key.z == 0 && oceanSurfaces.TryGetValue(cell.key, out ComputeBuffer ocean))
                {
                    properties.SetFloat("_SurfaceOceanCached", 1f);
                    properties.SetBuffer("_OceanSurface", ocean);
                }
                int vertices = segments * segments * 6;
                int instances = 8;
                if (cell.key.z == 0)
                {
                    vertices = 3072;
                    instances = 1;
                }
                else if (cell.key.z == 2)
                {
                    instances = 2;
                }
                commands.DrawProcedural(Matrix4x4.identity, surface, 0, MeshTopology.Triangles, vertices, instances, properties);
                if (!farOnly)
                {
                    DrawnObjects++;
                }
            }
        }

        /// <summary>운해는 절대 셀의 작은 높이장만 보관합니다. 원점 이동으로 재생성하지 않습니다.</summary>
        private void PrepareOcean(CommandBuffer commands)
        {
            if (oceanHash != frame.shapeHash)
            {
                ReleaseOcean();
                oceanHash = frame.shapeHash;
            }
            staleOcean.Clear();
            foreach (Vector3Int key in oceanSurfaces.Keys)
            {
                bool retained = false;
                foreach (CloudHybridMeshCache.Candidate candidate in cells.candidates)
                {
                    if (candidate.key == key)
                    {
                        retained = true;
                        break;
                    }
                }
                if (!retained)
                {
                    staleOcean.Add(key);
                }
            }
            foreach (Vector3Int key in staleOcean)
            {
                oceanSurfaces[key].Dispose();
                oceanSurfaces.Remove(key);
            }
            using (new ProfilingScope(commands, buildSampler))
            {
                foreach (CloudHybridMeshCache.Candidate candidate in cells.candidates)
                {
                    if (!candidate.visible || candidate.key.z != 0 || oceanSurfaces.ContainsKey(candidate.key))
                    {
                        continue;
                    }
                    ComputeBuffer data = new ComputeBuffer(17 * 17 * 2, 16);
                    oceanSurfaces.Add(candidate.key, data);
                    frame.BindGeneration(commands, oceanGenerator, 0, noise);
                    commands.SetComputeVectorParam(oceanGenerator, "_HybridObject", new Vector4(0, candidate.key.x, candidate.key.y, 16));
                    commands.SetComputeVectorParam(oceanGenerator, "_HybridGridMin", candidate.bounds.min);
                    commands.SetComputeVectorParam(oceanGenerator, "_HybridGridSize", candidate.bounds.size);
                    commands.SetComputeBufferParam(oceanGenerator, 0, "_OceanSurface", data);
                    commands.DispatchCompute(oceanGenerator, 0, 3, 3, 1);
                }
            }
        }

        /// <summary>높이장 버퍼는 형태 변경과 B 종료에서 같은 경로로 반환합니다.</summary>
        private void ReleaseOcean()
        {
            foreach (ComputeBuffer data in oceanSurfaces.Values)
            {
                data.Dispose();
            }
            oceanSurfaces.Clear();
        }

        /// <summary>동일 크기의 두 카메라를 재사용하고 resize/세 번째 카메라에서는 이전 GPU 타깃을 반환합니다.</summary>
        private Targets GetTargets(Camera camera, int width, int height)
        {
            int id = camera.GetEntityId().GetHashCode();
            if (cameras.TryGetValue(id, out Targets target))
            {
                if (target.width == width && target.height == height)
                {
                    target.used = Time.frameCount;
                    return target;
                }
                target.Dispose();
                cameras.Remove(id);
            }
            if (cameras.Count >= 2)
            {
                int oldest = 0;
                int frame = int.MaxValue;
                foreach (KeyValuePair<int, Targets> item in cameras)
                {
                    if (item.Value.used < frame)
                    {
                        oldest = item.Key;
                        frame = item.Value.used;
                    }
                }
                cameras[oldest].Dispose();
                cameras.Remove(oldest);
            }
            target = new Targets(width, height);
            target.used = Time.frameCount;
            cameras.Add(id, target);
            Allocations++;
            return target;
        }

        /// <summary>보수적인 표시 셀 수입니다. B는 CPU readback으로 삼각형 점유를 판정하지 않습니다.</summary>
        public override int[] PopulationCounts()
        {
            return (int[])population.Clone();
        }

        /// <summary>B가 소유한 카메라 출력·타일 버퍼·셰이더 인스턴스만 해제합니다.</summary>
        public override void Dispose()
        {
            foreach (Targets target in cameras.Values)
            {
                target.Dispose();
            }
            cameras.Clear();
            CoreUtils.Destroy(surface);
            CoreUtils.Destroy(composite);
            CoreUtils.Destroy(tiles);
            CoreUtils.Destroy(oceanGenerator);
            ReleaseOcean();
            LastOutput = null;
            Ready = false;
        }
    }
}
