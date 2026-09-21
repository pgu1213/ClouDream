// MANUAL RECONSTRUCTION FROM IL2CPP/ISIL -- NOT ORIGINAL SOURCE.
// Confidence: HIGH for control flow, six-layer array mapping and assignments.
// Confidence: MEDIUM for source-level formatting and local variable names.
// See 00_report/MANUAL_RECONSTRUCTION_NOTICE_KO.md.

using UnityEngine;

namespace Expanse;

public partial class CloudLayerInterpolator
{
    public CloudLayerInterpolator()
    {
        m_autoMode = true;
        m_transitionTime = 10f;
        m_bypassOffset = true;

        m_interpolatedTextures = new TextureInterpolator[6];
        m_currentTextures = new ProceduralNoiseGenerator[6];
        m_targetTextures = new ProceduralNoiseGenerator[6];
    }

    public void LoadPreset(UniversalCloudLayer preset)
    {
        m_prevTargetPreset = m_targetPreset;
        m_targetPreset = preset;
    }

    private void Update()
    {
        if (m_interpolatedLayer == null)
            m_interpolatedLayer =
                ScriptableObject.CreateInstance<UniversalCloudLayer>();

        populateTextureArrays();

        if (m_autoMode)
            updateAutoMode();

        if (m_currentPreset != m_prevCurrentPreset)
            onCurrentPresetChanged();

        if (m_targetPreset != m_prevTargetPreset)
            onTargetPresetChanged();

        pushInterpolatedSettings();
    }

    private void advanceCurrentToTarget()
    {
        m_prevCurrentPreset = m_currentPreset;
        m_prevTargetPreset = m_targetPreset;
        m_currentPreset = m_targetPreset;
        m_targetPreset = null;
    }

    private void updateAutoMode()
    {
        if (m_targetPreset == null)
        {
            m_interpolationAmount = 0f;
            return;
        }

        if (m_currentPreset == null)
        {
            m_interpolationAmount = 0f;
            advanceCurrentToTarget();
            return;
        }

        m_interpolationAmount +=
            Mathf.Min(1f, Time.deltaTime / m_transitionTime);

        if (m_interpolationAmount >= 1f)
        {
            m_interpolationAmount = 0f;
            advanceCurrentToTarget();
        }
    }

    private void onCurrentPresetChanged()
    {
        m_prevCurrentPreset = m_currentPreset;

        if (m_currentPreset == null)
            return;

        configurePresetGenerators(m_currentPreset, m_currentTextures);
        m_firstFrameAfterSwitch = true;
    }

    private void onTargetPresetChanged()
    {
        m_prevTargetPreset = m_targetPreset;

        if (m_targetPreset == null)
            return;

        configurePresetGenerators(m_targetPreset, m_targetTextures);
        m_firstFrameAfterSwitch = true;
    }

    private void configurePresetGenerators(
        UniversalCloudLayer preset,
        ProceduralNoiseGenerator[] generators)
    {
        for (int i = 0; i < 6; i++)
        {
            // Both calls are present before either current or target generator setup.
            m_interpolatedTextures[i].TexturesChanged();
            m_interpolatedTextures[i].ForceUpdate();

            ProceduralNoiseGenerator generator = generators[i];
            UniversalCloudLayer.UniversalCloudNoiseLayer noise =
                preset.noiseLayers[i];

            generator.m_dimension = i == 0
                ? NoiseDimension.TwoDimensional
                : CloudDatatypes.cloudGeometryTypeToNoiseDimension(
                    preset.renderSettings.geometryType);

            generator.m_noiseType = noise.renderSettings.noiseType;
            generator.m_scale = Vector2Int.FloorToInt(
                noise.renderSettings.scale);
            generator.m_octaves = noise.renderSettings.octaves;
            generator.m_octaveScale = noise.renderSettings.octaveScale;
            generator.m_octaveMultiplier =
                noise.renderSettings.octaveMultiplier;
        }
    }

    private void pushInterpolatedSettings()
    {
        if (m_targetPreset == null && m_currentPreset == null)
        {
            for (int i = 0; i < 6; i++)
                m_interpolatedTextures[i].DisableRendering();

            m_firstFrameAfterSwitch = false;
            return;
        }

        if (m_targetPreset != null && m_currentPreset != null)
        {
            UniversalCloudLayer.lerp(
                m_currentPreset,
                m_targetPreset,
                m_interpolationAmount,
                m_interpolatedLayer);

            m_cloudLayer.FromUniversal(
                m_interpolatedLayer,
                m_bypassOffset);

            for (int i = 0; i < 6; i++)
            {
                TextureInterpolator interpolator =
                    m_interpolatedTextures[i];
                UniversalCloudLayer.UniversalCloudNoiseLayer currentNoise =
                    m_currentPreset.noiseLayers[i];
                UniversalCloudLayer.UniversalCloudNoiseLayer targetNoise =
                    m_targetPreset.noiseLayers[i];

                interpolator.EnableRendering();
                interpolator.m_blend = m_interpolationAmount;
                interpolator.m_textureA = m_currentTextures[i];
                interpolator.m_textureB = m_targetTextures[i];
                interpolator.m_staticTextureA = currentNoise.noiseTexture;
                interpolator.m_staticTextureB = targetNoise.noiseTexture;

                interpolator.m_textureAStatic =
                    currentNoise.noiseTexture != null &&
                    !currentNoise.procedural;
                interpolator.m_textureBStatic =
                    targetNoise.noiseTexture != null &&
                    !targetNoise.procedural;

                // Coverage is the only layer with an explicit missing-texture fallback.
                if (i == 0)
                {
                    if (currentNoise.noiseTexture == null &&
                        !currentNoise.procedural)
                    {
                        interpolator.m_staticTextureA = Texture2D.blackTexture;
                        interpolator.m_textureAStatic = true;
                    }

                    if (targetNoise.noiseTexture == null &&
                        !targetNoise.procedural)
                    {
                        interpolator.m_staticTextureB = Texture2D.blackTexture;
                        interpolator.m_textureBStatic = true;
                    }
                }

                interpolator.m_tileA = 1f;
                interpolator.m_tileB = 1f;

                if (!m_firstFrameAfterSwitch)
                {
                    m_cloudLayer.SetTexture(
                        (CloudDatatypes.CloudNoiseLayer)i,
                        interpolator.GetTexture(),
                        targetNoise.renderSettings.tile);
                }
            }
        }
        else if (m_targetPreset != null)
        {
            for (int i = 0; i < 6; i++)
            {
                m_interpolatedTextures[i].DisableRendering();

                if (m_firstFrameAfterSwitch)
                    m_targetTextures[i].ForceUpdate();
            }
        }
        else
        {
            for (int i = 0; i < 6; i++)
            {
                m_interpolatedTextures[i].DisableRendering();

                if (m_firstFrameAfterSwitch)
                    m_currentTextures[i].ForceUpdate();
            }
        }

        m_firstFrameAfterSwitch = false;
    }

    public bool IsInterpolating()
    {
        return m_interpolationAmount > 0f;
    }

    private void populateTextureArrays()
    {
        m_interpolatedTextures[0] = m_coverageInterpolated;
        m_currentTextures[0] = m_coverageCurrent;
        m_targetTextures[0] = m_coverageTarget;

        m_interpolatedTextures[1] = m_baseInterpolated;
        m_currentTextures[1] = m_baseCurrent;
        m_targetTextures[1] = m_baseTarget;

        m_interpolatedTextures[2] = m_structureInterpolated;
        m_currentTextures[2] = m_structureCurrent;
        m_targetTextures[2] = m_structureTarget;

        m_interpolatedTextures[3] = m_detailInterpolated;
        m_currentTextures[3] = m_detailCurrent;
        m_targetTextures[3] = m_detailTarget;

        m_interpolatedTextures[4] = m_baseWarpInterpolated;
        m_currentTextures[4] = m_baseWarpCurrent;
        m_targetTextures[4] = m_baseWarpTarget;

        m_interpolatedTextures[5] = m_detailWarpInterpolated;
        m_currentTextures[5] = m_detailWarpCurrent;
        m_targetTextures[5] = m_detailWarpTarget;
    }
}
