//#define SHADOWS_ENABLED
//#define SHADOWS_FIXSHADOWIDX
using UnityEngine.Rendering;
using System.Collections.Generic;
using System;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
#if SHADOWS_ENABLED
    using ShadowExp;

    class ShadowSetup : IDisposable
    {
        // shadow related stuff
        const int k_MaxShadowDataSlots              = 64;
        const int k_MaxPayloadSlotsPerShadowData    =  4;
        ShadowmapBase[]         m_Shadowmaps;
        ShadowManager           m_ShadowMgr;
        static ComputeBuffer    s_ShadowDataBuffer;
        static ComputeBuffer    s_ShadowPayloadBuffer;

        public ShadowSetup(ShadowSettings shadowSettings, out IShadowManager shadowManager)
        {
            s_ShadowDataBuffer      = new ComputeBuffer(k_MaxShadowDataSlots, System.Runtime.InteropServices.Marshal.SizeOf(typeof(ShadowExp.ShadowData)));
            s_ShadowPayloadBuffer   = new ComputeBuffer(k_MaxShadowDataSlots * k_MaxPayloadSlotsPerShadowData, System.Runtime.InteropServices.Marshal.SizeOf(typeof(ShadowExp.ShadowData)));
            ShadowAtlas.AtlasInit atlasInit;
            atlasInit.baseInit.width           = (uint)shadowSettings.shadowAtlasWidth;
            atlasInit.baseInit.height          = (uint)shadowSettings.shadowAtlasHeight;
            atlasInit.baseInit.slices          = 1;
            atlasInit.baseInit.shadowmapBits   = 32;
            atlasInit.baseInit.shadowmapFormat = RenderTextureFormat.Shadowmap;
            atlasInit.baseInit.clearColor      = new Vector4(0.0f, 0.0f, 0.0f, 0.0f);
            atlasInit.baseInit.maxPayloadCount = 0;
            atlasInit.baseInit.shadowSupport   = ShadowmapBase.ShadowSupport.Directional;
            atlasInit.shaderKeyword            = null;
            atlasInit.cascadeCount             = shadowSettings.directionalLightCascadeCount;
            atlasInit.cascadeRatios            = shadowSettings.directionalLightCascades;

            var atlasInit2 = atlasInit;
            atlasInit2.baseInit.shadowSupport  = ShadowmapBase.ShadowSupport.Point | ShadowmapBase.ShadowSupport.Spot;
            m_Shadowmaps = new ShadowmapBase[] { new ShadowExp.ShadowAtlas(ref atlasInit), new ShadowExp.ShadowAtlas(ref atlasInit2) };

            ShadowContext.SyncDel syncer = (ShadowContext sc) =>
                {
                    // update buffers
                    uint offset, count;
                    ShadowExp.ShadowData[] sds;
                    sc.GetShadowDatas(out sds, out offset, out count);
                    Debug.Assert(offset == 0);
                    s_ShadowDataBuffer.SetData(sds);   // unfortunately we can't pass an offset or count to this function
                    ShadowPayload[] payloads;
                    sc.GetPayloads(out payloads, out offset, out count);
                    Debug.Assert(offset == 0);
                    s_ShadowPayloadBuffer.SetData(payloads);
                };

            // binding code. This needs to be in sync with ShadowContext.hlsl
            ShadowContext.BindDel binder = (ShadowContext sc, CommandBuffer cb) =>
                {
                    // bind buffers
                    cb.SetGlobalBuffer("_ShadowDatasExp", s_ShadowDataBuffer);
                    cb.SetGlobalBuffer("_ShadowPayloads", s_ShadowPayloadBuffer);
                    // bind textures
                    uint offset, count;
                    RenderTargetIdentifier[] tex;
                    sc.GetTex2DArrays(out tex, out offset, out count);
                    cb.SetGlobalTexture("_ShadowmapExp_Dir", tex[0]);
                    cb.SetGlobalTexture("_ShadowmapExp_PointSpot", tex[1]);
                    // TODO: Currently samplers are hard coded in ShadowContext.hlsl, so we can't really set them here
                };

            ShadowContext.CtxtInit scInit;
            scInit.storage.maxShadowDataSlots        = k_MaxShadowDataSlots;
            scInit.storage.maxPayloadSlots           = k_MaxShadowDataSlots * k_MaxPayloadSlotsPerShadowData;
            scInit.storage.maxTex2DArraySlots        = 4;
            scInit.storage.maxTexCubeArraySlots      = 2;
            scInit.storage.maxComparisonSamplerSlots = 2;
            scInit.storage.maxSamplerSlots           = 2;
            scInit.dataSyncer                        = syncer;
            scInit.resourceBinder                    = binder;

            m_ShadowMgr = new ShadowExp.ShadowManager(shadowSettings, ref scInit, m_Shadowmaps);
            shadowManager = m_ShadowMgr;
        }

        public void Dispose()
        {
            if (m_Shadowmaps != null)
            {
                (m_Shadowmaps[0] as ShadowAtlas).Dispose();
                (m_Shadowmaps[1] as ShadowAtlas).Dispose();
                m_Shadowmaps = null;
            }
            m_ShadowMgr = null;

            Utilities.SafeRelease(s_ShadowDataBuffer);
            Utilities.SafeRelease(s_ShadowPayloadBuffer);
        }
    }
#endif

    namespace TilePass
    {
        //-----------------------------------------------------------------------------
        // structure definition
        //-----------------------------------------------------------------------------

        [GenerateHLSL]
        public enum LightVolumeType
        {
            Cone,
            Sphere,
            Box,
            Count
        }

        [GenerateHLSL]
        public enum LightCategory
        {
            Punctual,
            Area,
            Projector,
            Env,
            Count
        }

        [GenerateHLSL]
        public class LightFeatureFlags
        {
            public static uint FEATURE_FLAG_LIGHT_PUNCTUAL    = 1 << 0;
            public static uint FEATURE_FLAG_LIGHT_AREA        = 1 << 1;
            public static uint FEATURE_FLAG_LIGHT_DIRECTIONAL = 1 << 2;
            public static uint FEATURE_FLAG_LIGHT_PROJECTOR   = 1 << 3;
            public static uint FEATURE_FLAG_LIGHT_ENV         = 1 << 4;
            public static uint FEATURE_FLAG_LIGHT_SKY         = 1 << 5;
        }

        [GenerateHLSL]
        public class LightDefinitions
        {
            public static int MAX_NR_LIGHTS_PER_CAMERA = 1024;
            public static int MAX_NR_BIGTILE_LIGHTS_PLUSONE = 512;      // may be overkill but the footprint is 2 bits per pixel using uint16.
            public static float VIEWPORT_SCALE_Z = 1.0f;

            // enable unity's original left-hand shader camera space (right-hand internally in unity).
            public static int USE_LEFTHAND_CAMERASPACE = 1;

            public static int TILE_SIZE_FPTL = 16;
            public static int TILE_SIZE_CLUSTERED = 32;

            // flags
            public static int IS_CIRCULAR_SPOT_SHAPE = 1;
            public static int HAS_COOKIE_TEXTURE = 2;
            public static int IS_BOX_PROJECTED = 4;
            public static int HAS_SHADOW = 8;

            // feature variants
            public static int NUM_FEATURE_VARIANTS = 16;
        }

        [GenerateHLSL]
        public struct SFiniteLightBound
        {
            public Vector3 boxAxisX;
            public Vector3 boxAxisY;
            public Vector3 boxAxisZ;
            public Vector3 center;        // a center in camera space inside the bounding volume of the light source.
            public Vector2 scaleXY;
            public float radius;
        };

        [GenerateHLSL]
        public struct LightVolumeData
        {
            public Vector3 lightPos;
            public uint lightVolume;

            public Vector3 lightAxisX;
            public uint lightCategory;

            public Vector3 lightAxisY;
            public float radiusSq;

            public Vector3 lightAxisZ;      // spot +Z axis
            public float cotan;

            public Vector3 boxInnerDist;
            public uint featureFlags;

            public Vector3 boxInvRange;
            public float unused2;
        };

        public class LightLoop : BaseLightLoop
        {
            public const int k_MaxDirectionalLightsOnScreen = 4;
            public const int k_MaxPunctualLightsOnScreen    = 512;
            public const int k_MaxAreaLightsOnScreen        = 64;
            public const int k_MaxProjectorLightsOnScreen   = 64;
            public const int k_MaxLightsOnScreen = k_MaxDirectionalLightsOnScreen + k_MaxPunctualLightsOnScreen + k_MaxAreaLightsOnScreen + k_MaxProjectorLightsOnScreen;
            public const int k_MaxEnvLightsOnScreen = 64;
            public const int k_MaxShadowOnScreen = 16;
            public const int k_MaxCascadeCount = 4; //Should be not less than m_Settings.directionalLightCascadeCount;

            // Static keyword is required here else we get a "DestroyBuffer can only be call in main thread"
            static ComputeBuffer s_DirectionalLightDatas = null;
            static ComputeBuffer s_LightDatas = null;
            static ComputeBuffer s_EnvLightDatas = null;
            static ComputeBuffer s_shadowDatas = null;

            static Texture2DArray m_DefaultTexture2DArray;

            TextureCacheCubemap m_CubeReflTexArray;
            TextureCache2D m_CookieTexArray;
            TextureCacheCubemap m_CubeCookieTexArray;

            public class LightList
            {
                public List<DirectionalLightData> directionalLights;
                public List<LightData> lights;
                public List<EnvLightData> envLights;
                public List<ShadowData> shadows;
                public Vector4[] directionalShadowSplitSphereSqr;

                public List<SFiniteLightBound> bounds;
                public List<LightVolumeData> lightVolumes;

                public void Clear()
                {
                    directionalLights.Clear();
                    lights.Clear();
                    envLights.Clear();
                    shadows.Clear();

                    bounds.Clear();
                    lightVolumes.Clear();
                }

                public void Allocate()
                {
                    directionalLights = new List<DirectionalLightData>();
                    lights = new List<LightData>();
                    envLights = new List<EnvLightData>();
                    shadows = new List<ShadowData>();
                    directionalShadowSplitSphereSqr = new Vector4[k_MaxCascadeCount];

                    bounds = new List<SFiniteLightBound>();
                    lightVolumes = new List<LightVolumeData>();
                }
            }

            LightList m_lightList;
            int m_punctualLightCount = 0;
            int m_areaLightCount = 0;
            int m_projectorLightCount = 0;
            int m_lightCount = 0;

            private ComputeShader buildScreenAABBShader { get { return m_PassResources.buildScreenAABBShader; } }
            private ComputeShader buildPerTileLightListShader { get { return m_PassResources.buildPerTileLightListShader; } }
            private ComputeShader buildPerBigTileLightListShader { get { return m_PassResources.buildPerBigTileLightListShader; } }
            private ComputeShader buildPerVoxelLightListShader { get { return m_PassResources.buildPerVoxelLightListShader; } }

            private ComputeShader clearDispatchIndirectShader { get { return m_PassResources.clearDispatchIndirectShader; } }
            private ComputeShader shadeOpaqueShader { get { return m_PassResources.shadeOpaqueShader; } }

            static int s_GenAABBKernel;
            static int s_GenListPerTileKernel;
            static int s_GenListPerVoxelKernel;
            static int s_ClearVoxelAtomicKernel;
            static int s_ClearDispatchIndirectKernel;
            static int s_shadeOpaqueDirectClusteredKernel;
            static int s_shadeOpaqueDirectFptlKernel;
            static int s_shadeOpaqueDirectClusteredDebugDisplayKernel;
            static int s_shadeOpaqueDirectFptlDebugDisplayKernel;
            static int[] s_shadeOpaqueIndirectClusteredKernels = new int[LightDefinitions.NUM_FEATURE_VARIANTS];
            static int[] s_shadeOpaqueIndirectFptlKernels = new int[LightDefinitions.NUM_FEATURE_VARIANTS];

            static ComputeBuffer s_LightVolumeDataBuffer = null;
            static ComputeBuffer s_ConvexBoundsBuffer = null;
            static ComputeBuffer s_AABBBoundsBuffer = null;
            static ComputeBuffer s_LightList = null;
            static ComputeBuffer s_TileList = null;
            static ComputeBuffer s_DispatchIndirectBuffer = null;

            static ComputeBuffer s_BigTileLightList = null;        // used for pre-pass coarse culling on 64x64 tiles
            static int s_GenListPerBigTileKernel;

            const bool k_UseDepthBuffer = true;      // only has an impact when EnableClustered is true (requires a depth-prepass)
            const bool k_UseAsyncCompute = true;        // should not use on mobile

            const int k_Log2NumClusters = 6;     // accepted range is from 0 to 6. NumClusters is 1<<g_iLog2NumClusters
            const float k_ClustLogBase = 1.02f;     // each slice 2% bigger than the previous
            float m_ClustScale;
            static ComputeBuffer s_PerVoxelLightLists = null;
            static ComputeBuffer s_PerVoxelOffset = null;
            static ComputeBuffer s_PerTileLogBaseTweak = null;
            static ComputeBuffer s_GlobalLightListAtomic = null;
            // clustered light list specific buffers and data end

            private static GameObject s_DefaultAdditionalLightDataGameObject;
            private static AdditionalLightData s_DefaultAdditionalLightData;

            bool usingFptl
            {
                get
                {
                    bool isEnabledMSAA = false;
                    Debug.Assert(!isEnabledMSAA || m_PassSettings.enableClustered);
                    bool disableFptl = (!m_PassSettings.enableFptlForOpaqueWhenClustered && m_PassSettings.enableClustered) || isEnabledMSAA;
                    return !disableFptl;
                }
            }

            private static AdditionalLightData DefaultAdditionalLightData
            {
                get
                {
                    if (s_DefaultAdditionalLightDataGameObject == null)
                    {
                        s_DefaultAdditionalLightDataGameObject = new GameObject("Default Light Data");
                        s_DefaultAdditionalLightDataGameObject.hideFlags = HideFlags.HideAndDontSave;
                        s_DefaultAdditionalLightData = s_DefaultAdditionalLightDataGameObject.AddComponent<AdditionalLightData>();
                        s_DefaultAdditionalLightDataGameObject.SetActive(false);
                    }
                    return s_DefaultAdditionalLightData;
                }
            }

            Material m_DeferredDirectMaterialSRT   = null;
            Material m_DeferredDirectMaterialMRT   = null;
            Material m_DeferredIndirectMaterialSRT = null;
            Material m_DeferredIndirectMaterialMRT = null;
            Material m_DeferredAllMaterialSRT      = null;
            Material m_DeferredAllMaterialMRT      = null;

            Material m_DebugViewTilesMaterial      = null;

            Material m_SingleDeferredMaterialSRT   = null;
            Material m_SingleDeferredMaterialMRT   = null;

#if (SHADOWS_ENABLED)
            // shadow related stuff
            FrameId                 m_FrameId;
            ShadowSetup             m_ShadowSetup; // doesn't actually have to reside here, it would be enough to pass the IShadowManager in from the outside
            IShadowManager          m_ShadowMgr;
            List<int>               m_ShadowRequests = new List<int>();
            Dictionary<int, int>    m_ShadowIndices = new Dictionary<int, int>();

            void InitShadowSystem(ShadowSettings shadowSettings)
            {
                m_ShadowSetup = new ShadowSetup(shadowSettings, out m_ShadowMgr);
            }

            void DeinitShadowSystem()
            {
                if (m_ShadowSetup != null)
                {
                    m_ShadowSetup.Dispose();
                    m_ShadowSetup = null;
                    m_ShadowMgr = null;
                }
            }

#endif


            int GetNumTileFtplX(Camera camera)
            {
                return (camera.pixelWidth + (LightDefinitions.TILE_SIZE_FPTL - 1)) / LightDefinitions.TILE_SIZE_FPTL;
            }

            int GetNumTileFtplY(Camera camera)
            {
                return (camera.pixelHeight + (LightDefinitions.TILE_SIZE_FPTL - 1)) / LightDefinitions.TILE_SIZE_FPTL;
            }

            int GetNumTileClusteredX(Camera camera)
            {
                return (camera.pixelWidth + (LightDefinitions.TILE_SIZE_CLUSTERED - 1)) / LightDefinitions.TILE_SIZE_CLUSTERED;
            }

            int GetNumTileClusteredY(Camera camera)
            {
                return (camera.pixelHeight + (LightDefinitions.TILE_SIZE_CLUSTERED - 1)) / LightDefinitions.TILE_SIZE_CLUSTERED;
            }

            bool GetFeatureVariantsEnabled()
            {
                return m_PassSettings.enableComputeLightEvaluation && m_PassSettings.enableComputeFeatureVariants && !(m_PassSettings.enableClustered && !m_PassSettings.enableFptlForOpaqueWhenClustered);
            }

            TileLightLoopProducer.TileSettings m_PassSettings;
            private TilePassResources m_PassResources;

            public LightLoop(TileLightLoopProducer producer)
            {
                m_PassSettings = producer.tileSettings;
                m_PassResources = producer.passResources;
            }

            public override void Build(TextureSettings textureSettings)
            {
                m_lightList = new LightList();
                m_lightList.Allocate();

                s_DirectionalLightDatas = new ComputeBuffer(k_MaxDirectionalLightsOnScreen, System.Runtime.InteropServices.Marshal.SizeOf(typeof(DirectionalLightData)));
                s_LightDatas = new ComputeBuffer(k_MaxPunctualLightsOnScreen + k_MaxAreaLightsOnScreen + k_MaxProjectorLightsOnScreen, System.Runtime.InteropServices.Marshal.SizeOf(typeof(LightData)));
                s_EnvLightDatas = new ComputeBuffer(k_MaxEnvLightsOnScreen, System.Runtime.InteropServices.Marshal.SizeOf(typeof(EnvLightData)));
                s_shadowDatas = new ComputeBuffer(k_MaxCascadeCount + k_MaxShadowOnScreen, System.Runtime.InteropServices.Marshal.SizeOf(typeof(ShadowData)));

                m_CookieTexArray = new TextureCache2D();
                m_CookieTexArray.AllocTextureArray(8, textureSettings.spotCookieSize, textureSettings.spotCookieSize, TextureFormat.RGBA32, true);
                m_CubeCookieTexArray = new TextureCacheCubemap();
                m_CubeCookieTexArray.AllocTextureArray(4, textureSettings.pointCookieSize, TextureFormat.RGBA32, true);
                m_CubeReflTexArray = new TextureCacheCubemap();
                m_CubeReflTexArray.AllocTextureArray(32, textureSettings.reflectionCubemapSize, TextureCache.GetPreferredHdrCompressedTextureFormat, true);

                s_GenAABBKernel = buildScreenAABBShader.FindKernel("ScreenBoundsAABB");

                bool enableFeatureVariants = GetFeatureVariantsEnabled();
                if (enableFeatureVariants)
                {
                    s_GenListPerTileKernel = buildPerTileLightListShader.FindKernel(m_PassSettings.enableBigTilePrepass ? "TileLightListGen_SrcBigTile_FeatureFlags" : "TileLightListGen_FeatureFlags");
                }
                else
                {
                    s_GenListPerTileKernel = buildPerTileLightListShader.FindKernel(m_PassSettings.enableBigTilePrepass ? "TileLightListGen_SrcBigTile" : "TileLightListGen");
                }
                s_AABBBoundsBuffer = new ComputeBuffer(2 * k_MaxLightsOnScreen, 3 * sizeof(float));
                s_ConvexBoundsBuffer = new ComputeBuffer(k_MaxLightsOnScreen, System.Runtime.InteropServices.Marshal.SizeOf(typeof(SFiniteLightBound)));
                s_LightVolumeDataBuffer = new ComputeBuffer(k_MaxLightsOnScreen, System.Runtime.InteropServices.Marshal.SizeOf(typeof(LightVolumeData)));
                s_DispatchIndirectBuffer = new ComputeBuffer(LightDefinitions.NUM_FEATURE_VARIANTS * 3, sizeof(uint), ComputeBufferType.IndirectArguments);

                if (m_PassSettings.enableClustered)
                {
                    var kernelName = m_PassSettings.enableBigTilePrepass ? (k_UseDepthBuffer ? "TileLightListGen_DepthRT_SrcBigTile" : "TileLightListGen_NoDepthRT_SrcBigTile") : (k_UseDepthBuffer ? "TileLightListGen_DepthRT" : "TileLightListGen_NoDepthRT");
                    s_GenListPerVoxelKernel = buildPerVoxelLightListShader.FindKernel(kernelName);
                    s_ClearVoxelAtomicKernel = buildPerVoxelLightListShader.FindKernel("ClearAtomic");
                    s_GlobalLightListAtomic = new ComputeBuffer(1, sizeof(uint));
                }

                if (m_PassSettings.enableBigTilePrepass)
                {
                    s_GenListPerBigTileKernel = buildPerBigTileLightListShader.FindKernel("BigTileLightListGen");
                }

                s_ClearDispatchIndirectKernel = clearDispatchIndirectShader.FindKernel("ClearDispatchIndirect");

                s_shadeOpaqueDirectClusteredKernel = shadeOpaqueShader.FindKernel("ShadeOpaque_Direct_Clustered");
                s_shadeOpaqueDirectFptlKernel = shadeOpaqueShader.FindKernel("ShadeOpaque_Direct_Fptl");
                s_shadeOpaqueDirectClusteredDebugDisplayKernel = shadeOpaqueShader.FindKernel("ShadeOpaque_Direct_Clustered_DebugDisplay");
                s_shadeOpaqueDirectFptlDebugDisplayKernel = shadeOpaqueShader.FindKernel("ShadeOpaque_Direct_Fptl_DebugDisplay");

                for (int variant = 0; variant < LightDefinitions.NUM_FEATURE_VARIANTS; variant++)
                {
                    s_shadeOpaqueIndirectClusteredKernels[variant] = shadeOpaqueShader.FindKernel("ShadeOpaque_Indirect_Clustered_Variant" + variant);
                    s_shadeOpaqueIndirectFptlKernels[variant] = shadeOpaqueShader.FindKernel("ShadeOpaque_Indirect_Fptl_Variant" + variant);
                }

                s_LightList = null;
                s_TileList = null;

                string[] tileKeywords = {"LIGHTLOOP_TILE_DIRECT", "LIGHTLOOP_TILE_INDIRECT", "LIGHTLOOP_TILE_ALL"};

                m_DeferredDirectMaterialSRT = Utilities.CreateEngineMaterial("Hidden/HDRenderPipeline/Deferred");
                Utilities.SelectKeyword(m_DeferredDirectMaterialSRT, tileKeywords, 0);
                m_DeferredDirectMaterialSRT.EnableKeyword("LIGHTLOOP_TILE_PASS");
                m_DeferredDirectMaterialSRT.DisableKeyword("OUTPUT_SPLIT_LIGHTING");
                m_DeferredDirectMaterialSRT.SetInt("_StencilRef", (int)StencilBits.Standard);
                m_DeferredDirectMaterialSRT.SetInt("_StencilCmp", 4 /* LEqual */);
                m_DeferredDirectMaterialSRT.SetInt("_SrcBlend", (int)BlendMode.One);
                m_DeferredDirectMaterialSRT.SetInt("_DstBlend", (int)BlendMode.Zero);

                m_DeferredDirectMaterialMRT = Utilities.CreateEngineMaterial("Hidden/HDRenderPipeline/Deferred");
                Utilities.SelectKeyword(m_DeferredDirectMaterialMRT, tileKeywords, 0);
                m_DeferredDirectMaterialMRT.EnableKeyword("LIGHTLOOP_TILE_PASS");
                m_DeferredDirectMaterialMRT.EnableKeyword("OUTPUT_SPLIT_LIGHTING");
                m_DeferredDirectMaterialMRT.SetInt("_StencilRef", (int)StencilBits.SSS);
                m_DeferredDirectMaterialMRT.SetInt("_StencilCmp", 3 /* Equal */);
                m_DeferredDirectMaterialMRT.SetInt("_SrcBlend", (int)BlendMode.One);
                m_DeferredDirectMaterialMRT.SetInt("_DstBlend", (int)BlendMode.Zero);

                m_DeferredIndirectMaterialSRT = Utilities.CreateEngineMaterial("Hidden/HDRenderPipeline/Deferred");
                Utilities.SelectKeyword(m_DeferredIndirectMaterialSRT, tileKeywords, 1);
                m_DeferredIndirectMaterialSRT.EnableKeyword("LIGHTLOOP_TILE_PASS");
                m_DeferredIndirectMaterialSRT.DisableKeyword("OUTPUT_SPLIT_LIGHTING");
                m_DeferredIndirectMaterialSRT.SetInt("_StencilRef", (int)StencilBits.Standard);
                m_DeferredIndirectMaterialSRT.SetInt("_StencilCmp", 4 /* LEqual */);
                m_DeferredIndirectMaterialSRT.SetInt("_SrcBlend", (int)BlendMode.One);
                m_DeferredIndirectMaterialSRT.SetInt("_DstBlend", (int)BlendMode.One); // Additive color & alpha source

                m_DeferredIndirectMaterialMRT = Utilities.CreateEngineMaterial("Hidden/HDRenderPipeline/Deferred");
                Utilities.SelectKeyword(m_DeferredIndirectMaterialMRT, tileKeywords, 1);
                m_DeferredIndirectMaterialMRT.EnableKeyword("LIGHTLOOP_TILE_PASS");
                m_DeferredIndirectMaterialMRT.EnableKeyword("OUTPUT_SPLIT_LIGHTING");
                m_DeferredIndirectMaterialMRT.SetInt("_StencilRef", (int)StencilBits.SSS);
                m_DeferredIndirectMaterialMRT.SetInt("_StencilCmp", 3 /* Equal */);
                m_DeferredIndirectMaterialMRT.SetInt("_SrcBlend", (int)BlendMode.One);
                m_DeferredIndirectMaterialMRT.SetInt("_DstBlend", (int)BlendMode.One); // Additive color & alpha source

                m_DeferredAllMaterialSRT = Utilities.CreateEngineMaterial("Hidden/HDRenderPipeline/Deferred");
                Utilities.SelectKeyword(m_DeferredAllMaterialSRT, tileKeywords, 2);
                m_DeferredAllMaterialSRT.EnableKeyword("LIGHTLOOP_TILE_PASS");
                m_DeferredAllMaterialSRT.DisableKeyword("OUTPUT_SPLIT_LIGHTING");
                m_DeferredAllMaterialSRT.SetInt("_StencilRef", (int)StencilBits.Standard);
                m_DeferredAllMaterialSRT.SetInt("_StencilCmp", 4 /* LEqual */);
                m_DeferredAllMaterialSRT.SetInt("_SrcBlend", (int)BlendMode.One);
                m_DeferredAllMaterialSRT.SetInt("_DstBlend", (int)BlendMode.Zero);

                m_DeferredAllMaterialMRT = Utilities.CreateEngineMaterial("Hidden/HDRenderPipeline/Deferred");
                Utilities.SelectKeyword(m_DeferredAllMaterialMRT, tileKeywords, 2);
                m_DeferredAllMaterialMRT.EnableKeyword("LIGHTLOOP_TILE_PASS");
                m_DeferredAllMaterialMRT.EnableKeyword("OUTPUT_SPLIT_LIGHTING");
                m_DeferredAllMaterialMRT.SetInt("_StencilRef", (int)StencilBits.SSS);
                m_DeferredAllMaterialMRT.SetInt("_StencilCmp", 3 /* Equal */);
                m_DeferredAllMaterialMRT.SetInt("_SrcBlend", (int)BlendMode.One);
                m_DeferredAllMaterialMRT.SetInt("_DstBlend", (int)BlendMode.Zero);

                m_DebugViewTilesMaterial = Utilities.CreateEngineMaterial("Hidden/HDRenderPipeline/DebugViewTiles");

                m_SingleDeferredMaterialSRT = Utilities.CreateEngineMaterial("Hidden/HDRenderPipeline/Deferred");
                m_SingleDeferredMaterialSRT.EnableKeyword("LIGHTLOOP_SINGLE_PASS");
                m_SingleDeferredMaterialSRT.DisableKeyword("OUTPUT_SPLIT_LIGHTING");
                m_SingleDeferredMaterialSRT.SetInt("_StencilRef", (int)StencilBits.Standard);
                m_SingleDeferredMaterialSRT.SetInt("_StencilCmp", 4 /* LEqual */);
                m_SingleDeferredMaterialSRT.SetInt("_SrcBlend", (int)BlendMode.One);
                m_SingleDeferredMaterialSRT.SetInt("_DstBlend", (int)BlendMode.Zero);

                m_SingleDeferredMaterialMRT = Utilities.CreateEngineMaterial("Hidden/HDRenderPipeline/Deferred");
                m_SingleDeferredMaterialMRT.EnableKeyword("LIGHTLOOP_SINGLE_PASS");
                m_SingleDeferredMaterialMRT.EnableKeyword("OUTPUT_SPLIT_LIGHTING");
                m_SingleDeferredMaterialMRT.SetInt("_StencilRef", (int)StencilBits.SSS);
                m_SingleDeferredMaterialMRT.SetInt("_StencilCmp", 3 /* Equal */);
                m_SingleDeferredMaterialMRT.SetInt("_SrcBlend", (int)BlendMode.One);
                m_SingleDeferredMaterialMRT.SetInt("_DstBlend", (int)BlendMode.Zero);

                m_DefaultTexture2DArray = new Texture2DArray(1, 1, 1, TextureFormat.ARGB32, false);
                m_DefaultTexture2DArray.SetPixels32(new Color32[1] { new Color32(128, 128, 128, 128) }, 0);
                m_DefaultTexture2DArray.Apply();

#if UNITY_EDITOR
                UnityEditor.SceneView.onSceneGUIDelegate -= OnSceneGUI;
                UnityEditor.SceneView.onSceneGUIDelegate += OnSceneGUI;
#endif

#if (SHADOWS_ENABLED)
                InitShadowSystem(ShadowSettings.Default);
#endif
            }

            public override void Cleanup()
            {
#if UNITY_EDITOR
                UnityEditor.SceneView.onSceneGUIDelegate -= OnSceneGUI;
#endif

                Utilities.SafeRelease(s_DirectionalLightDatas);
                Utilities.SafeRelease(s_LightDatas);
                Utilities.SafeRelease(s_EnvLightDatas);
                Utilities.SafeRelease(s_shadowDatas);

                if (m_CubeReflTexArray != null)
                {
                    m_CubeReflTexArray.Release();
                    m_CubeReflTexArray = null;
                }
                if (m_CookieTexArray != null)
                {
                    m_CookieTexArray.Release();
                    m_CookieTexArray = null;
                }
                if (m_CubeCookieTexArray != null)
                {
                    m_CubeCookieTexArray.Release();
                    m_CubeCookieTexArray = null;
                }

                ReleaseResolutionDependentBuffers();

                Utilities.SafeRelease(s_AABBBoundsBuffer);
                Utilities.SafeRelease(s_ConvexBoundsBuffer);
                Utilities.SafeRelease(s_LightVolumeDataBuffer);
                Utilities.SafeRelease(s_DispatchIndirectBuffer);

                // enableClustered
                Utilities.SafeRelease(s_GlobalLightListAtomic);

                Utilities.Destroy(m_DeferredDirectMaterialSRT);
                Utilities.Destroy(m_DeferredDirectMaterialMRT);
                Utilities.Destroy(m_DeferredIndirectMaterialSRT);
                Utilities.Destroy(m_DeferredIndirectMaterialMRT);
                Utilities.Destroy(m_DeferredAllMaterialSRT);
                Utilities.Destroy(m_DeferredAllMaterialMRT);

                Utilities.Destroy(m_DebugViewTilesMaterial);

                Utilities.Destroy(m_SingleDeferredMaterialSRT);
                Utilities.Destroy(m_SingleDeferredMaterialMRT);

                Utilities.Destroy(s_DefaultAdditionalLightDataGameObject);
                s_DefaultAdditionalLightDataGameObject = null;
                s_DefaultAdditionalLightData = null;
            }

            public override void NewFrame()
            {
                m_CookieTexArray.NewFrame();
                m_CubeCookieTexArray.NewFrame();
                m_CubeReflTexArray.NewFrame();
            }

            public override bool NeedResize()
            {
                return s_LightList == null || s_TileList == null ||
                    (s_BigTileLightList == null && m_PassSettings.enableBigTilePrepass) ||
                    (s_PerVoxelLightLists == null && m_PassSettings.enableClustered);
            }

            public override void ReleaseResolutionDependentBuffers()
            {
                Utilities.SafeRelease(s_LightList);
                Utilities.SafeRelease(s_TileList);

                // enableClustered
                Utilities.SafeRelease(s_PerVoxelLightLists);
                Utilities.SafeRelease(s_PerVoxelOffset);
                Utilities.SafeRelease(s_PerTileLogBaseTweak);

                // enableBigTilePrepass
                Utilities.SafeRelease(s_BigTileLightList);
            }

            int NumLightIndicesPerClusteredTile()
            {
                return 8 * (1 << k_Log2NumClusters);       // total footprint for all layers of the tile (measured in light index entries)
            }

            public override void AllocResolutionDependentBuffers(int width, int height)
            {
                var nrTilesX = (width + LightDefinitions.TILE_SIZE_FPTL - 1) / LightDefinitions.TILE_SIZE_FPTL;
                var nrTilesY = (height + LightDefinitions.TILE_SIZE_FPTL - 1) / LightDefinitions.TILE_SIZE_FPTL;
                var nrTiles = nrTilesX * nrTilesY;
                const int capacityUShortsPerTile = 32;
                const int dwordsPerTile = (capacityUShortsPerTile + 1) >> 1;        // room for 31 lights and a nrLights value.

                s_LightList = new ComputeBuffer((int)LightCategory.Count * dwordsPerTile * nrTiles, sizeof(uint));       // enough list memory for a 4k x 4k display
                s_TileList = new ComputeBuffer((int)LightDefinitions.NUM_FEATURE_VARIANTS * nrTiles, sizeof(uint));

                if (m_PassSettings.enableClustered)
                {
                    var nrClustersX = (width + LightDefinitions.TILE_SIZE_CLUSTERED - 1) / LightDefinitions.TILE_SIZE_CLUSTERED;
                    var nrClustersY = (height + LightDefinitions.TILE_SIZE_CLUSTERED - 1) / LightDefinitions.TILE_SIZE_CLUSTERED;
                    var nrClusterTiles = nrClustersX * nrClustersY;

                    s_PerVoxelOffset = new ComputeBuffer((int)LightCategory.Count * (1 << k_Log2NumClusters) * nrClusterTiles, sizeof(uint));
                    s_PerVoxelLightLists = new ComputeBuffer(NumLightIndicesPerClusteredTile() * nrClusterTiles, sizeof(uint));

                    if (k_UseDepthBuffer)
                    {
                        s_PerTileLogBaseTweak = new ComputeBuffer(nrClusterTiles, sizeof(float));
                    }
                }

                if (m_PassSettings.enableBigTilePrepass)
                {
                    var nrBigTilesX = (width + 63) / 64;
                    var nrBigTilesY = (height + 63) / 64;
                    var nrBigTiles = nrBigTilesX * nrBigTilesY;
                    s_BigTileLightList = new ComputeBuffer(LightDefinitions.MAX_NR_BIGTILE_LIGHTS_PLUSONE * nrBigTiles, sizeof(uint));
                }
            }

            static Matrix4x4 GetFlipMatrix()
            {
                Matrix4x4 flip = Matrix4x4.identity;
                bool isLeftHand = ((int)LightDefinitions.USE_LEFTHAND_CAMERASPACE) != 0;
                if (isLeftHand) flip.SetColumn(2, new Vector4(0.0f, 0.0f, -1.0f, 0.0f));
                return flip;
            }

            static Matrix4x4 WorldToCamera(Camera camera)
            {
                return GetFlipMatrix() * camera.worldToCameraMatrix;
            }

            static Matrix4x4 CameraProjection(Camera camera)
            {
                return camera.projectionMatrix * GetFlipMatrix();
            }

            public Vector3 GetLightColor(VisibleLight light)
            {
                return new Vector3(light.finalColor.r, light.finalColor.g, light.finalColor.b);
            }

            // Return number of added shadow
            public int GetShadows(VisibleLight light, int lightIndex, ref ShadowOutput shadowOutput, ShadowSettings shadowSettings)
            {
                for (int sliceIndex = 0; sliceIndex < shadowOutput.GetShadowSliceCountLightIndex(lightIndex); ++sliceIndex)
                {
                    ShadowData shadowData = new ShadowData();

                    int shadowSliceIndex = shadowOutput.GetShadowSliceIndex(lightIndex, sliceIndex);
                    shadowData.worldToShadow = shadowOutput.shadowSlices[shadowSliceIndex].shadowTransform.transpose; // Transpose for hlsl reading ?

                    shadowData.bias = light.light.shadowBias;
                    shadowData.invResolution = new Vector4(1.0f / shadowSettings.shadowAtlasWidth, 1.0f / shadowSettings.shadowAtlasHeight, 0.0f, 0.0f);
                    m_lightList.shadows.Add(shadowData);
                }

                return shadowOutput.GetShadowSliceCountLightIndex(lightIndex);
            }

            public bool GetDirectionalLightData(ShadowSettings shadowSettings, GPULightType gpuLightType, VisibleLight light, AdditionalLightData additionalData, int lightIndex, ref ShadowOutput shadowOutput, ref int directionalShadowcount)
            {
                var directionalLightData = new DirectionalLightData();

                float diffuseDimmer = m_PassSettings.diffuseGlobalDimmer * additionalData.lightDimmer;
                float specularDimmer = m_PassSettings.specularGlobalDimmer * additionalData.lightDimmer;
                if (diffuseDimmer  <= 0.0f && specularDimmer <= 0.0f)
                    return false;

                // Light direction for directional is opposite to the forward direction
                directionalLightData.forward = light.light.transform.forward;
                directionalLightData.up = light.light.transform.up;
                directionalLightData.right = light.light.transform.right;
                directionalLightData.positionWS = light.light.transform.position;
                directionalLightData.color = GetLightColor(light);
                directionalLightData.diffuseScale = additionalData.affectDiffuse ? diffuseDimmer : 0.0f;
                directionalLightData.specularScale = additionalData.affectSpecular ? specularDimmer : 0.0f;
                directionalLightData.invScaleX = 1.0f / light.light.transform.localScale.x;
                directionalLightData.invScaleY = 1.0f / light.light.transform.localScale.y;
                directionalLightData.cosAngle = 0.0f;
                directionalLightData.sinAngle = 0.0f;
                directionalLightData.shadowIndex = -1;
                directionalLightData.cookieIndex = -1;

                if (light.light.cookie != null)
                {
                    directionalLightData.tileCookie = (light.light.cookie.wrapMode == TextureWrapMode.Repeat);
                    directionalLightData.cookieIndex = m_CookieTexArray.FetchSlice(light.light.cookie);
                }

                bool hasDirectionalShadows = light.light.shadows != LightShadows.None && shadowOutput.GetShadowSliceCountLightIndex(lightIndex) != 0;
                bool hasDirectionalNotReachMaxLimit = directionalShadowcount == 0; // Only one cascade shadow allowed

                // If we have not found a directional shadow casting light yet, we register the last directional anyway as "sun".
                if (directionalShadowcount == 0)
                {
                    m_CurrentSunLight = light.light;
                }

                if (hasDirectionalShadows && hasDirectionalNotReachMaxLimit) // Note  < MaxShadows should be check at shadowOutput creation
                {
                    // Always choose the directional shadow casting light if it exists.
                    m_CurrentSunLight = light.light;

                    directionalLightData.shadowIndex = m_lightList.shadows.Count;
                    directionalShadowcount += GetShadows(light, lightIndex, ref shadowOutput, shadowSettings);

                    // Fill split information for shaders
                    for (int s = 0; s < k_MaxCascadeCount; ++s)
                    {
                        m_lightList.directionalShadowSplitSphereSqr[s] = shadowOutput.directionalShadowSplitSphereSqr[s];
                    }
                }

                m_lightList.directionalLights.Add(directionalLightData);

                return true;
            }

            float ComputeLinearDistanceFade(float distanceToCamera, float fadeDistance)
            {
                // Fade with distance calculation is just a linear fade from 90% of fade distance to fade distance. 90% arbitrarly chosen but should work well enough.
                float distanceFadeNear = 0.9f * fadeDistance;
                return 1.0f - Mathf.Clamp01((distanceToCamera - distanceFadeNear) / (fadeDistance - distanceFadeNear));
            }

            public bool GetLightData(ShadowSettings shadowSettings, Camera camera, GPULightType gpuLightType, VisibleLight light, AdditionalLightData additionalData, int lightIndex, ref ShadowOutput shadowOutput, ref int shadowCount)
            {
                var lightData = new LightData();

                lightData.lightType = gpuLightType;

                lightData.positionWS = light.light.transform.position;
                lightData.invSqrAttenuationRadius = 1.0f / (light.range * light.range);
                lightData.color = GetLightColor(light);

                lightData.forward = light.light.transform.forward; // Note: Light direction is oriented backward (-Z)
                lightData.up = light.light.transform.up;
                lightData.right = light.light.transform.right;

                if (lightData.lightType == GPULightType.Spot)
                {
                    var spotAngle = light.spotAngle;

                    var innerConePercent = additionalData.GetInnerSpotPercent01();
                    var cosSpotOuterHalfAngle = Mathf.Clamp(Mathf.Cos(spotAngle * 0.5f * Mathf.Deg2Rad), 0.0f, 1.0f);
                    var sinSpotOuterHalfAngle = Mathf.Sqrt(1.0f - cosSpotOuterHalfAngle * cosSpotOuterHalfAngle);
                    var cosSpotInnerHalfAngle = Mathf.Clamp(Mathf.Cos(spotAngle * 0.5f * innerConePercent * Mathf.Deg2Rad), 0.0f, 1.0f); // inner cone

                    var val = Mathf.Max(0.001f, (cosSpotInnerHalfAngle - cosSpotOuterHalfAngle));
                    lightData.angleScale = 1.0f / val;
                    lightData.angleOffset = -cosSpotOuterHalfAngle * lightData.angleScale;

                    // TODO: Currently the spot cookie code use the cotangent, either we fix the spot cookie code to not use cotangent
                    // or we clean the name here, store it in size.x for now
                    lightData.size.x = cosSpotOuterHalfAngle / sinSpotOuterHalfAngle;
                }
                else
                {
                    // 1.0f, 2.0f are neutral value allowing GetAngleAnttenuation in shader code to return 1.0
                    lightData.angleScale = 1.0f;
                    lightData.angleOffset = 2.0f;
                }

                float distanceToCamera = (lightData.positionWS - camera.transform.position).magnitude;
                float distanceFade = ComputeLinearDistanceFade(distanceToCamera, additionalData.fadeDistance);
                float lightScale = additionalData.lightDimmer * distanceFade;

                lightData.diffuseScale = additionalData.affectDiffuse ? lightScale * m_PassSettings.diffuseGlobalDimmer : 0.0f;
                lightData.specularScale = additionalData.affectSpecular ? lightScale * m_PassSettings.specularGlobalDimmer : 0.0f;

                if (lightData.diffuseScale <= 0.0f && lightData.specularScale <= 0.0f)
                    return false;

                lightData.IESIndex = -1;
                lightData.cookieIndex = -1;
                lightData.shadowIndex = -1;

                if (light.light.cookie != null)
                {
                    // TODO: add texture atlas support for cookie textures.
                    switch (light.lightType)
                    {
                        case LightType.Spot:
                            lightData.cookieIndex = m_CookieTexArray.FetchSlice(light.light.cookie);
                            break;
                        case LightType.Point:
                            lightData.cookieIndex = m_CubeCookieTexArray.FetchSlice(light.light.cookie);
                            break;
                    }

                    if (additionalData.archetype == LightArchetype.Projector)
                    {
                        lightData.cookieIndex = m_CookieTexArray.FetchSlice(light.light.cookie);
                    }
                }

                float shadowDistanceFade = ComputeLinearDistanceFade(distanceToCamera, additionalData.shadowFadeDistance);
                lightData.shadowDimmer = additionalData.shadowDimmer * shadowDistanceFade;

                // Setup shadow data arrays
                // In case lightData.shadowDimmer == 0.0 we need to avoid rendering the shadow map... see how it can be done with the culling (and more specifically, how can we do that BEFORE sending for shadows)
                bool hasShadows = lightData.shadowDimmer > 0.0f && distanceToCamera < shadowSettings.maxShadowDistance && light.light.shadows != LightShadows.None && shadowOutput.GetShadowSliceCountLightIndex(lightIndex) != 0;
                bool hasNotReachMaxLimit = shadowCount + (lightData.lightType == GPULightType.Point ? 6 : 1) <= k_MaxShadowOnScreen;

                // TODO: Read the comment about shadow limit/management at the beginning of this loop
                if (hasShadows && hasNotReachMaxLimit)
                {
                    // When we have a point light, we assumed that there is 6 consecutive PunctualShadowData
                    lightData.shadowIndex = m_lightList.shadows.Count;
                    shadowCount += GetShadows(light, lightIndex, ref shadowOutput, shadowSettings);
                }

                if (additionalData.archetype != LightArchetype.Punctual)
                {
                    lightData.size = new Vector2(additionalData.lightLength, additionalData.lightWidth);
                }

                m_lightList.lights.Add(lightData);

                return true;
            }

            // TODO: we should be able to do this calculation only with LightData without VisibleLight light, but for now pass both
            public void GetLightVolumeDataAndBound(LightCategory lightCategory, GPULightType gpuLightType, LightVolumeType lightVolumeType, VisibleLight light, LightData lightData, Matrix4x4 worldToView)
            {
                // Then Culling side
                var range = light.range;
                var lightToWorld = light.localToWorld;
                Vector3 lightPos = lightToWorld.GetColumn(3);

                // Fill bounds
                var bound = new SFiniteLightBound();
                var lightVolumeData = new LightVolumeData();

                lightVolumeData.lightCategory = (uint)lightCategory;
                lightVolumeData.lightVolume = (uint)lightVolumeType;

                if (gpuLightType == GPULightType.Spot || gpuLightType == GPULightType.ProjectorPyramid)
                {
                    Vector3 lightDir = lightToWorld.GetColumn(2);

                    // represents a left hand coordinate system in world space
                    Vector3 vx = lightToWorld.GetColumn(0);     // X axis in world space
                    Vector3 vy = lightToWorld.GetColumn(1);     // Y axis in world space
                    Vector3 vz = lightDir;                      // Z axis in world space

                    // transform to camera space (becomes a left hand coordinate frame in Unity since Determinant(worldToView)<0)
                    vx = worldToView.MultiplyVector(vx);
                    vy = worldToView.MultiplyVector(vy);
                    vz = worldToView.MultiplyVector(vz);

                    const float pi = 3.1415926535897932384626433832795f;
                    const float degToRad = (float)(pi / 180.0);

                    var sa = light.light.spotAngle;
                    var cs = Mathf.Cos(0.5f * sa * degToRad);
                    var si = Mathf.Sin(0.5f * sa * degToRad);

                    if (gpuLightType == GPULightType.ProjectorPyramid)
                    {
                        Vector3 lightPosToProjWindowCorner = (0.5f * lightData.size.x) * vx + (0.5f * lightData.size.y) * vy + 1.0f * vz;
                        cs = Vector3.Dot(vz, Vector3.Normalize(lightPosToProjWindowCorner));
                        si = Mathf.Sqrt(1.0f - cs * cs);
                    }

                    const float FltMax = 3.402823466e+38F;
                    var ta = cs > 0.0f ? (si / cs) : FltMax;
                    var cota = si > 0.0f ? (cs / si) : FltMax;

                    //const float cotasa = l.GetCotanHalfSpotAngle();

                    // apply nonuniform scale to OBB of spot light
                    var squeeze = true;//sa < 0.7f * 90.0f;      // arb heuristic
                    var fS = squeeze ? ta : si;
                    bound.center = worldToView.MultiplyPoint(lightPos + ((0.5f * range) * lightDir));    // use mid point of the spot as the center of the bounding volume for building screen-space AABB for tiled lighting.

                    // scale axis to match box or base of pyramid
                    bound.boxAxisX = (fS * range) * vx;
                    bound.boxAxisY = (fS * range) * vy;
                    bound.boxAxisZ = (0.5f * range) * vz;

                    // generate bounding sphere radius
                    var fAltDx = si;
                    var fAltDy = cs;
                    fAltDy = fAltDy - 0.5f;
                    //if(fAltDy<0) fAltDy=-fAltDy;

                    fAltDx *= range; fAltDy *= range;

                    // Handle case of pyramid with this select (currently unused)
                    var altDist = Mathf.Sqrt(fAltDy * fAltDy + (true ? 1.0f : 2.0f) * fAltDx * fAltDx);
                    bound.radius = altDist > (0.5f * range) ? altDist : (0.5f * range);       // will always pick fAltDist
                    bound.scaleXY = squeeze ? new Vector2(0.01f, 0.01f) : new Vector2(1.0f, 1.0f);

                    lightVolumeData.lightAxisX = vx;
                    lightVolumeData.lightAxisY = vy;
                    lightVolumeData.lightAxisZ = vz;
                    lightVolumeData.lightPos = worldToView.MultiplyPoint(lightPos);
                    lightVolumeData.radiusSq = range * range;
                    lightVolumeData.cotan = cota;
                    lightVolumeData.featureFlags = (gpuLightType == GPULightType.Spot) ? LightFeatureFlags.FEATURE_FLAG_LIGHT_PUNCTUAL
                                                                                       : LightFeatureFlags.FEATURE_FLAG_LIGHT_PROJECTOR;
                }
                else if (gpuLightType == GPULightType.Point)
                {
                    bool isNegDeterminant = Vector3.Dot(worldToView.GetColumn(0), Vector3.Cross(worldToView.GetColumn(1), worldToView.GetColumn(2))) < 0.0f; // 3x3 Determinant.

                    bound.center = worldToView.MultiplyPoint(lightPos);
                    bound.boxAxisX.Set(range, 0, 0);
                    bound.boxAxisY.Set(0, range, 0);
                    bound.boxAxisZ.Set(0, 0, isNegDeterminant ? (-range) : range);    // transform to camera space (becomes a left hand coordinate frame in Unity since Determinant(worldToView)<0)
                    bound.scaleXY.Set(1.0f, 1.0f);
                    bound.radius = range;

                    // represents a left hand coordinate system in world space since det(worldToView)<0
                    var lightToView = worldToView * lightToWorld;
                    Vector3 vx = lightToView.GetColumn(0);
                    Vector3 vy = lightToView.GetColumn(1);
                    Vector3 vz = lightToView.GetColumn(2);

                    // fill up ldata
                    lightVolumeData.lightAxisX = vx;
                    lightVolumeData.lightAxisY = vy;
                    lightVolumeData.lightAxisZ = vz;
                    lightVolumeData.lightPos = bound.center;
                    lightVolumeData.radiusSq = range * range;
                    lightVolumeData.featureFlags = LightFeatureFlags.FEATURE_FLAG_LIGHT_PUNCTUAL;
                }
                else if (gpuLightType == GPULightType.Rectangle)
                {
                    Vector3 centerVS = worldToView.MultiplyPoint(lightData.positionWS);
                    Vector3 xAxisVS = worldToView.MultiplyVector(lightData.right);
                    Vector3 yAxisVS = worldToView.MultiplyVector(lightData.up);
                    Vector3 zAxisVS = worldToView.MultiplyVector(lightData.forward);
                    float radius = 1.0f / Mathf.Sqrt(lightData.invSqrAttenuationRadius);

                    Vector3 dimensions = new Vector3(lightData.size.x * 0.5f + radius, lightData.size.y * 0.5f + radius, radius);

                    dimensions.z *= 0.5f;
                    centerVS += zAxisVS * radius * 0.5f;

                    bound.center = centerVS;
                    bound.boxAxisX = dimensions.x * xAxisVS;
                    bound.boxAxisY = dimensions.y * yAxisVS;
                    bound.boxAxisZ = dimensions.z * zAxisVS;
                    bound.scaleXY.Set(1.0f, 1.0f);
                    bound.radius = dimensions.magnitude;

                    lightVolumeData.lightPos = centerVS;
                    lightVolumeData.lightAxisX = xAxisVS;
                    lightVolumeData.lightAxisY = yAxisVS;
                    lightVolumeData.lightAxisZ = zAxisVS;
                    lightVolumeData.boxInnerDist = dimensions;
                    lightVolumeData.boxInvRange.Set(1e5f, 1e5f, 1e5f);
                    lightVolumeData.featureFlags = LightFeatureFlags.FEATURE_FLAG_LIGHT_AREA;
                }
                else if (gpuLightType == GPULightType.Line)
                {
                    Vector3 centerVS = worldToView.MultiplyPoint(lightData.positionWS);
                    Vector3 xAxisVS = worldToView.MultiplyVector(lightData.right);
                    Vector3 yAxisVS = worldToView.MultiplyVector(lightData.up);
                    Vector3 zAxisVS = worldToView.MultiplyVector(lightData.forward);
                    float radius = 1.0f / Mathf.Sqrt(lightData.invSqrAttenuationRadius);

                    Vector3 dimensions = new Vector3(lightData.size.x * 0.5f + radius, radius, radius);

                    bound.center = centerVS;
                    bound.boxAxisX = dimensions.x * xAxisVS;
                    bound.boxAxisY = dimensions.y * yAxisVS;
                    bound.boxAxisZ = dimensions.z * zAxisVS;
                    bound.scaleXY.Set(1.0f, 1.0f);
                    bound.radius = dimensions.magnitude;

                    lightVolumeData.lightPos = centerVS;
                    lightVolumeData.lightAxisX = xAxisVS;
                    lightVolumeData.lightAxisY = yAxisVS;
                    lightVolumeData.lightAxisZ = zAxisVS;
                    lightVolumeData.boxInnerDist = new Vector3(lightData.size.x * 0.5f, 0.01f, 0.01f);
                    lightVolumeData.boxInvRange.Set(1.0f / radius, 1.0f / radius, 1.0f / radius);
                    lightVolumeData.featureFlags = LightFeatureFlags.FEATURE_FLAG_LIGHT_AREA;
                }
                else if (gpuLightType == GPULightType.ProjectorOrtho)
                {
                    Vector3 posVS   = worldToView.MultiplyPoint(lightData.positionWS);
                    Vector3 xAxisVS = worldToView.MultiplyVector(lightData.right);
                    Vector3 yAxisVS = worldToView.MultiplyVector(lightData.up);
                    Vector3 zAxisVS = worldToView.MultiplyVector(lightData.forward);

                    // Projector lights point forwards (along Z). The projection window is aligned with the XY plane.
                    Vector3 boxDims  = new Vector3(lightData.size.x, lightData.size.y, 1000000.0f);
                    Vector3 halfDims = 0.5f * boxDims;

                    bound.center   = posVS;
                    bound.boxAxisX = halfDims.x * xAxisVS;                                                    // Should this be halved or not?
                    bound.boxAxisY = halfDims.y * yAxisVS;                                                    // Should this be halved or not?
                    bound.boxAxisZ = halfDims.z * zAxisVS;                                                    // Should this be halved or not?
                    bound.radius   = halfDims.magnitude;                                                      // Radius of a circumscribed sphere?
                    bound.scaleXY.Set(1.0f, 1.0f);

                    lightVolumeData.lightPos     = posVS;                                                     // Is this the center of the volume?
                    lightVolumeData.lightAxisX   = xAxisVS;
                    lightVolumeData.lightAxisY   = yAxisVS;
                    lightVolumeData.lightAxisZ   = zAxisVS;
                    lightVolumeData.boxInnerDist = halfDims;                                                  // No idea what this is. Document your code
                    lightVolumeData.boxInvRange.Set(1.0f / halfDims.x, 1.0f / halfDims.y, 1.0f / halfDims.z); // No idea what this is. Document your code
                    lightVolumeData.featureFlags = LightFeatureFlags.FEATURE_FLAG_LIGHT_PROJECTOR;
                }
                else
                {
                    Debug.Assert(false, "TODO: encountered an unknown GPULightType.");
                }

                m_lightList.bounds.Add(bound);
                m_lightList.lightVolumes.Add(lightVolumeData);
            }

            public void GetEnvLightData(VisibleReflectionProbe probe)
            {
                var envLightData = new EnvLightData();

                // CAUTION: localToWorld is the transform for the widget of the reflection probe. i.e the world position of the point use to do the cubemap capture (mean it include the local offset)
                envLightData.positionWS = probe.localToWorld.GetColumn(3);

                envLightData.envShapeType = EnvShapeType.None;

                // TODO: Support sphere influence in UI
                if (probe.boxProjection != 0)
                {
                    envLightData.envShapeType = EnvShapeType.Box;
                }

                // remove scale from the matrix (Scale in this matrix is use to scale the widget)
                envLightData.right = probe.localToWorld.GetColumn(0);
                envLightData.right.Normalize();
                envLightData.up = probe.localToWorld.GetColumn(1);
                envLightData.up.Normalize();
                envLightData.forward = probe.localToWorld.GetColumn(2);
                envLightData.forward.Normalize();

                // Artists prefer to have blend distance inside the volume!
                // So we let the current UI but we assume blendDistance is an inside factor instead
                // Blend distance can't be larger than the max radius
                // probe.bounds.extents is BoxSize / 2
                float maxBlendDist = Mathf.Min(probe.bounds.extents.x, Mathf.Min(probe.bounds.extents.y, probe.bounds.extents.z));
                float blendDistance = Mathf.Min(maxBlendDist, probe.blendDistance);
                envLightData.innerDistance = probe.bounds.extents - new Vector3(blendDistance, blendDistance, blendDistance);

                envLightData.envIndex = m_CubeReflTexArray.FetchSlice(probe.texture);

                envLightData.offsetLS = probe.center; // center is misnamed, it is the offset (in local space) from center of the bounding box to the cubemap capture point
                envLightData.blendDistance = blendDistance;

                m_lightList.envLights.Add(envLightData);
            }

            public void GetEnvLightVolumeDataAndBound(VisibleReflectionProbe probe, LightVolumeType lightVolumeType, Matrix4x4 worldToView)
            {
                var bound = new SFiniteLightBound();
                var lightVolumeData = new LightVolumeData();

                var bnds = probe.bounds;
                var boxOffset = probe.center;                  // reflection volume offset relative to cube map capture point
                var blendDistance = probe.blendDistance;

                var mat = probe.localToWorld;

                Vector3 vx = mat.GetColumn(0);
                Vector3 vy = mat.GetColumn(1);
                Vector3 vz = mat.GetColumn(2);
                Vector3 vw = mat.GetColumn(3);
                vx.Normalize(); // Scale shouldn't affect the probe or its bounds
                vy.Normalize();
                vz.Normalize();

                // C is reflection volume center in world space (NOT same as cube map capture point)
                var e = bnds.extents;       // 0.5f * Vector3.Max(-boxSizes[p], boxSizes[p]);
                var C = vx * boxOffset.x + vy * boxOffset.y + vz * boxOffset.z + vw;

                var combinedExtent = e + new Vector3(blendDistance, blendDistance, blendDistance);

                // transform to camera space (becomes a left hand coordinate frame in Unity since Determinant(worldToView)<0)
                vx = worldToView.MultiplyVector(vx);
                vy = worldToView.MultiplyVector(vy);
                vz = worldToView.MultiplyVector(vz);

                var Cw = worldToView.MultiplyPoint(C);

                bound.center = Cw;
                bound.boxAxisX = combinedExtent.x * vx;
                bound.boxAxisY = combinedExtent.y * vy;
                bound.boxAxisZ = combinedExtent.z * vz;
                bound.scaleXY.Set(1.0f, 1.0f);
                bound.radius = combinedExtent.magnitude;


                lightVolumeData.lightCategory = (uint)LightCategory.Env;
                lightVolumeData.lightVolume = (uint)lightVolumeType;
                lightVolumeData.featureFlags = LightFeatureFlags.FEATURE_FLAG_LIGHT_ENV;

                lightVolumeData.lightPos = Cw;
                lightVolumeData.lightAxisX = vx;
                lightVolumeData.lightAxisY = vy;
                lightVolumeData.lightAxisZ = vz;
                var delta = combinedExtent - e;
                lightVolumeData.boxInnerDist = e;
                lightVolumeData.boxInvRange.Set(1.0f / delta.x, 1.0f / delta.y, 1.0f / delta.z);

                m_lightList.bounds.Add(bound);
                m_lightList.lightVolumes.Add(lightVolumeData);
            }

            public override void PrepareLightsForGPU(ShadowSettings shadowSettings, CullResults cullResults, Camera camera, ref ShadowOutput shadowOutput)
            {
                m_lightList.Clear();

                if (cullResults.visibleLights.Length != 0 || cullResults.visibleReflectionProbes.Length != 0)
                {
#if (SHADOWS_ENABLED)
                    // 0. deal with shadows
                    {
                        m_FrameId.frameCount++;
                        // get the indices for all lights that want to have shadows
                        m_ShadowRequests.Clear();
                        m_ShadowRequests.Capacity = cullResults.visibleLights.Length;
                        int lcnt = cullResults.visibleLights.Length;
                        for (int i = 0; i < lcnt; ++i)
                        {
                            if (cullResults.visibleLights[i].light.shadows != LightShadows.None)
                                m_ShadowRequests.Add(i);
                        }
                        // pass this list to a routine that assigns shadows based on some heuristic
                        uint    shadowRequestCount = (uint)m_ShadowRequests.Count;
                        int[]   shadowRequests = m_ShadowRequests.ToArray();
                        int[]   shadowDataIndices;
                        uint    originalRequestCount = shadowRequestCount;
                        m_ShadowMgr.ProcessShadowRequests(m_FrameId, cullResults, camera, cullResults.visibleLights,
                            ref shadowRequestCount, shadowRequests, out shadowDataIndices);

                        // update the visibleLights with the shadow information
                        m_ShadowIndices.Clear();
                        for (uint i = 0; i < shadowRequestCount; i++)
                        {
                            m_ShadowIndices.Add(shadowRequests[i], shadowDataIndices[i]);
                        }
                    }
#endif
                    float oldSpecularGlobalDimmer = m_PassSettings.specularGlobalDimmer;
                    // Change some parameters in case of "special" rendering (can be preview, reflection, etc.
                    if (camera.cameraType == CameraType.Reflection)
                    {
                        m_PassSettings.specularGlobalDimmer = 0.0f;
                    }

                    // 1. Count the number of lights and sort all light by category, type and volume
                    int directionalLightcount = 0;
                    int punctualLightcount = 0;
                    int areaLightCount = 0;
                    int projectorLightCount = 0;

                    int lightCount = Math.Min(cullResults.visibleLights.Length, k_MaxLightsOnScreen);
                    var sortKeys = new uint[lightCount];
                    int sortCount = 0;

                    for (int lightIndex = 0, numLights = cullResults.visibleLights.Length; (lightIndex < numLights) && (sortCount < lightCount); ++lightIndex)
                    {
                        var light = cullResults.visibleLights[lightIndex];

                        // We only process light with additional data
                        var additionalData = light.light.GetComponent<AdditionalLightData>();

                        if (additionalData == null)
                            additionalData = DefaultAdditionalLightData;

                        LightCategory lightCategory = LightCategory.Count;
                        GPULightType gpuLightType = GPULightType.Point;
                        LightVolumeType lightVolumeType = LightVolumeType.Count;

                    // Note: LightType.Area is offline only, use for baking, no need to test it
                    if (additionalData.archetype == LightArchetype.Punctual)
                    {
                        switch (light.lightType)
                        {
                            case LightType.Point:
                                if (punctualLightcount >= k_MaxPunctualLightsOnScreen)
                                    continue;
                                lightCategory = LightCategory.Punctual;
                                gpuLightType = GPULightType.Point;
                                lightVolumeType = LightVolumeType.Sphere;
                                break;

                            case LightType.Spot:
                                if (punctualLightcount >= k_MaxPunctualLightsOnScreen)
                                    continue;
                                lightCategory = LightCategory.Punctual;
                                gpuLightType = GPULightType.Spot;
                                lightVolumeType = LightVolumeType.Cone;
                                break;

                            case LightType.Directional:
                                if (directionalLightcount >= k_MaxDirectionalLightsOnScreen)
                                    continue;
                                lightCategory = LightCategory.Punctual;
                                gpuLightType = GPULightType.Directional;
                                // No need to add volume, always visible
                                lightVolumeType = LightVolumeType.Count; // Count is none
                                break;

                            default:
                                Debug.Assert(false, "TODO: encountered an unknown LightType.");
                                break;
                        }
                    }
                    else
                    {
                        switch (additionalData.archetype)
                        {
                            case LightArchetype.Area:
                                if (areaLightCount >= k_MaxAreaLightsOnScreen) { continue; }
                                lightCategory   = LightCategory.Area;
                                gpuLightType    = (additionalData.lightWidth > 0) ? GPULightType.Rectangle : GPULightType.Line;
                                lightVolumeType = LightVolumeType.Box;
                                break;
                            case LightArchetype.Projector:
                                if (projectorLightCount >= k_MaxProjectorLightsOnScreen) { continue; }
                                lightCategory = LightCategory.Projector;
                                switch (light.lightType)
                                {
                                    case LightType.Directional:
                                        gpuLightType    = GPULightType.ProjectorOrtho;
                                        lightVolumeType = LightVolumeType.Box;
                                        break;
                                    case LightType.Spot:
                                        gpuLightType    = GPULightType.ProjectorPyramid;
                                        lightVolumeType = LightVolumeType.Cone;
                                        break;
                                    default:
                                        Debug.Assert(false, "Projectors can only be Spot or Directional lights.");
                                        break;
                                }
                                break;
                            default:
                                Debug.Assert(false, "TODO: encountered an unknown LightArchetype.");
                                break;
                        }
                    }

#if (SHADOWS_ENABLED)
                    uint shadow = m_ShadowIndices.ContainsKey(lightIndex) ? 1u : 0;
                    // 5 bit (0x1F) light category, 5 bit (0x1F) GPULightType, 5 bit (0x1F) lightVolume, 1 bit for shadow casting, 16 bit index
                    sortKeys[sortCount++] = (uint)lightCategory << 27 | (uint)gpuLightType << 22 | (uint)lightVolumeType << 17 | shadow << 16 | (uint)lightIndex;
#else
                        // 5 bit (0x1F) light category, 5 bit (0x1F) GPULightType, 6 bit (0x3F) lightVolume, 16 bit index
                        sortKeys[sortCount++] = (uint)lightCategory << 27 | (uint)gpuLightType << 22 | (uint)lightVolumeType << 16 | (uint)lightIndex;
#endif
                    }

                    Array.Sort(sortKeys);

                    // TODO: Refactor shadow management
                    // The good way of managing shadow:
                    // Here we sort everyone and we decide which light is important or not (this is the responsibility of the lightloop)
                    // we allocate shadow slot based on maximum shadow allowed on screen and attribute slot by bigger solid angle
                    // THEN we ask to the ShadowRender to render the shadow, not the reverse as it is today (i.e render shadow than expect they
                    // will be use...)
                    // The lightLoop is in charge, not the shadow pass.
                    // For now we will still apply the maximum of shadow here but we don't apply the sorting by priority + slot allocation yet
                    int directionalShadowcount = 0;
                    int shadowCount = 0;

                    // 2. Go thought all lights, convert them to GPU format.
                    // Create simultaneously data for culling (LigthVolumeData and rendering)
                    var worldToView = WorldToCamera(camera);

                    for (int sortIndex = 0; sortIndex < sortCount; ++sortIndex)
                    {
                        // In 1. we have already classify and sorted the light, we need to use this sorted order here
                        uint sortKey = sortKeys[sortIndex];
                        LightCategory lightCategory = (LightCategory)((sortKey >> 27) & 0x1F);
                        GPULightType gpuLightType = (GPULightType)((sortKey >> 22) & 0x1F);
#if (SHADOWS_ENABLED)
                        LightVolumeType lightVolumeType = (LightVolumeType)((sortKey >> 17) & 0x1F);
#else
                        LightVolumeType lightVolumeType = (LightVolumeType)((sortKey >> 16) & 0x3F);
#endif
                        int lightIndex = (int)(sortKey & 0xFFFF);

                        var light = cullResults.visibleLights[lightIndex];
                        var additionalData = light.light.GetComponent<AdditionalLightData>() ?? DefaultAdditionalLightData;

                        // Directional rendering side, it is separated as it is always visible so no volume to handle here
                        if (gpuLightType == GPULightType.Directional)
                        {
                            if (GetDirectionalLightData(shadowSettings, gpuLightType, light, additionalData, lightIndex, ref shadowOutput, ref directionalShadowcount))
                                directionalLightcount++;

#if (SHADOWS_ENABLED && SHADOWS_FIXSHADOWIDX)
                            // fix up shadow information
                            int shadowIdxDir;
                            if (m_ShadowIndices.TryGetValue(lightIndex, out shadowIdxDir))
                            {
                                var lightData = m_lightList.directionalLights[m_lightList.directionalLights.Count - 1];
                                lightData.shadowIndex = shadowIdxDir;
                                m_lightList.directionalLights[m_lightList.directionalLights.Count - 1] = lightData;
                            }
#endif
                            continue;
                        }

                        // Punctual, area, projector lights - the rendering side.
                        if (GetLightData(shadowSettings, camera, gpuLightType, light, additionalData, lightIndex, ref shadowOutput, ref shadowCount))
                        {
                            switch (lightCategory)
                            {
                                case LightCategory.Punctual:
                                    punctualLightcount++;
                                    break;
                                case LightCategory.Area:
                                    areaLightCount++;
                                    break;
                                case LightCategory.Projector:
                                    projectorLightCount++;
                                    break;
                                default:
                                    Debug.Assert(false, "TODO: encountered an unknown LightCategory.");
                                    break;
                            }

                            // Then culling side. Must be call in this order as we pass the created Light data to the function
                            GetLightVolumeDataAndBound(lightCategory, gpuLightType, lightVolumeType, light, m_lightList.lights[m_lightList.lights.Count - 1], worldToView);
                        }

#if (SHADOWS_ENABLED && SHADOWS_FIXSHADOWIDX)
                        // fix up shadow information
                        int shadowIdx;
                        if (m_ShadowIndices.TryGetValue(lightIndex, out shadowIdx))
                        {
                            var lightData = m_lightList.lights[m_lightList.lights.Count - 1];
                            lightData.shadowIndex = shadowIdx;
                            m_lightList.lights[m_lightList.lights.Count - 1] = lightData;
                        }
#endif
                    }

                    // Sanity check
                    Debug.Assert(m_lightList.directionalLights.Count == directionalLightcount);
                    Debug.Assert(m_lightList.lights.Count == areaLightCount + punctualLightcount + projectorLightCount);

                    m_punctualLightCount  = punctualLightcount;
                    m_areaLightCount      = areaLightCount;
                    m_projectorLightCount = projectorLightCount;

                    // Redo everything but this time with envLights
                    int envLightCount = 0;

                    int probeCount = Math.Min(cullResults.visibleReflectionProbes.Length, k_MaxEnvLightsOnScreen);
                    sortKeys = new uint[probeCount];
                    sortCount = 0;

                    for (int probeIndex = 0, numProbes = cullResults.visibleReflectionProbes.Length; (probeIndex < numProbes) && (sortCount < probeCount); probeIndex++)
                    {
                        var probe = cullResults.visibleReflectionProbes[probeIndex];

                        // probe.texture can be null when we are adding a reflection probe in the editor
                        if (probe.texture == null || envLightCount >= k_MaxEnvLightsOnScreen)
                            continue;

                        // TODO: Support LightVolumeType.Sphere, currently in UI there is no way to specify a sphere influence volume
                        LightVolumeType lightVolumeType = probe.boxProjection != 0 ? LightVolumeType.Box : LightVolumeType.Box;
                        ++envLightCount;

                        // 16 bit lightVolume, 16 bit index
                        sortKeys[sortCount++] = (uint)lightVolumeType << 16 | (uint)probeIndex;
                    }

                    // Not necessary yet but call it for future modification with sphere influence volume
                    Array.Sort(sortKeys);

                    for (int sortIndex = 0; sortIndex < sortCount; ++sortIndex)
                    {
                        // In 1. we have already classify and sorted the light, we need to use this sorted order here
                        uint sortKey = sortKeys[sortIndex];
                        LightVolumeType lightVolumeType = (LightVolumeType)((sortKey >> 16) & 0xFFFF);
                        int probeIndex = (int)(sortKey & 0xFFFF);

                        VisibleReflectionProbe probe = cullResults.visibleReflectionProbes[probeIndex];

                        GetEnvLightData(probe);

                        GetEnvLightVolumeDataAndBound(probe, lightVolumeType, worldToView);
                    }

                    // Sanity check
                    Debug.Assert(m_lightList.envLights.Count == envLightCount);

                    // Restore values after "special rendering"
                    m_PassSettings.specularGlobalDimmer = oldSpecularGlobalDimmer;
                }

                m_lightCount = m_lightList.lights.Count + m_lightList.envLights.Count;
                Debug.Assert(m_lightList.bounds.Count == m_lightCount);
                Debug.Assert(m_lightList.lightVolumes.Count == m_lightCount);

                UpdateDataBuffers();
            }

            void VoxelLightListGeneration(CommandBuffer cmd, Camera camera, Matrix4x4 projscr, Matrix4x4 invProjscr, RenderTargetIdentifier cameraDepthBufferRT)
            {
                // clear atomic offset index
                cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_ClearVoxelAtomicKernel, "g_LayeredSingleIdxBuffer", s_GlobalLightListAtomic);
                cmd.DispatchCompute(buildPerVoxelLightListShader, s_ClearVoxelAtomicKernel, 1, 1, 1);

                cmd.SetComputeIntParam(buildPerVoxelLightListShader, "_EnvLightIndexShift", m_lightList.lights.Count);
                cmd.SetComputeIntParam(buildPerVoxelLightListShader, "g_iNrVisibLights", m_lightCount);
                Utilities.SetMatrixCS(cmd, buildPerVoxelLightListShader, "g_mScrProjection", projscr);
                Utilities.SetMatrixCS(cmd, buildPerVoxelLightListShader, "g_mInvScrProjection", invProjscr);

                cmd.SetComputeIntParam(buildPerVoxelLightListShader, "g_iLog2NumClusters", k_Log2NumClusters);

                //Vector4 v2_near = invProjscr * new Vector4(0.0f, 0.0f, 0.0f, 1.0f);
                //Vector4 v2_far = invProjscr * new Vector4(0.0f, 0.0f, 1.0f, 1.0f);
                //float nearPlane2 = -(v2_near.z/v2_near.w);
                //float farPlane2 = -(v2_far.z/v2_far.w);
                var nearPlane = camera.nearClipPlane;
                var farPlane = camera.farClipPlane;
                cmd.SetComputeFloatParam(buildPerVoxelLightListShader, "g_fNearPlane", nearPlane);
                cmd.SetComputeFloatParam(buildPerVoxelLightListShader, "g_fFarPlane", farPlane);

                const float C = (float)(1 << k_Log2NumClusters);
                var geomSeries = (1.0 - Mathf.Pow(k_ClustLogBase, C)) / (1 - k_ClustLogBase);        // geometric series: sum_k=0^{C-1} base^k
                m_ClustScale = (float)(geomSeries / (farPlane - nearPlane));

                cmd.SetComputeFloatParam(buildPerVoxelLightListShader, "g_fClustScale", m_ClustScale);
                cmd.SetComputeFloatParam(buildPerVoxelLightListShader, "g_fClustBase", k_ClustLogBase);

                cmd.SetComputeTextureParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "g_depth_tex", cameraDepthBufferRT);
                cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "g_vLayeredLightList", s_PerVoxelLightLists);
                cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "g_LayeredOffset", s_PerVoxelOffset);
                cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "g_LayeredSingleIdxBuffer", s_GlobalLightListAtomic);
                if (m_PassSettings.enableBigTilePrepass)
                    cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "g_vBigTileLightList", s_BigTileLightList);

                if (k_UseDepthBuffer)
                {
                    cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "g_logBaseBuffer", s_PerTileLogBaseTweak);
                }

                cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "g_vBoundsBuffer", s_AABBBoundsBuffer);
                cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "_LightVolumeData", s_LightVolumeDataBuffer);
                cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "g_data", s_ConvexBoundsBuffer);

                var numTilesX = GetNumTileClusteredX(camera);
                var numTilesY = GetNumTileClusteredY(camera);
                cmd.DispatchCompute(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, numTilesX, numTilesY, 1);
            }

            public override void BuildGPULightLists(Camera camera, ScriptableRenderContext loop, RenderTargetIdentifier cameraDepthBufferRT)
            {
                var w = camera.pixelWidth;
                var h = camera.pixelHeight;
                var numBigTilesX = (w + 63) / 64;
                var numBigTilesY = (h + 63) / 64;

                // camera to screen matrix (and it's inverse)
                var proj = CameraProjection(camera);
                var temp = new Matrix4x4();
                temp.SetRow(0, new Vector4(0.5f * w, 0.0f, 0.0f, 0.5f * w));
                temp.SetRow(1, new Vector4(0.0f, 0.5f * h, 0.0f, 0.5f * h));
                temp.SetRow(2, new Vector4(0.0f, 0.0f, 0.5f, 0.5f));
                temp.SetRow(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
                var projscr = temp * proj;
                var invProjscr = projscr.inverse;

                var cmd = new CommandBuffer() { name = "" };
                cmd.SetRenderTarget(new RenderTargetIdentifier((Texture)null));

                // generate screen-space AABBs (used for both fptl and clustered).
                if (m_lightCount != 0)
                {
                    temp.SetRow(0, new Vector4(1.0f, 0.0f, 0.0f, 0.0f));
                    temp.SetRow(1, new Vector4(0.0f, 1.0f, 0.0f, 0.0f));
                    temp.SetRow(2, new Vector4(0.0f, 0.0f, 0.5f, 0.5f));
                    temp.SetRow(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
                    var projh = temp * proj;
                    var invProjh = projh.inverse;

                    cmd.SetComputeIntParam(buildScreenAABBShader, "g_iNrVisibLights", m_lightCount);
                    cmd.SetComputeBufferParam(buildScreenAABBShader, s_GenAABBKernel, "g_data", s_ConvexBoundsBuffer);

                    Utilities.SetMatrixCS(cmd, buildScreenAABBShader, "g_mProjection", projh);
                    Utilities.SetMatrixCS(cmd, buildScreenAABBShader, "g_mInvProjection", invProjh);
                    cmd.SetComputeBufferParam(buildScreenAABBShader, s_GenAABBKernel, "g_vBoundsBuffer", s_AABBBoundsBuffer);
                    cmd.DispatchCompute(buildScreenAABBShader, s_GenAABBKernel, (m_lightCount + 7) / 8, 1, 1);
                }

                // enable coarse 2D pass on 64x64 tiles (used for both fptl and clustered).
                if (m_PassSettings.enableBigTilePrepass)
                {
                    cmd.SetComputeIntParams(buildPerBigTileLightListShader, "g_viDimensions", new int[2] { w, h });
                    cmd.SetComputeIntParam(buildPerBigTileLightListShader, "_EnvLightIndexShift", m_lightList.lights.Count);
                    cmd.SetComputeIntParam(buildPerBigTileLightListShader, "g_iNrVisibLights", m_lightCount);
                    Utilities.SetMatrixCS(cmd, buildPerBigTileLightListShader, "g_mScrProjection", projscr);
                    Utilities.SetMatrixCS(cmd, buildPerBigTileLightListShader, "g_mInvScrProjection", invProjscr);
                    cmd.SetComputeFloatParam(buildPerBigTileLightListShader, "g_fNearPlane", camera.nearClipPlane);
                    cmd.SetComputeFloatParam(buildPerBigTileLightListShader, "g_fFarPlane", camera.farClipPlane);
                    cmd.SetComputeBufferParam(buildPerBigTileLightListShader, s_GenListPerBigTileKernel, "g_vLightList", s_BigTileLightList);
                    cmd.SetComputeBufferParam(buildPerBigTileLightListShader, s_GenListPerBigTileKernel, "g_vBoundsBuffer", s_AABBBoundsBuffer);
                    cmd.SetComputeBufferParam(buildPerBigTileLightListShader, s_GenListPerBigTileKernel, "_LightVolumeData", s_LightVolumeDataBuffer);
                    cmd.SetComputeBufferParam(buildPerBigTileLightListShader, s_GenListPerBigTileKernel, "g_data", s_ConvexBoundsBuffer);
                    cmd.DispatchCompute(buildPerBigTileLightListShader, s_GenListPerBigTileKernel, numBigTilesX, numBigTilesY, 1);
                }

                bool enableFeatureVariants = GetFeatureVariantsEnabled();
                if (enableFeatureVariants)
                {
                    cmd.SetComputeBufferParam(clearDispatchIndirectShader, s_ClearDispatchIndirectKernel, "g_DispatchIndirectBuffer", s_DispatchIndirectBuffer);
                    cmd.DispatchCompute(clearDispatchIndirectShader, s_ClearDispatchIndirectKernel, 1, 1, 1);
                }

                if (usingFptl)       // optimized for opaques only
                {
                    cmd.SetComputeIntParams(buildPerTileLightListShader, "g_viDimensions", new int[2] { w, h });
                    cmd.SetComputeIntParam(buildPerTileLightListShader, "_EnvLightIndexShift", m_lightList.lights.Count);
                    cmd.SetComputeIntParam(buildPerTileLightListShader, "g_iNrVisibLights", m_lightCount);

                    cmd.SetComputeBufferParam(buildPerTileLightListShader, s_GenListPerTileKernel, "g_vBoundsBuffer", s_AABBBoundsBuffer);
                    cmd.SetComputeBufferParam(buildPerTileLightListShader, s_GenListPerTileKernel, "_LightVolumeData", s_LightVolumeDataBuffer);
                    cmd.SetComputeBufferParam(buildPerTileLightListShader, s_GenListPerTileKernel, "g_data", s_ConvexBoundsBuffer);

                    Utilities.SetMatrixCS(cmd, buildPerTileLightListShader, "g_mScrProjection", projscr);
                    Utilities.SetMatrixCS(cmd, buildPerTileLightListShader, "g_mInvScrProjection", invProjscr);
                    cmd.SetComputeTextureParam(buildPerTileLightListShader, s_GenListPerTileKernel, "g_depth_tex", cameraDepthBufferRT);
                    cmd.SetComputeBufferParam(buildPerTileLightListShader, s_GenListPerTileKernel, "g_vLightList", s_LightList);
                    if (m_PassSettings.enableBigTilePrepass)
                        cmd.SetComputeBufferParam(buildPerTileLightListShader, s_GenListPerTileKernel, "g_vBigTileLightList", s_BigTileLightList);


                    cmd.SetComputeTextureParam(buildPerTileLightListShader, s_GenListPerTileKernel, "_GBufferTexture0", Shader.PropertyToID("_GBufferTexture0"));
                    cmd.SetComputeTextureParam(buildPerTileLightListShader, s_GenListPerTileKernel, "_GBufferTexture1", Shader.PropertyToID("_GBufferTexture1"));
                    cmd.SetComputeTextureParam(buildPerTileLightListShader, s_GenListPerTileKernel, "_GBufferTexture2", Shader.PropertyToID("_GBufferTexture2"));
                    cmd.SetComputeTextureParam(buildPerTileLightListShader, s_GenListPerTileKernel, "_GBufferTexture3", Shader.PropertyToID("_GBufferTexture3"));

                    if (enableFeatureVariants)
                    {
                        uint baseFeatureFlags = 0;
                        if (m_lightList.directionalLights.Count > 0)
                        {
                            baseFeatureFlags |= LightFeatureFlags.FEATURE_FLAG_LIGHT_DIRECTIONAL;
                        }
                        if (Shader.GetGlobalInt("_EnvLightSkyEnabled") != 0)
                        {
                            baseFeatureFlags |= LightFeatureFlags.FEATURE_FLAG_LIGHT_SKY;
                        }
                        cmd.SetComputeBufferParam(buildPerTileLightListShader, s_GenListPerTileKernel, "g_DispatchIndirectBuffer", s_DispatchIndirectBuffer);
                        cmd.SetComputeBufferParam(buildPerTileLightListShader, s_GenListPerTileKernel, "g_TileList", s_TileList);
                        cmd.SetComputeIntParam(buildPerTileLightListShader, "g_BaseFeatureFlags", (int)baseFeatureFlags);
                    }

                    var numTilesX = GetNumTileFtplX(camera);
                    var numTilesY = GetNumTileFtplY(camera);
                    cmd.DispatchCompute(buildPerTileLightListShader, s_GenListPerTileKernel, numTilesX, numTilesY, 1);
                }

                if (m_PassSettings.enableClustered)        // works for transparencies too.
                {
                    VoxelLightListGeneration(cmd, camera, projscr, invProjscr, cameraDepthBufferRT);
                }

                loop.ExecuteCommandBuffer(cmd);
                cmd.Dispose();
            }

            // This is a workaround for global properties not being accessible from compute.
            // When activeComputeShader is set, all calls to SetGlobalXXX will set the property on the select compute shader instead of the global scope.
            private ComputeShader activeComputeShader;
            private int activeComputeKernel;
            private CommandBuffer activeCommandBuffer;
            private void SetGlobalPropertyRedirect(ComputeShader computeShader, int computeKernel, CommandBuffer commandBuffer)
            {
                activeComputeShader = computeShader;
                activeComputeKernel = computeKernel;
                activeCommandBuffer = commandBuffer;
            }

            private void SetGlobalTexture(string name, Texture value)
            {
                if (activeComputeShader)
                    activeCommandBuffer.SetComputeTextureParam(activeComputeShader, activeComputeKernel, name, value);
                else
                    activeCommandBuffer.SetGlobalTexture(name, value);
            }

            private void SetGlobalBuffer(string name, ComputeBuffer buffer)
            {
                if (activeComputeShader)
                    activeCommandBuffer.SetComputeBufferParam(activeComputeShader, activeComputeKernel, name, buffer);
                else
                    activeCommandBuffer.SetGlobalBuffer(name, buffer);
            }

            private void SetGlobalInt(string name, int value)
            {
                if (activeComputeShader)
                    activeCommandBuffer.SetComputeIntParam(activeComputeShader, name, value);
                else
                    Shader.SetGlobalInt(name, value);
            }

            private void SetGlobalFloat(string name, float value)
            {
                if (activeComputeShader)
                    activeCommandBuffer.SetComputeFloatParam(activeComputeShader, name, value);
                else
                    activeCommandBuffer.SetGlobalFloat(name, value);
            }

            private void SetGlobalVector(string name, Vector4 value)
            {
                if (activeComputeShader)
                    activeCommandBuffer.SetComputeVectorParam(activeComputeShader, name, value);
                else
                    activeCommandBuffer.SetGlobalVector(name, value);
            }

            private void SetGlobalVectorArray(string name, Vector4[] values)
            {
                if (activeComputeShader)
                {
                    int numVectors = values.Length;
                    var data = new float[numVectors * 4];

                    for (int n = 0; n < numVectors; n++)
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            data[4 * n + i] = values[n][i];
                        }
                    }

                    activeCommandBuffer.SetComputeFloatParams(activeComputeShader, name, data);
                }
                else
                {
                    activeCommandBuffer.SetGlobalVectorArray(name, values);
                }
            }

            private void UpdateDataBuffers()
            {
                s_DirectionalLightDatas.SetData(m_lightList.directionalLights.ToArray());
                s_LightDatas.SetData(m_lightList.lights.ToArray());
                s_EnvLightDatas.SetData(m_lightList.envLights.ToArray());
                s_shadowDatas.SetData(m_lightList.shadows.ToArray());

                // These two buffers have been set in Rebuild()
                s_ConvexBoundsBuffer.SetData(m_lightList.bounds.ToArray());
                s_LightVolumeDataBuffer.SetData(m_lightList.lightVolumes.ToArray());
            }

            private void BindGlobalParams(CommandBuffer cmd, ComputeShader computeShader, int kernelIndex, Camera camera, ScriptableRenderContext loop)
            {
#if (SHADOWS_ENABLED)
                m_ShadowMgr.BindResources(loop);
#endif
                SetGlobalBuffer("g_vLightListGlobal", !usingFptl ? s_PerVoxelLightLists : s_LightList);       // opaques list (unless MSAA possibly)

                SetGlobalTexture("_CookieTextures", m_CookieTexArray.GetTexCache());
                SetGlobalTexture("_CookieCubeTextures", m_CubeCookieTexArray.GetTexCache());
                SetGlobalTexture("_EnvTextures", m_CubeReflTexArray.GetTexCache());

                SetGlobalBuffer("_DirectionalLightDatas", s_DirectionalLightDatas);
                SetGlobalInt("_DirectionalLightCount", m_lightList.directionalLights.Count);
                SetGlobalBuffer("_LightDatas", s_LightDatas);
                SetGlobalInt("_PunctualLightCount", m_punctualLightCount);
                SetGlobalInt("_AreaLightCount", m_areaLightCount);
                SetGlobalInt("_ProjectorLightCount", m_projectorLightCount);
                SetGlobalBuffer("_EnvLightDatas", s_EnvLightDatas);
                SetGlobalInt("_EnvLightCount", m_lightList.envLights.Count);
                SetGlobalBuffer("_ShadowDatas", s_shadowDatas);
                SetGlobalVectorArray("_DirShadowSplitSpheres", m_lightList.directionalShadowSplitSphereSqr);

                SetGlobalInt("_NumTileFtplX", GetNumTileFtplX(camera));
                SetGlobalInt("_NumTileFtplY", GetNumTileFtplY(camera));

                SetGlobalInt("_NumTileClusteredX", GetNumTileClusteredX(camera));
                SetGlobalInt("_NumTileClusteredY", GetNumTileClusteredY(camera));

                if (m_PassSettings.enableBigTilePrepass)
                    SetGlobalBuffer("g_vBigTileLightList", s_BigTileLightList);

                if (m_PassSettings.enableClustered)
                {
                    SetGlobalFloat("g_fClustScale", m_ClustScale);
                    SetGlobalFloat("g_fClustBase", k_ClustLogBase);
                    SetGlobalFloat("g_fNearPlane", camera.nearClipPlane);
                    SetGlobalFloat("g_fFarPlane", camera.farClipPlane);
                    SetGlobalFloat("g_iLog2NumClusters", k_Log2NumClusters);

                    SetGlobalFloat("g_isLogBaseBufferEnabled", k_UseDepthBuffer ? 1 : 0);

                    SetGlobalBuffer("g_vLayeredOffsetsBuffer", s_PerVoxelOffset);
                    if (k_UseDepthBuffer)
                    {
                        SetGlobalBuffer("g_logBaseBuffer", s_PerTileLogBaseTweak);
                    }
                }
            }

            private void PushGlobalParams(Camera camera, ScriptableRenderContext loop, ComputeShader computeShader, int kernelIndex)
            {
                var cmd = new CommandBuffer { name = "Push Global Parameters" };

#if (SHADOWS_ENABLED)
                // Shadows
                m_ShadowMgr.SyncData();
#endif
                SetGlobalPropertyRedirect(computeShader, kernelIndex, cmd);
                BindGlobalParams(cmd, computeShader, kernelIndex, camera, loop);
                SetGlobalPropertyRedirect(null, 0, null);

                loop.ExecuteCommandBuffer(cmd);
                cmd.Dispose();
            }

#if UNITY_EDITOR
            private Vector2 m_mousePosition = Vector2.zero;

            private void OnSceneGUI(UnityEditor.SceneView sceneview)
            {
                m_mousePosition = Event.current.mousePosition;
            }

#endif

            public override void RenderShadows(ScriptableRenderContext renderContext, CullResults cullResults)
            {
#if (SHADOWS_ENABLED)
                // kick off the shadow jobs here
                m_ShadowMgr.RenderShadows(m_FrameId, renderContext, cullResults, cullResults.visibleLights);
#endif
            }

            private void SetupDebugDisplayMode(bool debugDisplayEnable)
            {
                Utilities.SetKeyword(m_DeferredDirectMaterialSRT, "DEBUG_DISPLAY", debugDisplayEnable);
                Utilities.SetKeyword(m_DeferredDirectMaterialMRT, "DEBUG_DISPLAY", debugDisplayEnable);
                Utilities.SetKeyword(m_DeferredIndirectMaterialSRT, "DEBUG_DISPLAY", debugDisplayEnable);
                Utilities.SetKeyword(m_DeferredIndirectMaterialMRT, "DEBUG_DISPLAY", debugDisplayEnable);
                Utilities.SetKeyword(m_DeferredAllMaterialSRT, "DEBUG_DISPLAY", debugDisplayEnable);
                Utilities.SetKeyword(m_DeferredAllMaterialMRT, "DEBUG_DISPLAY", debugDisplayEnable);
                Utilities.SetKeyword(m_SingleDeferredMaterialSRT, "DEBUG_DISPLAY", debugDisplayEnable);
                Utilities.SetKeyword(m_SingleDeferredMaterialMRT, "DEBUG_DISPLAY", debugDisplayEnable);
            }

            public override void RenderLightingDebug(HDCamera hdCamera, ScriptableRenderContext renderContext, RenderTargetIdentifier colorBuffer)
            {
                if (m_PassSettings.tileDebugByCategory == TileLightLoopProducer.TileSettings.TileDebug.None)
                    return;

                var cmd = new CommandBuffer();
                cmd.name = "Tiled Lighting Debug";

                bool bUseClusteredForDeferred = !usingFptl;

                int w = hdCamera.camera.pixelWidth;
                int h = hdCamera.camera.pixelHeight;
                int numTilesX = (w + 15) / 16;
                int numTilesY = (h + 15) / 16;
                int numTiles = numTilesX * numTilesY;

                Vector2 mousePixelCoord = Input.mousePosition;
#if UNITY_EDITOR
                if (!UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    mousePixelCoord = m_mousePosition;
                    mousePixelCoord.y = (hdCamera.screenSize.y - 1.0f) - mousePixelCoord.y;
                }
#endif

                // Debug tiles
                PushGlobalParams(hdCamera.camera, renderContext, null, 0);
                if (m_PassSettings.tileDebugByCategory == TileLightLoopProducer.TileSettings.TileDebug.FeatureVariants)
                {
                    if (GetFeatureVariantsEnabled())
                    {
                        // featureVariants
                        Utilities.SetupMaterialHDCamera(hdCamera, m_DebugViewTilesMaterial);
                        m_DebugViewTilesMaterial.SetInt("_NumTiles", numTiles);
                        m_DebugViewTilesMaterial.SetInt("_ViewTilesFlags", (int)m_PassSettings.tileDebugByCategory);
                        m_DebugViewTilesMaterial.SetVector("_MousePixelCoord", mousePixelCoord);
                        m_DebugViewTilesMaterial.SetBuffer("g_TileList", s_TileList);
                        m_DebugViewTilesMaterial.SetBuffer("g_DispatchIndirectBuffer", s_DispatchIndirectBuffer);
                        m_DebugViewTilesMaterial.EnableKeyword(bUseClusteredForDeferred ? "USE_CLUSTERED_LIGHTLIST" : "USE_FPTL_LIGHTLIST");
                        m_DebugViewTilesMaterial.DisableKeyword(!bUseClusteredForDeferred ? "USE_CLUSTERED_LIGHTLIST" : "USE_FPTL_LIGHTLIST");
                        m_DebugViewTilesMaterial.DisableKeyword("SHOW_LIGHT_CATEGORIES");
                        m_DebugViewTilesMaterial.EnableKeyword("SHOW_FEATURE_VARIANTS");
                        cmd.SetRenderTarget(colorBuffer);
                        cmd.DrawProcedural(Matrix4x4.identity, m_DebugViewTilesMaterial, 0, MeshTopology.Triangles, numTiles * 6);
                    }
                }
                else if (m_PassSettings.tileDebugByCategory != TileLightLoopProducer.TileSettings.TileDebug.None)
                {
                    // lightCategories
                    Utilities.SetupMaterialHDCamera(hdCamera, m_DebugViewTilesMaterial);
                    m_DebugViewTilesMaterial.SetInt("_ViewTilesFlags", (int)m_PassSettings.tileDebugByCategory);
                    m_DebugViewTilesMaterial.SetVector("_MousePixelCoord", mousePixelCoord);
                    m_DebugViewTilesMaterial.EnableKeyword(bUseClusteredForDeferred ? "USE_CLUSTERED_LIGHTLIST" : "USE_FPTL_LIGHTLIST");
                    m_DebugViewTilesMaterial.DisableKeyword(!bUseClusteredForDeferred ? "USE_CLUSTERED_LIGHTLIST" : "USE_FPTL_LIGHTLIST");
                    m_DebugViewTilesMaterial.EnableKeyword("SHOW_LIGHT_CATEGORIES");
                    m_DebugViewTilesMaterial.DisableKeyword("SHOW_FEATURE_VARIANTS");

                    cmd.Blit(null, colorBuffer, m_DebugViewTilesMaterial, 0);
                }
                SetGlobalPropertyRedirect(null, 0, null);

                renderContext.ExecuteCommandBuffer(cmd);
                cmd.Dispose();
            }

            public override void RenderDeferredLighting(HDCamera hdCamera, ScriptableRenderContext renderContext,
                                                        DebugDisplaySettings debugDisplaySettings,
                                                        RenderTargetIdentifier[] colorBuffers, RenderTargetIdentifier depthStencilBuffer, RenderTargetIdentifier depthStencilTexture,
                                                        bool outputSplitLightingForSSS)
            {
                var bUseClusteredForDeferred = !usingFptl;

                using (new Utilities.ProfilingSample((m_PassSettings.enableTileAndCluster ? "TilePass - Deferred Lighting Pass" : "SinglePass - Deferred Lighting Pass") + (outputSplitLightingForSSS ? " MRT" : ""), renderContext))
                {
                    var cmd = new CommandBuffer();
                    cmd.name = bUseClusteredForDeferred ? "Clustered pass" : "Tiled pass";

                    var camera = hdCamera.camera;

                    SetupDebugDisplayMode(debugDisplaySettings.IsDebugDisplayEnabled());

                    if (!m_PassSettings.enableTileAndCluster)
                    {
                        PushGlobalParams(camera, renderContext, null, 0);

                        // This is a debug brute force renderer to debug tile/cluster which render all the lights
                        if (outputSplitLightingForSSS)
                        {
                            Utilities.DrawFullScreen(cmd, m_SingleDeferredMaterialMRT, hdCamera, colorBuffers, depthStencilBuffer);
                        }
                        else
                        {
                            // Note: in the enum StencilBits, Standard is before SSS and the stencil is setup to greater equal. So the code below is draw all stencil bit except SSS
                            m_SingleDeferredMaterialSRT.SetInt("_StencilRef", (int)(debugDisplaySettings.renderingDebugSettings.enableSSS ? StencilBits.Standard : StencilBits.SSS));
                            Utilities.DrawFullScreen(cmd, m_SingleDeferredMaterialSRT, hdCamera, colorBuffers[0], depthStencilBuffer);
                        }
                    }
                    else
                    {
                        int w = camera.pixelWidth;
                        int h = camera.pixelHeight;
                        int numTilesX = (w + 15) / 16;
                        int numTilesY = (h + 15) / 16;
                        int numTiles = numTilesX * numTilesY;

                        if (m_PassSettings.enableComputeLightEvaluation)
                        {
                            bool enableFeatureVariants = GetFeatureVariantsEnabled() && !debugDisplaySettings.IsDebugDisplayEnabled();

                            int numVariants = 1;
                            if (enableFeatureVariants)
                                numVariants = LightDefinitions.NUM_FEATURE_VARIANTS;

                            for (int variant = 0; variant < numVariants; variant++)
                            {
                                int kernel;

                                if (enableFeatureVariants)
                                {
                                    kernel = usingFptl ? s_shadeOpaqueIndirectFptlKernels[variant] : s_shadeOpaqueIndirectClusteredKernels[variant];
                                }
                                else
                                {
                                    if (debugDisplaySettings.IsDebugDisplayEnabled())
                                    {
                                        kernel = usingFptl ? s_shadeOpaqueDirectFptlDebugDisplayKernel : s_shadeOpaqueDirectClusteredDebugDisplayKernel;
                                    }
                                    else
                                    {
                                        kernel = usingFptl ? s_shadeOpaqueDirectFptlKernel : s_shadeOpaqueDirectClusteredKernel;
                                    }
                                }

                                // Pass global parameters to compute shader
                                // TODO: get rid of this by making global parameters visible to compute shaders
                                PushGlobalParams(camera, renderContext, shadeOpaqueShader, kernel);

                                // TODO: Update value like in ApplyDebugDisplaySettings() call. Sadly it is high likely that this will not be keep in sync. we really need to get rid of this by making global parameters visible to compute shaders
                                cmd.SetComputeIntParam(shadeOpaqueShader, "_DebugViewMaterial", Shader.GetGlobalInt("_DebugViewMaterial"));
                                cmd.SetComputeVectorParam(shadeOpaqueShader, "_DebugLightingAlbedo", Shader.GetGlobalVector("_DebugLightingAlbedo"));
                                cmd.SetComputeVectorParam(shadeOpaqueShader, "_DebugLightingSmoothness", Shader.GetGlobalVector("_DebugLightingSmoothness"));

                                cmd.SetComputeBufferParam(shadeOpaqueShader, kernel, "g_vLightListGlobal", bUseClusteredForDeferred ? s_PerVoxelLightLists : s_LightList);

                                cmd.SetComputeTextureParam(shadeOpaqueShader, kernel, "_MainDepthTexture", depthStencilTexture);
                                cmd.SetComputeTextureParam(shadeOpaqueShader, kernel, "_GBufferTexture0", Shader.PropertyToID("_GBufferTexture0"));
                                cmd.SetComputeTextureParam(shadeOpaqueShader, kernel, "_GBufferTexture1", Shader.PropertyToID("_GBufferTexture1"));
                                cmd.SetComputeTextureParam(shadeOpaqueShader, kernel, "_GBufferTexture2", Shader.PropertyToID("_GBufferTexture2"));
                                cmd.SetComputeTextureParam(shadeOpaqueShader, kernel, "_GBufferTexture3", Shader.PropertyToID("_GBufferTexture3"));
                                cmd.SetComputeTextureParam(shadeOpaqueShader, kernel, "g_tShadowBuffer", Shader.PropertyToID("g_tShadowBuffer"));

                                cmd.SetComputeTextureParam(shadeOpaqueShader, kernel, "_LtcData", Shader.GetGlobalTexture(Shader.PropertyToID("_LtcData")));
                                cmd.SetComputeTextureParam(shadeOpaqueShader, kernel, "_PreIntegratedFGD", Shader.GetGlobalTexture("_PreIntegratedFGD"));
                                cmd.SetComputeTextureParam(shadeOpaqueShader, kernel, "_LtcGGXMatrix", Shader.GetGlobalTexture("_LtcGGXMatrix"));
                                cmd.SetComputeTextureParam(shadeOpaqueShader, kernel, "_LtcDisneyDiffuseMatrix", Shader.GetGlobalTexture("_LtcDisneyDiffuseMatrix"));
                                cmd.SetComputeTextureParam(shadeOpaqueShader, kernel, "_LtcMultiGGXFresnelDisneyDiffuse", Shader.GetGlobalTexture("_LtcMultiGGXFresnelDisneyDiffuse"));

                                Matrix4x4 viewToWorld = camera.cameraToWorldMatrix;
                                Matrix4x4 worldToView = camera.worldToCameraMatrix;
                                Matrix4x4 viewProjection = hdCamera.viewProjectionMatrix;
                                Matrix4x4 invViewProjection = hdCamera.invViewProjectionMatrix;

                                Utilities.SetMatrixCS(cmd, shadeOpaqueShader, "unity_MatrixV", worldToView);
                                Utilities.SetMatrixCS(cmd, shadeOpaqueShader, "unity_MatrixInvV", viewToWorld);
                                Utilities.SetMatrixCS(cmd, shadeOpaqueShader, "unity_MatrixVP", viewProjection);

                                Utilities.SetMatrixCS(cmd, shadeOpaqueShader, "_InvViewProjMatrix", invViewProjection);
                                Utilities.SetMatrixCS(cmd, shadeOpaqueShader, "_ViewProjMatrix", viewProjection);
                                Utilities.SetMatrixCS(cmd, shadeOpaqueShader, "g_mInvScrProjection", Shader.GetGlobalMatrix("g_mInvScrProjection"));
                                cmd.SetComputeVectorParam(shadeOpaqueShader, "_ScreenSize", hdCamera.screenSize);
                                cmd.SetComputeIntParam(shadeOpaqueShader, "_UseTileLightList", Shader.GetGlobalInt("_UseTileLightList"));

                                cmd.SetComputeVectorParam(shadeOpaqueShader, "_Time", Shader.GetGlobalVector("_Time"));
                                cmd.SetComputeVectorParam(shadeOpaqueShader, "_SinTime", Shader.GetGlobalVector("_SinTime"));
                                cmd.SetComputeVectorParam(shadeOpaqueShader, "_CosTime", Shader.GetGlobalVector("_CosTime"));
                                cmd.SetComputeVectorParam(shadeOpaqueShader, "unity_DeltaTime", Shader.GetGlobalVector("unity_DeltaTime"));
                                cmd.SetComputeVectorParam(shadeOpaqueShader, "_WorldSpaceCameraPos", Shader.GetGlobalVector("_WorldSpaceCameraPos"));
                                cmd.SetComputeVectorParam(shadeOpaqueShader, "_ProjectionParams", Shader.GetGlobalVector("_ProjectionParams"));
                                cmd.SetComputeVectorParam(shadeOpaqueShader, "_ScreenParams", Shader.GetGlobalVector("_ScreenParams"));
                                cmd.SetComputeVectorParam(shadeOpaqueShader, "_ZBufferParams", Shader.GetGlobalVector("_ZBufferParams"));
                                cmd.SetComputeVectorParam(shadeOpaqueShader, "unity_OrthoParams", Shader.GetGlobalVector("unity_OrthoParams"));
                                cmd.SetComputeIntParam(shadeOpaqueShader, "_EnvLightSkyEnabled", Shader.GetGlobalInt("_EnvLightSkyEnabled"));

                                Texture skyTexture = Shader.GetGlobalTexture("_SkyTexture");
                                Texture IESArrayTexture = Shader.GetGlobalTexture("_IESArray");
                                cmd.SetComputeTextureParam(shadeOpaqueShader, kernel, "_IESArray", IESArrayTexture ? IESArrayTexture : m_DefaultTexture2DArray);
                                cmd.SetComputeTextureParam(shadeOpaqueShader, kernel, "_SkyTexture", skyTexture ? skyTexture : m_DefaultTexture2DArray);

                                // Since we need the stencil test, the compute path does not currently support SSS.
                                cmd.SetComputeTextureParam(shadeOpaqueShader, kernel, "combinedLightingUAV", colorBuffers[0]);

                                // always do deferred lighting in blocks of 16x16 (not same as tiled light size)

                                if (enableFeatureVariants)
                                {
                                    cmd.SetComputeIntParam(shadeOpaqueShader, "g_TileListOffset", variant * numTiles);
                                    cmd.SetComputeBufferParam(shadeOpaqueShader, kernel, "g_TileList", s_TileList);
                                    cmd.DispatchCompute(shadeOpaqueShader, kernel, s_DispatchIndirectBuffer, (uint)variant * 3 * sizeof(uint));
                                }
                                else
                                {
                                    cmd.DispatchCompute(shadeOpaqueShader, kernel, numTilesX, numTilesY, 1);
                                }
                            }
                        }
                        else
                        {
                            // Pixel shader evaluation
                            PushGlobalParams(camera, renderContext, null, 0);

                            if (m_PassSettings.enableSplitLightEvaluation)
                            {
                                if (outputSplitLightingForSSS)
                                {
                                    Utilities.SelectKeyword(m_DeferredDirectMaterialMRT, "USE_CLUSTERED_LIGHTLIST", "USE_FPTL_LIGHTLIST", bUseClusteredForDeferred);
                                    Utilities.DrawFullScreen(cmd, m_DeferredDirectMaterialMRT, hdCamera, colorBuffers, depthStencilBuffer);

                                    Utilities.SelectKeyword(m_DeferredIndirectMaterialMRT, "USE_CLUSTERED_LIGHTLIST", "USE_FPTL_LIGHTLIST", bUseClusteredForDeferred);
                                    Utilities.DrawFullScreen(cmd, m_DeferredIndirectMaterialMRT, hdCamera, colorBuffers, depthStencilBuffer);
                                }
                                else
                                {
                                    // Note: in the enum StencilBits, Standard is before SSS and the stencil is setup to greater equal. So the code below is draw all stencil bit except SSS
                                    m_DeferredDirectMaterialSRT.SetInt("_StencilRef", (int)(debugDisplaySettings.renderingDebugSettings.enableSSS ? StencilBits.Standard : StencilBits.SSS));
                                    Utilities.SelectKeyword(m_DeferredDirectMaterialSRT, "USE_CLUSTERED_LIGHTLIST", "USE_FPTL_LIGHTLIST", bUseClusteredForDeferred);
                                    Utilities.DrawFullScreen(cmd, m_DeferredDirectMaterialSRT, hdCamera, colorBuffers[0], depthStencilBuffer);

                                    m_DeferredIndirectMaterialSRT.SetInt("_StencilRef", (int)(debugDisplaySettings.renderingDebugSettings.enableSSS ? StencilBits.Standard : StencilBits.SSS));
                                    Utilities.SelectKeyword(m_DeferredIndirectMaterialSRT, "USE_CLUSTERED_LIGHTLIST", "USE_FPTL_LIGHTLIST", bUseClusteredForDeferred);
                                    Utilities.DrawFullScreen(cmd, m_DeferredIndirectMaterialSRT, hdCamera, colorBuffers[0], depthStencilBuffer);
                                }
                            }
                            else
                            {
                                if (outputSplitLightingForSSS)
                                {
                                    Utilities.SelectKeyword(m_DeferredAllMaterialMRT, "USE_CLUSTERED_LIGHTLIST", "USE_FPTL_LIGHTLIST", bUseClusteredForDeferred);
                                    Utilities.DrawFullScreen(cmd, m_DeferredAllMaterialMRT, hdCamera, colorBuffers, depthStencilBuffer);
                                }
                                else
                                {
                                    // Note: in the enum StencilBits, Standard is before SSS and the stencil is setup to greater equal. So the code below is draw all stencil bit except SSS
                                    m_DeferredAllMaterialSRT.SetInt("_StencilRef", (int)(debugDisplaySettings.renderingDebugSettings.enableSSS ? StencilBits.Standard : StencilBits.SSS));
                                    Utilities.SelectKeyword(m_DeferredAllMaterialSRT, "USE_CLUSTERED_LIGHTLIST", "USE_FPTL_LIGHTLIST", bUseClusteredForDeferred);
                                    Utilities.DrawFullScreen(cmd, m_DeferredAllMaterialSRT, hdCamera, colorBuffers[0], depthStencilBuffer);
                                }
                            }
                        }
                    }

                    SetGlobalPropertyRedirect(null, 0, null);

                    renderContext.ExecuteCommandBuffer(cmd);
                    cmd.Dispose();
                } // TilePass - Deferred Lighting Pass
            }

            public override void RenderForward(Camera camera, ScriptableRenderContext renderContext, bool renderOpaque)
            {
                // Note: if we use render opaque with deferred tiling we need to render a opaque depth pass for these opaque objects
                bool useFptl = renderOpaque && usingFptl;

                var cmd = new CommandBuffer();

                if (!m_PassSettings.enableTileAndCluster)
                {
                    cmd.name = "Forward pass";
                    cmd.EnableShaderKeyword("LIGHTLOOP_SINGLE_PASS");
                    cmd.DisableShaderKeyword("LIGHTLOOP_TILE_PASS");
                }
                else
                {
                    cmd.name = useFptl ? "Forward Tiled pass" : "Forward Clustered pass";
                    // say that we want to use tile of single loop
                    cmd.EnableShaderKeyword("LIGHTLOOP_TILE_PASS");
                    cmd.DisableShaderKeyword("LIGHTLOOP_SINGLE_PASS");
                    cmd.SetGlobalFloat("_UseTileLightList", useFptl ? 1 : 0);      // leaving this as a dynamic toggle for now for forward opaques to keep shader variants down.
                    cmd.SetGlobalBuffer("g_vLightListGlobal", useFptl ? s_LightList : s_PerVoxelLightLists);
                }

                renderContext.ExecuteCommandBuffer(cmd);
                cmd.Dispose();
            }
        }
    }
}
