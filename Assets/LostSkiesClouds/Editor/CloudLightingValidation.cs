using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>시간대와 조명만 바꾼 상태에서 생산용 구름 렌더 경로를 검증합니다.</summary>
    public static class CloudLightingValidation
    {
        private const float Tolerance = 0.0001f;

        [Serializable]
        public sealed class Report
        {
            public bool passed;
            public bool finiteGpuValues;
            public bool geometryUnchanged;
            public bool timeChangesRadiance;
            public bool noLightProducesBlack;
            public bool directColorReachesClouds;
            public bool ambientIlluminatesClouds;

            public bool zeroDurationTransitionImmediate;
            public bool interruptedTransitionContinuous;
            public bool interruptedTransitionCompletes;
            public bool timeClampedToProfile;
            public bool automaticPlaybackStopsAtEnd;
            public bool weatherDensityPreserved;
            public bool weatherTransitionContinuous;

            public bool sceneStateRestoredOnDisable;
            public bool profileRemovalRestoresScene;
            public bool profileRemovalPreservesWeather;
            public bool instanceOnlyVolumeSupported;
            public bool externalProfileAdopted;
            public bool externalProfileRestored;
            public bool externalReplacementPreservedOnDisable;
            public bool sourceProfilesUnchanged;

            public int cloudyPixels;
            public float maximumTransmissionDifference;
            public float dayToSunsetRadianceDifference;
            public float sunsetToTwilightRadianceDifference;
            public float unlitMaximumRadiance;
            public float ambientMeanLuminance;
            public Color redDirectMean;
            public Color blueDirectMean;

            public string error;
        }

        private sealed class GpuFrame
        {
            public Color[] lighting;
            public Color[] transmission;
        }

        /// <summary>GPU 빛 반응과 시간/날씨 전환을 검사하고 결과를 JSON으로 저장합니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Validate Time Of Day Lighting")]
        public static Report Run()
        {
            Report report = new Report();
            GameObject host = new GameObject("Cloud lighting validation");
            host.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                CheckGpu(host, report);
                CheckTransitions(host, report);
                CheckLifecycle(report);
                report.passed = true;
            }
            catch (Exception exception)
            {
                report.error = exception.ToString();
                Debug.LogException(exception);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                Directory.CreateDirectory("Screenshots");
                File.WriteAllText("Screenshots/CloudLighting-Validation.json", JsonUtility.ToJson(report, true));
            }

            return report;
        }

        /// <summary>동일한 밀도장에서 세 시간대, 직사광 색, 무광원, 주변광을 비교합니다.</summary>
        private static void CheckGpu(GameObject host, Report report)
        {
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.farClipPlane = 100000f;
            camera.fieldOfView = 68f;
            camera.aspect = 1f;
            camera.transform.SetPositionAndRotation(new Vector3(1222f, 3300f, -6800f), Quaternion.Euler(10f, 15f, 0f));

            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/LostSkiesClouds/Runtime/CloudRaymarch.compute");
            ComputeShader noise = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/LostSkiesClouds/SourceShaders/CloudGenerator.asset");
            TextAsset preset = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/LostSkiesClouds/Presets/normal.json");
            CloudSkyProfile sky = AssetDatabase.LoadAssetAtPath<CloudSkyProfile>("Assets/LostSkiesClouds/Presets/SparseSky.asset");
            Require(shader != null && noise != null && preset != null && sky != null, "Lighting validation resources are missing.");

            using (LostSkiesCloudRenderer renderer = new LostSkiesCloudRenderer(shader, noise, preset))
            {
                renderer.includeOcean = true;
                renderer.skyProfile = sky;
                renderer.settings.coverageIntensity = 0.165f;
                renderer.settings.density = 160000f;
                renderer.Resize(96, 96, 303);

                GpuFrame day = Draw(renderer, camera, CloudLightingState.Day());
                GpuFrame sunset = Draw(renderer, camera, CloudLightingState.Sunset());
                GpuFrame twilight = Draw(renderer, camera, CloudLightingState.Twilight());
                report.maximumTransmissionDifference = Mathf.Max(MaximumDifference(day.transmission, sunset.transmission), MaximumDifference(day.transmission, twilight.transmission));
                report.geometryUnchanged = report.maximumTransmissionDifference <= Tolerance;
                Require(report.geometryUnchanged, "Lighting changed cloud transmittance or depth.");

                for (int pixel = 0; pixel < day.transmission.Length; pixel++)
                {
                    if (day.transmission[pixel].r < 0.95f)
                    {
                        report.cloudyPixels++;
                    }
                }

                Require(report.cloudyPixels > 64, "The lighting test view contains too few clouds.");
                report.dayToSunsetRadianceDifference = MaximumRgbDifference(day.lighting, sunset.lighting);
                report.sunsetToTwilightRadianceDifference = MaximumRgbDifference(sunset.lighting, twilight.lighting);
                report.timeChangesRadiance = report.dayToSunsetRadianceDifference > 0.001f && report.sunsetToTwilightRadianceDifference > 0.001f;
                Require(report.timeChangesRadiance, "Different times did not change integrated cloud lighting.");

                CloudLightingState unlit = CloudLightingState.Day();
                unlit.sunLux = 0f;
                unlit.directMultiplier = 0f;
                unlit.silverLining = 0f;
                unlit.ambientIntensity = 0f;
                GpuFrame dark = Draw(renderer, camera, unlit);
                report.unlitMaximumRadiance = MaximumRgb(dark.lighting);
                report.noLightProducesBlack = report.unlitMaximumRadiance <= Tolerance;
                Require(report.noLightProducesBlack, "Clouds emit light with both direct and ambient illumination disabled.");

                CloudLightingState red = CloudLightingState.Day();
                red.ambientIntensity = 0f;
                red.sunColor = new Color(1f, 0.05f, 0.05f, 1f);
                CloudLightingState blue = red;
                blue.sunColor = new Color(0.05f, 0.05f, 1f, 1f);
                report.redDirectMean = MeanRgb(Draw(renderer, camera, red).lighting);
                report.blueDirectMean = MeanRgb(Draw(renderer, camera, blue).lighting);
                report.directColorReachesClouds = report.redDirectMean.r > report.redDirectMean.b * 2f && report.blueDirectMean.b > report.blueDirectMean.r * 2f;
                Require(report.directColorReachesClouds, "The raymarch discarded the direct light color.");

                CloudLightingState ambient = unlit;
                ambient.ambientIntensity = 1f;
                ambient.ambientSkyColor = new Color(0.3f, 0.65f, 1f, 1f);
                ambient.ambientHorizonColor = new Color(0.4f, 0.6f, 0.9f, 1f);
                Color ambientMean = MeanRgb(Draw(renderer, camera, ambient).lighting);
                report.ambientMeanLuminance = ambientMean.r * 0.2126f + ambientMean.g * 0.7152f + ambientMean.b * 0.0722f;
                report.ambientIlluminatesClouds = report.ambientMeanLuminance > 0.0001f;
                Require(report.ambientIlluminatesClouds, "Sky ambient light did not illuminate clouds without sunlight.");
                report.finiteGpuValues = true;
            }
        }

        /// <summary>시간의 즉시 변경, 중간 재전환, 범위 제한과 기존 날씨 밀도를 검사합니다.</summary>
        private static void CheckTransitions(GameObject host, Report report)
        {
            CloudLightingProfile profile = ScriptableObject.CreateInstance<CloudLightingProfile>();
            CloudWeatherProfile weatherTarget = ScriptableObject.CreateInstance<CloudWeatherProfile>();
            CloudWeatherProfile weatherOther = ScriptableObject.CreateInstance<CloudWeatherProfile>();

            try
            {
                CloudWeatherController weather = host.AddComponent<CloudWeatherController>();
                CloudTimeOfDayController time = host.AddComponent<CloudTimeOfDayController>();
                time.profile = profile;
                time.weatherSource = weather;
                time.autoAdvance = false;
                time.SetTime(profile.StartHour);
                time.TransitionTo(profile.EndHour, 0f);
                report.zeroDurationTransitionImmediate = Mathf.Abs(time.TimeOfDay - profile.EndHour) <= Tolerance;
                Require(report.zeroDurationTransitionImmediate, "Zero-duration time transition did not apply immediately.");

                time.SetTime(profile.StartHour);
                time.TransitionTo(profile.EndHour, 10f);
                time.Advance(4f);
                float interruptedHour = time.TimeOfDay;
                CloudLightingState interruptedLighting = time.CurrentLighting;
                Require(interruptedHour > profile.StartHour && interruptedHour < profile.EndHour, "Time transition did not advance.");
                time.TransitionTo(profile.StartHour, 6f);
                CloudLightingState restartedLighting = time.CurrentLighting;
                report.interruptedTransitionContinuous = Mathf.Abs(time.TimeOfDay - interruptedHour) <= Tolerance && MaximumLightingDifference(interruptedLighting, restartedLighting) <= Tolerance;
                Require(report.interruptedTransitionContinuous, "Restarting the time transition caused a lighting jump.");
                time.Advance(6f);
                report.interruptedTransitionCompletes = Mathf.Abs(time.TimeOfDay - profile.StartHour) <= Tolerance;
                Require(report.interruptedTransitionCompletes, "Interrupted time transition did not reach the new target.");

                time.SetTime(-100f);
                bool lowerClamp = Mathf.Abs(time.TimeOfDay - profile.StartHour) <= Tolerance;
                time.SetTime(100f);
                bool upperClamp = Mathf.Abs(time.TimeOfDay - profile.EndHour) <= Tolerance;
                report.timeClampedToProfile = lowerClamp && upperClamp;
                Require(report.timeClampedToProfile, "Preview time escaped the supported profile range.");

                time.SetTime(profile.StartHour);
                time.autoAdvance = true;
                time.Advance(time.playbackSeconds + 1f);
                report.automaticPlaybackStopsAtEnd = !time.autoAdvance && Mathf.Abs(time.TimeOfDay - profile.EndHour) <= Tolerance;
                Require(report.automaticPlaybackStopsAtEnd, "Automatic playback did not stop at twilight.");

                weatherTarget.environment.densityMultiplier = 0.63f;
                weatherTarget.environment.skyDensityMultiplier = 0.42f;
                weatherTarget.environment.coverageOffset = 0.024f;
                weather.TransitionTo(weatherTarget, 0f);
                Vector3 samplePosition = new Vector3(500f, 3600f, -900f);
                CloudEnvironment expected = weather.Evaluate(samplePosition);
                time.SetTime(profile.StartHour);
                CloudEnvironment dayWeather = time.Evaluate(samplePosition);
                time.SetTime(profile.EndHour);
                CloudEnvironment eveningWeather = time.Evaluate(samplePosition);
                report.weatherDensityPreserved = SameDensity(expected, dayWeather) && SameDensity(expected, eveningWeather);
                Require(report.weatherDensityPreserved, "Time-of-day composition replaced the weather density settings.");
                Require(dayWeather.lighting.active && eveningWeather.lighting.active, "Time-of-day lighting was not supplied through CloudEnvironmentSource.");

                weatherOther.environment.densityMultiplier = 1.3f;
                weatherOther.environment.skyDensityMultiplier = 0.9f;
                weather.TransitionTo(weatherOther, 8f);
                weather.Advance(3f);
                CloudEnvironment beforeRestart = time.Evaluate(samplePosition);
                weather.TransitionTo(weatherTarget, 5f);
                CloudEnvironment afterRestart = time.Evaluate(samplePosition);
                report.weatherTransitionContinuous = SameDensity(beforeRestart, afterRestart) && MaximumLightingDifference(beforeRestart.lighting, afterRestart.lighting) <= Tolerance;
                Require(report.weatherTransitionContinuous, "Restarting weather caused a discontinuity in the composed environment.");
                weather.Advance(5f);
                Require(SameDensity(time.Evaluate(samplePosition), weatherTarget.environment), "Composed weather did not reach the restarted transition target.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
                UnityEngine.Object.DestroyImmediate(weatherTarget);
                UnityEngine.Object.DestroyImmediate(weatherOther);
            }
        }

        /// <summary>소유 프로필의 복구, 연결 해제, 외부 교체를 임시 장면 객체로 검사합니다.</summary>
        private static void CheckLifecycle(Report report)
        {
            GameObject host = new GameObject("Time of day lifecycle validation");
            host.hideFlags = HideFlags.HideAndDontSave;
            CloudLightingProfile lightingProfile = ScriptableObject.CreateInstance<CloudLightingProfile>();
            CloudWeatherProfile weatherProfile = ScriptableObject.CreateInstance<CloudWeatherProfile>();
            VolumeProfile shared = CreateSkyProfile(3f, 0.005f);
            VolumeProfile instance = CreateSkyProfile(4f, 0.007f);
            VolumeProfile replacement = CreateSkyProfile(5f, 0.009f);
            CloudTimeOfDayController time = null;
            Volume volume = null;

            try
            {
                // 검사 전용 Volume과 Light는 실제 화면에 영향을 주지 않도록 비활성 상태로 둡니다.
                volume = host.AddComponent<Volume>();
                volume.enabled = false;
                volume.sharedProfile = shared;
                Light sun = host.AddComponent<Light>();
                sun.enabled = false;
                sun.type = LightType.Directional;
                sun.lightUnit = LightUnit.Lux;
                sun.intensity = 12345f;
                sun.color = new Color(0.62f, 0.74f, 0.83f);
                sun.useColorTemperature = true;
                sun.transform.rotation = Quaternion.Euler(17f, 23f, 4f);
                Quaternion originalRotation = sun.transform.rotation;
                Color originalColor = sun.color;
                float originalIntensity = sun.intensity;
                LightUnit originalUnit = sun.lightUnit;

                CloudWeatherController weather = host.AddComponent<CloudWeatherController>();
                weatherProfile.environment.densityMultiplier = 0.57f;
                weatherProfile.environment.lighting = CloudLightingState.Sunset();
                weather.TransitionTo(weatherProfile, 0f);

                time = host.AddComponent<CloudTimeOfDayController>();
                time.profile = lightingProfile;
                time.weatherSource = weather;
                time.autoAdvance = false;
                time.skyVolume = volume;
                time.sun = sun;
                time.SetTime(lightingProfile.sunsetHour);
                Require(volume.HasInstantiatedProfile() && volume.profile != shared, "Time preview did not create an owned sky profile.");
                Require(Mathf.Abs(sun.intensity - time.CurrentLighting.sunLux) <= Tolerance, "Time preview did not apply sunlight.");

                time.enabled = false;
                report.sceneStateRestoredOnDisable = !volume.HasInstantiatedProfile() && volume.sharedProfile == shared
                    && SunRestored(sun, originalRotation, originalColor, originalIntensity, originalUnit);
                Require(report.sceneStateRestoredOnDisable, "Disabling time preview did not restore the original sky and sun.");

                time.enabled = true;
                time.SetTime(lightingProfile.EndHour);
                time.profile = null;
                // Play 모드 Update가 사용하는 Advance 경로에서도 연결 해제가 복구되어야 합니다.
                time.Advance(0f);
                report.profileRemovalRestoresScene = !volume.HasInstantiatedProfile() && !time.CurrentLighting.active
                    && SunRestored(sun, originalRotation, originalColor, originalIntensity, originalUnit);
                Require(report.profileRemovalRestoresScene, "Removing the time profile left stale sky or sunlight overrides.");
                CloudEnvironment upstream = weather.Evaluate(Vector3.zero);
                CloudEnvironment withoutTimeProfile = time.Evaluate(Vector3.zero);
                report.profileRemovalPreservesWeather = JsonUtility.ToJson(upstream) == JsonUtility.ToJson(withoutTimeProfile);
                Require(report.profileRemovalPreservesWeather, "A missing time profile replaced upstream weather values.");

                volume.sharedProfile = null;
                volume.profile = instance;
                time.profile = lightingProfile;
                time.ApplyCurrent();
                bool clonedInstance = volume.HasInstantiatedProfile() && volume.profile != instance && HasAerosolDensity(volume.profile, 0.007f);
                time.enabled = false;
                report.instanceOnlyVolumeSupported = clonedInstance && volume.HasInstantiatedProfile() && volume.profile == instance;
                Require(report.instanceOnlyVolumeSupported, "A Volume with only an instantiated profile could not be controlled and restored.");

                time.enabled = true;
                volume.profile = replacement;
                time.SetTime(lightingProfile.sunsetHour);
                report.externalProfileAdopted = volume.HasInstantiatedProfile() && volume.profile != replacement && HasAerosolDensity(volume.profile, 0.009f);
                Require(report.externalProfileAdopted, "An externally replaced profile was ignored.");
                time.enabled = false;
                report.externalProfileRestored = volume.HasInstantiatedProfile() && volume.profile == replacement;
                Require(report.externalProfileRestored, "Disabling preview did not restore the most recent external profile.");

                time.enabled = true;
                volume.profile = instance;
                time.enabled = false;
                report.externalReplacementPreservedOnDisable = volume.HasInstantiatedProfile() && volume.profile == instance;
                Require(report.externalReplacementPreservedOnDisable, "Disabling preview overwrote an external profile before the next update.");

                report.sourceProfilesUnchanged = HasFixedExposure(shared, 3f) && HasFixedExposure(instance, 4f) && HasFixedExposure(replacement, 5f)
                    && HasAerosolDensity(shared, 0.005f) && HasAerosolDensity(instance, 0.007f) && HasAerosolDensity(replacement, 0.009f);
                Require(report.sourceProfilesUnchanged, "Preview modified a source Volume profile.");
            }
            finally
            {
                if (time != null)
                {
                    time.enabled = false;
                }

                if (volume != null)
                {
                    volume.profile = null;
                    volume.sharedProfile = null;
                }

                UnityEngine.Object.DestroyImmediate(host);
                DestroySkyProfile(shared);
                DestroySkyProfile(instance);
                DestroySkyProfile(replacement);
                UnityEngine.Object.DestroyImmediate(lightingProfile);
                UnityEngine.Object.DestroyImmediate(weatherProfile);
            }
        }

        /// <summary>원본과 복제본을 구분할 수 있는 독립 하늘/노출 프로필을 만듭니다.</summary>
        private static VolumeProfile CreateSkyProfile(float exposureValue, float aerosolDensity)
        {
            VolumeProfile profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.hideFlags = HideFlags.HideAndDontSave;
            PhysicallyBasedSky sky = profile.Add<PhysicallyBasedSky>(true);
            sky.hideFlags = HideFlags.HideAndDontSave;
            sky.aerosolDensity.Override(aerosolDensity);
            Exposure exposure = profile.Add<Exposure>(true);
            exposure.hideFlags = HideFlags.HideAndDontSave;
            exposure.fixedExposure.Override(exposureValue);
            return profile;
        }

        /// <summary>검사용 프로필과 그 컴포넌트를 모두 해제합니다.</summary>
        private static void DestroySkyProfile(VolumeProfile profile)
        {
            if (profile == null)
            {
                return;
            }

            foreach (VolumeComponent component in profile.components)
            {
                UnityEngine.Object.DestroyImmediate(component);
            }

            profile.components.Clear();
            UnityEngine.Object.DestroyImmediate(profile);
        }

        /// <summary>빛의 방향, 색, 물리 단위와 색온도 사용 여부가 원래 상태인지 검사합니다.</summary>
        private static bool SunRestored(Light sun, Quaternion rotation, Color color, float intensity, LightUnit unit)
        {
            return Quaternion.Angle(sun.transform.rotation, rotation) < 0.01f && sun.color == color
                && Mathf.Abs(sun.intensity - intensity) <= Tolerance && sun.lightUnit == unit && sun.useColorTemperature;
        }

        /// <summary>복제본이 어느 원본에서 만들어졌는지 고정 대기 설정으로 확인합니다.</summary>
        private static bool HasAerosolDensity(VolumeProfile profile, float expected)
        {
            PhysicallyBasedSky sky;
            return profile.TryGet(out sky) && Mathf.Abs(sky.aerosolDensity.value - expected) <= Tolerance;
        }

        /// <summary>시간 미리보기가 원본 노출 값을 변경하지 않았는지 확인합니다.</summary>
        private static bool HasFixedExposure(VolumeProfile profile, float expected)
        {
            Exposure exposure;
            return profile.TryGet(out exposure) && Mathf.Abs(exposure.fixedExposure.value - expected) <= Tolerance;
        }

        /// <summary>생산용 GPU 경로를 실행하고 빛과 투과율의 유효성을 확인합니다.</summary>
        private static GpuFrame Draw(LostSkiesCloudRenderer renderer, Camera camera, CloudLightingState lighting)
        {
            renderer.skyLighting = lighting;
            using (CommandBuffer commands = new CommandBuffer())
            {
                renderer.Render(commands, camera, lighting.GetSunDirection(), lighting.sunColor, 1f);
                Graphics.ExecuteCommandBuffer(commands);
            }

            GpuFrame frame = new GpuFrame();
            frame.lighting = Read(renderer.lighting);
            frame.transmission = Read(renderer.transmittance);
            for (int pixel = 0; pixel < frame.lighting.Length; pixel++)
            {
                CheckFinite(frame.lighting[pixel]);
                CheckFinite(frame.transmission[pixel]);
                Require(frame.lighting[pixel].r >= 0f && frame.lighting[pixel].g >= 0f && frame.lighting[pixel].b >= 0f, "GPU radiance is negative.");
                Require(frame.transmission[pixel].r >= 0f && frame.transmission[pixel].r <= 1f, "GPU transmittance is outside [0, 1].");
            }

            return frame;
        }

        /// <summary>검증에서만 동기 GPU 읽기를 사용하여 실제 적분 출력을 가져옵니다.</summary>
        private static Color[] Read(RenderTexture texture)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBAFloat);
            request.WaitForCompletion();
            Require(!request.hasError, "Lighting validation GPU readback failed.");
            return request.GetData<Color>().ToArray();
        }

        /// <summary>RGBA 전체가 유한한 수인지 확인하여 깊이 출력까지 검사합니다.</summary>
        private static void CheckFinite(Color value)
        {
            for (int channel = 0; channel < 4; channel++)
            {
                Require(!float.IsNaN(value[channel]) && !float.IsInfinity(value[channel]), "GPU produced NaN or infinity.");
            }
        }

        /// <summary>두 이미지의 색과 깊이에 나타난 최대 절대 차이를 반환합니다.</summary>
        private static float MaximumDifference(Color[] first, Color[] second)
        {
            Require(first.Length == second.Length, "GPU frame sizes differ.");
            float maximum = 0f;
            for (int pixel = 0; pixel < first.Length; pixel++)
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    maximum = Mathf.Max(maximum, Mathf.Abs(first[pixel][channel] - second[pixel][channel]));
                }
            }

            return maximum;
        }

        /// <summary>깊이를 제외하고 두 이미지의 적분 RGB 차이를 구합니다.</summary>
        private static float MaximumRgbDifference(Color[] first, Color[] second)
        {
            Require(first.Length == second.Length, "GPU frame sizes differ.");
            float maximum = 0f;
            for (int pixel = 0; pixel < first.Length; pixel++)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    maximum = Mathf.Max(maximum, Mathf.Abs(first[pixel][channel] - second[pixel][channel]));
                }
            }

            return maximum;
        }

        /// <summary>빛이 없는 검사에서 잔여 발광의 최대값을 찾습니다.</summary>
        private static float MaximumRgb(Color[] pixels)
        {
            float maximum = 0f;
            foreach (Color pixel in pixels)
            {
                maximum = Mathf.Max(maximum, pixel.r, pixel.g, pixel.b);
            }

            return maximum;
        }

        /// <summary>화면 평균 RGB를 계산하여 빛의 색과 주변광 기여를 비교합니다.</summary>
        private static Color MeanRgb(Color[] pixels)
        {
            Color total = Color.clear;
            foreach (Color pixel in pixels)
            {
                total.r += pixel.r;
                total.g += pixel.g;
                total.b += pixel.b;
            }

            return total / Mathf.Max(1, pixels.Length);
        }

        /// <summary>시간대가 바뀌어도 날씨 공급자의 형태 관련 값이 유지되는지 확인합니다.</summary>
        private static bool SameDensity(CloudEnvironment first, CloudEnvironment second)
        {
            return Mathf.Abs(first.densityMultiplier - second.densityMultiplier) <= Tolerance && Mathf.Abs(first.skyDensityMultiplier - second.skyDensityMultiplier) <= Tolerance && Mathf.Abs(first.coverageOffset - second.coverageOffset) <= Tolerance;
        }

        /// <summary>전환을 재시작하는 순간 모든 공개 조명 값이 그대로인지 확인합니다.</summary>
        private static float MaximumLightingDifference(CloudLightingState first, CloudLightingState second)
        {
            if (first.active != second.active)
            {
                return float.PositiveInfinity;
            }

            float maximum = Mathf.Max(Mathf.Abs(first.sunElevation - second.sunElevation), Mathf.Abs(first.sunAzimuth - second.sunAzimuth), Mathf.Abs(first.sunLux - second.sunLux));
            maximum = Mathf.Max(maximum, Mathf.Abs(first.ambientIntensity - second.ambientIntensity), Mathf.Abs(first.directMultiplier - second.directMultiplier), Mathf.Abs(first.silverLining - second.silverLining));
            maximum = Mathf.Max(maximum, Mathf.Abs(first.skyExposure - second.skyExposure), Mathf.Abs(first.exposureEV - second.exposureEV));
            maximum = Mathf.Max(maximum, Mathf.Abs(first.skyBlend - second.skyBlend));
            Color[] firstColors = { first.sunColor, first.ambientSkyColor, first.ambientHorizonColor, first.horizonTint, first.zenithTint, first.skyTopColor, first.skyHorizonColor, first.skyLowerColor };
            Color[] secondColors = { second.sunColor, second.ambientSkyColor, second.ambientHorizonColor, second.horizonTint, second.zenithTint, second.skyTopColor, second.skyHorizonColor, second.skyLowerColor };
            maximum = Mathf.Max(maximum, MaximumDifference(firstColors, secondColors));
            return maximum;
        }

        /// <summary>조건이 실패하면 보고서에서 원인을 확인할 수 있도록 명확히 중단합니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}

