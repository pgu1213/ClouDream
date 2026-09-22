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
            LayeredBillows = 3,
            ConceptV2 = 4
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

        [Header("Concept V2: 큰 운해 형태 (m)")]
        [Tooltip("노이즈 텍스처의 월드 반복 주기입니다. 생성기 내부 scale 4를 포함하므로 봉우리 간격 자체는 아닙니다.")]
        [Min(12000f)]
        public float oceanMacroPeriod = 24000f;

        [Range(0f, 4000f)]
        public float oceanRelief = 1400f;

        [Range(-180f, 180f)]
        public float regionalFlowDegrees = 25f;

        [Range(0f, 1f)]
        public float ridgeStrength = 0.65f;

        [Header("Concept V2: 렌더와 조명 형태의 변위 (m)")]
        [Min(2000f)]
        public float conceptBillowPeriod = 6400f;

        [Range(0f, 600f)]
        public float viewBillowDisplacement = 170f;

        [Range(0f, 200f)]
        public float lightBillowDisplacement = 25f;

        [Range(0f, 160f)]
        public float fineBillowDisplacement = 35f;

        [Header("Concept V2: 큰 명암과 공기 원근")]
        [Range(0f, 1f)]
        public float macroNormalBlend = 0.85f;

        [Range(8f, 400f)]
        public float macroNormalStep = 150f;

        [Range(0.2f, 3f)]
        public float paletteContrast = 1f;

        [Range(0f, 3f)]
        public float aerialScale = 1f;

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

            ApplyConceptSettings(commands, shader);
        }

        /// <summary>V2 전용 GPU 계약을 설정합니다. 기존 mode 0~3은 이 값을 참조하지 않습니다.</summary>
        private void ApplyConceptSettings(CommandBuffer commands, ComputeShader shader)
        {
            // 큰 형태 주기, 운해 높이 변화, 렌더 변위, 조명 변위를 모두 월드 미터로 전달합니다.
            commands.SetComputeVectorParam(shader, "_ConceptShape", new Vector4(
                Mathf.Max(12000f, oceanMacroPeriod), Mathf.Clamp(oceanRelief, 0f, 4000f),
                Mathf.Clamp(viewBillowDisplacement, 0f, 600f), Mathf.Clamp(lightBillowDisplacement, 0f, 200f)));

            // 법선 혼합, 최대 차분 간격, 팔레트 대비, 공기 원근 강도입니다.
            commands.SetComputeVectorParam(shader, "_ConceptLighting", new Vector4(
                Mathf.Clamp01(macroNormalBlend), Mathf.Clamp(macroNormalStep, 8f, 400f),
                Mathf.Clamp(paletteContrast, 0.2f, 3f), Mathf.Clamp(aerialScale, 0f, 3f)));

            // 변위 텍스처 주기, 미세 변위, 지역 흐름 방향(라디안), 능선 비중입니다.
            commands.SetComputeVectorParam(shader, "_ConceptDetail", new Vector4(
                Mathf.Max(2000f, conceptBillowPeriod), Mathf.Clamp(fineBillowDisplacement, 0f, 160f),
                regionalFlowDegrees * Mathf.Deg2Rad, Mathf.Clamp01(ridgeStrength)));
        }
    }
}
