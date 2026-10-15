Shader "Hidden/ClouDream/HybridProxy"
{
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }

        Pass
        {
            Name "Surface"
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma use_dxc dx12
            #pragma only_renderers d3d11
            #pragma vertex SurfaceVertex
            #pragma fragment SurfaceFragment
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
            #include "CloudHybridRendering.hlsl"
            #include "CloudProceduralSurface.hlsl"

            struct HybridTriangle
            {
                float3 a, b, c;
                float3 na, nb, nc;
            };

            StructuredBuffer<HybridTriangle> _HybridTriangles;

            struct SurfaceVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 position : TEXCOORD0;
                float3 normal : TEXCOORD1;
            };

            struct SurfaceOutput
            {
                float4 light : SV_Target0;
                float2 depth : SV_Target1;
            };

            // 간접 정점 버퍼에서 큰 형태를 읽고 동일한 노이즈 변위를 적용합니다.
            SurfaceVaryings SurfaceVertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
            {
                float3 position, normal;
                CloudProceduralPoint(vertex, instance, position, normal);
                SurfaceVaryings output;
                float3 relativePosition = GetCameraRelativePositionWS(position - _HybridOrigin.xyz);
                output.positionCS = mul(UNITY_MATRIX_UNJITTERED_VP, float4(relativePosition, 1));
                output.position = position;
                output.normal = normal;
                return output;
            }

            // 같은 구름의 표면 결과만 저장합니다. 볼륨 결과와의 보간은 다음 단계에서 한 번 수행합니다.
            SurfaceOutput SurfaceFragment(SurfaceVaryings input)
            {
                float2 uv = input.positionCS.xy * _HybridOutputSize.zw;
                float3 ray = normalize(input.position - _HybridCamera.xyz);
                float distance = length(input.position - _HybridCamera.xyz);
                clip(HybridSceneDistance(uv, ray) - distance);
                int2 cell = (int2)_HybridObject.yz;
                if (_HybridObject.x > 0.5 && _HybridObject.x < 1.5)
                {
                    clip(_SkyPlacement.y - CellRandom(cell, 0));
                }
                if (_HybridObject.x > 1.5)
                {
                    clip(saturate(_SkyPlacement.y * (0.65 / 0.72)) - CellRandom(cell, 140));
                }
                float4 field = HybridBaseField(input.position);
                float density = field.w * _HybridEnvironment.x;
                if (_HybridObject.x > 0.5)
                {
                    density *= _HybridEnvironment.y;
                }

                float thickness = max(120, field.y * 0.9);
                float transmission = exp(-max(0, density) * thickness * 0.009 * _HybridEnvironment.w);
                float opacity = 1 - transmission;
                float3 normal = normalize(input.normal);
                if (_HybridObject.x < 0.5 && _HybridCamera.y < _OceanBounds.x)
                {
                    // 연속 운해 아래에서는 다른 타일의 상단 뒷면이 하부 위로 겹치지 않습니다.
                    clip(_OceanBounds.x + 1 - input.position.y);
                    normal = float3(0, -1, 0);
                }
                SurfaceOutput output;
                output.light = float4(HybridShade(input.position, normal, ray, false) * opacity, transmission);
                output.depth = float2(distance, thickness);
                return output;
            }
            ENDHLSL
        }
    }
}
