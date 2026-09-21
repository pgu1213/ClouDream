using UnityEngine;

namespace ClouDream.LostSkies
{
    public sealed class CloudWorldOrigin : MonoBehaviour
    {
        [SerializeField]
        private Vector3 samplingOffset;
        public Vector3 SamplingOffset
        {
            get
            {
                return samplingOffset;
            }
        }

        /// <summary>게임이 모든 씬 오브젝트에 더한 이동량을 전달합니다. 구름 샘플 좌표는 고정됩니다.</summary>
        public void ApplySceneTranslation(Vector3 sceneTranslation)
        {
            samplingOffset -= sceneTranslation;
        }

        /// <summary>원점 이동 이후에도 동일한 절대 구름 위치를 돌려줍니다.</summary>
        public Vector3 ToCloudPosition(Vector3 scenePosition)
        {
            return scenePosition + samplingOffset;
        }
    }
}
