using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    [ExecuteInEditMode]
    public class SceneSettings : MonoBehaviour
    {
        public CommonSettings commonSettings
        {
            get { return m_CommonSettings; }
        }

        public SkySettings skySettings
        {
            get { return m_SkySettings; }
        }

        [SerializeField] private CommonSettings m_CommonSettings = null;
        [SerializeField] private SkySettings    m_SkySettings = null;

        // Use this for initialization
        void OnEnable()
        {
            SceneSettingsManager.instance.AddSceneSettings(this);

            HDRenderPipelineInstance hdPipeline = RenderPipelineManager.currentPipeline as HDRenderPipelineInstance;

            if (hdPipeline != null)
            {
                hdPipeline.OnSceneLoad();
            }
        }

        void OnDisable()
        {
            SceneSettingsManager.instance.RemoveSceneSettings(this);
        }

        void OnValidate()
        {
            // If the setting is already the one currently used we need to tell the manager to reapply it.
            if (SceneSettingsManager.instance.GetCurrentSceneSetting())
            {
                SceneSettingsManager.instance.UpdateCurrentSceneSetting();
            }
        }
    }
}
