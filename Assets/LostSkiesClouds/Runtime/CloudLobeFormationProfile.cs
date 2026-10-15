using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    /// <summary>B안의 큰 덩어리와 얕은 실루엣 변위를 조절합니다. 시간대 색은 기존 환경 공급자가 담당합니다.</summary>
    [CreateAssetMenu(menuName = "ClouDream/Clouds/Stylized Lobes")]
    public sealed class CloudLobeFormationProfile : CloudFormationProfile
    {
        [Range(0.6f, 1.4f)] public float width = 1f;

        [Range(0.6f, 1.4f)] public float height = 1f;

        [Range(0.05f, 0.4f)] public float join = 0.16f;

        [Range(0.7f, 1.6f)] public float shoulder = 1.15f;

        [Range(0f, 120f)] public float surfaceDetail = 45f;

        [Range(0.01f, 0.3f)] public float silhouetteSoftness = 0.10f;

        /// <summary>같은 로브 입력을 표면과 국소 밀도에 전달하고 CPU 생성식은 중복하지 않습니다.</summary>
        public override bool TryGetSurfaceLobes(out Vector4 body, out Vector4 detail)
        {
            body = new Vector4(width, height, join, shoulder);
            detail = new Vector4(surfaceDetail, silhouetteSoftness, 0f, 0f);
            return true;
        }

        /// <summary>공통 형태 프로필 계약으로 같은 로브 입력을 GPU 커널에도 전달합니다.</summary>
        public override void Apply(CommandBuffer commands, ComputeShader shader)
        {
            TryGetSurfaceLobes(out Vector4 body, out Vector4 detail);
            commands.SetComputeVectorParam(shader, "_LobeBody", body);
            commands.SetComputeVectorParam(shader, "_LobeDetail", detail);
        }
    }
}
