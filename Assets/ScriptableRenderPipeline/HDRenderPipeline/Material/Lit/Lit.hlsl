//-----------------------------------------------------------------------------
// SurfaceData and BSDFData
//-----------------------------------------------------------------------------

// SurfaceData is define in Lit.cs which generate Lit.cs.hlsl
#include "Lit.cs.hlsl"

// In case we pack data uint16 buffer we need to change the output render target format to uint16
// TODO: Is there a way to automate these output type based on the format declare in lit.cs ?
#if SHADEROPTIONS_PACK_GBUFFER_IN_U16
#define GBufferType0 uint4
#define GBufferType1 uint4

// TODO: How to abstract that ? We would like to avoid this PS4 test here
#ifdef SHADER_API_PS4
// On PS4 we need to specify manually the format of the output render target, output type is not enough
#pragma PSSL_target_output_format(target 0 FMT_UINT16_ABGR)
#pragma PSSL_target_output_format(target 1 FMT_UINT16_ABGR)
#endif

#else
#define GBufferType0 float4
#define GBufferType1 float4
#define GBufferType2 float4
#define GBufferType3 float4
#endif

// Reference Lambert diffuse / GGX Specular for IBL and area lights
#ifdef HAS_LIGHTLOOP // Both reference define below need to be define only if LightLoop is present, else we get a compile error
//#define LIT_DISPLAY_REFERENCE_AREA
//#define LIT_DISPLAY_REFERENCE_IBL
#endif
// Use Lambert diffuse instead of Disney diffuse
// #define LIT_DIFFUSE_LAMBERT_BRDF
// Use optimization of Precomputing LambdaV
// TODO: Test if this is a win
// #define LIT_USE_BSDF_PRE_LAMBDAV
// TODO: Check if anisotropy with a dynamic if on anisotropy > 0 is performant. Because it may mean we always calculate both isotropy and anisotropy case.
// Maybe we should always calculate anisotropy in case of standard ? Don't think the compile can optimize correctly.

SamplerState ltc_linear_clamp_sampler;
// TODO: This one should be set into a constant Buffer at pass frequency (with _Screensize)
TEXTURE2D(_PreIntegratedFGD);
TEXTURE2D_ARRAY(_LtcData); // We pack the 3 Ltc data inside a texture array
#define LTC_GGX_MATRIX_INDEX 0 // RGBA
#define LTC_DISNEY_DIFFUSE_MATRIX_INDEX 1 // RGBA
#define LTC_MULTI_GGX_FRESNEL_DISNEY_DIFFUSE_INDEX 2 // RGB, A unused
#define LTC_LUT_SIZE   64
#define LTC_LUT_SCALE  ((LTC_LUT_SIZE - 1) * rcp(LTC_LUT_SIZE))
#define LTC_LUT_OFFSET (0.5 * rcp(LTC_LUT_SIZE))

// SSS parameters
#define SSS_N_PROFILES        8
#define SSS_UNIT_CONVERSION   (1.0 / 300.0)                // From 1/3 centimeters to meters
#define SSS_LOW_THICKNESS     0.005                        // 0.5 cm
#define CENTIMETERS_TO_METERS (1.0 / 100.0)

uint   _EnableSSS;                                         // Globally toggles subsurface scattering on/off
uint   _TransmissionFlags;                                 // 1 bit/profile; 0 = inf. thick, 1 = supports transmission
uint   _TexturingModeFlags;                                // 1 bit/profile; 0 = PreAndPostScatter, 1 = PostScatter
float4 _TintColors[SSS_N_PROFILES];                        // For transmission; alpha is unused
float  _ThicknessRemaps[SSS_N_PROFILES][2];                // Remap: 0 = start, 1 = end - start
float4 _HalfRcpVariancesAndLerpWeights[SSS_N_PROFILES][2]; // 2x Gaussians per color channel, A is the the associated interpolation weight

//-----------------------------------------------------------------------------
// Helper functions/variable specific to this material
//-----------------------------------------------------------------------------

float PackMaterialId(int materialId)
{
    return float(materialId) / 3.0;
}

int UnpackMaterialId(float f)
{
    return int(round(f * 3.0));
}

// For image based lighting, a part of the BSDF is pre-integrated.
// This is done both for specular and diffuse (in case of DisneyDiffuse)
void GetPreIntegratedFGD(float NdotV, float perceptualRoughness, float3 fresnel0, out float3 specularFGD, out float diffuseFGD)
{
    // Pre-integrate GGX FGD
    //  _PreIntegratedFGD.x = Gv * (1 - Fc)  with Fc = (1 - H.L)^5
    //  _PreIntegratedFGD.y = Gv * Fc
    // Pre integrate DisneyDiffuse FGD:
    // _PreIntegratedFGD.z = DisneyDiffuse
    float3 preFGD = SAMPLE_TEXTURE2D_LOD(_PreIntegratedFGD, ltc_linear_clamp_sampler, float2(NdotV, perceptualRoughness), 0).xyz;

    // f0 * Gv * (1 - Fc) + Gv * Fc
    specularFGD = fresnel0 * preFGD.x + preFGD.y;

#ifdef LIT_DIFFUSE_LAMBERT_BRDF
    diffuseFGD = 1.0;
#else
    diffuseFGD = preFGD.z;
#endif
}

void ApplyDebugToBSDFData(inout BSDFData bsdfData)
{
#ifdef DEBUG_DISPLAY
    if (_DebugLightingMode == DEBUGLIGHTINGMODE_SPECULAR_LIGHTING)
    {
        bool overrideSmoothness = _DebugLightingSmoothness.x != 0.0;
        float overrideSmoothnessValue = _DebugLightingSmoothness.y;

        if (overrideSmoothness)
        {
            bsdfData.perceptualRoughness = PerceptualSmoothnessToPerceptualRoughness(overrideSmoothnessValue);
            bsdfData.roughness = PerceptualRoughnessToRoughness(bsdfData.perceptualRoughness);
        }
    }

    if (_DebugLightingMode == DEBUGLIGHTINGMODE_DIFFUSE_LIGHTING)
    {
        bsdfData.diffuseColor = _DebugLightingAlbedo.xyz;
    }
#endif
}

// Evaluates transmittance for a linear combination of two normalized 2D Gaussians.
// Computes results for each color channel separately.
// Ref: Real-Time Realistic Skin Translucency (2010), equation 9 (modified).
float3 ComputeTransmittance(float3 halfRcpVariance1, float lerpWeight1,
                            float3 halfRcpVariance2, float lerpWeight2,
                            float3 tintColor, float thickness, float radiusScale)
{

    // Thickness and SSS radius are decoupled for artists.
    // In theory, we should modify the thickness by the inverse of the radius scale of the profile.
    // thickness /= radiusScale;
    thickness /= CENTIMETERS_TO_METERS;

    float t2 = thickness * thickness;

    // TODO: 6 exponentials is kind of expensive... Should we use a LUT instead?
    // T = lerp(exp(-t2 * halfRcpVariance1), exp(-t2 * halfRcpVariance2), lerpWeight2)
    float3 transmittance = exp(-t2 * halfRcpVariance1) * lerpWeight1
                         + exp(-t2 * halfRcpVariance2) * lerpWeight2;
    return transmittance * tintColor;
}

void FillMaterialIdStandardData(float3 baseColor, float specular, float metallic, float roughness, float3 normalWS, float3 tangentWS, float anisotropy, inout BSDFData bsdfData)
{
    bsdfData.diffuseColor = baseColor * (1.0 - metallic);
    bsdfData.fresnel0 = lerp(float3(specular.xxx), baseColor, metallic);

    // TODO: encode specular

    bsdfData.tangentWS = tangentWS;
    bsdfData.bitangentWS = cross(normalWS, tangentWS);
    ConvertAnisotropyToRoughness(roughness, anisotropy, bsdfData.roughnessT, bsdfData.roughnessB);
    bsdfData.anisotropy = anisotropy;
}

void FillMaterialIdSSSData(float3 baseColor, int subsurfaceProfile, float subsurfaceRadius, float thickness, inout BSDFData bsdfData)
{
    bsdfData.diffuseColor = baseColor;

    // TODO take from subsurfaceProfile
    bsdfData.fresnel0 = 0.04; // Should be 0.028 for the skin
    bsdfData.subsurfaceProfile = subsurfaceProfile;
    // Make the Std. Dev. of 1 correspond to the effective radius of 1 cm (three-sigma rule).
    bsdfData.subsurfaceRadius = SSS_UNIT_CONVERSION * subsurfaceRadius + 0.0001;
    bsdfData.thickness = CENTIMETERS_TO_METERS * (_ThicknessRemaps[subsurfaceProfile][0] +
                                                  _ThicknessRemaps[subsurfaceProfile][1] * thickness);

    bsdfData.enableTransmission = IsBitSet(_TransmissionFlags, subsurfaceProfile);
    if (bsdfData.enableTransmission)
    {
        bsdfData.transmittance = ComputeTransmittance(  _HalfRcpVariancesAndLerpWeights[subsurfaceProfile][0].xyz,
                                                        _HalfRcpVariancesAndLerpWeights[subsurfaceProfile][0].w,
                                                        _HalfRcpVariancesAndLerpWeights[subsurfaceProfile][1].xyz,
                                                        _HalfRcpVariancesAndLerpWeights[subsurfaceProfile][1].w,
                                                        _TintColors[subsurfaceProfile].rgb, bsdfData.thickness, bsdfData.subsurfaceRadius);
    }

    #ifndef SSS_FILTER_HORIZONTAL_AND_COMBINE // When doing the SSS comine pass, we must not apply the modification of diffuse color
    // Handle post-scatter, or pre- and post-scatter texturing.
    // We modify diffuseColor here so it affect all the lighting + GI (lightprobe / lightmap) (Need to be done also in GBuffer pass) + transmittance
    // diffuseColor will be solely use during lighting pass. The other contribution will be apply in subsurfacescattering convolution.
    bool performPostScatterTexturing = IsBitSet(_TexturingModeFlags, subsurfaceProfile);
    bsdfData.diffuseColor = performPostScatterTexturing ? float3(1.0, 1.0, 1.0) : sqrt(bsdfData.diffuseColor);
    #endif
}

//-----------------------------------------------------------------------------
// conversion function for forward
//-----------------------------------------------------------------------------

BSDFData ConvertSurfaceDataToBSDFData(SurfaceData surfaceData)
{
    BSDFData bsdfData;
    ZERO_INITIALIZE(BSDFData, bsdfData);

    bsdfData.specularOcclusion = surfaceData.specularOcclusion;
    bsdfData.normalWS = surfaceData.normalWS;
    bsdfData.perceptualRoughness = PerceptualSmoothnessToPerceptualRoughness(surfaceData.perceptualSmoothness);
    bsdfData.roughness = PerceptualRoughnessToRoughness(bsdfData.perceptualRoughness);
    bsdfData.materialId = surfaceData.materialId;

    if (bsdfData.materialId == MATERIALID_LIT_STANDARD)
    {
        FillMaterialIdStandardData(surfaceData.baseColor, surfaceData.specular, surfaceData.metallic, bsdfData.roughness, surfaceData.normalWS, surfaceData.tangentWS, surfaceData.anisotropy, bsdfData);
        bsdfData.materialId = surfaceData.anisotropy > 0.0 ? MATERIALID_LIT_ANISO : bsdfData.materialId;
    }
    else if (bsdfData.materialId == MATERIALID_LIT_SSS)
    {
        FillMaterialIdSSSData(surfaceData.baseColor, surfaceData.subsurfaceProfile, surfaceData.subsurfaceRadius, surfaceData.thickness, bsdfData);
    }
    else if (bsdfData.materialId == MATERIALID_LIT_SPECULAR)
    {
        bsdfData.diffuseColor = surfaceData.baseColor;
        bsdfData.fresnel0 = surfaceData.specularColor;
    }

    ApplyDebugToBSDFData(bsdfData);

    return bsdfData;
}

//-----------------------------------------------------------------------------
// conversion function for deferred
//-----------------------------------------------------------------------------

// Tetra encoding 10:10 + 2 seems equivalent to oct 11:11, as oct is cheaper use that. Let here for future testing in reflective scene for comparison
//#define USE_NORMAL_TETRAHEDRON_ENCODING

// Encode SurfaceData (BSDF parameters) into GBuffer
// Must be in sync with RT declared in HDRenderPipeline.cs ::Rebuild
void EncodeIntoGBuffer( SurfaceData surfaceData,
                        float3 bakeDiffuseLighting,
                        #if SHADEROPTIONS_PACK_GBUFFER_IN_U16
                        out GBufferType0 outGBufferU0,
                        out GBufferType1 outGBufferU1
                        #else
                        out GBufferType0 outGBuffer0,
                        out GBufferType1 outGBuffer1,
                        out GBufferType2 outGBuffer2,
                        out GBufferType3 outGBuffer3
                        #endif
                        )
{
    #if SHADEROPTIONS_PACK_GBUFFER_IN_U16
    float4 outGBuffer0, outGBuffer1, outGBuffer2, outGBuffer3;
    #endif

    // RT0 - 8:8:8:8 sRGB
    outGBuffer0 = float4(surfaceData.baseColor, surfaceData.specularOcclusion);

    // RT1 - 10:10:10:2
    // We store perceptualRoughness instead of roughness because it save a sqrt ALU when decoding
    // (as we want both perceptualRoughness and roughness for the lighting due to Disney Diffuse model)
#ifdef USE_NORMAL_TETRAHEDRON_ENCODING
    // Encode normal on 20bit + 2bit (faceIndex) with tetrahedal compression
    uint faceIndex;
    float2 tetraNormalWS = PackNormalTetraEncode(surfaceData.normalWS, faceIndex);
    // Store faceIndex on two bits with perceptualRoughness
    outGBuffer1 = float4(tetraNormalWS * 0.5 + 0.5, PackFloatInt10bit(PerceptualSmoothnessToPerceptualRoughness(surfaceData.perceptualSmoothness), faceIndex, 4.0), PackMaterialId(surfaceData.materialId));
#else
    // Encode normal on 20bit with oct compression + 2bit of sign
    float2 octNormalWS = PackNormalOctEncode(surfaceData.normalWS);
    // To have more precision encode the sign of xy in a separate uint
    uint octNormalSign = (octNormalWS.x > 0.0 ? 1 : 0) + (octNormalWS.y > 0.0 ? 2 : 0);
    // Store octNormalSign on two bits with perceptualRoughness
    outGBuffer1 = float4(abs(octNormalWS), PackFloatInt10bit(PerceptualSmoothnessToPerceptualRoughness(surfaceData.perceptualSmoothness), octNormalSign, 4.0), PackMaterialId(surfaceData.materialId));
#endif

    // RT2 - 8:8:8:8
    if (surfaceData.materialId == MATERIALID_LIT_STANDARD)
    {
        // Encode tangent on 16bit with oct compression
        float2 octTangentWS = PackNormalOctEncode(surfaceData.tangentWS);
        // TODO: store metal and specular together, specular should be an enum (fixed value)
        outGBuffer2 = float4(octTangentWS * 0.5 + 0.5, surfaceData.anisotropy, surfaceData.metallic);
    }
    else if (surfaceData.materialId == MATERIALID_LIT_SSS)
    {
        outGBuffer2 = float4(surfaceData.subsurfaceRadius, surfaceData.thickness, 0.0, surfaceData.subsurfaceProfile * rcp(SSS_N_PROFILES - 1));
    }
    else if (surfaceData.materialId == MATERIALID_LIT_SPECULAR)
    {
        outGBuffer2 = float4(surfaceData.specularColor, 0.0);
    }

    // Lighting
    outGBuffer3 = float4(bakeDiffuseLighting, 0.0);

    #if SHADEROPTIONS_PACK_GBUFFER_IN_U16
    // Now pack all buffer into 2 uint buffer

    // We don't have hardware sRGB to store base color in case we pack int u16, so rather than perform full sRGB encoding just use cheap gamma20
    // TODO: test alternative like FastLinearToSRGB to better match unpacked gbuffer
    outGBuffer0.xyz = LinearToGamma20(outGBuffer0.xyz);

    uint packedGBuffer1 = PackR10G10B10A2(outGBuffer1);

    outGBufferU0 = uint4(   PackFloatToUInt(outGBuffer0.x, 8, 0)  | PackFloatToUInt(outGBuffer0.y, 8, 8),
                            PackFloatToUInt(outGBuffer0.z, 8, 0)  | PackFloatToUInt(outGBuffer0.w, 8, 8),
                            (packedGBuffer1 & 0x0000FFFF),
                            (packedGBuffer1 & 0xFFFF0000) >> 16);

    uint packedGBuffer3 = PackR11G11B10f(outGBuffer3.xyz);

    outGBufferU1 = uint4(   PackFloatToUInt(outGBuffer2.x, 8, 0)  | PackFloatToUInt(outGBuffer2.y, 8, 8),
                            PackFloatToUInt(outGBuffer2.z, 8, 0)  | PackFloatToUInt(outGBuffer2.w, 8, 8),
                            (packedGBuffer3 & 0x0000FFFF),
                            (packedGBuffer3 & 0xFFFF0000) >> 16);
    #endif
}

float4 DecodeGBuffer0(GBufferType0 encodedGBuffer0)
{
    float4 decodedGBuffer0;
#if SHADEROPTIONS_PACK_GBUFFER_IN_U16
    decodedGBuffer0.x = UnpackUIntToFloat(encodedGBuffer0.x, 8, 0);
    decodedGBuffer0.y = UnpackUIntToFloat(encodedGBuffer0.x, 8, 8);
    decodedGBuffer0.z = UnpackUIntToFloat(encodedGBuffer0.y, 8, 0);
    decodedGBuffer0.w = UnpackUIntToFloat(encodedGBuffer0.y, 8, 8);

    decodedGBuffer0.xyz = Gamma20ToLinear(encodedGBuffer0.xyz);
#else
    decodedGBuffer0 = encodedGBuffer0;
#endif
    return decodedGBuffer0;
}

void DecodeFromGBuffer(
#if SHADEROPTIONS_PACK_GBUFFER_IN_U16
    GBufferType0 inGBufferU0,
    GBufferType1 inGBufferU1,
#else
    GBufferType0 inGBuffer0,
    GBufferType1 inGBuffer1,
    GBufferType2 inGBuffer2,
    GBufferType3 inGBuffer3,
#endif
    uint featureFlags,
    out BSDFData bsdfData,
    out float3 bakeDiffuseLighting)
{
    ZERO_INITIALIZE(BSDFData, bsdfData);

#if SHADEROPTIONS_PACK_GBUFFER_IN_U16
    float4 inGBuffer0, inGBuffer1, inGBuffer2, inGBuffer3;

    inGBuffer0 = DecodeGBuffer0(inGBufferU0);

    uint packedGBuffer1 = inGBufferU0.z | inGBufferU0.w << 16;
    inGBuffer1 = UnpackR10G10B10A2(packedGBuffer1);

    inGBuffer2.x = UnpackUIntToFloat(inGBufferU1.x, 8, 0);
    inGBuffer2.y = UnpackUIntToFloat(inGBufferU1.x, 8, 8);
    inGBuffer2.z = UnpackUIntToFloat(inGBufferU1.y, 8, 0);
    inGBuffer2.w = UnpackUIntToFloat(inGBufferU1.y, 8, 8);

    uint packedGBuffer3 = inGBufferU1.z | inGBufferU1.w << 16;
    inGBuffer3.xyz = UnpackR11G11B10f(packedGBuffer1);
    inGBuffer3.w = 0.0;
#endif

    float3 baseColor = inGBuffer0.rgb;
    bsdfData.specularOcclusion = inGBuffer0.a;

#ifdef USE_NORMAL_TETRAHEDRON_ENCODING
    uint faceIndex;
    UnpackFloatInt10bit(inGBuffer1.b, 4.0, bsdfData.perceptualRoughness, faceIndex);
    bsdfData.normalWS = UnpackNormalTetraEncode(inGBuffer1.xy * 2.0 - 1.0, faceIndex);
#else
    uint octNormalSign;
    UnpackFloatInt10bit(inGBuffer1.b, 4.0, bsdfData.perceptualRoughness, octNormalSign);
    inGBuffer1.r *= (octNormalSign & 1) ? 1.0 : -1.0;
    inGBuffer1.g *= (octNormalSign & 2) ? 1.0 : -1.0;
    bsdfData.normalWS = UnpackNormalOctEncode(float2(inGBuffer1.r, inGBuffer1.g));
#endif

    bsdfData.roughness = PerceptualRoughnessToRoughness(bsdfData.perceptualRoughness);

    int supportsStandard = (featureFlags & (FEATURE_FLAG_MATERIAL_LIT_STANDARD | FEATURE_FLAG_MATERIAL_LIT_ANISO)) != 0;
    int supportsSSS = (featureFlags & (FEATURE_FLAG_MATERIAL_LIT_SSS)) != 0;
    int supportsSpecular = (featureFlags & (FEATURE_FLAG_MATERIAL_LIT_SPECULAR)) != 0;

    if(supportsStandard + supportsSSS + supportsSpecular > 1)
    {
        bsdfData.materialId = UnpackMaterialId(inGBuffer1.a);   // only fetch materialid if it is not statically known from feature flags
    }
    else
    {
        // materialid is statically known. this allows the compiler to eliminate a lot of code.
        if(supportsStandard) bsdfData.materialId = MATERIALID_LIT_STANDARD;
        else if(supportsSSS) bsdfData.materialId = MATERIALID_LIT_SSS;
        else bsdfData.materialId = MATERIALID_LIT_SPECULAR;
    }

    if (supportsStandard && bsdfData.materialId == MATERIALID_LIT_STANDARD)
    {
        float metallic = inGBuffer2.a;
        float specular = 0.04; // TODO extract spec
        float anisotropy = inGBuffer2.b;
        float3 tangentWS = UnpackNormalOctEncode(float2(inGBuffer2.rg * 2.0 - 1.0));
        FillMaterialIdStandardData(baseColor, specular, metallic, bsdfData.roughness, bsdfData.normalWS, tangentWS, anisotropy, bsdfData);

        if ((featureFlags & FEATURE_FLAG_MATERIAL_LIT_ANISO) && (featureFlags & FEATURE_FLAG_MATERIAL_LIT_STANDARD) == 0 || anisotropy > 0)
        {
            bsdfData.materialId = MATERIALID_LIT_ANISO;
        }
    }
    else if (supportsSSS && bsdfData.materialId == MATERIALID_LIT_SSS)
    {
        int subsurfaceProfile = (SSS_N_PROFILES - 0.9) * inGBuffer2.a;
        float subsurfaceRadius = inGBuffer2.r;
        float thickness = inGBuffer2.g;
        FillMaterialIdSSSData(baseColor, subsurfaceProfile, subsurfaceRadius, thickness, bsdfData);
    }
    else if (supportsSpecular && bsdfData.materialId == MATERIALID_LIT_SPECULAR)
    {
        bsdfData.diffuseColor = baseColor;
        bsdfData.fresnel0 = inGBuffer2.rgb;
    }

    bakeDiffuseLighting = inGBuffer3.rgb;

    ApplyDebugToBSDFData(bsdfData);
}

uint MaterialFeatureFlagsFromGBuffer(
#if SHADEROPTIONS_PACK_GBUFFER_IN_U16
    GBufferType0 inGBufferU0,
    GBufferType1 inGBufferU1
#else
    GBufferType0 inGBuffer0,
    GBufferType1 inGBuffer1,
    GBufferType2 inGBuffer2,
    GBufferType3 inGBuffer3
#endif
)
{
#if SHADEROPTIONS_PACK_GBUFFER_IN_U16
    float4 inGBuffer0, inGBuffer1, inGBuffer2, inGBuffer3;

    inGBuffer0 = DecodeGBuffer0(inGBufferU0);

    uint packedGBuffer1 = inGBufferU0.z | inGBufferU0.w << 16;
    inGBuffer1 = UnpackR10G10B10A2(packedGBuffer1);

    inGBuffer2.x = UnpackUIntToFloat(inGBufferU1.x, 8, 0);
    inGBuffer2.y = UnpackUIntToFloat(inGBufferU1.x, 8, 8);
    inGBuffer2.z = UnpackUIntToFloat(inGBufferU1.y, 8, 0);
    inGBuffer2.w = UnpackUIntToFloat(inGBufferU1.y, 8, 8);

    uint packedGBuffer3 = inGBufferU1.z | inGBufferU1.w << 16;
    inGBuffer3.xyz = UnpackR11G11B10f(packedGBuffer1);
    inGBuffer3.w = 0.0;
#endif

    int materialId = UnpackMaterialId(inGBuffer1.a);
    float anisotropy = inGBuffer2.b;

    uint featureFlags = 0;
    if (materialId == MATERIALID_LIT_STANDARD)
    {
        featureFlags |= (anisotropy > 0) ? FEATURE_FLAG_MATERIAL_LIT_ANISO : FEATURE_FLAG_MATERIAL_LIT_STANDARD;
    }
    else if (materialId == MATERIALID_LIT_SSS)
    {
        featureFlags |= FEATURE_FLAG_MATERIAL_LIT_SSS;
    }
    else if (materialId == MATERIALID_LIT_SPECULAR)
    {
        featureFlags |= FEATURE_FLAG_MATERIAL_LIT_SPECULAR;
    }
    return featureFlags;
}


//-----------------------------------------------------------------------------
// Debug method (use to display values)
//-----------------------------------------------------------------------------

void GetSurfaceDataDebug(uint paramId, SurfaceData surfaceData, inout float3 result, inout bool needLinearToSRGB)
{
    GetGeneratedSurfaceDataDebug(paramId, surfaceData, result, needLinearToSRGB);

    switch (paramId)
    {
        // TODO: Remap here!
        case DEBUGVIEW_LIT_SURFACEDATA_SPECULAR:
            result = surfaceData.specular.xxx;
            break;
    }
}

void GetBSDFDataDebug(uint paramId, BSDFData bsdfData, inout float3 result, inout bool needLinearToSRGB)
{
    GetGeneratedBSDFDataDebug(paramId, bsdfData, result, needLinearToSRGB);
}

//-----------------------------------------------------------------------------
// PreLightData
//-----------------------------------------------------------------------------

// Precomputed lighting data to send to the various lighting functions
struct PreLightData
{
    // General
    float NdotV;   // Between 0.0001 and 1
    float unNdotV; // Between -1 and 1


    // GGX iso
    float ggxLambdaV;

    // GGX Aniso
    float TdotV;
    float BdotV;
    float anisoGGXLambdaV;

    // IBL
    float3 iblDirWS;    // Dominant specular direction, used for IBL in EvaluateBSDF_Env()
    float  iblMipLevel;

    float3 specularFGD; // Store preconvole BRDF for both specular and diffuse
    float diffuseFGD;

    // area light
    float3x3 ltcXformGGX;                // TODO: make sure the compiler not wasting VGPRs on constants
    float3x3 ltcXformDisneyDiffuse;      // TODO: make sure the compiler not wasting VGPRs on constants
    float    ltcGGXFresnelMagnitudeDiff; // The difference of magnitudes of GGX and Fresnel
    float    ltcGGXFresnelMagnitude;
    float    ltcDisneyDiffuseMagnitude;
};

PreLightData GetPreLightData(float3 V, PositionInputs posInput, BSDFData bsdfData)
{
    PreLightData preLightData;

    // General
    float3 iblNormalWS = bsdfData.normalWS;

    preLightData.unNdotV = dot(bsdfData.normalWS, V);
    // GetShiftedNdotV return a positive NdotV
    // In case a material use negative normal for double sided lighting like Speedtree  they need to do a new calculation
    preLightData.NdotV = GetShiftedNdotV(iblNormalWS, V, preLightData.unNdotV);

    // GGX iso
    preLightData.ggxLambdaV = GetSmithJointGGXLambdaV(preLightData.NdotV, bsdfData.roughness);

    // GGX aniso
    if (bsdfData.materialId == MATERIALID_LIT_ANISO)
    {
        preLightData.TdotV = dot(bsdfData.tangentWS, V);
        preLightData.BdotV = dot(bsdfData.bitangentWS, V);
        preLightData.anisoGGXLambdaV = GetSmithJointGGXAnisoLambdaV(preLightData.TdotV, preLightData.BdotV, preLightData.NdotV, bsdfData.roughnessT, bsdfData.roughnessB);
        // Tangent = highlight stretch (anisotropy) direction. Bitangent = grain (brush) direction.
        iblNormalWS = GetAnisotropicModifiedNormal(bsdfData.bitangentWS, iblNormalWS, V, bsdfData.anisotropy);

        // NOTE: If we follow the theory we should use the modified normal for the different calculation implying a normal (like NdotV) and use iblNormalWS
        // into function like GetSpecularDominantDir(). However modified normal is just a hack. The goal is just to stretch a cubemap, no accuracy here.
        // With this in mind and for performance reasons we chose to only use modified normal to calculate R.
    }

    // IBL
    GetPreIntegratedFGD(preLightData.NdotV, bsdfData.perceptualRoughness, bsdfData.fresnel0, preLightData.specularFGD, preLightData.diffuseFGD);

    // We need to take into account the modified normal for faking anisotropic here.
    float3 iblR = reflect(-V, iblNormalWS);
    preLightData.iblDirWS = GetSpecularDominantDir(bsdfData.normalWS, iblR, bsdfData.roughness, preLightData.NdotV);
    preLightData.iblMipLevel = PerceptualRoughnessToMipmapLevel(bsdfData.perceptualRoughness);

    // Area light
    // UVs for sampling the LUTs
    float theta = FastACos(preLightData.NdotV);
    float2 uv = LTC_LUT_OFFSET + LTC_LUT_SCALE * float2(bsdfData.perceptualRoughness, theta * INV_HALF_PI);

    // Get the inverse LTC matrix for GGX
    // Note we load the matrix transpose (avoid to have to transpose it in shader)
    preLightData.ltcXformGGX      = 0.0;
    preLightData.ltcXformGGX._m22 = 1.0;
    preLightData.ltcXformGGX._m00_m02_m11_m20 = SAMPLE_TEXTURE2D_ARRAY_LOD(_LtcData, ltc_linear_clamp_sampler, uv, LTC_GGX_MATRIX_INDEX, 0);

    // Get the inverse LTC matrix for Disney Diffuse
    // Note we load the matrix transpose (avoid to have to transpose it in shader)
    preLightData.ltcXformDisneyDiffuse      = 0.0;
    preLightData.ltcXformDisneyDiffuse._m22 = 1.0;
    preLightData.ltcXformDisneyDiffuse._m00_m02_m11_m20 = SAMPLE_TEXTURE2D_ARRAY_LOD(_LtcData, ltc_linear_clamp_sampler, uv, LTC_DISNEY_DIFFUSE_MATRIX_INDEX, 0);

    float3 ltcMagnitude = SAMPLE_TEXTURE2D_ARRAY_LOD(_LtcData, ltc_linear_clamp_sampler, uv, LTC_MULTI_GGX_FRESNEL_DISNEY_DIFFUSE_INDEX, 0).rgb;
    preLightData.ltcGGXFresnelMagnitudeDiff = ltcMagnitude.r;
    preLightData.ltcGGXFresnelMagnitude     = ltcMagnitude.g;
    preLightData.ltcDisneyDiffuseMagnitude  = ltcMagnitude.b;

    return preLightData;
}

//-----------------------------------------------------------------------------
// bake lighting function
//-----------------------------------------------------------------------------

// GetBakedDiffuseLigthing function compute the bake lighting + emissive color to be store in emissive buffer (Deferred case)
// In forward it must be add to the final contribution.
// This function require the 3 structure surfaceData, builtinData, bsdfData because it may require both the engine side data, and data that will not be store inside the gbuffer.
float3 GetBakedDiffuseLigthing(SurfaceData surfaceData, BuiltinData builtinData, BSDFData bsdfData, PreLightData preLightData)
{
    // Premultiply bake diffuse lighting information with DisneyDiffuse pre-integration
    return builtinData.bakeDiffuseLighting * preLightData.diffuseFGD * surfaceData.ambientOcclusion * bsdfData.diffuseColor + builtinData.emissiveColor;
}

//-----------------------------------------------------------------------------
// light transport functions
//-----------------------------------------------------------------------------

LightTransportData GetLightTransportData(SurfaceData surfaceData, BuiltinData builtinData, BSDFData bsdfData)
{
    LightTransportData lightTransportData;

    // diffuseColor for lightmapping should basically be diffuse color.
    // But rough metals (black diffuse) still scatter quite a lot of light around, so
    // we want to take some of that into account too.

    lightTransportData.diffuseColor = bsdfData.diffuseColor + bsdfData.fresnel0 * bsdfData.roughness * 0.5 * surfaceData.metallic;
    lightTransportData.emissiveColor = builtinData.emissiveColor;

    return lightTransportData;
}

//-----------------------------------------------------------------------------
// LightLoop related function (Only include if required)
// HAS_LIGHTLOOP is define in Lighting.hlsl
//-----------------------------------------------------------------------------

#ifdef HAS_LIGHTLOOP

//-----------------------------------------------------------------------------
// BSDF share between directional light, punctual light and area light (reference)
//-----------------------------------------------------------------------------

void BSDF(  float3 V, float3 L, float3 positionWS, PreLightData preLightData, BSDFData bsdfData,
            out float3 diffuseLighting,
            out float3 specularLighting)
{
    float NdotL    = saturate(dot(bsdfData.normalWS, L));
    float NdotV    = preLightData.unNdotV;             // This value must not be clamped
    float LdotV    = dot(L, V);
    // GCN Optimization: reference PBR Diffuse Lighting for GGX + Smith Microsurfaces
    float invLenLV = rsqrt(abs(2.0 * LdotV + 2.0));    // invLenLV = rcp(length(L + V))
    float NdotH    = saturate((NdotL + NdotV) * invLenLV);
    float LdotH    = saturate(invLenLV * LdotV + invLenLV);

    float3 F = F_Schlick(bsdfData.fresnel0, LdotH);

    float Vis;
    float D;
    // TODO: this way of handling aniso may not be efficient, or maybe with material classification, need to check perf here
    // Maybe always using aniso maybe a win ?
    if (bsdfData.materialId == MATERIALID_LIT_ANISO)
    {
        float3 H = (L + V) * invLenLV;
        // For anisotropy we must not saturate these values
        float TdotH = dot(bsdfData.tangentWS, H);
        float TdotL = dot(bsdfData.tangentWS, L);
        float BdotH = dot(bsdfData.bitangentWS, H);
        float BdotL = dot(bsdfData.bitangentWS, L);

        bsdfData.roughnessT = ClampRoughnessForAnalyticalLights(bsdfData.roughnessT);
        bsdfData.roughnessB = ClampRoughnessForAnalyticalLights(bsdfData.roughnessB);

        #ifdef LIT_USE_BSDF_PRE_LAMBDAV
        Vis = V_SmithJointGGXAnisoLambdaV(preLightData.TdotV, preLightData.BdotV, preLightData.NdotV, TdotL, BdotL, NdotL,
                                          bsdfData.roughnessT, bsdfData.roughnessB, preLightData.anisoGGXLambdaV);
        #else
        // TODO: Do comparison between this correct version and the one from isotropic and see if there is any visual difference
        Vis = V_SmithJointGGXAniso(preLightData.TdotV, preLightData.BdotV, preLightData.NdotV, TdotL, BdotL, NdotL,
                                   bsdfData.roughnessT, bsdfData.roughnessB);
        #endif

        D = D_GGXAniso(TdotH, BdotH, NdotH, bsdfData.roughnessT, bsdfData.roughnessB);
    }
    else
    {
        bsdfData.roughness = ClampRoughnessForAnalyticalLights(bsdfData.roughness);

        #ifdef LIT_USE_BSDF_PRE_LAMBDAV
        Vis = V_SmithJointGGX(NdotL, preLightData.NdotV, bsdfData.roughness, preLightData.ggxLambdaV);
        #else
        Vis = V_SmithJointGGX(NdotL, preLightData.NdotV, bsdfData.roughness);
        #endif
        D = D_GGX(NdotH, bsdfData.roughness);
    }
    specularLighting = F * (Vis * D);

#ifdef LIT_DIFFUSE_LAMBERT_BRDF
    float  diffuseTerm = Lambert();
#elif LIT_DIFFUSE_GGX_BRDF
    float3 diffuseTerm = DiffuseGGX(bsdfData.diffuseColor, preLightData.NdotV, NdotL, NdotH, LdotV, bsdfData.perceptualRoughness);
#else
    float  diffuseTerm = DisneyDiffuse(preLightData.NdotV, NdotL, LdotH, bsdfData.perceptualRoughness);
#endif

    diffuseLighting = bsdfData.diffuseColor * diffuseTerm;
}

//-----------------------------------------------------------------------------
// EvaluateBSDF_Directional
//-----------------------------------------------------------------------------

void EvaluateBSDF_Directional(  LightLoopContext lightLoopContext,
                                float3 V, PositionInputs posInput, PreLightData preLightData, DirectionalLightData lightData, BSDFData bsdfData,
                                out float3 diffuseLighting,
                                out float3 specularLighting)
{
    float3 positionWS = posInput.positionWS;

    float3 L = -lightData.forward; // Lights are pointing backward in Unity
    float illuminance = saturate(dot(bsdfData.normalWS, L));

    diffuseLighting  = float3(0.0, 0.0, 0.0);
    specularLighting = float3(0.0, 0.0, 0.0);
    float4 cookie    = float4(1.0, 1.0, 1.0, 1.0);
    float  shadow    = 1;

    [branch] if (lightData.shadowIndex >= 0)
    {
#ifdef SHADOWS_USE_SHADOWCTXT
        shadow = GetDirectionalShadowAttenuation(lightLoopContext.shadowContext, positionWS, lightData.shadowIndex, L, posInput.unPositionSS);
#else
        shadow = GetDirectionalShadowAttenuation(lightLoopContext, positionWS, lightData.shadowIndex, L, posInput.unPositionSS);
#endif
        illuminance *= shadow;
    }

    [branch] if (lightData.cookieIndex >= 0)
    {
        float3 lightToSurface = positionWS - lightData.positionWS;

        // Project 'lightToSurface' onto the light's axes.
        float2 coord = float2(dot(lightToSurface, lightData.right), dot(lightToSurface, lightData.up));

        // Compute the NDC coordinates (in [-1, 1]^2).
        coord.x *= lightData.invScaleX;
        coord.y *= lightData.invScaleY;

        if (lightData.tileCookie || (abs(coord.x) <= 1 && abs(coord.y) <= 1))
        {
            // Remap the texture coordinates from [-1, 1]^2 to [0, 1]^2.
            coord = coord * 0.5 + 0.5;

            // Tile the texture if the 'repeat' wrap mode is enabled.
            if (lightData.tileCookie) { coord = frac(coord); }

            cookie = SampleCookie2D(lightLoopContext, coord, lightData.cookieIndex);
        }
        else
        {
            cookie = float4(0, 0, 0, 0);
        }

        illuminance *= cookie.a;
    }

    [branch] if (illuminance > 0.0)
    {
        BSDF(V, L, positionWS, preLightData, bsdfData, diffuseLighting, specularLighting);

        diffuseLighting  *= (cookie.rgb * lightData.color) * (illuminance * lightData.diffuseScale);
        specularLighting *= (cookie.rgb * lightData.color) * (illuminance * lightData.specularScale);
    }

    [branch] if (bsdfData.enableTransmission)
    {
        // Reverse the normal + do some wrap lighting to have a nicer transition between regular lighting and transmittance
        // Ref: Steve McAuley - Energy-Conserving Wrapped Diffuse
        const float w = 0.15;
        float illuminance = saturate((dot(-bsdfData.normalWS, L) + w) / ((1.0 + w) * (1.0 + w)));

        // For low thickness, we can reuse the shadowing status for the back of the object.
        shadow       = (bsdfData.thickness <= SSS_LOW_THICKNESS) ? shadow : 1;
        illuminance *= shadow * cookie.a;

        // The difference between the Disney Diffuse and the Lambertian BRDF for transmission is negligible.
        float3 backLight = (cookie.rgb * lightData.color) * (illuminance * lightData.diffuseScale * Lambert());
        // TODO: multiplication by 'diffuseColor' and 'transmittance' is the same for each light.
        float3 transmittedLight = backLight * bsdfData.diffuseColor * bsdfData.transmittance;

        // We use diffuse lighting for accumulation since it is going to be blurred during the SSS pass.
        diffuseLighting += transmittedLight;
    }
}

//-----------------------------------------------------------------------------
// EvaluateBSDF_Punctual
//-----------------------------------------------------------------------------

void EvaluateBSDF_Punctual( LightLoopContext lightLoopContext,
                            float3 V, PositionInputs posInput, PreLightData preLightData, LightData lightData, BSDFData bsdfData,
                            out float3 diffuseLighting,
                            out float3 specularLighting)
{
    float3 positionWS = posInput.positionWS;

    // All punctual light type in the same formula, attenuation is neutral depends on light type.
    // light.positionWS is the normalize light direction in case of directional light and invSqrAttenuationRadius is 0
    // mean dot(unL, unL) = 1 and mean GetDistanceAttenuation() will return 1
    // For point light and directional GetAngleAttenuation() return 1

    float3 unL = lightData.positionWS - positionWS;
    float3 L = normalize(unL);

    float attenuation = GetDistanceAttenuation(unL, lightData.invSqrAttenuationRadius);
    // Reminder: lights are ortiented backward (-Z)
    attenuation *= GetAngleAttenuation(L, -lightData.forward, lightData.angleScale, lightData.angleOffset);
    float illuminance = saturate(dot(bsdfData.normalWS, L)) * attenuation;

    diffuseLighting  = float3(0.0, 0.0, 0.0);
    specularLighting = float3(0.0, 0.0, 0.0);
    float4 cookie    = float4(1.0, 1.0, 1.0, 1.0);
    float  shadow    = 1;

    // TODO: measure impact of having all these dynamic branch here and the gain (or not) of testing illuminace > 0

    //[branch] if (lightData.IESIndex >= 0 && illuminance > 0.0)
    //{
    //    float3x3 lightToWorld = float3x3(lightData.right, lightData.up, lightData.forward);
    //    float2 sphericalCoord = GetIESTextureCoordinate(lightToWorld, L);
    //    illuminance *= SampleIES(lightLoopContext, lightData.IESIndex, sphericalCoord, 0).r;
    //}

    [branch] if (lightData.shadowIndex >= 0)
    {
        float3 offset = float3(0.0, 0.0, 0.0); // GetShadowPosOffset(nDotL, normal);
#ifdef SHADOWS_USE_SHADOWCTXT
        shadow = GetPunctualShadowAttenuation(lightLoopContext.shadowContext, positionWS + offset, lightData.shadowIndex, L, posInput.unPositionSS);
#else
        shadow = GetPunctualShadowAttenuation(lightLoopContext, lightData.lightType, positionWS + offset, lightData.shadowIndex, L, posInput.unPositionSS);
#endif
        shadow = lerp(1.0, shadow, lightData.shadowDimmer);

        illuminance *= shadow;
    }

    [branch] if (lightData.cookieIndex >= 0)
    {
        float3x3 lightToWorld = float3x3(lightData.right, lightData.up, lightData.forward);

        // Rotate 'L' into the light space.
        // We perform the negation because lights are oriented backwards (-Z).
        float3 coord = mul(-L, transpose(lightToWorld));

        [branch] if (lightData.lightType == GPULIGHTTYPE_SPOT)
        {
            // Perform the perspective projection of the hemisphere onto the disk.
            coord.xy /= coord.z;

            // Rescale the projective coordinates to fit into the [-1, 1]^2 range.
            float cotOuterHalfAngle = lightData.size.x;
            coord.xy *= cotOuterHalfAngle;

            // Remap the texture coordinates from [-1, 1]^2 to [0, 1]^2.
            coord.xy = coord.xy * 0.5 + 0.5;

            cookie = SampleCookie2D(lightLoopContext, coord.xy, lightData.cookieIndex);
        }
        else // GPULIGHTTYPE_POINT
        {
            cookie = SampleCookieCube(lightLoopContext, coord, lightData.cookieIndex);
        }

        illuminance *= cookie.a;
    }

    [branch] if (illuminance > 0.0)
    {
        BSDF(V, L, positionWS, preLightData, bsdfData, diffuseLighting, specularLighting);

        diffuseLighting  *= (cookie.rgb * lightData.color) * (illuminance * lightData.diffuseScale);
        specularLighting *= (cookie.rgb * lightData.color) * (illuminance * lightData.specularScale);
    }

    [branch] if (bsdfData.enableTransmission)
    {
        // Reverse the normal + do some wrap lighting to have a nicer transition between regular lighting and transmittance
        // Ref: Steve McAuley - Energy-Conserving Wrapped Diffuse
        const float w = 0.15;
        float illuminance = saturate((dot(-bsdfData.normalWS, L) + w) / ((1.0 + w) * (1.0 + w)));
        illuminance *= attenuation;

        // For low thickness, we can reuse the shadowing status for the back of the object.
        shadow       = (bsdfData.thickness <= SSS_LOW_THICKNESS) ? shadow : 1;
        illuminance *= shadow * cookie.a;

        // The difference between the Disney Diffuse and the Lambertian BRDF for transmission is negligible.
        float3 backLight = (cookie.rgb * lightData.color) * (illuminance * lightData.diffuseScale * Lambert());
        // TODO: multiplication by 'diffuseColor' and 'transmittance' is the same for each light.
        float3 transmittedLight = backLight * bsdfData.diffuseColor * bsdfData.transmittance;

        // We use diffuse lighting for accumulation since it is going to be blurred during the SSS pass.
        diffuseLighting += transmittedLight;
    }
}

//-----------------------------------------------------------------------------
// EvaluateBSDF_Projector
//-----------------------------------------------------------------------------

void EvaluateBSDF_Projector(LightLoopContext lightLoopContext,
                            float3 V, PositionInputs posInput, PreLightData preLightData, LightData lightData, BSDFData bsdfData,
                            out float3 diffuseLighting,
                            out float3 specularLighting)
{
    float3 positionWS = posInput.positionWS;

    // Translate and rotate 'positionWS' into the light space.
    float3 positionLS = mul(positionWS - lightData.positionWS,
                            transpose(float3x3(lightData.right, lightData.up, lightData.forward)));

    if (lightData.lightType == GPULIGHTTYPE_PROJECTOR_PYRAMID)
    {
        // Perform perspective division.
        positionLS *= rcp(positionLS.z);
    }
    else
    {
        // For orthographic projection, the Z coordinate plays no role.
        positionLS.z = 0;
    }

    // Compute the NDC position (in [-1, 1]^2). TODO: precompute the inverse?
    float2 positionNDC = positionLS.xy * rcp(0.5 * lightData.size);

    // Perform clipping.
    float clipFactor = ((positionLS.z >= 0) && (abs(positionNDC.x) <= 1 && abs(positionNDC.y) <= 1)) ? 1 : 0;

    float3 L = -lightData.forward; // Lights are pointing backward in Unity
    float illuminance = saturate(dot(bsdfData.normalWS, L) * clipFactor);

    diffuseLighting  = float3(0.0, 0.0, 0.0);
    specularLighting = float3(0.0, 0.0, 0.0);
    float4 cookie    = float4(1.0, 1.0, 1.0, 1.0);
    float shadow = 1;

    [branch] if (lightData.shadowIndex >= 0)
    {
#ifdef SHADOWS_USE_SHADOWCTXT
        shadow = GetDirectionalShadowAttenuation(lightLoopContext.shadowContext, positionWS, lightData.shadowIndex, L, posInput.unPositionSS);
#else
        shadow = GetDirectionalShadowAttenuation(lightLoopContext, positionWS, lightData.shadowIndex, L, posInput.unPositionSS);
#endif
        illuminance *= shadow;
    }

    [branch] if (lightData.cookieIndex >= 0)
    {
        // Compute the texture coordinates in [0, 1]^2.
        float2 coord = positionNDC * 0.5 + 0.5;

        cookie = SampleCookie2D(lightLoopContext, coord, lightData.cookieIndex);

        illuminance *= cookie.a;
    }

    [branch] if (illuminance > 0.0)
    {
        BSDF(V, L, positionWS, preLightData, bsdfData, diffuseLighting, specularLighting);

        diffuseLighting  *= (cookie.rgb * lightData.color) * (illuminance * lightData.diffuseScale);
        specularLighting *= (cookie.rgb * lightData.color) * (illuminance * lightData.specularScale);
    }

    [branch] if (bsdfData.enableTransmission)
    {
        // Reverse the normal + do some wrap lighting to have a nicer transition between regular lighting and transmittance
        // Ref: Steve McAuley - Energy-Conserving Wrapped Diffuse
        const float w = 0.15;
        float illuminance = saturate((dot(-bsdfData.normalWS, L) + w) / ((1.0 + w) * (1.0 + w)));
        illuminance *= clipFactor;

        // For low thickness, we can reuse the shadowing status for the back of the object.
        shadow       = (bsdfData.thickness <= SSS_LOW_THICKNESS) ? shadow : 1;
        illuminance *= shadow * cookie.a;

        // The difference between the Disney Diffuse and the Lambertian BRDF for transmission is negligible.
        float3 backLight = (cookie.rgb * lightData.color) * (illuminance * lightData.diffuseScale * Lambert());
        // TODO: multiplication by 'diffuseColor' and 'transmittance' is the same for each light.
        float3 transmittedLight = backLight * bsdfData.diffuseColor * bsdfData.transmittance;

        // We use diffuse lighting for accumulation since it is going to be blurred during the SSS pass.
        diffuseLighting += transmittedLight;
    }
}

//-----------------------------------------------------------------------------
// EvaluateBSDF_Line - Reference
//-----------------------------------------------------------------------------

void IntegrateBSDF_LineRef(float3 V, float3 positionWS,
                           PreLightData preLightData, LightData lightData, BSDFData bsdfData,
                           out float3 diffuseLighting, out float3 specularLighting,
                           int sampleCount = 128)
{
    diffuseLighting  = float3(0.0, 0.0, 0.0);
    specularLighting = float3(0.0, 0.0, 0.0);

    const float  len = lightData.size.x;
    const float3 T   = lightData.right;
    const float3 P1  = lightData.positionWS - T * (0.5 * len);
    const float  dt  = len * rcp(sampleCount);
    const float  off = 0.5 * dt;

    // Uniformly sample the line segment with the Pdf = 1 / len.
    const float invPdf = len;

    for (int i = 0; i < sampleCount; ++i)
    {
        // Place the sample in the middle of the interval.
        float  t     = off + i * dt;
        float3 sPos  = P1 + t * T;
        float3 unL   = sPos - positionWS;
        float  dist2 = dot(unL, unL);
        float3 L     = normalize(unL);
        float  sinLT = length(cross(L, T));
        float  NdotL = saturate(dot(bsdfData.normalWS, L));

        float3 lightDiff, lightSpec;

        BSDF(V, L, positionWS, preLightData, bsdfData, lightDiff, lightSpec);

        // The value of the specular BSDF could be infinite.
        // Summing up infinities leads to NaNs.
        lightSpec = min(lightSpec, FLT_MAX);

        diffuseLighting  += lightDiff * (sinLT / dist2 * NdotL);
        specularLighting += lightSpec * (sinLT / dist2 * NdotL);
    }

    // The factor of 2 is due to the fact: Integral{0, 2 PI}{max(0, cos(x))dx} = 2.
    float normFactor = 2.0 * invPdf * rcp(sampleCount);

    diffuseLighting  *= normFactor * lightData.diffuseScale  * lightData.color;
    specularLighting *= normFactor * lightData.specularScale * lightData.color;
}

//-----------------------------------------------------------------------------
// EvaluateBSDF_Line - Approximation with Linearly Transformed Cosines
//-----------------------------------------------------------------------------

void EvaluateBSDF_Line(LightLoopContext lightLoopContext,
                       float3 V, PositionInputs posInput,
                       PreLightData preLightData, LightData lightData, BSDFData bsdfData,
                       out float3 diffuseLighting, out float3 specularLighting)
{
    float3 positionWS = posInput.positionWS;

#ifdef LIT_DISPLAY_REFERENCE_AREA
    IntegrateBSDF_LineRef(V, positionWS, preLightData, lightData, bsdfData,
                          diffuseLighting, specularLighting);
#else
    diffuseLighting  = float3(0.0, 0.0, 0.0);
    specularLighting = float3(0.0, 0.0, 0.0);

    float  len = lightData.size.x;
    float3 T   = lightData.right;

    float3 unL = lightData.positionWS - positionWS;

    // Pick the major axis of the ellipsoid.
    float3 axis = lightData.right;

    // We define the ellipsoid s.t. r1 = (r + len / 2), r2 = r3 = r.
    // TODO: This could be precomputed.
    float radius         = rsqrt(lightData.invSqrAttenuationRadius);
    float invAspectRatio = radius / (radius + (0.5 * len));

    // Compute the light attenuation.
    float intensity = GetEllipsoidalDistanceAttenuation(unL,  lightData.invSqrAttenuationRadius,
                                                        axis, invAspectRatio);

    // Terminate if the shaded point is too far away.
    if (intensity == 0.0) return;

    lightData.diffuseScale  *= intensity;
    lightData.specularScale *= intensity;

    // TODO: This could be precomputed.
    float3 P1 = lightData.positionWS - T * (0.5 * len);
    float3 P2 = lightData.positionWS + T * (0.5 * len);

    // Translate the endpoints s.t. the shaded point is at the origin of the coordinate system.
    P1 -= positionWS;
    P2 -= positionWS;

    // Construct a view-dependent orthonormal basis around N.
    // TODO: it could be stored in PreLightData, since all LTC lights compute it more than once.
    float3x3 basis;
    basis[0] = normalize(V - bsdfData.normalWS * preLightData.NdotV);
    basis[1] = normalize(cross(bsdfData.normalWS, basis[0]));
    basis[2] = bsdfData.normalWS;

    // Rotate the endpoints into the local coordinate system (left-handed).
    P1 = mul(P1, transpose(basis));
    P2 = mul(P2, transpose(basis));

    // Compute the binormal.
    float3 B = normalize(cross(P1, P2));

    float ltcValue;

    // Evaluate the diffuse part.
    {
    #ifdef LIT_DIFFUSE_LAMBERT_BRDF
        ltcValue = LTCEvaluate(P1, P2, B, k_identity3x3);
    #else
        ltcValue = LTCEvaluate(P1, P2, B, preLightData.ltcXformDisneyDiffuse);
    #endif

    #ifndef LIT_DIFFUSE_LAMBERT_BRDF
        ltcValue *= preLightData.ltcDisneyDiffuseMagnitude;
    #endif

        ltcValue *= lightData.diffuseScale;
        diffuseLighting = bsdfData.diffuseColor * lightData.color * ltcValue;
    }

    // Evaluate the specular part.
    {
        // TODO: the fit seems rather poor. The scaling factor of 0.5 allows us
        // to match the reference for rough metals, but further darkens dielectrics.
        float3 fresnelTerm = bsdfData.fresnel0 * preLightData.ltcGGXFresnelMagnitudeDiff
                           + (float3)preLightData.ltcGGXFresnelMagnitude;

        ltcValue  = LTCEvaluate(P1, P2, B, preLightData.ltcXformGGX);
        ltcValue *= lightData.specularScale;
        specularLighting = fresnelTerm * lightData.color * ltcValue;
    }
#endif // LIT_DISPLAY_REFERENCE_AREA
}

//-----------------------------------------------------------------------------
// EvaluateBSDF_Area - Reference
//-----------------------------------------------------------------------------

void IntegrateBSDF_AreaRef(float3 V, float3 positionWS,
                           PreLightData preLightData, LightData lightData, BSDFData bsdfData,
                           out float3 diffuseLighting, out float3 specularLighting,
                           uint sampleCount = 512)
{
    // Add some jittering on Hammersley2d
    float2 randNum = InitRandom(V.xy * 0.5 + 0.5);

    diffuseLighting = float3(0.0, 0.0, 0.0);
    specularLighting = float3(0.0, 0.0, 0.0);

    for (uint i = 0; i < sampleCount; ++i)
    {
        float3 P = float3(0.0, 0.0, 0.0);   // Sample light point. Random point on the light shape in local space.
        float3 Ns = float3(0.0, 0.0, 0.0);  // Unit surface normal at P
        float lightPdf = 0.0;               // Pdf of the light sample

        float2 u = Hammersley2d(i, sampleCount);
        u = frac(u + randNum);

        // Lights in Unity point backward.
        float4x4 localToWorld = float4x4(float4(lightData.right, 0.0), float4(lightData.up, 0.0), float4(-lightData.forward, 0.0), float4(lightData.positionWS, 1.0));

        switch (lightData.lightType)
        {
            case GPULIGHTTYPE_SPHERE:
                SampleSphere(u, localToWorld, lightData.size.x, lightPdf, P, Ns);
                break;
            case GPULIGHTTYPE_HEMISPHERE:
                SampleHemisphere(u, localToWorld, lightData.size.x, lightPdf, P, Ns);
                break;
            case GPULIGHTTYPE_CYLINDER:
                SampleCylinder(u, localToWorld, lightData.size.x, lightData.size.y, lightPdf, P, Ns);
                break;
            case GPULIGHTTYPE_RECTANGLE:
                SampleRectangle(u, localToWorld, lightData.size.x, lightData.size.y, lightPdf, P, Ns);
                break;
            case GPULIGHTTYPE_DISK:
                SampleDisk(u, localToWorld, lightData.size.x, lightPdf, P, Ns);
                break;
            // case GPULIGHTTYPE_LINE: handled by a separate function.
        }

        // Get distance
        float3 unL = P - positionWS;
        float sqrDist = dot(unL, unL);
        float3 L = normalize(unL);

        // Cosine of the angle between the light direction and the normal of the light's surface.
        float cosLNs = saturate(dot(-L, Ns));

        // We calculate area reference light with the area integral rather than the solid angle one.
        float illuminance = cosLNs * saturate(dot(bsdfData.normalWS, L)) / (sqrDist * lightPdf);

        float3 localDiffuseLighting = float3(0.0, 0.0, 0.0);
        float3 localSpecularLighting = float3(0.0, 0.0, 0.0);

        if (illuminance > 0.0)
        {
            BSDF(V, L, positionWS, preLightData, bsdfData, localDiffuseLighting, localSpecularLighting);
            localDiffuseLighting *= lightData.color * illuminance * lightData.diffuseScale;
            localSpecularLighting *= lightData.color * illuminance * lightData.specularScale;
        }

        diffuseLighting += localDiffuseLighting;
        specularLighting += localSpecularLighting;
    }

    diffuseLighting /= float(sampleCount);
    specularLighting /= float(sampleCount);
}

//-----------------------------------------------------------------------------
// EvaluateBSDF_Area - Approximation with Linearly Transformed Cosines
//-----------------------------------------------------------------------------

// #define ELLIPSOIDAL_ATTENUATION

void EvaluateBSDF_Area(LightLoopContext lightLoopContext,
                       float3 V, PositionInputs posInput,
                       PreLightData preLightData, LightData lightData, BSDFData bsdfData,
                       out float3 diffuseLighting, out float3 specularLighting)
{
    float3 positionWS = posInput.positionWS;

#ifdef LIT_DISPLAY_REFERENCE_AREA
    IntegrateBSDF_AreaRef(V, positionWS, preLightData, lightData, bsdfData,
                          diffuseLighting, specularLighting);
#else
    diffuseLighting  = float3(0.0, 0.0, 0.0);
    specularLighting = float3(0.0, 0.0, 0.0);

    float3 unL = lightData.positionWS - positionWS;

    [branch]
    if (dot(lightData.forward, unL) >= 0.0001)
    {
        // The light is back-facing.
        return;
    }

    // Rotate the light direction into the light space.
    float3x3 lightToWorld = float3x3(lightData.right, lightData.up, -lightData.forward);
    unL = mul(unL, transpose(lightToWorld));

    // TODO: This could be precomputed.
    float halfWidth  = lightData.size.x * 0.5;
    float halfHeight = lightData.size.y * 0.5;

    // Define the dimensions of the attenuation volume.
    // TODO: This could be precomputed.
    float  radius     = rsqrt(lightData.invSqrAttenuationRadius);
    float3 invHalfDim = rcp(float3(radius + halfWidth,
                                   radius + halfHeight,
                                   radius));

    // Compute the light attenuation.
#ifdef ELLIPSOIDAL_ATTENUATION
    // The attenuation volume is an axis-aligned ellipsoid s.t.
    // r1 = (r + w / 2), r2 = (r + h / 2), r3 = r.
    float intensity = GetEllipsoidalDistanceAttenuation(unL, invHalfDim);
#else
    // The attenuation volume is an axis-aligned box s.t.
    // hX = (r + w / 2), hY = (r + h / 2), hZ = r.
    float intensity = GetBoxDistanceAttenuation(unL, invHalfDim);
#endif

    // Terminate if the shaded point is too far away.
    if (intensity == 0.0) return;

    lightData.diffuseScale  *= intensity;
    lightData.specularScale *= intensity;

    // TODO: store 4 points and save 12 cycles (24x MADs - 12x MOVs).
    float3 p0 = lightData.positionWS + lightData.right *  halfWidth + lightData.up *  halfHeight;
    float3 p1 = lightData.positionWS + lightData.right *  halfWidth + lightData.up * -halfHeight;
    float3 p2 = lightData.positionWS + lightData.right * -halfWidth + lightData.up * -halfHeight;
    float3 p3 = lightData.positionWS + lightData.right * -halfWidth + lightData.up *  halfHeight;

    float4x3 matL = float4x3(p0, p1, p2, p3) - float4x3(positionWS, positionWS, positionWS, positionWS);

    float ltcValue;

    // Evaluate the diffuse part.
    {
    #ifdef LIT_DIFFUSE_LAMBERT_BRDF
        ltcValue = LTCEvaluate(matL, V, bsdfData.normalWS, preLightData.NdotV, k_identity3x3);
    #else
        ltcValue = LTCEvaluate(matL, V, bsdfData.normalWS, preLightData.NdotV, preLightData.ltcXformDisneyDiffuse);
    #endif

    #ifndef LIT_DIFFUSE_LAMBERT_BRDF
        ltcValue *= preLightData.ltcDisneyDiffuseMagnitude;
    #endif

        ltcValue *= lightData.diffuseScale;
        diffuseLighting = bsdfData.diffuseColor * lightData.color * ltcValue;
    }

    // Evaluate the specular part.
    {
        // TODO: the fit seems rather poor. The scaling factor of 0.5 allows us
        // to match the reference for rough metals, but further darkens dielectrics.
        float3 fresnelTerm = bsdfData.fresnel0 * preLightData.ltcGGXFresnelMagnitudeDiff
                           + (float3)preLightData.ltcGGXFresnelMagnitude;

        ltcValue  = LTCEvaluate(matL, V, bsdfData.normalWS, preLightData.NdotV, preLightData.ltcXformGGX);
        ltcValue *= lightData.specularScale;
        specularLighting = fresnelTerm * lightData.color * ltcValue;
    }
#endif // LIT_DISPLAY_REFERENCE_AREA
}

//-----------------------------------------------------------------------------
// EvaluateBSDF_Env - Reference
// ----------------------------------------------------------------------------

// Ref: Moving Frostbite to PBR (Appendix A)
float3 IntegrateLambertIBLRef(LightLoopContext lightLoopContext,
                              float3 V, EnvLightData lightData, BSDFData bsdfData,
                              uint sampleCount = 4096)
{
    float3x3 localToWorld = float3x3(bsdfData.tangentWS, bsdfData.bitangentWS, bsdfData.normalWS);
    float3   acc          = float3(0.0, 0.0, 0.0);

    // Add some jittering on Hammersley2d
    float2 randNum  = InitRandom(V.xy * 0.5 + 0.5);

    for (uint i = 0; i < sampleCount; ++i)
    {
        float2 u    = Hammersley2d(i, sampleCount);
        u           = frac(u + randNum);

        float3 L;
        float NdotL;
        float weightOverPdf;
        ImportanceSampleLambert(u, localToWorld, L, NdotL, weightOverPdf);

        if (NdotL > 0.0)
        {
            float4 val = SampleEnv(lightLoopContext, lightData.envIndex, L, 0);

            // diffuse Albedo is apply here as describe in ImportanceSampleLambert function
            acc += bsdfData.diffuseColor * LambertNoPI() * weightOverPdf * val.rgb;
        }
    }

    return acc / sampleCount;
}

float3 IntegrateDisneyDiffuseIBLRef(LightLoopContext lightLoopContext,
                                    float3 V, PreLightData preLightData, EnvLightData lightData, BSDFData bsdfData,
                                    uint sampleCount = 4096)
{
    float3x3 localToWorld = float3x3(bsdfData.tangentWS, bsdfData.bitangentWS, bsdfData.normalWS);
    float3   acc          = float3(0.0, 0.0, 0.0);

    // Add some jittering on Hammersley2d
    float2 randNum  = InitRandom(V.xy * 0.5 + 0.5);

    for (uint i = 0; i < sampleCount; ++i)
    {
        float2 u    = Hammersley2d(i, sampleCount);
        u           = frac(u + randNum);

        float3 L;
        float NdotL;
        float weightOverPdf;
        // for Disney we still use a Cosine importance sampling, true Disney importance sampling imply a look up table
        ImportanceSampleLambert(u, localToWorld, L, NdotL, weightOverPdf);

        if (NdotL > 0.0)
        {
            float3 H = normalize(L + V);
            float LdotH = dot(L, H);
            // Note: we call DisneyDiffuse that require to multiply by Albedo / PI. Divide by PI is already taken into account
            // in weightOverPdf of ImportanceSampleLambert call.
            float disneyDiffuse = DisneyDiffuse(preLightData.NdotV, NdotL, LdotH, bsdfData.perceptualRoughness);

            // diffuse Albedo is apply here as describe in ImportanceSampleLambert function
            float4 val = SampleEnv(lightLoopContext, lightData.envIndex, L, 0);
            acc += bsdfData.diffuseColor * disneyDiffuse * weightOverPdf * val.rgb;
        }
    }

    return acc / sampleCount;
}

// Ref: Moving Frostbite to PBR (Appendix A)
float3 IntegrateSpecularGGXIBLRef(LightLoopContext lightLoopContext,
                                  float3 V, PreLightData preLightData, EnvLightData lightData, BSDFData bsdfData,
                                  uint sampleCount = 4096)
{
    float3x3 localToWorld = float3x3(bsdfData.tangentWS, bsdfData.bitangentWS, bsdfData.normalWS);
    float3   acc          = float3(0.0, 0.0, 0.0);

    // Add some jittering on Hammersley2d
    float2 randNum  = InitRandom(V.xy * 0.5 + 0.5);

    for (uint i = 0; i < sampleCount; ++i)
    {
        float2 u    = Hammersley2d(i, sampleCount);
        u           = frac(u + randNum);

        float VdotH;
        float NdotL;
        float3 L;
        float weightOverPdf;

        // GGX BRDF
        if (bsdfData.materialId == MATERIALID_LIT_ANISO)
        {
            ImportanceSampleAnisoGGX(u, V, localToWorld, bsdfData.roughnessT, bsdfData.roughnessB, preLightData.NdotV, L, VdotH, NdotL, weightOverPdf);
        }
        else
        {
            ImportanceSampleGGX(u, V, localToWorld, bsdfData.roughness, preLightData.NdotV, L, VdotH, NdotL, weightOverPdf);
        }


        if (NdotL > 0.0)
        {
            // Fresnel component is apply here as describe in ImportanceSampleGGX function
            float3 FweightOverPdf = F_Schlick(bsdfData.fresnel0, VdotH) * weightOverPdf;

            float4 val = SampleEnv(lightLoopContext, lightData.envIndex, L, 0);

            acc += FweightOverPdf * val.rgb;
        }
    }

    return acc / sampleCount;
}

//-----------------------------------------------------------------------------
// EvaluateBSDF_Env
// ----------------------------------------------------------------------------

// _preIntegratedFGD and _CubemapLD are unique for each BRDF
void EvaluateBSDF_Env(  LightLoopContext lightLoopContext,
                        float3 V, PositionInputs posInput, PreLightData preLightData, EnvLightData lightData, BSDFData bsdfData,
                        out float3 diffuseLighting, out float3 specularLighting, out float2 weight)
{
    float3 positionWS = posInput.positionWS;

#ifdef LIT_DISPLAY_REFERENCE_IBL

    specularLighting = IntegrateSpecularGGXIBLRef(lightLoopContext, V, preLightData, lightData, bsdfData);

/*
    #ifdef LIT_DIFFUSE_LAMBERT_BRDF
    diffuseLighting = IntegrateLambertIBLRef(lightData, V, bsdfData);
    #else
    diffuseLighting = IntegrateDisneyDiffuseIBLRef(lightLoopContext, V, preLightData, lightData, bsdfData);
    #endif
*/
    diffuseLighting = float3(0.0, 0.0, 0.0);

    weight = float2(0.0, 1.0);

#else
    // TODO: factor this code in common, so other material authoring don't require to rewrite everything,
    // also think about how such a loop can handle 2 cubemap at the same time as old unity. Macro can allow to do that
    // but we need to have UNITY_SAMPLE_ENV_LOD replace by a true function instead that is define by the lighting arcitecture.
    // Also not sure how to deal with 2 intersection....
    // Box and sphere are related to light property (but we have also distance based roughness etc...)

    // TODO: test the strech from Tomasz
    // float shrinkedRoughness = AnisotropicStrechAtGrazingAngle(bsdfData.roughness, bsdfData.perceptualRoughness, NdotV);

    // In this code we redefine a bit the behavior of the reflcetion proble. We separate the projection volume (the proxy of the scene) form the influence volume (what pixel on the screen is affected)

    // 1. First determine the projection volume

    // In Unity the cubemaps are capture with the localToWorld transform of the component.
    // This mean that location and oritention matter. So after intersection of proxy volume we need to convert back to world.

    // CAUTION: localToWorld is the transform use to convert the cubemap capture point to world space (mean it include the offset)
    // the center of the bounding box is thus in locals space: positionLS - offsetLS
    // We use this formulation as it is the one of legacy unity that was using only AABB box.

    float3 R = preLightData.iblDirWS;
    float3x3 worldToLocal = transpose(float3x3(lightData.right, lightData.up, lightData.forward)); // worldToLocal assume no scaling
    float3 positionLS = positionWS - lightData.positionWS;
    positionLS = mul(positionLS, worldToLocal).xyz - lightData.offsetLS; // We want to calculate the intersection from the center of the bounding box.

    if (lightData.envShapeType == ENVSHAPETYPE_BOX)
    {
        float3 dirLS = mul(R, worldToLocal);
        float3 boxOuterDistance = lightData.innerDistance + float3(lightData.blendDistance, lightData.blendDistance, lightData.blendDistance);
        float dist = BoxRayIntersectSimple(positionLS, dirLS, -boxOuterDistance, boxOuterDistance);

        // No need to normalize for fetching cubemap
        // We can reuse dist calculate in LS directly in WS as there is no scaling. Also the offset is already include in lightData.positionWS
        R = (positionWS + dist * R) - lightData.positionWS;

        // TODO: add distance based roughness
    }
    else if (lightData.envShapeType == ENVSHAPETYPE_SPHERE)
    {
        float3 dirLS = mul(R, worldToLocal);
        float sphereOuterDistance = lightData.innerDistance.x + lightData.blendDistance;
        float dist = SphereRayIntersectSimple(positionLS, dirLS, sphereOuterDistance);

        R = (positionWS + dist * R) - lightData.positionWS;
    }

    // 2. Apply the influence volume (Box volume is used for culling whatever the influence shape)
    // TODO: In the future we could have an influence volume inside the projection volume (so with a different transform, in this case we will need another transform)
    weight.y = 1.0;

    if (lightData.envShapeType == ENVSHAPETYPE_SPHERE)
    {
        float distFade = max(length(positionLS) - lightData.innerDistance.x, 0.0);
        weight.y = saturate(1.0 - distFade / max(lightData.blendDistance, 0.0001)); // avoid divide by zero
    }
    else if (lightData.envShapeType == ENVSHAPETYPE_BOX ||
             lightData.envShapeType == ENVSHAPETYPE_NONE)
    {
        // Calculate falloff value, so reflections on the edges of the volume would gradually blend to previous reflection.
        float distFade = DistancePointBox(positionLS, -lightData.innerDistance, lightData.innerDistance);
        weight.y = saturate(1.0 - distFade / max(lightData.blendDistance, 0.0001)); // avoid divide by zero
    }

    // Smooth weighting
    weight.x = 0.0;
    weight.y = smoothstep01(weight.y);

    // TODO: we must always perform a weight calculation as due to tiled rendering we need to smooth out cubemap at boundaries.
    // So goal is to split into two category and have an option to say if we parallax correct or not.

    float4 preLD = SampleEnv(lightLoopContext, lightData.envIndex, R, preLightData.iblMipLevel);
    specularLighting = preLD.rgb * preLightData.specularFGD;

    // Apply specular occlusion on it
    specularLighting *= bsdfData.specularOcclusion;
    diffuseLighting = float3(0.0, 0.0, 0.0);

#endif
}

#endif // #ifdef HAS_LIGHTLOOP
