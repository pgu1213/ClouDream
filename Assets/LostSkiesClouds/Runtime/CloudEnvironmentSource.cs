using UnityEngine;

namespace ClouDream.LostSkies
{
    /// <summary>날씨 또는 바이옴 시스템이 공통 렌더러에 환경 값을 공급하는 기반 클래스입니다.</summary>
    public abstract class CloudEnvironmentSource : MonoBehaviour
    {

        /// <summary>절대 월드 위치의 환경을 반환합니다. 바이옴 구현은 이 메서드를 재정의합니다.</summary>
        public abstract CloudEnvironment Evaluate(Vector3 worldPosition);
    }
}
