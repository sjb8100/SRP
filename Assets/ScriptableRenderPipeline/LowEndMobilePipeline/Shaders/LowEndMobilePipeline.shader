// Shader targeted for LowEnd mobile devices. Single Pass Forward Rendering. Shader Model 2
Shader "ScriptableRenderPipeline/LowEndMobile/NonPBR"
{
    // Keep properties of StandardSpecular shader for upgrade reasons.
    Properties
    {
        _Color("Color", Color) = (1,1,1,1)
        _MainTex("Base (RGB) Glossiness / Alpha (A)", 2D) = "white" {}

        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        _Shininess("Shininess", Range(0.01, 1.0)) = 1.0
        _GlossMapScale("Smoothness Factor", Range(0.0, 1.0)) = 1.0

        _Glossiness("Glossiness", Range(0.0, 1.0)) = 0.5
        [Enum(Specular Alpha,0,Albedo Alpha,1)] _SmoothnessTextureChannel("Smoothness texture channel", Float) = 0

        _Cube ("Reflection Cubemap", CUBE) = "" {}
        _ReflectionSource("Reflection Source", Float) = 0

        [HideInInspector] _SpecSource("Specular Color Source", Float) = 0.0
        _SpecColor("Specular", Color) = (1.0, 1.0, 1.0)
        _SpecGlossMap("Specular", 2D) = "white" {}
        [HideInInspector] _GlossinessSource("Glossiness Source", Float) = 0.0
        [ToggleOff] _SpecularHighlights("Specular Highlights", Float) = 1.0
        [ToggleOff] _GlossyReflections("Glossy Reflections", Float) = 1.0

        [HideInInspector] _BumpScale("Scale", Float) = 1.0
        [NoScaleOffset] _BumpMap("Normal Map", 2D) = "bump" {}

        _Parallax("Height Scale", Range(0.005, 0.08)) = 0.02
        _ParallaxMap("Height Map", 2D) = "black" {}

        _EmissionColor("Emission Color", Color) = (0,0,0)
        _EmissionMap("Emission", 2D) = "white" {}

        _DetailMask("Detail Mask", 2D) = "white" {}

        _DetailAlbedoMap("Detail Albedo x2", 2D) = "grey" {}
        _DetailNormalMapScale("Scale", Float) = 1.0
        _DetailNormalMap("Normal Map", 2D) = "bump" {}

        [Enum(UV0,0,UV1,1)] _UVSec("UV Set for secondary textures", Float) = 0

            // Blending state
            [HideInInspector] _Mode("__mode", Float) = 0.0
            [HideInInspector] _SrcBlend("__src", Float) = 1.0
            [HideInInspector] _DstBlend("__dst", Float) = 0.0
            [HideInInspector] _ZWrite("__zw", Float) = 1.0
    }

        SubShader
        {
            Tags { "RenderType" = "Opaque" "RenderPipeline" = "LowEndMobilePipeline" }
            LOD 300

            Pass
            {
                Name "LD_SINGLE_PASS_FORWARD"
                Tags { "LightMode" = "LowEndMobileForward" }

            // Use same blending / depth states as Standard shader
            Blend[_SrcBlend][_DstBlend]
            ZWrite[_ZWrite]

            CGPROGRAM
            #pragma target 2.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature _ _ALPHATEST_ON _ALPHABLEND_ON
            #pragma shader_feature _ _SPECGLOSSMAP _SPECGLOSSMAP_BASE_ALPHA _SPECULAR_COLOR
            #pragma shader_feature _NORMALMAP
            #pragma shader_feature _EMISSION_MAP
            #pragma shader_feature _ _REFLECTION_CUBEMAP _REFLECTION_PROBE

            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ _LIGHT_PROBES_ON
            #pragma multi_compile _ _HARD_SHADOWS _SOFT_SHADOWS _HARD_SHADOWS_CASCADES _SOFT_SHADOWS_CASCADES
            #pragma multi_compile _ _VERTEX_LIGHTS
            #pragma multi_compile_fog
            #pragma only_renderers d3d9 d3d11 d3d11_9x glcore gles gles3 metal

            #include "UnityCG.cginc"
            #include "UnityStandardBRDF.cginc"
            #include "UnityStandardInput.cginc"
            #include "UnityStandardUtils.cginc"
            #include "LowEndMobilePipelineCore.cginc"

            v2f vert(LowendVertexInput v)
            {
                v2f o = (v2f)0;

                o.uv01.xy = TRANSFORM_TEX(v.texcoord, _MainTex);
                o.uv01.zw = v.lightmapUV * unity_LightmapST.xy + unity_LightmapST.zw;
                o.hpos = UnityObjectToClipPos(v.vertex);

                o.posWS = mul(unity_ObjectToWorld, v.vertex).xyz;

                o.viewDir.xyz = normalize(_WorldSpaceCameraPos - o.posWS);
                half3 normal = normalize(UnityObjectToWorldNormal(v.normal));

#if _NORMALMAP
                half sign = v.tangent.w * unity_WorldTransformParams.w;
                half3 tangent = normalize(UnityObjectToWorldDir(v.tangent));
                half3 binormal = cross(normal, tangent) * v.tangent.w;

                // Initialize tangetToWorld in column-major to benefit from better glsl matrix multiplication code
                o.tangentToWorld0 = half3(tangent.x, binormal.x, normal.x);
                o.tangentToWorld1 = half3(tangent.y, binormal.y, normal.y);
                o.tangentToWorld2 = half3(tangent.z, binormal.z, normal.z);
#else
                o.normal = normal;
#endif

#if defined(_VERTEX_LIGHTS)
                half4 diffuseAndSpecular = half4(1.0, 1.0, 1.0, 1.0);
                for (int lightIndex = globalLightData.x; lightIndex < globalLightData.y; ++lightIndex)
                {
                    LightInput lightInput;
                    half NdotL;
                    INITIALIZE_LIGHT(lightInput, lightIndex);
                    o.fogCoord.yzw += EvaluateOneLight(lightInput, diffuseAndSpecular.rgb, diffuseAndSpecular, normal, o.posWS, o.viewDir.xyz, NdotL);
                }
#endif

#ifdef _LIGHT_PROBES_ON
                o.fogCoord.yzw += max(half3(0, 0, 0), ShadeSH9(half4(normal, 1)));
#endif

                UNITY_TRANSFER_FOG(o, o.hpos);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half4 diffuseAlpha = tex2D(_MainTex, i.uv01.xy);
                half3 diffuse = diffuseAlpha.rgb * _Color.rgb;
                half alpha = diffuseAlpha.a * _Color.a;

                // Keep for compatibility reasons. Shader Inpector throws a warning when using cutoff
                // due overdraw performance impact.
#ifdef _ALPHATEST_ON
                clip(alpha - _Cutoff);
#endif

                half3 normal;
                NormalMap(i, normal);

                half4 specularGloss;
                SpecularGloss(i.uv01.xy, diffuse, alpha, specularGloss);

                half3 viewDir = i.viewDir.xyz;

                // TODO: Restrict pixel lights by 4. This way we can keep moderate constrain for most LD project
                // and can benefit from better data layout/avoid branching by doing vec math.
                half3 color = half3(0, 0, 0);
                for (int lightIndex = 0; lightIndex < globalLightData.x; ++lightIndex)
                {
                    LightInput lightData;
                    half NdotL;
                    INITIALIZE_LIGHT(lightData, lightIndex);
                    color += EvaluateOneLight(lightData, diffuse, specularGloss, normal, i.posWS, viewDir, NdotL); 
#ifdef _SHADOWS
                    if (lightIndex == 0)
                    {
                    	#if _NORMALMAP
                    	float3 vertexNormal = float3(i.tangentToWorld0.z, i.tangentToWorld1.z, i.tangentToWorld2.z);
                    	#else
                    	float3 vertexNormal = i.normal;
                    	#endif
                        float bias = max(globalLightData.z, (1.0 - NdotL) * globalLightData.w);
                        color *= ComputeShadowAttenuation(i, vertexNormal * bias);
                    }
#endif
                }

                half3 emissionColor;
                Emission(i, emissionColor);
                color += emissionColor;

#if defined(LIGHTMAP_ON)
                color += (DecodeLightmap(UNITY_SAMPLE_TEX2D(unity_Lightmap, i.uv01.zw)) + i.fogCoord.yzw) * diffuse;
#elif defined(_VERTEX_LIGHTS) || defined(_LIGHT_PROBES_ON)
                color += i.fogCoord.yzw * diffuse;
#endif

#if _REFLECTION_CUBEMAP
                // TODO: we can use reflect vec to compute specular instead of half when computing cubemap reflection
                half3 reflectVec = reflect(-i.viewDir.xyz, normal);
                color += texCUBE(_Cube, reflectVec).rgb * specularGloss.rgb;
#elif defined(_REFLECTION_PROBE)
                half3 reflectVec = reflect(-i.viewDir.xyz, normal);
                half4 reflectionProbe = UNITY_SAMPLE_TEXCUBE(unity_SpecCube0, reflectVec);
                color += reflectionProbe.rgb * (reflectionProbe.a * unity_SpecCube0_HDR.x) * specularGloss.rgb;
#endif

                UNITY_APPLY_FOG(i.fogCoord, color);

                return OutputColor(color, alpha);
            };
            ENDCG
        }

        Pass
        {
            Name "LD_SHADOW_CASTER"
            Tags { "Lightmode" = "ShadowCaster" }

            ZWrite On ZTest LEqual

            CGPROGRAM
            #pragma target 2.0
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            float4 vert(float4 pos : POSITION) : SV_POSITION
            {
            	float4 clipPos = UnityObjectToClipPos(pos);
#if defined(UNITY_REVERSED_Z)
                clipPos.z = min(clipPos.z, UNITY_NEAR_CLIP_VALUE);
#else
                clipPos.z = max(clipPos.z, UNITY_NEAR_CLIP_VALUE);
#endif
                return clipPos;
            }

            half4 frag() : SV_TARGET
            {
                return 0;
            }
            ENDCG
        }

        // This pass it not used during regular rendering, only for lightmap baking.
        Pass
        {
            Name "LD_META"
            Tags{ "LightMode" = "Meta" }

            Cull Off

            CGPROGRAM
            #pragma vertex vert_meta
            #pragma fragment frag_meta

            #pragma shader_feature _EMISSION
            #pragma shader_feature _METALLICGLOSSMAP
            #pragma shader_feature _ _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma shader_feature ___ _DETAIL_MULX2
            #pragma shader_feature EDITOR_VISUALIZATION

            #include "UnityStandardMeta.cginc"
            ENDCG
        }
    }
    Fallback "Standard (Specular setup)"
    CustomEditor "LowendMobilePipelineMaterialEditor"
}
