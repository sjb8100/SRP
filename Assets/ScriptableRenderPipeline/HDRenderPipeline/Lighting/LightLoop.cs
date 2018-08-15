using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    public class BaseLightLoop
    {
        protected Light m_CurrentSunLight = null;

        // TODO: We should rather put the texture settings in LightLoop, but how do we serialize it ?
        public virtual void Build(TextureSettings textureSettings) {}

        public virtual void Cleanup() {}

        public virtual bool NeedResize() { return false;  }

        public virtual void AllocResolutionDependentBuffers(int width, int height) {}

        public virtual void ReleaseResolutionDependentBuffers() {}

        public virtual void NewFrame() {}

        public virtual void PrepareLightsForGPU(ShadowSettings shadowSettings, CullResults cullResults, Camera camera, ref ShadowOutput shadowOutput) {}
        public virtual void RenderShadows(ScriptableRenderContext renderContext, CullResults cullResults) {}

        // TODO: this should not be part of the interface but for now make something working
        public virtual void BuildGPULightLists(Camera camera, ScriptableRenderContext loop, RenderTargetIdentifier cameraDepthBufferRT) {}

        public virtual void RenderDeferredLighting( HDCamera hdCamera, ScriptableRenderContext renderContext,
                                                    DebugDisplaySettings debugDisplaySettings,
                                                    RenderTargetIdentifier[] colorBuffers, RenderTargetIdentifier depthStencilBuffer, RenderTargetIdentifier depthStencilTexture,
                                                    bool outputSplitLightingForSSS) { }

        public virtual void RenderForward(Camera camera, ScriptableRenderContext renderContext, bool renderOpaque) {}

        public virtual void RenderLightingDebug(HDCamera hdCamera, ScriptableRenderContext renderContext, RenderTargetIdentifier colorBuffer) {}

        public Light GetCurrentSunLight() { return m_CurrentSunLight;  }
    }
}
