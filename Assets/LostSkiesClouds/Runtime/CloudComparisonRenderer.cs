using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies
{
    public enum CloudComparisonVariant { HybridA, SurfaceB }

    public enum CloudComparisonView { Full, SurfaceOnly, SurfaceAndVolume, Off, Reference }

    /// <summary>A/B가 같은 상태·수명·계측 계약으로 연결되는 실제 렌더링 확장점입니다.</summary>
    internal abstract class CloudComparisonRenderer : IDisposable
    {
        public readonly ProfilingSampler totalSampler;

        public readonly ProfilingSampler buildSampler;

        public readonly ProfilingSampler surfaceSampler;

        public readonly ProfilingSampler volumeSampler;

        public readonly ProfilingSampler compositeSampler;

        public CloudComparisonView View { get; set; }

        public bool ReverseOrder { get; set; }

        public abstract bool Ready { get; protected set; }

        public abstract string Status { get; protected set; }

        public abstract long Bytes { get; }

        public abstract int Builds { get; }

        public abstract int Failures { get; }

        public abstract int Missing { get; }

        public abstract int ResidencyMissing { get; }

        public abstract int Pending { get; }

        public abstract int DrawnObjects { get; protected set; }

        public abstract int Allocations { get; protected set; }

        public abstract RenderTexture LastOutput { get; protected set; }

        /// <summary>두 경로의 패스 이름을 분리하되 총 비교 마커는 호출자에서 공유합니다.</summary>
        protected CloudComparisonRenderer(string prefix)
        {
            totalSampler = new ProfilingSampler("Cloud." + prefix + "Total");
            buildSampler = new ProfilingSampler("Cloud." + prefix + "Build");
            surfaceSampler = new ProfilingSampler("Cloud." + prefix + "Surface");
            volumeSampler = new ProfilingSampler("Cloud." + prefix + "Volume");
            compositeSampler = new ProfilingSampler("Cloud." + prefix + "Composite");
        }

        /// <summary>생성·표면·볼륨·합성 GPU 측정을 함께 켜거나 끕니다.</summary>
        public void SetRecording(bool enabled)
        {
            totalSampler.enableRecording = enabled;
            buildSampler.enableRecording = enabled;
            surfaceSampler.enableRecording = enabled;
            volumeSampler.enableRecording = enabled;
            compositeSampler.enableRecording = enabled;
        }

        /// <summary>카메라의 형태·환경·예산을 준비합니다.</summary>
        public abstract bool Prepare(CustomPassContext context, LostSkiesCloudPass source, CloudHybridQualityProfile quality);

        /// <summary>준비된 현재 카메라를 그립니다.</summary>
        public abstract void Render(CustomPassContext context, CloudHybridQualityProfile quality);

        /// <summary>운해·상층·띠의 셀 수를 반환합니다.</summary>
        public abstract int[] PopulationCounts();

        /// <summary>모든 소유 GPU 자원을 반환합니다.</summary>
        public abstract void Dispose();
    }
}
