using UnityEngine;
using UnityEngine.Rendering;

namespace ClouDream.LostSkies
{
    /// <summary>기존 볼륨 렌더 경로에서 형태와 산란의 미술적 표현만 바꾸는 프로필입니다.</summary>
    [CreateAssetMenu(menuName = "ClouDream/Clouds/Surface Style")]
    public sealed class CloudStyleProfile : CloudFormationProfile
    {
        public enum ShapeMethod
        {
            SoftNoise = 1,
            SculptedLobes = 2,
            LayeredBillows = 3
        }

        [Header("입체 형태")]
        public ShapeMethod method = ShapeMethod.SculptedLobes;
        [Range(0.015f, 0.3f)] public float edgeSoftness = 0.10f;
        [Range(0f, 0.15f)] public float surfaceDisplacement = 0.035f;
        [Range(0f, 4f)] public float noiseMip = 2f;
        [Min(200f)] public float oceanLobeSize = 1700f;

        [Header("둥근 굴곡의 크기와 강도")]
        [Min(500f)] public float billowPeriod = 4200f;
        [Range(0f, 0.6f)] public float billowStrength = 0.37f;
        [Min(300f)] public float fineBillowPeriod = 1900f;
        [Range(0f, 0.2f)] public float fineBillowStrength = 0.065f;

        [Header("빛과 그늘의 면")]
        [Range(0f, 1f)] public float softBandStrength = 0.6f;
        [Range(0f, 1f)] public float ambientOcclusion = 0.45f;
        [Range(0f, 1f)] public float shadowLift = 0.18f;
        [Range(0.25f, 2f)] public float extinctionScale = 0.9f;

        /// <summary>스타일 값을 전달합니다. 밀도장과 조명은 모든 카메라에서 동일하게 평가됩니다.</summary>
        public override void Apply(CommandBuffer commands, ComputeShader shader)
        {
            commands.SetComputeIntParam(shader, "_StyleMode", (int)method);
            commands.SetComputeVectorParam(shader, "_StyleShape", new Vector4(
                Mathf.Max(0.015f, edgeSoftness), Mathf.Max(0f, surfaceDisplacement),
                Mathf.Clamp(noiseMip, 0f, 4f), Mathf.Max(200f, oceanLobeSize)));

            commands.SetComputeVectorParam(shader, "_StyleLight", new Vector4(
                Mathf.Clamp01(softBandStrength), Mathf.Clamp01(ambientOcclusion),
                Mathf.Clamp01(shadowLift), Mathf.Max(0.25f, extinctionScale)));

            commands.SetComputeVectorParam(shader, "_StyleDetail", new Vector4(
                Mathf.Max(500f, billowPeriod), Mathf.Clamp(billowStrength, 0f, 0.6f),
                Mathf.Max(300f, fineBillowPeriod), Mathf.Clamp(fineBillowStrength, 0f, 0.2f)));
        }
    }
}
