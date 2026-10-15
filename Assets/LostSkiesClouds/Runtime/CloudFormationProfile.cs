using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    /// <summary>공통 렌더러에 구름 형태의 설정을 전달하는 확장 지점입니다.</summary>
    public abstract class CloudFormationProfile : ScriptableObject
    {
        /// <summary>공유 큰 형태를 제공하는 프로필만 메시/볼륨 하이브리드를 지원합니다.</summary>
        public virtual bool SupportsHybridRendering
        {
            get { return false; }
        }

        /// <summary>하이브리드 생성기의 입력 계약입니다. 미지원 형태는 기존 경로를 유지합니다.</summary>
        public virtual bool TryGetHybridGeometry(out CloudHybridGeometry geometry)
        {
            geometry = default;
            return false;
        }

        /// <summary>정확한 차분 법선의 공간 공유 계약을 제공하는 형태만 지원합니다.</summary>
        public virtual bool SupportsNormalFieldCache
        {
            get { return false; }
        }

        /// <summary>정확 밀도와 깊이 계약으로 원경 표면 추출을 지원하는 형태만 명시합니다.</summary>
        public virtual bool SupportsFarSurface
        {
            get
            {
                return false;
            }
        }

        /// <summary>생성식을 보존하는 상층 형태 필드 계약이 있는 프로필만 캐시를 허용합니다.</summary>
        public virtual bool SupportsShapeFieldCache
        {
            get
            {
                return false;
            }
        }

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

        /// <summary>운해 3D 필드의 생성식과 캐시 키가 공유할 보정된 형태 매개변수입니다.</summary>
        public virtual bool TryGetOceanShapeFieldParameters(out Vector4 shape, out Vector4 detail)
        {
            shape = Vector4.zero;
            detail = Vector4.zero;
            return false;
        }

        /// <summary>연속 운해의 절차식과 필드 캐시가 공유할 하단과 두께입니다.</summary>
        public virtual bool TryGetOceanShapeFieldBounds(out Vector2 bounds)
        {
            bounds = Vector2.zero;
            return false;
        }
    }
}
