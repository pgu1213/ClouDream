using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEditor;
using UnityEditor.SceneManagement;
using ClouDream;

public static class BuildCloudSea
{
    const string Root = "Assets/CloudSea";
    public static string Build()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play Mode before building.");
        var current = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (current.isDirty) throw new InvalidOperationException("Save the current scene before building.");
        Directory.CreateDirectory(Root + "/Scenes");
        Directory.CreateDirectory(Root + "/Settings");
        Directory.CreateDirectory(Root + "/Textures");
        AssetDatabase.Refresh();
        // Preserve the original OutdoorsScene; work in a dedicated exploration scene.
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var sourcePipeline = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
        string pipelinePath = Root + "/Settings/CloudSeaHDRP.asset";
        var pipeline = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(pipelinePath);
        if (pipeline == null) { pipeline = UnityEngine.Object.Instantiate(sourcePipeline); AssetDatabase.CreateAsset(pipeline, pipelinePath); }
        var serialized = new SerializedObject(pipeline);
        serialized.FindProperty("m_RenderPipelineSettings.supportVolumetricClouds").boolValue = true;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;

        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(Root + "/Settings/CloudSeaDay.asset");
        if (profile == null) { profile = ScriptableObject.CreateInstance<VolumeProfile>(); AssetDatabase.CreateAsset(profile, Root + "/Settings/CloudSeaDay.asset"); }
        var environment = Get<VisualEnvironment>(profile);
        environment.skyType.Override((int)SkyType.PhysicallyBased);
        var sky = Get<PhysicallyBasedSky>(profile);
        sky.type.Override(PhysicallyBasedSkyModel.EarthAdvanced);
        sky.groundTint.Override(new Color(0.36f, 0.43f, 0.49f));
        sky.aerosolDensity.Override(0.006f);
        sky.horizonTint.Override(new Color(0.83f, 0.94f, 1));
        sky.zenithTint.Override(new Color(0.8f, 0.93f, 1));
        sky.updateMode.Override(EnvironmentUpdateMode.OnChanged);
        var exposure = Get<Exposure>(profile);
        exposure.mode.Override(ExposureMode.Fixed); exposure.fixedExposure.Override(12);
        var tonemap = Get<Tonemapping>(profile); tonemap.mode.Override(TonemappingMode.ACES);
        var fog = Get<Fog>(profile); fog.enabled.Override(false);
        Get<VolumetricClouds>(profile);

        var volumeObject = new GameObject("Cloud Sea - Weather and Height Profiles");
        var volume = volumeObject.AddComponent<Volume>(); volume.isGlobal = true; volume.priority = 10; volume.sharedProfile = profile;
        var world = volumeObject.AddComponent<CloudSeaWorld>();
        world.weatherMap = SaveTexture(world.GenerateWeatherMap(), Root + "/Textures/WeatherMap.asset");
        world.heightLut = SaveTexture(world.GenerateHeightLut(), Root + "/Textures/HeightLUT.asset");
        world.Apply();

        var sunObject = new GameObject("Sun - Warm Daylight");
        sunObject.transform.rotation = Quaternion.Euler(38, -115, 0);
        var sun = sunObject.AddComponent<Light>(); sun.type = LightType.Directional;
        sun.color = new Color(1, 0.97f, 0.93f); sun.intensity = 100000; sun.shadows = LightShadows.Soft;
        var hdSun = sunObject.AddComponent<HDAdditionalLightData>(); hdSun.angularDiameter = 1.1f;
        RenderSettings.sun = sun;

        var cameraObject = new GameObject("Cloud Explorer"); cameraObject.tag = "MainCamera";
        cameraObject.transform.SetPositionAndRotation(new Vector3(0, 2400, -3200), Quaternion.Euler(12, 0, 0));
        var camera = cameraObject.AddComponent<Camera>(); camera.nearClipPlane = 0.3f; camera.farClipPlane = 100000; camera.fieldOfView = 68;
        var hdCamera = cameraObject.AddComponent<HDAdditionalCameraData>();
        hdCamera.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
        hdCamera.customRenderingSettings = true;
        hdCamera.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.VolumetricClouds, true);
        hdCamera.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.VolumetricClouds] = true;
        cameraObject.AddComponent<AudioListener>(); cameraObject.AddComponent<CloudSeaFlight>();

        EditorUtility.SetDirty(profile); EditorUtility.SetDirty(world); EditorUtility.SetDirty(pipeline);
        AssetDatabase.SaveAssets();
        EditorSceneManager.SaveScene(scene, Root + "/Scenes/CloudSea.unity");
        var builds = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        if (!builds.Exists(s => s.path == scene.path)) { builds.Insert(0, new EditorBuildSettingsScene(scene.path, true)); EditorBuildSettings.scenes = builds.ToArray(); }
        if (SceneView.lastActiveSceneView != null)
        {
            SceneView.lastActiveSceneView.LookAtDirect(camera.transform.position + camera.transform.forward * 1000, camera.transform.rotation, 1000);
            SceneView.lastActiveSceneView.sceneViewState.alwaysRefresh = true;
        }
        Selection.activeGameObject = volumeObject;
        return "CloudSea scene, HDRP asset, cloud profiles, generated weather map and height LUT saved.";
    }

    static T Get<T>(VolumeProfile profile) where T : VolumeComponent
    {
        if (profile.TryGet<T>(out var component)) return component;
        component = profile.Add<T>(true); AssetDatabase.AddObjectToAsset(component, profile); return component;
    }
    static Texture2D SaveTexture(Texture2D texture, string path)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (existing == null) { AssetDatabase.CreateAsset(texture, path); return texture; }
        EditorUtility.CopySerialized(texture, existing); existing.Apply(); UnityEngine.Object.DestroyImmediate(texture); return existing;
    }
}
