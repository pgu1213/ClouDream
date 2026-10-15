using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies
{
    /// <summary>기존 패스의 입력만 공유하고 자체 GPU 자원을 소유하는 독립 하이브리드 패스입니다.</summary>
    [Serializable]
    public sealed class CloudHybridPass : CustomPass
    {
        public ComputeShader meshGenerator;

        public Shader surfaceShader;

        public Shader compositeShader;

        public Shader proxyShader;

        public Shader lobeSurfaceShader;

        public Shader lobeCompositeShader;

        public ComputeShader lobeTileShader;

        public ComputeShader oceanSurfaceShader;

        public CloudFormationProfile lobeProfile;

        [NonSerialized] public CloudComparisonVariant variant;

        [NonSerialized] public CloudComparisonView view;

        [NonSerialized] public bool reverseSurfaceOrder;

        private CloudComparisonVariant activeVariant;

        public CloudHybridQualityProfile quality;

        // 연결은 같은 볼륨의 CloudHybridSwitch가 담당합니다. 기존 패스의 직렬화 형식은 바꾸지 않습니다.
        [NonSerialized] public LostSkiesCloudPass source;

        [NonSerialized] public bool requested;

        [NonSerialized] public bool actual;

        [NonSerialized] public bool collectGpuTiming;

        private CloudComparisonRenderer renderer;

        public string Status { get; private set; } = "Reference active";

        public bool Ready { get { return renderer != null && renderer.Ready; } }

        public int ResidencyMissing
        {
            get
            {
                if (renderer == null)
                {
                    return 0;
                }

                return renderer.ResidencyMissing;
            }
        }

        public long Bytes
        {
            get
            {
                if (renderer == null)
                {
                    return 0;
                }

                return renderer.Bytes;
            }
        }

        public int Builds
        {
            get
            {
                if (renderer == null)
                {
                    return 0;
                }

                return renderer.Builds;
            }
        }

        public int Failures
        {
            get
            {
                if (renderer == null)
                {
                    return 0;
                }

                return renderer.Failures;
            }
        }

        public int Missing
        {
            get
            {
                if (renderer == null)
                {
                    return 0;
                }

                return renderer.Missing;
            }
        }

        public int Pending
        {
            get
            {
                if (renderer == null)
                {
                    return 0;
                }

                return renderer.Pending;
            }
        }

        public int DrawnObjects
        {
            get
            {
                if (renderer == null)
                {
                    return 0;
                }

                return renderer.DrawnObjects;
            }
        }

        public int TargetAllocations
        {
            get
            {
                if (renderer == null)
                {
                    return 0;
                }

                return renderer.Allocations;
            }
        }

        public RenderTexture LastOutput
        {
            get
            {
                if (renderer == null)
                {
                    return null;
                }

                return renderer.LastOutput;
            }
        }

        /// <summary>독립 렌더러의 측정값이며 전체 HDRP 프레임 시간과 구분합니다.</summary>
        public float[] ReadGpuTimes()
        {
            if (renderer == null)
            {
                return new float[5];
            }

            return new float[] { renderer.totalSampler.gpuElapsedTime, renderer.buildSampler.gpuElapsedTime,
                renderer.surfaceSampler.gpuElapsedTime, renderer.volumeSampler.gpuElapsedTime,
                renderer.compositeSampler.gpuElapsedTime };
        }

        /// <summary>운해·상층·띠의 논리 셀/비어 있지 않은 셀 수를 반환합니다.</summary>
        public int[] ReadGeometryCounts()
        {
            if (renderer == null)
            {
                return new int[6];
            }

            return renderer.PopulationCounts();
        }

        /// <summary>필수 셰이더 연결과 D3D12 지원만 확인합니다. 에셋은 이 단계에서 변경하지 않습니다.</summary>
        protected override void Setup(ScriptableRenderContext context, CommandBuffer commands)
        {
            Status = "Preparing";
        }

        /// <summary>같은 카메라 호출 안에서 전환 여부를 정해 두 패스가 동시에 화면을 그리지 않게 합니다.</summary>
        protected override void Execute(CustomPassContext context)
        {
            actual = false;
            if (source == null)
            {
                Status = "Source not connected";
                return;
            }

            source.skipForHybrid = false;
            if (!requested)
            {
                ReleaseRenderer();
                Status = "Reference active";
                return;
            }

            Camera camera = context.hdCamera.camera;
            if (camera.cameraType == CameraType.Reflection || camera.orthographic
                || (!Application.isPlaying && !source.renderInEditor)
                || (camera.cameraType == CameraType.SceneView && !source.renderInEditor))
            {
                return;
            }

            if (meshGenerator == null || surfaceShader == null || compositeShader == null || quality == null)
            {
                Status = "Missing hybrid resources / reference active";
                return;
            }

            string unsupported = LostSkiesCloudRenderer.GetGraphicsApiUnsupportedReason();
            if (!string.IsNullOrEmpty(unsupported))
            {
                Status = unsupported;
                return;
            }

            if (renderer != null && activeVariant != variant)
            {
                ReleaseRenderer();
            }
            if (renderer == null)
            {
                if (variant == CloudComparisonVariant.SurfaceB)
                {
                    if (lobeSurfaceShader == null || lobeCompositeShader == null || lobeTileShader == null || lobeProfile == null || oceanSurfaceShader == null)
                    {
                        Status = "B resources not connected";
                        return;
                    }
                    renderer = new CloudSurfaceCloudRenderer(lobeSurfaceShader, lobeCompositeShader, lobeTileShader, lobeProfile, oceanSurfaceShader);
                }
                else
                {
                    if (proxyShader == null)
                    {
                        Status = "A proxy not connected";
                        return;
                    }
                    renderer = new CloudHybridRenderer(meshGenerator, surfaceShader, compositeShader, proxyShader);
                }
                activeVariant = variant;
            }
            renderer.View = view;
            renderer.ReverseOrder = reverseSurfaceOrder;
            renderer.SetRecording(collectGpuTiming);
            using (new ProfilingScope(context.cmd, renderer.totalSampler))
            using (new ProfilingScope(context.cmd, source.comparisonSampler))
            {
                actual = renderer.Prepare(context, source, quality);
                if (actual)
                {
                    renderer.Render(context, quality);
                    source.skipForHybrid = true;
                }
            }

            Status = renderer.Status;
        }

        /// <summary>명시적 OFF나 종료 시 소유 자원을 반환하고 기존 패스의 실행을 복구합니다.</summary>
        public void ReleaseRenderer()
        {
            if (source != null)
            {
                source.skipForHybrid = false;
            }

            if (renderer != null)
            {
                renderer.Dispose();
                renderer = null;
            }

            actual = false;
        }

        /// <summary>도메인 재로드와 볼륨 해제에서도 동일한 복원 경로를 사용합니다.</summary>
        protected override void Cleanup()
        {
            ReleaseRenderer();
        }
    }
}
