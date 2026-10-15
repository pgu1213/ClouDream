Shader "Hidden/ClouDream/LobeSurface"
{
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma target 5.0
            #pragma use_dxc dx12
            #pragma vertex LobeVertex
            #pragma fragment LobeFragment
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
            #include "CloudHybridRendering.hlsl"
            #define CLOUD_LOBE_SURFACE
            #include "CloudProceduralSurface.hlsl"
            float _LobeFarOnly;
            struct LobeVaryings { float4 positionCS : SV_POSITION; float3 position : TEXCOORD0; float3 normal : TEXCOORD1; };
            struct LobeOutput { float4 light : SV_Target0; float2 distance : SV_Target1; };

            // 로브 파라미터로 직접 정점을 만들고 같은 등가면으로 투영합니다.
            LobeVaryings LobeVertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
            {
                LobeVaryings output;
                CloudProceduralPoint(vertex, instance, output.position, output.normal);
                float3 relative = GetCameraRelativePositionWS(output.position - _HybridOrigin.xyz);
                output.positionCS = mul(UNITY_MATRIX_UNJITTERED_VP, float4(relative, 1));
                return output;
            }

            // 큰 명암 면은 공유 팔레트를 쓰고 두 번의 해석적 밀도로만 차폐를 근사합니다.
            float3 LobeSurfaceShade(float3 position, float3 normal, float3 ray, int kind, int2 cell)
            {
                float depth = 0;
                [loop]
                for (int index = 0; index < (int)_HybridLightSettings.y; index++)
                {
                    depth += LobeDensity(position + _HybridLightDirection.xyz * (index + 1) * 160, kind, cell)
                        * 160 * 0.009 * _HybridEnvironment.w;
                }
                float3 light = ConceptIncidentLight(depth, saturate(1 - depth * 0.08), dot(ray, _HybridLightDirection.xyz), normal);
                float air = smoothstep(_HybridAir.x, _HybridAir.y, length(position - _HybridCamera.xyz)) * _HybridAir.z;
                return lerp(light, _HybridAirColor.rgb, saturate(air));
            }

            // 거의 불투명한 내부는 깊이 버퍼가 순서를 정하고 실루엣의 좁은 띠만 디더로 부드럽게 합니다.
            LobeOutput LobeFragment(LobeVaryings input)
            {
                int kind = (int)_HybridObject.x;
                int2 cell = (int2)_HybridObject.yz;
                if (kind == 1)
                {
                    clip(_SkyPlacement.y - CellRandom(cell, 0));
                }
                if (kind == 2)
                {
                    clip(saturate(_SkyPlacement.y * (0.65 / 0.72)) - CellRandom(cell, 140));
                }
                float densityScale = _HybridEnvironment.x;
                if (kind != 0)
                {
                    densityScale *= _HybridEnvironment.y * _SkyPlacement.w;
                }
                clip(densityScale - 0.0001);
                float2 uv = input.positionCS.xy * _HybridOutputSize.zw;
                float3 ray = normalize(input.position - _HybridCamera.xyz);
                float distance = length(input.position - _HybridCamera.xyz);
                clip(HybridSceneDistance(uv, ray) - distance);
                float4 cameraField = LobeField(_HybridCamera.xyz, kind, cell);
                float weight = LobeLocalWeight(kind, cell);
                if (cameraField.x > 0)
                {
                    weight *= 1 - smoothstep(_HybridQuality.x, _HybridQuality.y, distance);
                }
                if (_LobeFarOnly > 0.5)
                {
                    clip(0.0001 - weight);
                }
                float3 normal = normalize(input.normal);
                if (kind == 0 && _HybridCamera.y < _OceanBounds.x)
                {
                    clip(_OceanBounds.x + 1 - input.position.y);
                    normal = float3(0, -1, 0);
                }
                if (kind != 0)
                {
                    float facing = abs(dot(normal, -ray));
                    float band = saturate(facing / max(0.01, _LobeDetail.y));
                    float dither = frac(dot(floor(input.positionCS.xy), float2(0.06711056, 0.00583715)));
                    dither = frac(52.9829189 * dither);
                    clip(band - dither);
                }
                LobeOutput output;
                output.light = float4(LobeSurfaceShade(input.position, normal, ray, kind, cell), 0);
                output.distance = float2(distance, weight);
                return output;
            }
            ENDHLSL
        }
    }
}
