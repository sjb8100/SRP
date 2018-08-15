//
// This file was automatically generated from Assets/ScriptableRenderPipeline/HDRenderPipeline/Shadow/ShadowBase.cs.  Please don't edit by hand.
//

#ifndef SHADOWBASE_CS_HLSL
#define SHADOWBASE_CS_HLSL
//
// UnityEngine.Experimental.Rendering.HDPipeline.GPUShadowType:  static fields
//
#define GPUSHADOWTYPE_POINT (0)
#define GPUSHADOWTYPE_SPOT (1)
#define GPUSHADOWTYPE_DIRECTIONAL (2)
#define GPUSHADOWTYPE_MAX (3)
#define GPUSHADOWTYPE_UNKNOWN (3)
#define GPUSHADOWTYPE_ALL (3)

//
// UnityEngine.Experimental.Rendering.HDPipeline.GPUShadowSampling:  static fields
//
#define GPUSHADOWSAMPLING_PCF_1TAP (0)
#define GPUSHADOWSAMPLING_PCF_9TAPS_ADAPTIVE (1)
#define GPUSHADOWSAMPLING_VSM_1TAP (2)
#define GPUSHADOWSAMPLING_MSM_1TAP (3)

// Generated from UnityEngine.Experimental.Rendering.HDPipeline.ShadowExp.ShadowData
// PackingRules = Exact
struct ShadowData
{
    float4x4 worldToShadow;
    float4 scaleOffset;
    float2 texelSizeRcp;
    uint id;
    int shadowType;
    uint payloadOffset;
    int lightType;
    float bias;
    float quality;
};

//
// Accessors for UnityEngine.Experimental.Rendering.HDPipeline.ShadowExp.ShadowData
//
float4x4 GetWorldToShadow(ShadowData value)
{
	return value.worldToShadow;
}
float4 GetScaleOffset(ShadowData value)
{
	return value.scaleOffset;
}
float2 GetTexelSizeRcp(ShadowData value)
{
	return value.texelSizeRcp;
}
uint GetId(ShadowData value)
{
	return value.id;
}
int GetShadowType(ShadowData value)
{
	return value.shadowType;
}
uint GetPayloadOffset(ShadowData value)
{
	return value.payloadOffset;
}
int GetLightType(ShadowData value)
{
	return value.lightType;
}
float GetBias(ShadowData value)
{
	return value.bias;
}
float GetQuality(ShadowData value)
{
	return value.quality;
}


#endif
