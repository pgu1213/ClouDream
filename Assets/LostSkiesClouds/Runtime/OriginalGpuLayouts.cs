// GPU layouts recovered from the supplied Expanse metadata. No original DLL is required.
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ClouDream.LostSkies
{
    [Serializable, StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct UniversalCloudLayerRenderSettings
    {
        public int geometryType;
        public Vector2 geometryXExtent;
        public Vector2 geometryYExtent;
        public Vector2 geometryZExtent;

        public float geometryHeight;
        public float coverageIntensity;
        public float structureIntensity;
        public float structureMultiply;

        public float detailIntensity;
        public float detailMultiply;
        public float baseWarpIntensity;
        public float detailWarpIntensity;

        public Vector2 windSkew;
        public Vector3 attenuationOrigin;
        public int coverageTile;
        public int baseTile;

        public int structureTile;
        public int detailTile;
        public int baseWarpTile;
        public int detailWarpTile;

        public Vector3 coverageOffset;
        public Vector3 baseOffset;
        public Vector3 structureOffset;
        public Vector3 detailOffset;

        public Vector3 baseWarpOffset;
        public Vector3 detailWarpOffset;
        public int coverageStochasticSampling;
        public int baseStochasticSampling;

        public float coverageStochasticSamplingScale;
        public float baseStochasticSamplingScale;
        public float density;
        public float attenuationDistance;

        public float attenuationBias;
        public Vector2 rampUp;
        public Vector3 extinctionCoefficients;
        public Vector3 scatteringCoefficients;

        public float multipleScatteringMultiplier;
        public float shadowPersistence;
        public float silverSpread;
        public float silverIntensity;

        public float anisotropy;
        public float ambient;
        public Vector2 ambientHeightRange;
        public Vector2 ambientStrengthRange;

        public int selfShadowing;
        public int highQualityShadows;
        public float shadowSampleJitter;
        public float maxSelfShadowDistance;

        public int ambientShadowing;
        public Vector2 heightShadowRange;
        public Vector2 heightShadowIntensity;
        public float lightPollutionDimmer;

        public int castShadows;
        public float apparentThickness;
        public float multipleScatteringReceptiveField;
        public float multipleScatteringBias;

        public float maxShadowIntensity;
        public int reprojectionFrames;
        public int useTemporalDenoising;
        public int neighborhoodClamping;

        public float temporalDenoisingRatio;
        public float sampleJitter;
        public float pixelJitter;
        public float coarseStepSize;

        public float detailStepSize;
        public Vector2 coarseStepRange;
        public Vector2 detailStepRange;
        public Vector2 stepDistanceRange;

        public float maxFlythroughDistance;
        public Vector2 LODDistances;
        public float mediaZeroThreshold;
        public float transmittanceZeroThreshold;

        public int maxConsecutiveZeroSamples;
        public int lightSamplingStrategy;
        public int denoisingDepthRejection;
        public Vector2 denoisingDepthRejectionThreshold;

    }

    [Serializable, StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GlobalRenderSettings
    {
        public float planetRadius;
        public float atmosphereRadius;
        public Vector3 planetOriginOffset;
        public float clipFade;

        public float apScale;
        public Color groundTint;
        public float groundEmissionMultiplier;
        public Matrix4x4 planetRotation;

        public int hasAlbedoTexture;
        public int hasEmissionTexture;
        public Vector3 scatterTint;
        public Vector3 lightPollution;

        public int nightSkyReflections;
        public int planetReflections;
        public int fullscreenAtmosphere;
        public int useMS;

        public int useAP;
        public int samplesT;
        public int samplesAP;
        public int samplesSS;

        public int samplesMS;
        public int samplesMSAcc;
        public int samplesFog;
        public int samplesScreenspaceShadows;

        public int importanceSample;
        public int AP_importanceSample;
        public float AP_depthSkew;
        public float AP_renderDistance;

        public float fog_depthSkew;
        public float fogDepthBias;
        public int fogHistoryFrames;
        public int fogBlurDenoising;

        public int downsampledDepthMip;
        public int dither;
        public int cullCloudsBehindGeometry;
        public float cloudShadowMapFilmPlaneScale;

        public int cloudShadowMapBlurRadius;
        public int amortizeCloudReflections;
        public float cloudSubresolution;
        public Vector2 interactiveCloudsFadeIn;

    }

    [Serializable, StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct DirectionalLightRenderSettings
    {
        public Vector3 direction;
        public Vector3 positionOffset;
        public Vector3 lightColor;
        public float penumbraRadius;

        public int useShadowmap;
        public int shadowmapNDCSign;
        public int volumetricGeometryShadows;
        public int volumetricCloudShadows;

        public int volumetricCloudShadowIndex;
    }

    [Serializable, StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct CloudPrimitive
    {
        public Vector4 position;
        public Vector4 data;
        public Vector4 color;
    }

    [Serializable, StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct CloudLinePrimitive
    {
        public Vector4 pt0;
        public Vector4 pt1;
        public Vector4 data;
        public Color color0;

        public Color color1;
    }
}
