using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    [CreateAssetMenu(menuName = "ClouDream/Clouds/Continuous Ocean")]
    public sealed class CloudOceanProfile : CloudFormationProfile
    {
        [Header("연속 운해의 범위 (m)")]
        public float bottom = -1600f;
        [Min(100f)]
        public float thickness = 2700f;

        /// <summary>기존 운해의 높이 범위를 공유 밀도장에 전달합니다.</summary>
        public override void Apply(CommandBuffer commands, ComputeShader shader)
        {
            commands.SetComputeVectorParam(shader, "_OceanBounds", new Vector4(bottom, Mathf.Max(100f, thickness), 0f, 0f));
        }
    }
}
