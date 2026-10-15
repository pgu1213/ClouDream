using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies
{
    /// <summary>메시 생성·독립 표현 보간·최종 합성을 수행하는 별도 렌더 경로입니다.</summary>
    internal sealed class CloudHybridRenderer : IDisposable
    {
        /// <summary>한 카메라의 임시 표면 결과와 누적 L/T를 소유합니다.</summary>
        private sealed class Targets : IDisposable
        {
            public RenderTexture surface;

            public RenderTexture linearDepth;

            public RenderTexture accumulation;

            public RenderTexture sceneDepth;

            public int width;

            public int height;

            public int lastUsed;

            public long Bytes { get { return (long)width * height * 32; } }

            /// <summary>렌더·UAV 없이 필요한 2D 타깃만 만들고 기존 해상도는 재사용합니다.</summary>
            public Targets(int outputWidth, int outputHeight)
            {
                width = outputWidth;
                height = outputHeight;
                surface = Create("Hybrid surface", RenderTextureFormat.ARGBHalf, 24);
                linearDepth = Create("Hybrid surface distance", RenderTextureFormat.RGFloat, 0);
                accumulation = Create("Hybrid accumulated L T", RenderTextureFormat.ARGBHalf, 0);
                sceneDepth = Create("Hybrid reconstruction scene depth", RenderTextureFormat.RFloat, 0);
            }

            /// <summary>그리기와 합성 사이에 sRGB 변환 없이 선형 값을 저장합니다.</summary>
            private RenderTexture Create(string name, RenderTextureFormat format, int depth)
            {
                RenderTexture texture = new RenderTexture(width, height, depth, format, RenderTextureReadWrite.Linear);
                texture.name = name;
                texture.filterMode = FilterMode.Bilinear;
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.Create();
                return texture;
            }

            /// <summary>메모리 회수는 출력 카메라 퇴출·resize·패스 종료에서 동일합니다.</summary>
            public void Dispose()
            {
                CloudRenderTargets.Release(surface);
                CloudRenderTargets.Release(linearDepth);
                CloudRenderTargets.Release(accumulation);
                CloudRenderTargets.Release(sceneDepth);
            }
        }

        public readonly ProfilingSampler totalSampler = new ProfilingSampler("Cloud.HybridTotal");

        public readonly ProfilingSampler buildSampler = new ProfilingSampler("Cloud.HybridBuild");

        public readonly ProfilingSampler surfaceSampler = new ProfilingSampler("Cloud.HybridSurface");

        public readonly ProfilingSampler volumeSampler = new ProfilingSampler("Cloud.HybridVolume");

        public readonly ProfilingSampler compositeSampler = new ProfilingSampler("Cloud.HybridComposite");

        private readonly Dictionary<int, Targets> cameraTargets = new Dictionary<int, Targets>();

        private readonly CloudHybridFrame frame = new CloudHybridFrame();

        private readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();

        private readonly RenderTargetIdentifier[] surfaceBuffers = new RenderTargetIdentifier[2];

        private readonly ComputeShader generator;

        private readonly Material surface;

        private readonly Material composite;

        private readonly Texture3D noise;

        private readonly CloudHybridMeshCache cache;

        public bool Ready { get; private set; }

        public string Status { get; private set; } = "Warming up";

        public int DrawnObjects { get; private set; }

        public int VolumeObjects { get; private set; }

        public int Allocations { get; private set; }

        public long Bytes
        {
            get { return cache.Bytes + TargetBytes(); }
        }

        public int Builds { get { return cache.Builds; } }

        public int Failures { get { return cache.Failures; } }

        public int Missing { get { return cache.Missing; } }

        public int ResidencyMissing { get { return cache.ResidencyMissing; } }

        public int Pending { get { return cache.PendingCount; } }

        /// <summary>GPU가 실제 생성한 세 종류 메시의 수를 전달합니다.</summary>
        public int[] PopulationCounts()
        {
            return cache.PopulationCounts();
        }

        public RenderTexture LastOutput { get; private set; }

        /// <summary>원본 입력 노이즈는 공유 에셋으로 읽고 별도 셰이더 인스턴스만 소유합니다.</summary>
        public CloudHybridRenderer(ComputeShader shader, Shader surfaceShader, Shader compositeShader)
        {
            generator = UnityEngine.Object.Instantiate(shader);
            surface = CoreUtils.CreateEngineMaterial(surfaceShader);
            composite = CoreUtils.CreateEngineMaterial(compositeShader);
            noise = CloudNoiseLibrary.Load("WORLEY3D", 128, 4f, 1, 0.5f);
            cache = new CloudHybridMeshCache(generator);
        }

        /// <summary>프레임 상태와 생성 예산을 준비하며 자료가 없는 동안 기존 경로가 화면을 담당합니다.</summary>
        public bool Prepare(CustomPassContext context, LostSkiesCloudPass source, CloudHybridQualityProfile quality)
        {
            Camera camera = context.hdCamera.camera;
            Ready = false;
            if (!frame.Update(source, camera))
            {
                Status = "Unsupported formation / reference active";
                return false;
            }

            quality.Sanitize();
            int width = Mathf.Max(64, Mathf.RoundToInt(context.hdCamera.actualWidth * quality.resolutionScale));
            int height = Mathf.Max(64, Mathf.RoundToInt(context.hdCamera.actualHeight * quality.resolutionScale));
            Targets targets = GetTargets(camera, width, height);
            using (new ProfilingScope(context.cmd, buildSampler))
            {
                cache.Prepare(context.cmd, generator, frame, quality, camera, noise, TargetBytes());
            }

            Ready = cache.Missing == 0 && cache.Failures == 0;
            Status = "Ready";
            if (!Ready)
            {
                Status = "Preparing " + cache.Missing + " cells / " + cache.PendingCount + " jobs";
            }

            if (cache.Failures > 0)
            {
                Status = "Mesh overflow/readback failure / reference active";
            }

            LastOutput = targets.accumulation;
            return Ready;
        }

        /// <summary>한 구름의 메시와 볼륨을 보간한 후 다른 구름과 누적하고 HDRP 색 버퍼에 합성합니다.</summary>
        public void Render(CustomPassContext context, CloudHybridQualityProfile quality)
        {
            if (!Ready)
            {
                return;
            }

            Camera camera = context.hdCamera.camera;
            Targets targets = cameraTargets[camera.GetEntityId().GetHashCode()];
            frame.BindRendering(properties, camera, quality, noise, targets.width, targets.height);
            context.cmd.SetRenderTarget(targets.sceneDepth);
            context.cmd.SetViewport(new Rect(0f, 0f, targets.width, targets.height));
            CoreUtils.DrawFullScreen(context.cmd, composite, properties, 2);
            context.cmd.SetRenderTarget(targets.accumulation);
            context.cmd.SetViewport(new Rect(0f, 0f, targets.width, targets.height));
            context.cmd.ClearRenderTarget(false, true, new Color(0f, 0f, 0f, 1f));
            DrawnObjects = 0;
            VolumeObjects = 0;
            foreach (CloudHybridMeshCache.Entry entry in cache.Visible)
            {
                if (entry.triangleCount == 0)
                {
                    continue;
                }

                entry.Bind(properties);
                Rect rectangle = ScreenBounds(camera, entry.bounds, frame.origin, targets.width, targets.height);
                if (rectangle.width <= 0f || rectangle.height <= 0f)
                {
                    continue;
                }

                float boundDistance = Vector3.Distance(frame.cameraPosition, entry.bounds.ClosestPoint(frame.cameraPosition));
                float allowVolume = 0f;
                if (boundDistance < quality.volumeFar)
                {
                    allowVolume = 1f;
                }

                properties.SetFloat("_HybridVolumeAllowed", allowVolume);
                surfaceBuffers[0] = entryTarget(targets.surface);
                surfaceBuffers[1] = entryTarget(targets.linearDepth);
                context.cmd.DisableScissorRect();
                context.cmd.SetRenderTarget(surfaceBuffers, targets.surface);
                context.cmd.SetViewport(new Rect(0f, 0f, targets.width, targets.height));
                context.cmd.ClearRenderTarget(true, true, new Color(0f, 0f, 0f, 1f));
                context.cmd.EnableScissorRect(rectangle);
                using (new ProfilingScope(context.cmd, surfaceSampler))
                {
                    context.cmd.DrawProceduralIndirect(Matrix4x4.identity, surface, 0,
                        MeshTopology.Triangles, entry.arguments, 0, properties);
                }

                properties.SetTexture("_HybridSurfaceLight", targets.surface);
                properties.SetTexture("_HybridSurfaceDepth", targets.linearDepth);
                context.cmd.SetRenderTarget(targets.accumulation);
                context.cmd.EnableScissorRect(rectangle);
                using (new ProfilingScope(context.cmd, volumeSampler))
                {
                    CoreUtils.DrawFullScreen(context.cmd, composite, properties, 0);
                }

                context.cmd.DisableScissorRect();

                DrawnObjects++;
                if (Vector3.Distance(frame.cameraPosition, entry.bounds.ClosestPoint(frame.cameraPosition)) < quality.volumeFar)
                {
                    VolumeObjects++;
                }
            }

            context.cmd.DisableScissorRect();

            CoreUtils.SetRenderTarget(context.cmd, context.cameraColorBuffer);
            context.cmd.SetViewport(new Rect(0f, 0f, context.hdCamera.actualWidth, context.hdCamera.actualHeight));
            properties.SetTexture("_HybridAccumulation", targets.accumulation);
            properties.SetTexture("_HybridLowSceneDepth", targets.sceneDepth);
            using (new ProfilingScope(context.cmd, compositeSampler))
            {
                CoreUtils.DrawFullScreen(context.cmd, composite, properties, 1);
            }
        }

        /// <summary>교체 중인 타깃의 깊이 소유권을 유지하는 식별자입니다.</summary>
        private static RenderTargetIdentifier entryTarget(RenderTexture target)
        {
            return new RenderTargetIdentifier(target);
        }

        /// <summary>절대 셀 경계를 화면으로 투영합니다. 근평면 교차는 전체 화면으로 보수적으로 처리합니다.</summary>
        private static Rect ScreenBounds(Camera camera, Bounds absoluteBounds, Vector3 origin, int width, int height)
        {
            Vector3 minimum = absoluteBounds.min - origin;
            Vector3 maximum = absoluteBounds.max - origin;
            float left = 1f;
            float right = 0f;
            float bottom = 1f;
            float top = 0f;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 position = minimum;
                if ((corner & 1) != 0)
                {
                    position.x = maximum.x;
                }
                if ((corner & 2) != 0)
                {
                    position.y = maximum.y;
                }
                if ((corner & 4) != 0)
                {
                    position.z = maximum.z;
                }

                Vector3 viewport = camera.WorldToViewportPoint(position);
                if (viewport.z <= camera.nearClipPlane)
                {
                    return new Rect(0f, 0f, width, height);
                }

                left = Mathf.Min(left, viewport.x);
                right = Mathf.Max(right, viewport.x);
                // Game와 RenderTexture의 Y 반전에 무관하게 두 영역을 모두 포함합니다.
                bottom = Mathf.Min(bottom, Mathf.Min(viewport.y, 1f - viewport.y));
                top = Mathf.Max(top, Mathf.Max(viewport.y, 1f - viewport.y));
            }

            float x0 = Mathf.Max(0f, Mathf.Floor(left * width) - 2f);
            float x1 = Mathf.Min(width, Mathf.Ceil(right * width) + 2f);
            float y0 = Mathf.Max(0f, Mathf.Floor(bottom * height) - 2f);
            float y1 = Mathf.Min(height, Mathf.Ceil(top * height) + 2f);
            return new Rect(x0, y0, Mathf.Max(0f, x1 - x0), Mathf.Max(0f, y1 - y0));
        }

        /// <summary>카메라 두 개까지 분리 보존하고 새 크기에서만 재할당합니다.</summary>
        private Targets GetTargets(Camera camera, int width, int height)
        {
            int id = camera.GetEntityId().GetHashCode();
            if (cameraTargets.TryGetValue(id, out Targets targets))
            {
                if (targets.width == width && targets.height == height)
                {
                    targets.lastUsed = Time.frameCount;
                    return targets;
                }

                targets.Dispose();
                cameraTargets.Remove(id);
            }

            if (cameraTargets.Count >= 2)
            {
                int oldestId = 0;
                int oldestFrame = int.MaxValue;
                foreach (KeyValuePair<int, Targets> pair in cameraTargets)
                {
                    if (pair.Value.lastUsed < oldestFrame)
                    {
                        oldestId = pair.Key;
                        oldestFrame = pair.Value.lastUsed;
                    }
                }

                cameraTargets[oldestId].Dispose();
                cameraTargets.Remove(oldestId);
            }

            targets = new Targets(width, height);
            targets.lastUsed = Time.frameCount;
            cameraTargets.Add(id, targets);
            Allocations++;
            return targets;
        }

        /// <summary>메시 예산 계산에 모든 카메라의 타깃을 포함합니다.</summary>
        private long TargetBytes()
        {
            long bytes = 0;
            foreach (Targets targets in cameraTargets.Values)
            {
                bytes += targets.Bytes;
            }

            return bytes;
        }

        /// <summary>명시적 계측에서만 GPU 마커 recorder를 켭니다.</summary>
        public void SetRecording(bool enabled)
        {
            totalSampler.enableRecording = enabled;
            buildSampler.enableRecording = enabled;
            surfaceSampler.enableRecording = enabled;
            volumeSampler.enableRecording = enabled;
            compositeSampler.enableRecording = enabled;
        }

        /// <summary>모든 소유 카메라·캐시·머티리얼·컴퓨트 인스턴스를 해제합니다.</summary>
        public void Dispose()
        {
            cache.Dispose();
            foreach (Targets targets in cameraTargets.Values)
            {
                targets.Dispose();
            }

            cameraTargets.Clear();
            CoreUtils.Destroy(surface);
            CoreUtils.Destroy(composite);
            CoreUtils.Destroy(generator);
            LastOutput = null;
            Ready = false;
        }
    }
}
