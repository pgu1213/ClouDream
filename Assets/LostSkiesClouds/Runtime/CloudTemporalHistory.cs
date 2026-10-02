using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    /// <summary>한 카메라의 색·투과율·장면 깊이·표본 나이를 보관하고 안전한 재사용만 준비합니다.</summary>
    internal sealed class CloudTemporalHistory : IDisposable
    {
        private RenderTexture previousLighting;
        private RenderTexture previousTransmission;
        private RenderTexture previousState;

        // RGFloat: R은 역 Z 장면 깊이, G는 마지막 실제 적분 이후 경과 프레임(0~3)입니다.
        public RenderTexture State { get; private set; }

        private bool valid;
        private int previousKey;
        private int previousPhases;
        private int phase;
        private Vector3 previousPosition;
        private Quaternion previousRotation;
        private Vector3 previousOrigin;
        private Vector3 previousWind;
        private Vector4 previousLens;
        private Vector3 previousForward;
        private Vector3 previousRight;
        private Vector3 previousUp;
        private double previousTime;
        private int previousCamera;

        public int Allocations { get; private set; }
        public int Resets { get; private set; }
        public bool ReusingHistory { get; private set; }
        public string Status { get; private set; } = "Off";
        public long Bytes { get; private set; }

        private ProfilingSampler prepareSampler;
        private ProfilingSampler copySampler;

        /// <summary>배열 RT와 형식을 원 출력에 맞추고 깊이·나이는 full float로 유지합니다.</summary>
        private static RenderTexture Create(int width, int height, RenderTextureFormat format, string name)
        {
            RenderTexture texture = new RenderTexture(width, height, 0, format);
            texture.dimension = TextureDimension.Tex2DArray;
            texture.volumeDepth = 1;
            texture.enableRandomWrite = true;
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.name = name;
            texture.Create();
            return texture;
        }

        /// <summary>활성화 시에만 카메라당 네 개의 이력 RT를 할당합니다.</summary>
        private void EnsureTargets(CloudRenderTargets targets)
        {
            if (State != null)
            {
                return;
            }

            previousLighting = Create(targets.width, targets.height, targets.lighting.format, "Cloud previous lighting");
            previousTransmission = Create(targets.width, targets.height, targets.transmittance.format, "Cloud previous transmission");
            previousState = Create(targets.width, targets.height, RenderTextureFormat.RGFloat, "Cloud previous depth and age");
            State = Create(targets.width, targets.height, RenderTextureFormat.RGFloat, "Cloud temporal depth and age");
            prepareSampler = new ProfilingSampler("Cloud.TemporalReproject");
            copySampler = new ProfilingSampler("Cloud.TemporalStore");
            int bytesPerPixel = 32;
            if (targets.compact)
            {
                bytesPerPixel = 28;
            }

            Bytes = (long)targets.width * targets.height * bytesPerPixel;
            Allocations++;
        }

        /// <summary>이력을 폐기한 프레임은 전체 광선을 계산하고 다음 프레임부터 순환 갱신합니다.</summary>
        public void Prepare(CommandBuffer commands, ComputeShader shader, CloudRenderTargets targets,
            Camera camera, Vector3 origin, Vector3 wind, int key, int phases)
        {
            EnsureTargets(targets);
            Vector3 position = camera.transform.position + origin;
            Vector4 lens = new Vector4(camera.fieldOfView, camera.aspect, camera.nearClipPlane, camera.farClipPlane);
            double now = Time.realtimeSinceStartupAsDouble;
            string reset = string.Empty;
            if (!valid)
            {
                reset = "First frame / resized";
            }
            else if (key != previousKey || phases != previousPhases)
            {
                reset = "Lighting / shape / settings changed";
            }
            else if (origin != previousOrigin)
            {
                reset = "World origin changed";
            }
            else if (lens != previousLens || camera.GetEntityId().GetHashCode() != previousCamera)
            {
                reset = "Camera / projection changed";
            }
            else if (Vector3.Distance(position, previousPosition) > 128f
                || Quaternion.Angle(camera.transform.rotation, previousRotation) > 8f)
            {
                reset = "Camera cut / fast motion";
            }
            else if ((wind - previousWind).sqrMagnitude > 0.000001f)
            {
                reset = "Fast wind change";
            }
            else if (now - previousTime > 0.5 || now < previousTime)
            {
                reset = "Frame gap";
            }

            ReusingHistory = string.IsNullOrEmpty(reset);
            Status = "Reprojecting";
            if (!ReusingHistory)
            {
                Status = reset;
                Resets++;
                phase = 0;
            }

            float tangent = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            Vector3 forward = camera.transform.forward;
            Vector3 right = camera.transform.right * tangent * camera.aspect;
            Vector3 up = camera.transform.up * tangent;
            int compact = 0;
            int historyValid = 0;
            int motion = 0;
            if (targets.compact)
            {
                compact = 1;
                shader.EnableKeyword("CLOUD_COMPACT_TARGETS");
            }
            else
            {
                shader.DisableKeyword("CLOUD_COMPACT_TARGETS");
            }

            if (ReusingHistory)
            {
                historyValid = 1;
            }

            if ((position - previousPosition).sqrMagnitude > 0.000001f
                || Quaternion.Angle(camera.transform.rotation, previousRotation) > 0.0001f)
            {
                motion = 1;
            }

            int kernel = shader.FindKernel("Reproject");
            commands.SetComputeVectorParam(shader, "_TemporalSize", new Vector4(targets.width, targets.height, 1f / targets.width, 1f / targets.height));
            commands.SetComputeIntParam(shader, "_TemporalCompact", compact);
            commands.SetComputeIntParam(shader, "_TemporalValid", historyValid);
            commands.SetComputeIntParam(shader, "_TemporalMotion", motion);
            commands.SetComputeIntParam(shader, "_TemporalPhases", phases);
            commands.SetComputeIntParam(shader, "_TemporalPhase", phase);
            commands.SetComputeVectorParam(shader, "_TemporalPosition", position);
            commands.SetComputeVectorParam(shader, "_TemporalForward", forward);
            commands.SetComputeVectorParam(shader, "_TemporalRight", right);
            commands.SetComputeVectorParam(shader, "_TemporalUp", up);
            commands.SetComputeVectorParam(shader, "_PreviousPosition", previousPosition);
            commands.SetComputeVectorParam(shader, "_PreviousForward", previousForward);
            commands.SetComputeVectorParam(shader, "_PreviousRight", previousRight);
            commands.SetComputeVectorParam(shader, "_PreviousUp", previousUp);
            commands.SetComputeVectorParam(shader, "_TemporalZ", new Vector4(
                (camera.farClipPlane / camera.nearClipPlane - 1f) / camera.farClipPlane, 1f / camera.farClipPlane, 0, 0));
            commands.SetComputeTextureParam(shader, kernel, "_PreviousLighting", previousLighting);
            commands.SetComputeTextureParam(shader, kernel, "_PreviousTransmission", previousTransmission);
            commands.SetComputeTextureParam(shader, kernel, "_PreviousState", previousState);
            commands.SetComputeTextureParam(shader, kernel, "_CurrentDepth", targets.depth);
            commands.SetComputeTextureParam(shader, kernel, "_CurrentLighting", targets.lighting);
            commands.SetComputeTextureParam(shader, kernel, "_CurrentTransmission", targets.transmittance);
            commands.SetComputeTextureParam(shader, kernel, "_CurrentState", State);
            using (new ProfilingScope(commands, prepareSampler))
            {
                commands.DispatchCompute(shader, kernel, targets.width / 8, targets.height / 8, 1);
            }

            previousKey = key;
            previousPhases = phases;
            previousPosition = position;
            previousRotation = camera.transform.rotation;
            previousOrigin = origin;
            previousLens = lens;
            previousWind = wind;
            previousForward = forward;
            previousRight = right;
            previousUp = up;
            previousTime = now;
            previousCamera = camera.GetEntityId().GetHashCode();
            phase = (phase + 1) % phases;
        }

        /// <summary>재투영과 새 적분이 끝난 출력만 다음 프레임 이력으로 저장합니다.</summary>
        public void Store(CommandBuffer commands, CloudRenderTargets targets)
        {
            using (new ProfilingScope(commands, copySampler))
            {
                commands.CopyTexture(targets.lighting, previousLighting);
                commands.CopyTexture(targets.transmittance, previousTransmission);
                commands.CopyTexture(State, previousState);
            }

            valid = true;
        }

        /// <summary>설정 OFF·해상도 변경·카메라 퇴출 시 이력을 즉시 돌려줍니다.</summary>
        public void Dispose()
        {
            CloudRenderTargets.Release(previousLighting);
            CloudRenderTargets.Release(previousTransmission);
            CloudRenderTargets.Release(previousState);
            CloudRenderTargets.Release(State);
            previousLighting = null;
            previousTransmission = null;
            previousState = null;
            State = null;
            if (prepareSampler != null)
            {
                prepareSampler.Dispose();
                prepareSampler = null;
            }

            if (copySampler != null)
            {
                copySampler.Dispose();
                copySampler = null;
            }

            Bytes = 0;
            valid = false;
            ReusingHistory = false;
            Status = "Off";
        }
    }
}
