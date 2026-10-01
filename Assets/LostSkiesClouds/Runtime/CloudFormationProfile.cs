using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    /// <summary>공통 렌더러에 구름 형태의 설정을 전달하는 확장 지점입니다.</summary>
    public abstract class CloudFormationProfile : ScriptableObject
    {

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
    }
}
