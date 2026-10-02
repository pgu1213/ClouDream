using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    /// <summary>공통 렌더러에 구름 형태의 설정을 전달하는 확장 지점입니다.</summary>
    public abstract class CloudFormationProfile : ScriptableObject
    {
        /// <summary>거리별 적분/입사광 간격의 경계 검사를 통과한 형태만 품질 분리를 허용합니다.</summary>
        public virtual bool SupportsDistanceQuality
        {
            get
            {
                return false;
            }
        }

        /// <summary>깊이 모멘트 계약이 검증된 형태만 시간 재투영을 허용합니다.</summary>
        public virtual bool SupportsTemporalReprojection
        {
            get
            {
                return false;
            }
        }

        /// <summary>실행 중 프로필 값 변경도 이전 프레임을 무효화하도록 렌더 입력의 키를 제공합니다.</summary>
        public virtual int GetTemporalStateHash()
        {
            return 0;
        }

        /// <summary>ConceptIncidentLight만으로 전체 팔레트 조명을 평가할 수 있는 형태만 지원을 선언합니다.</summary>
        public virtual bool SupportsPaletteLightingFastPath
        {
            get
            {
                return false;
            }
        }

        /// <summary>검증한 형태만 공간 차폐 캐시를 사용하고 다른 확장 형태는 기존 조명을 유지합니다.</summary>
        public virtual bool SupportsSpatialLightCache
        {
            get
            {
                return false;
            }
        }

        /// <summary>이 형태에 필요한 GPU 매개변수를 기록합니다. 노이즈 텍스처는 재생성하지 않습니다.</summary>
        public abstract void Apply(CommandBuffer commands, ComputeShader shader);

        /// <summary>상층 셀 캐시를 지원하는 형태만 GPU 배치 계약을 제공합니다. 다른 형태는 절차식으로 평가합니다.</summary>
        public virtual bool TryGetSkyCellParameters(out Vector4 placement, out Vector4 altitude, out Vector4 shape)
        {
            placement = Vector4.zero;
            altitude = Vector4.zero;
            shape = Vector4.zero;
            return false;
        }

        /// <summary>정규화된 운해 필드를 지원하는 스타일만 주기·방향·능선 비중의 캐시 키를 제공합니다.</summary>
        public virtual bool TryGetOceanHeightParameters(out Vector4 parameters)
        {
            parameters = Vector4.zero;
            return false;
        }
    }
}
