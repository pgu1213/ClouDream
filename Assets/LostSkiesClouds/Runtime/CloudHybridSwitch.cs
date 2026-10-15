using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies
{
    /// <summary>같은 Custom Pass Volume의 두 렌더 경로를 연결합니다. 기본 채택은 검증 후 별도로 수행합니다.</summary>
    [ExecuteAlways, RequireComponent(typeof(CustomPassVolume))]
    public sealed class CloudHybridSwitch : MonoBehaviour
    {
        public bool useHybrid;

        public CloudComparisonVariant variant = CloudComparisonVariant.HybridA;

        public CloudComparisonView view = CloudComparisonView.Full;

        public CloudHybridPass Hybrid { get; private set; }

        public LostSkiesCloudPass Reference { get; private set; }

        /// <summary>동일 볼륨의 실제 패스 객체를 연결하고 하이브리드를 기존 패스 앞에 둡니다.</summary>
        private void OnEnable()
        {
            Connect();
        }

        /// <summary>검증 씬이나 Play UI가 같은 연결을 재사용합니다.</summary>
        public bool Connect()
        {
            CustomPassVolume volume = GetComponent<CustomPassVolume>();
            Hybrid = null;
            Reference = null;
            foreach (CustomPass pass in volume.customPasses)
            {
                if (pass is CloudHybridPass candidate)
                {
                    Hybrid = candidate;
                }

                if (pass is LostSkiesCloudPass reference)
                {
                    Reference = reference;
                }
            }

            if (Hybrid == null || Reference == null)
            {
                return false;
            }

            Hybrid.source = Reference;
            Hybrid.requested = useHybrid;
            Hybrid.variant = variant;
            Hybrid.view = view;
            int hybridIndex = volume.customPasses.IndexOf(Hybrid);
            int referenceIndex = volume.customPasses.IndexOf(Reference);
            if (hybridIndex > referenceIndex)
            {
                volume.customPasses.RemoveAt(hybridIndex);
                volume.customPasses.Insert(referenceIndex, Hybrid);
            }

            return true;
        }

        /// <summary>입력만 갱신합니다. 실제 경로는 카메라별 Execute 안에서 결정합니다.</summary>
        private void Update()
        {
            if (Hybrid == null || Reference == null)
            {
                Connect();
            }

            if (Hybrid != null)
            {
                Hybrid.requested = useHybrid;
                Hybrid.variant = variant;
                Hybrid.view = view;
            }
        }

        /// <summary>명시적 전환을 UI·벤치마크·검증에서 공통으로 사용합니다.</summary>
        public void SetHybrid(bool enabled)
        {
            useHybrid = enabled;
            if (enabled && view == CloudComparisonView.Reference)
            {
                view = CloudComparisonView.Full;
            }
            if (Hybrid != null)
            {
                Hybrid.requested = enabled;
                if (!enabled)
                {
                    Hybrid.ReleaseRenderer();
                }
            }
        }

        /// <summary>같은 카메라에서 A/B와 비용 분리용 표시 단계를 명시적으로 전환합니다.</summary>
        public void SetComparison(CloudComparisonVariant nextVariant, CloudComparisonView nextView)
        {
            variant = nextVariant;
            view = nextView;
            SetHybrid(nextView != CloudComparisonView.Reference);
            if (Hybrid != null)
            {
                Hybrid.variant = variant;
                Hybrid.view = view;
            }
        }

        /// <summary>동작 연결이 해제되면 원래 볼류메트릭 경로로 복귀합니다.</summary>
        private void OnDisable()
        {
            if (Hybrid != null)
            {
                Hybrid.requested = false;
                Hybrid.ReleaseRenderer();
            }

            if (Reference != null)
            {
                Reference.skipForHybrid = false;
            }
        }
    }
}
