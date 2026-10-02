Shader "Hidden/ClouDream/ReconstructionValidation"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Validate
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
            #include "../Runtime/CloudReconstruction.hlsl"
            Texture2D<float4> _ValidationDepth;
            float4 _ValidationSize;

            // 생산용 복원 함수의 premultiplied RGB와 투과율을 노출·하늘 합성 없이 검사합니다.
            float4 Validate(Varyings input) : SV_Target
            {
                float2 uv = input.positionCS.xy / _ValidationSize.xy;
                float depth = _ValidationDepth.Load(int3((int2)input.positionCS.xy, 0)).r;
                CloudReconstructionSample sample = CloudReconstruct(uv, depth, 1);
                return float4(sample.lighting.rgb, sample.transmission);
            }
            ENDHLSL
        }
    }
}
