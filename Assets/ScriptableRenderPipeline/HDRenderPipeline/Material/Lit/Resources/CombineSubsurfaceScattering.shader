Shader "Hidden/HDRenderPipeline/CombineSubsurfaceScattering"
{
    Properties
    {
        [HideInInspector] _DstBlend("", Float) = 1 // Can be set to 1 for blending with specular
    }

    SubShader
    {
        Pass
        {
            Stencil
            {
                Ref  1 // StencilBits.SSS
                Comp Equal
                Pass Keep
            }

            Cull   Off
            ZTest  Always
            ZWrite Off
            Blend  One [_DstBlend]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 ps4 metal // TEMP: until we go further in dev
            // #pragma enable_d3d11_debug_symbols

            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile _ SSS_FILTER_HORIZONTAL_AND_COMBINE

            //-------------------------------------------------------------------------------------
            // Include
            //-------------------------------------------------------------------------------------

            #include "../../../../ShaderLibrary/Common.hlsl"
            #include "../../../ShaderConfig.cs.hlsl"
            #include "../../../ShaderVariables.hlsl"
            #define UNITY_MATERIAL_LIT // Needs to be defined before including Material.hlsl
            #include "../../../Material/Material.hlsl"

            //-------------------------------------------------------------------------------------
            // Inputs & outputs
            //-------------------------------------------------------------------------------------

            #define N_PROFILES 8
            #define N_SAMPLES  11

            float4 _FilterKernels[N_PROFILES][N_SAMPLES]; // RGB = weights, A = radial distance
            float4 _HalfRcpWeightedVariances[N_PROFILES]; // RGB for chromatic, A for achromatic

            TEXTURE2D(_IrradianceSource);                 // RGB = irradiance on the back side of the object
            DECLARE_GBUFFER_TEXTURE(_GBufferTexture);     // Contains the albedo and SSS parameters

            //-------------------------------------------------------------------------------------
            // Implementation
            //-------------------------------------------------------------------------------------

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_Position;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                PositionInputs posInput = GetPositionInput(input.positionCS.xy, _ScreenSize.zw, uint2(0, 0));

                float3 unused;

                BSDFData bsdfData;
                FETCH_GBUFFER(gbuffer, _GBufferTexture, posInput.unPositionSS);
                DECODE_FROM_GBUFFER(gbuffer, 0xFFFFFFFF, bsdfData, unused);

                int   profileID    = bsdfData.subsurfaceProfile;
                float distScale    = bsdfData.subsurfaceRadius;
                float invDistScale = rcp(distScale);

                // Reconstruct the view-space position.
                float  rawDepth    = LOAD_TEXTURE2D(_MainDepthTexture, posInput.unPositionSS).r;
                float3 centerPosVS = ComputeViewSpacePosition(posInput.positionSS, rawDepth, _InvProjMatrix);

                // Compute the dimensions of the surface fragment viewed as a quad facing the camera.
                float fragWidth  = ddx_fine(centerPosVS.x);
                float fragheight = ddy_fine(centerPosVS.y);
                float stepSizeX  = rcp(fragWidth);
                float stepSizeY  = rcp(fragheight);

                // Compute the filtering direction.
            #ifdef SSS_FILTER_HORIZONTAL_AND_COMBINE
                float  stepSize      = stepSizeX;
                float2 unitDirection = float2(1, 0);
            #else
                float  stepSize      = stepSizeY;
                float2 unitDirection = float2(0, 1);
            #endif

                float2   scaledDirection  = distScale * stepSize * unitDirection;
                float    phi              = 0; // Random rotation; unused for now
                float2x2 rotationMatrix   = float2x2(cos(phi), -sin(phi), sin(phi), cos(phi));
                float2   rotatedDirection = mul(rotationMatrix, scaledDirection);

                // Load (1 / (2 * WeightedVariance)) for bilateral weighting.
            #ifdef RBG_BILATERAL_WEIGHTS
                float3 halfRcpVariance = _HalfRcpWeightedVariances[profileID].rgb;
            #else
                float  halfRcpVariance = _HalfRcpWeightedVariances[profileID].a;
            #endif

                // Take the first (central) sample.
                float2 samplePosition   = posInput.unPositionSS;
                float3 sampleWeight     = _FilterKernels[profileID][0].rgb;
                float3 sampleIrradiance = LOAD_TEXTURE2D(_IrradianceSource, samplePosition).rgb;

                // Accumulate filtered irradiance.
                float3 totalIrradiance = sampleWeight * sampleIrradiance;

                // Make sure bilateral filtering does not cause energy loss.
                // TODO: ask Morten if there is a better way to do this.
                float3 totalWeight = sampleWeight;

                [unroll]
                for (int i = 1; i < N_SAMPLES; i++)
                {
                    samplePosition = posInput.unPositionSS + rotatedDirection * _FilterKernels[profileID][i].a;
                    sampleWeight   = _FilterKernels[profileID][i].rgb;

                    rawDepth         = LOAD_TEXTURE2D(_MainDepthTexture, samplePosition).r;
                    sampleIrradiance = LOAD_TEXTURE2D(_IrradianceSource, samplePosition).rgb;

                    // Apply bilateral weighting.
                    // Ref #1: Skin Rendering by Pseudo–Separable Cross Bilateral Filtering.
                    // Ref #2: Separable SSS, Supplementary Materials, Section E.
                    float sampleDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                    float zDistance   = invDistScale * sampleDepth - (invDistScale * centerPosVS.z);
                    sampleWeight     *= exp(-zDistance * zDistance * halfRcpVariance);

                    if (any(sampleIrradiance) == false)
                    {
                        // The irradiance is 0. This could happen for 2 reasons.
                        // Most likely, the surface fragment does not have an SSS material.
                        // Alternatively, the surface fragment could be completely shadowed.
                        // Our blur is energy-preserving, so 'sampleWeight' should be set to 0.
                        // We do not terminate the loop since we want to gather the contribution
                        // of the remaining samples (e.g. in case of hair covering skin).
                        continue;
                    }

                    totalIrradiance += sampleWeight * sampleIrradiance;
                    totalWeight     += sampleWeight;
                }

            #ifdef SSS_FILTER_HORIZONTAL_AND_COMBINE
                bool performPostScatterTexturing = IsBitSet(_TexturingModeFlags, profileID);

                // It's either post-scatter, or pre- and post-scatter texturing.
                float3 diffuseContrib = performPostScatterTexturing ? bsdfData.diffuseColor
                                                                    : sqrt(bsdfData.diffuseColor);
                return float4(diffuseContrib * totalIrradiance / totalWeight, 1.0);
            #else
                return float4(totalIrradiance / totalWeight, 1.0);
            #endif
            }
            ENDHLSL
        }
    }
    Fallback Off
}
