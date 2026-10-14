using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    /// <summary>카메라별 형상 이력과 제한된 조명 이력을 분리하고 필요한 8×8 타일만 준비합니다.</summary>
    internal sealed class CloudTemporalHistory : IDisposable
    {
        private RenderTexture previousLighting;

        private RenderTexture previousTransmission;

        private RenderTexture previousState;

        private RenderTexture previousDepthMoments;

        // RGFloat: R은 역 Z 장면 깊이, G는 마지막 실제 적분 이후 경과 프레임입니다.
        public RenderTexture State { get; private set; }

        // RGFloat: 실제 볼륨 적분의 opacity 가중 평균 거리(m), 거리 분산(m²)입니다.
        public RenderTexture DepthMoments { get; private set; }

        // uint2 타일 좌표의 append 목록과 DispatchIndirect의 (groupX, 1, 1) 계약입니다.
        public ComputeBuffer RetraceTiles { get; private set; }

        public ComputeBuffer RetraceArguments { get; private set; }

        // CloudTemporalRejection 순서의 uint 13개입니다. 진단 OFF에서는 원자 연산을 하지 않습니다.
        public ComputeBuffer DiagnosticsBuffer { get; private set; }

        private readonly uint[] zeroDiagnostics = new uint[(int)CloudTemporalRejection.Count];

        private readonly CloudTemporalLightingState[] lightingFrames = new CloudTemporalLightingState[4];

        private readonly Vector3[] windFrames = new Vector3[4];

        private CloudTemporalLightingState pendingLighting;

        private Vector3 pendingWind;

        // 구름 몸체 이동 거리가 아니라 노이즈 좌표의 등가 미터 변화 한계입니다.
        public const float MaximumDeformationNoiseShift = 0.5f;

        private int lightingFrameCount;

        private int tileCapacity;

        private bool valid;

        private int previousShapeKey;

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

        public int LightingRefreshes { get; private set; }

        public bool ReusingHistory { get; private set; }

        public bool CompactTilesActive { get; private set; }

        public bool DiagnosticsActive { get; private set; }

        public float LightingInputChange { get; private set; }

        public float DeformationInputChange { get; private set; }

        public string Status { get; private set; } = "Off";

        public long Bytes { get; private set; }

        private ProfilingSampler prepareSampler;

        private ProfilingSampler copySampler;

        /// <summary>배열 RT와 형식을 원 출력에 맞추고 깊이·분산·나이는 full float로 유지합니다.</summary>
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

        /// <summary>형상 모멘트와 조명 이력은 카메라별로 할당하며 크기 변경은 소유 타깃이 해제합니다.</summary>
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
            previousDepthMoments = Create(targets.width, targets.height, RenderTextureFormat.RGFloat, "Cloud previous depth moments");
            DepthMoments = Create(targets.width, targets.height, RenderTextureFormat.RGFloat, "Cloud temporal depth moments");
            tileCapacity = targets.width / 8 * (targets.height / 8);
            RetraceTiles = new ComputeBuffer(tileCapacity, sizeof(uint) * 2, ComputeBufferType.Append);
            RetraceArguments = new ComputeBuffer(3, sizeof(uint), ComputeBufferType.IndirectArguments);
            RetraceArguments.SetData(new uint[] { 0, 1, 1 });
            DiagnosticsBuffer = new ComputeBuffer(zeroDiagnostics.Length, sizeof(uint));
            DiagnosticsBuffer.SetData(zeroDiagnostics);
            prepareSampler = new ProfilingSampler("Cloud.TemporalReproject");
            copySampler = new ProfilingSampler("Cloud.TemporalStore");
            int bytesPerPixel = 48;
            if (targets.compact)
            {
                bytesPerPixel = 44;
            }

            Bytes = (long)targets.width * targets.height * bytesPerPixel
                + (long)tileCapacity * sizeof(uint) * 2 + (3 + zeroDiagnostics.Length) * sizeof(uint);
            Allocations++;
        }

        /// <summary>현 입력을 각 이력이 실제 생성된 프레임과 비교하여 누적 변화와 왕복 변화를 제한합니다.</summary>
        private Vector4 LightingAgeChanges(CloudTemporalLightingState lighting)
        {
            Vector4 changes = Vector4.one;
            float largest = 0f;
            for (int index = 0; index < lightingFrameCount; index++)
            {
                largest = Mathf.Max(largest, lighting.Difference(lightingFrames[index]));
                changes[index] = largest;
            }

            return changes;
        }

        /// <summary>정규화된 XZ 바람 입력의 차이를 현재 굴곡 노이즈 좌표의 등가 미터로 변환합니다.</summary>
        private static float NoiseCoordinateShift(Vector3 current, Vector3 previous, float noisePeriod)
        {
            Vector2 difference = new Vector2(current.x - previous.x, current.z - previous.z);
            if (difference.sqrMagnitude == 0f)
            {
                return 0f;
            }

            if (noisePeriod <= 0f || float.IsNaN(noisePeriod) || float.IsInfinity(noisePeriod))
            {
                return float.MaxValue;
            }

            float shift = difference.magnitude * 0.13f * noisePeriod;
            if (float.IsNaN(shift) || float.IsInfinity(shift))
            {
                return float.MaxValue;
            }

            return shift;
        }

        /// <summary>실제 이력 프레임과 현재 변형 입력의 차이를 비교하며 왕복 변화의 경로 길이는 누적하지 않습니다.</summary>
        private Vector4 DeformationAgeChanges(Vector3 wind, float noisePeriod)
        {
            Vector4 changes = Vector4.one * float.MaxValue;
            float largest = 0f;
            for (int index = 0; index < lightingFrameCount; index++)
            {
                largest = Mathf.Max(largest, NoiseCoordinateShift(wind, windFrames[index], noisePeriod));
                changes[index] = largest;
            }

            return changes;
        }

        /// <summary>형상 리셋과 조명 갱신을 구분하며 오래되거나 급변한 RGB를 별도 사유로 재적분합니다.</summary>
        public void Prepare(CommandBuffer commands, ComputeShader shader, CloudRenderTargets targets,
            Camera camera, Vector3 origin, Vector3 wind, int shapeKey, CloudTemporalLightingState lighting,
            int phases, bool compactTiles, bool diagnostics, float noisePeriod = 0f)
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
            else if (shapeKey != previousShapeKey || phases != previousPhases)
            {
                reset = "Shape / integration settings changed";
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
            else if (NoiseCoordinateShift(wind, previousWind, noisePeriod) > MaximumDeformationNoiseShift)
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
                lightingFrameCount = 0;
            }

            Vector4 ageChanges = LightingAgeChanges(lighting);
            Vector4 deformationChanges = DeformationAgeChanges(wind, noisePeriod);
            LightingInputChange = ageChanges.x;
            DeformationInputChange = deformationChanges.x;
            int lightingAgeLimit = phases - 1;
            int deformationAgeLimit = phases - 1;
            if (ReusingHistory && deformationChanges.x > 0.0000001f)
            {
                deformationAgeLimit = Mathf.Min(deformationAgeLimit, 2);
                Status = "Reprojecting with bounded deformation history";
            }
            if (ReusingHistory && ageChanges.x > 0.000001f)
            {
                lightingAgeLimit = Mathf.Min(lightingAgeLimit, 2);
                Status = "Reprojecting with bounded lighting history";
                if (ageChanges.x > CloudTemporalLightingState.MaximumHistoryInputChange)
                {
                    LightingRefreshes++;
                    Status = "Lighting refresh; shape history retained";
                }
            }

            pendingLighting = lighting;
            pendingWind = wind;
            CompactTilesActive = compactTiles && tileCapacity <= 65535;
            DiagnosticsActive = diagnostics;
            float tangent = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            Vector3 forward = camera.transform.forward;
            Vector3 right = camera.transform.right * tangent * camera.aspect;
            Vector3 up = camera.transform.up * tangent;
            int compact = 0;
            int historyValid = 0;
            int motion = 0;
            int translation = 0;
            int compactDispatch = 0;
            int collectDiagnostics = 0;
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

            if ((position - previousPosition).sqrMagnitude > 0.000001f)
            {
                translation = 1;
                motion = 1;
            }

            if (Quaternion.Angle(camera.transform.rotation, previousRotation) > 0.0001f)
            {
                motion = 1;
            }

            if (CompactTilesActive)
            {
                compactDispatch = 1;
                commands.SetBufferCounterValue(RetraceTiles, 0);
            }

            if (DiagnosticsActive)
            {
                collectDiagnostics = 1;
                commands.SetBufferData(DiagnosticsBuffer, zeroDiagnostics);
            }

            int kernel = shader.FindKernel("Reproject");
            commands.SetComputeVectorParam(shader, "_TemporalSize", new Vector4(targets.width, targets.height, 1f / targets.width, 1f / targets.height));
            commands.SetComputeIntParam(shader, "_TemporalCompact", compact);
            commands.SetComputeIntParam(shader, "_TemporalValid", historyValid);
            commands.SetComputeIntParam(shader, "_TemporalMotion", motion);
            commands.SetComputeIntParam(shader, "_TemporalTranslation", translation);
            commands.SetComputeIntParam(shader, "_TemporalPhases", phases);
            commands.SetComputeIntParam(shader, "_TemporalPhase", phase);
            commands.SetComputeIntParam(shader, "_TemporalCompactTiles", compactDispatch);
            commands.SetComputeIntParam(shader, "_TemporalDiagnosticsEnabled", collectDiagnostics);
            commands.SetComputeIntParam(shader, "_TemporalLightingAgeLimit", lightingAgeLimit);
            commands.SetComputeIntParam(shader, "_TemporalDeformationAgeLimit", deformationAgeLimit);
            commands.SetComputeFloatParam(shader, "_TemporalLightingLimit", CloudTemporalLightingState.MaximumHistoryInputChange);
            commands.SetComputeFloatParam(shader, "_TemporalDeformationLimit", MaximumDeformationNoiseShift);
            commands.SetComputeVectorParam(shader, "_TemporalLightingAgeChange", ageChanges);
            commands.SetComputeVectorParam(shader, "_TemporalDeformationAgeChange", deformationChanges);
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
            commands.SetComputeTextureParam(shader, kernel, "_PreviousDepthMoments", previousDepthMoments);
            commands.SetComputeTextureParam(shader, kernel, "_CurrentDepth", targets.depth);
            commands.SetComputeTextureParam(shader, kernel, "_CurrentLighting", targets.lighting);
            commands.SetComputeTextureParam(shader, kernel, "_CurrentTransmission", targets.transmittance);
            commands.SetComputeTextureParam(shader, kernel, "_CurrentState", State);
            commands.SetComputeTextureParam(shader, kernel, "_CurrentDepthMoments", DepthMoments);
            commands.SetComputeBufferParam(shader, kernel, "_TemporalRetraceTiles", RetraceTiles);
            commands.SetComputeBufferParam(shader, kernel, "_TemporalDiagnostics", DiagnosticsBuffer);
            using (new ProfilingScope(commands, prepareSampler))
            {
                commands.DispatchCompute(shader, kernel, targets.width / 8, targets.height / 8, 1);
                if (CompactTilesActive)
                {
                    commands.CopyCounterValue(RetraceTiles, RetraceArguments, 0);
                }
            }

            previousShapeKey = shapeKey;
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

        /// <summary>재투영과 새 적분이 끝난 색·형상 모멘트 및 그 조명 입력만 다음 이력으로 저장합니다.</summary>
        public void Store(CommandBuffer commands, CloudRenderTargets targets)
        {
            using (new ProfilingScope(commands, copySampler))
            {
                commands.CopyTexture(targets.lighting, previousLighting);
                commands.CopyTexture(targets.transmittance, previousTransmission);
                commands.CopyTexture(State, previousState);
                commands.CopyTexture(DepthMoments, previousDepthMoments);
            }

            for (int index = lightingFrames.Length - 1; index > 0; index--)
            {
                lightingFrames[index] = lightingFrames[index - 1];
                windFrames[index] = windFrames[index - 1];
            }

            lightingFrames[0] = pendingLighting;
            windFrames[0] = pendingWind;
            lightingFrameCount = Mathf.Min(lightingFrames.Length, lightingFrameCount + 1);
            valid = true;
        }

        /// <summary>명시적 진단 요청에서만 GPU 집계를 읽습니다. 일반 렌더 경로에서는 CPU 대기를 만들지 않습니다.</summary>
        public uint[] ReadDiagnostics()
        {
            uint[] counts = new uint[(int)CloudTemporalRejection.Count];
            if (DiagnosticsActive && DiagnosticsBuffer != null)
            {
                DiagnosticsBuffer.GetData(counts);
            }

            return counts;
        }

        /// <summary>설정 OFF·해상도 변경·카메라 퇴출 시 모든 이력과 간접 실행 버퍼를 해제합니다.</summary>
        public void Dispose()
        {
            CloudRenderTargets.Release(previousLighting);
            CloudRenderTargets.Release(previousTransmission);
            CloudRenderTargets.Release(previousState);
            CloudRenderTargets.Release(previousDepthMoments);
            CloudRenderTargets.Release(State);
            CloudRenderTargets.Release(DepthMoments);
            previousLighting = null;
            previousTransmission = null;
            previousState = null;
            previousDepthMoments = null;
            State = null;
            DepthMoments = null;
            if (RetraceTiles != null)
            {
                RetraceTiles.Release();
                RetraceTiles = null;
            }

            if (RetraceArguments != null)
            {
                RetraceArguments.Release();
                RetraceArguments = null;
            }

            if (DiagnosticsBuffer != null)
            {
                DiagnosticsBuffer.Release();
                DiagnosticsBuffer = null;
            }

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
            CompactTilesActive = false;
            DiagnosticsActive = false;
            lightingFrameCount = 0;
            Status = "Off";
        }
    }
}
