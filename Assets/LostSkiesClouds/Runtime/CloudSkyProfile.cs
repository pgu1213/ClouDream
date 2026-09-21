using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    [CreateAssetMenu(menuName = "ClouDream/Clouds/Sparse Sky")]
    public sealed class CloudSkyProfile : CloudFormationProfile
    {
        [Header("큰 구름 사이의 간격")]
        [Min(4000f)]
        public float spacing = 11500f;
        [Range(0f, 1f)]
        public float occupiedCells = 0.72f;
        public int seed = 73;

        [Header("중심 고도와 반경의 변화 (m)")]
        public Vector2 altitude = new Vector2(4900f, 7600f);
        public Vector2 horizontalRadius = new Vector2(2000f, 3900f);
        public Vector2 verticalRadius = new Vector2(1100f, 2400f);

        [Header("덩어리와 표면")]
        [Range(0.2f, 2f)]
        public float density = 1f;
        [Range(0f, 0.5f)]
        public float erosion = 0.19f;

        /// <summary>유효 범위를 보정하여 셀 경계를 넘지 않는 가변 구름 설정을 전달합니다.</summary>
        public override void Apply(CommandBuffer commands, ComputeShader shader)
        {
            float cellSize = Mathf.Max(4000f, spacing);
            Vector2 heights = OrderedRange(altitude, -100000f);
            Vector2 widths = OrderedRange(horizontalRadius, 100f);
            Vector2 depths = OrderedRange(verticalRadius, 100f);
            // 회전과 중심 흔들림 이후에도 이웃 셀과 절단면이 생기지 않도록 제한합니다.
            widths.x = Mathf.Min(widths.x, cellSize * 0.38f);
            widths.y = Mathf.Min(widths.y, cellSize * 0.38f);
            commands.SetComputeVectorParam(shader, "_SkyPlacement", new Vector4(cellSize, Mathf.Clamp01(occupiedCells), seed, Mathf.Max(0f, density)));
            commands.SetComputeVectorParam(shader, "_SkyAltitude", new Vector4(heights.x, heights.y, depths.x, depths.y));
            commands.SetComputeVectorParam(shader, "_SkyShape", new Vector4(widths.x, widths.y, Mathf.Clamp(erosion, 0f, 0.5f), 1f));
        }

        /// <summary>Inspector 또는 외부 코드에서 뒤집힌 범위를 안전한 순서로 정리합니다.</summary>
        public static Vector2 OrderedRange(Vector2 range, float minimum)
        {
            return new Vector2(Mathf.Max(minimum, Mathf.Min(range.x, range.y)), Mathf.Max(minimum, Mathf.Max(range.x, range.y)));
        }
    }
}
