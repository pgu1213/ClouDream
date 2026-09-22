using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies.Editor
{
    /// <summary>기존 시간대 호환과 새 달빛 밤의 상태·전환·광원 소유권을 실제 컴포넌트로 검사합니다.</summary>
    public static class CloudNightValidation
    {
        private const float Tolerance = 0.0001f;

        [Serializable]
        public sealed class Report
        {
            public bool passed;
            public bool legacyProfilesUnchanged;
            public int legacySamples;
            public bool fourStateEndpoints;
            public bool interruptedTransitionContinuous;
            public bool interruptedTransitionCompletes;
            public bool twilightShortcutPreserved;
            public bool zeroDurationAndRangeValid;
            public bool automaticPlaybackStopsAtNight;
            public bool keyLightFollowsMoon;
            public bool atmosphereDirectionContinuous;
            public float twilightAtmosphereAngleDifference;
            public bool keyDirectionMatchesSceneMoon;
            public bool disabledRestoresLights;
            public bool nullProfileRestoresLights;
            public bool replacedLightRestored;
            public bool roleSwapRestoresLights;
            public bool legacyProfileReleasesMoon;
            public bool weatherDensityPreserved;
            public bool weatherTintsMoonAndPalette;
            public float maximumLegacyDifference;
            public string error;
        }

        private struct LightSnapshot
        {
            public Quaternion rotation;
            public Color color;
            public float intensity;
            public LightUnit unit;
            public bool temperature;
            public bool enabled;

            /// <summary>외부에서 설정한 광원 상태를 복원 비교용으로 저장합니다.</summary>
            public static LightSnapshot Capture(Light light)
            {
                LightSnapshot snapshot = new LightSnapshot();
                snapshot.rotation = light.transform.rotation;
                snapshot.color = light.color;
                snapshot.intensity = light.intensity;
                snapshot.unit = light.lightUnit;
                snapshot.temperature = light.useColorTemperature;
                snapshot.enabled = light.enabled;
                return snapshot;
            }

            /// <summary>광원의 방향과 모든 소유 필드가 검사 전 값으로 복원됐는지 비교합니다.</summary>
            public bool Matches(Light light)
            {
                return Quaternion.Angle(rotation, light.transform.rotation) < 0.01f
                    && ColorDifference(color, light.color) <= Tolerance
                    && Mathf.Abs(intensity - light.intensity) <= Tolerance
                    && unit == light.lightUnit && temperature == light.useColorTemperature
                    && enabled == light.enabled;
            }
        }

        /// <summary>장면을 저장하지 않고 임시 객체로 밤 확장을 검증한 뒤 별도 JSON 보고서를 남깁니다.</summary>
        [MenuItem("ClouDream/Lost Skies/Validate Moonlit Night")]
        public static Report Run()
        {
            Report report = new Report();
            try
            {
                CheckLegacy(report);
                CheckTransitions(report);
                CheckLightOwnership(report);
                CheckWeather(report);
                report.passed = true;
            }
            catch (Exception exception)
            {
                report.error = exception.ToString();
                Debug.LogException(exception);
            }
            finally
            {
                Directory.CreateDirectory("Screenshots");
                File.WriteAllText("Screenshots/CloudNight-Validation.json", JsonUtility.ToJson(report, true));
            }

            return report;
        }

        /// <summary>기존 에셋과 기본 프로필이 종전의 세 구간 함수와 같은 결과인지 여러 시각에서 검사합니다.</summary>
        private static void CheckLegacy(Report report)
        {
            CloudLightingProfile temporary = ScriptableObject.CreateInstance<CloudLightingProfile>();
            try
            {
                CloudLightingProfile[] profiles =
                {
                    temporary,
                    AssetDatabase.LoadAssetAtPath<CloudLightingProfile>("Assets/LostSkiesClouds/Presets/TimeOfDay-Lighting.asset"),
                    AssetDatabase.LoadAssetAtPath<CloudLightingProfile>("Assets/LostSkiesClouds/Presets/TimeOfDay-Concept.asset")
                };

                foreach (CloudLightingProfile profile in profiles)
                {
                    Require(profile != null, "A legacy lighting profile is missing.");
                    Require(!profile.enableMoonlitNight, "A legacy asset unexpectedly opted into night.");
                    string original = JsonUtility.ToJson(profile);
                    for (int index = 0; index <= 128; index++)
                    {
                        float hour = Mathf.Lerp(-1f, 25f, index / 128f);
                        CloudLightingState expected = EvaluateLegacy(profile, hour);
                        CloudLightingState actual = profile.Evaluate(hour);
                        report.maximumLegacyDifference = Mathf.Max(report.maximumLegacyDifference,
                            StateDifference(expected, actual));
                        report.legacySamples++;
                    }

                    Require(original == JsonUtility.ToJson(profile), "Reading a legacy profile mutated its serialized state.");
                    Require(Mathf.Abs(profile.EndHour - Mathf.Max(profile.dayHour + 0.02f, profile.twilightHour)) <= Tolerance,
                        "A legacy profile no longer stops at twilight.");
                }

                report.legacyProfilesUnchanged = report.maximumLegacyDifference <= Tolerance;
                Require(report.legacyProfilesUnchanged, "Legacy three-state lighting changed.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temporary);
            }
        }

        /// <summary>확장 전 계산식을 독립 보존하여 밤 추가가 기존 시간 구간을 늘리지 않았는지 검증합니다.</summary>
        private static CloudLightingState EvaluateLegacy(CloudLightingProfile profile, float hour)
        {
            float end = Mathf.Max(profile.dayHour + 0.02f, profile.twilightHour);
            float middle = Mathf.Clamp(profile.sunsetHour, profile.dayHour + 0.01f, end - 0.01f);
            if (hour <= middle)
            {
                float amount = Mathf.InverseLerp(profile.dayHour, middle, hour);
                return CloudLightingState.Blend(profile.day, profile.sunset, Mathf.SmoothStep(0f, 1f, amount));
            }

            float lateAmount = Mathf.InverseLerp(middle, end, hour);
            return CloudLightingState.Blend(profile.sunset, profile.twilight, Mathf.SmoothStep(0f, 1f, lateAmount));
        }

        /// <summary>네 시각의 선택과 밤 전환 도중 재전환, 끝점 제한, 자동 정지를 검사합니다.</summary>
        private static void CheckTransitions(Report report)
        {
            GameObject host = new GameObject("Night transition validation");
            host.hideFlags = HideFlags.HideAndDontSave;
            CloudLightingProfile profile = CreateConceptProfile();
            try
            {
                CloudTimeOfDayController time = host.AddComponent<CloudTimeOfDayController>();
                time.profile = profile;
                time.autoAdvance = false;
                float[] hours = { profile.StartHour, profile.sunsetHour, profile.TwilightHour, profile.EndHour };
                CloudLightingState[] states = { profile.day, profile.sunset, profile.twilight, profile.night };
                for (int index = 0; index < hours.Length; index++)
                {
                    time.SetTime(hours[index]);
                    Require(StateDifference(time.CurrentLighting, states[index]) <= Tolerance,
                        "A lighting keyframe did not evaluate to its authored state.");
                }

                report.fourStateEndpoints = true;

                // 직접광 선택은 황혼 직후 바뀌지만 강도가 남아 있는 하늘 잔광은 연속이어야 합니다.
                CloudLightingState beforeMoon = profile.Evaluate(profile.TwilightHour - 0.001f);
                CloudLightingState afterMoon = profile.Evaluate(profile.TwilightHour + 0.001f);
                report.twilightAtmosphereAngleDifference = Vector3.Angle(beforeMoon.GetAtmosphereDirection(), afterMoon.GetAtmosphereDirection());
                report.atmosphereDirectionContinuous = report.twilightAtmosphereAngleDifference < 0.05f
                    && !beforeMoon.UsesMoonKey() && afterMoon.UsesMoonKey();
                Require(report.atmosphereDirectionContinuous, "The atmosphere direction jumped when the moon became the key light.");

                time.SetTime(profile.StartHour);
                time.TransitionTo(profile.EndHour, 10f);
                time.Advance(7f);
                float interruptedHour = time.TimeOfDay;
                CloudLightingState interrupted = time.CurrentLighting;
                Require(interruptedHour > profile.TwilightHour, "The interrupted transition did not reach the moon blend interval.");
                time.TransitionTo(profile.sunsetHour, 4f);
                report.interruptedTransitionContinuous = Mathf.Abs(time.TimeOfDay - interruptedHour) <= Tolerance
                    && StateDifference(interrupted, time.CurrentLighting) <= Tolerance;
                Require(report.interruptedTransitionContinuous, "Interrupting night caused a state jump.");
                time.Advance(4f);
                report.interruptedTransitionCompletes = StateDifference(time.CurrentLighting, profile.sunset) <= Tolerance;
                Require(report.interruptedTransitionCompletes, "The interrupted night transition missed sunset.");

                time.PreviewTwilight();
                report.twilightShortcutPreserved = Mathf.Abs(time.TimeOfDay - profile.twilightHour) <= Tolerance
                    && time.CurrentLighting.GetKeyStrength() <= Tolerance;
                Require(report.twilightShortcutPreserved, "Twilight preview selected night.");
                time.TransitionTo(24f, 0f);
                bool upperClamp = Mathf.Abs(time.TimeOfDay - profile.EndHour) <= Tolerance;
                time.SetTime(-1f);
                report.zeroDurationAndRangeValid = upperClamp && Mathf.Abs(time.TimeOfDay - profile.StartHour) <= Tolerance;
                Require(report.zeroDurationAndRangeValid, "Night preview escaped its non-cyclic supported range.");

                time.autoAdvance = true;
                time.Advance(time.playbackSeconds + 1f);
                report.automaticPlaybackStopsAtNight = !time.autoAdvance && Mathf.Abs(time.TimeOfDay - profile.EndHour) <= Tolerance;
                Require(report.automaticPlaybackStopsAtNight, "Playback did not stop at the last night keyframe.");

                CloudLightingState night = time.CurrentLighting;
                night.sunColor = Color.red;
                report.keyLightFollowsMoon = night.UsesMoonKey() && night.GetKeyStrength() > 0f
                    && ColorDifference(night.GetKeyColor(), night.moonColor) <= Tolerance
                    && Vector3.Dot(night.GetKeyDirection(), night.GetMoonDirection()) > 0.99999f
                    && Vector3.Dot(night.GetAtmosphereDirection(), night.GetMoonDirection()) > 0.99999f;
                Require(report.keyLightFollowsMoon, "Night key light retained the sun direction or sun color.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        /// <summary>달 교체, 비활성화, 프로필 해제와 태양/달 역할 교환에서도 원래 광원이 복원되는지 검사합니다.</summary>
        private static void CheckLightOwnership(Report report)
        {
            GameObject host = new GameObject("Night light ownership validation");
            host.hideFlags = HideFlags.HideAndDontSave;
            CloudLightingProfile profile = CreateConceptProfile();
            CloudLightingProfile legacy = ScriptableObject.CreateInstance<CloudLightingProfile>();
            try
            {
                Light sun = CreateLight(host.transform, "Test sun", 1234f);
                Light moon = CreateLight(host.transform, "Test moon", 2345f);
                Light replacement = CreateLight(host.transform, "Replacement moon", 3456f);
                LightSnapshot sunOriginal = LightSnapshot.Capture(sun);
                LightSnapshot moonOriginal = LightSnapshot.Capture(moon);
                LightSnapshot replacementOriginal = LightSnapshot.Capture(replacement);

                CloudTimeOfDayController time = host.AddComponent<CloudTimeOfDayController>();
                time.autoAdvance = false;
                time.profile = profile;
                time.sun = sun;
                time.moon = moon;
                time.SetTime(profile.EndHour);
                CloudLightingState state = time.CurrentLighting;
                report.keyDirectionMatchesSceneMoon = moon.enabled
                    && Vector3.Dot(-moon.transform.forward, state.GetKeyDirection()) > 0.99999f
                    && ColorDifference(moon.color, state.GetKeyColor()) <= Tolerance
                    && Mathf.Abs(moon.intensity - state.moonLux) <= Tolerance;
                Require(report.keyDirectionMatchesSceneMoon, "The scene moon and cloud key light disagree.");

                time.moon = replacement;
                time.ApplyCurrent();
                report.replacedLightRestored = moonOriginal.Matches(moon) && replacement.enabled;
                Require(report.replacedLightRestored, "Replacing the moon left its previous object overridden.");

                time.enabled = false;
                report.disabledRestoresLights = sunOriginal.Matches(sun) && replacementOriginal.Matches(replacement);
                Require(report.disabledRestoresLights, "Disabling the controller did not restore both lights.");
                time.enabled = true;
                time.profile = null;
                time.ApplyCurrent();
                report.nullProfileRestoresLights = sunOriginal.Matches(sun) && replacementOriginal.Matches(replacement)
                    && !time.CurrentLighting.active;
                Require(report.nullProfileRestoresLights, "Removing the profile left stale moon or sun ownership.");

                time.profile = profile;
                time.SetTime(profile.EndHour);
                time.profile = legacy;
                time.ApplyCurrent();
                report.legacyProfileReleasesMoon = replacementOriginal.Matches(replacement);
                Require(report.legacyProfileReleasesMoon, "Returning to a legacy profile kept controlling the moon.");

                time.profile = profile;
                time.SetTime(profile.EndHour);
                time.sun = replacement;
                time.moon = sun;
                time.ApplyCurrent();
                time.enabled = false;
                report.roleSwapRestoresLights = sunOriginal.Matches(sun) && replacementOriginal.Matches(replacement)
                    && moonOriginal.Matches(moon);
                Require(report.roleSwapRestoresLights, "Swapping sun and moon captured an already overridden state.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(profile);
                UnityEngine.Object.DestroyImmediate(legacy);
            }
        }

        /// <summary>시간 공급자가 날씨 밀도를 보존하고 달빛과 세 입사광 색면에 상대 보정을 전달하는지 검사합니다.</summary>
        private static void CheckWeather(Report report)
        {
            GameObject host = new GameObject("Night weather validation");
            host.hideFlags = HideFlags.HideAndDontSave;
            CloudLightingProfile profile = CreateConceptProfile();
            CloudWeatherProfile weatherProfile = ScriptableObject.CreateInstance<CloudWeatherProfile>();
            try
            {
                CloudWeatherController weather = host.AddComponent<CloudWeatherController>();
                CloudTimeOfDayController time = host.AddComponent<CloudTimeOfDayController>();
                time.profile = profile;
                time.weatherSource = weather;
                time.autoAdvance = false;
                time.SetTime(profile.EndHour);
                CloudLightingState neutral = time.CurrentLighting;
                Require(StateDifference(CloudEnvironment.ClearDay().TintLighting(neutral), neutral) <= Tolerance,
                    "Clear weather changed the authored night palette.");

                weatherProfile.environment.densityMultiplier = 0.67f;
                weatherProfile.environment.skyDensityMultiplier = 0.39f;
                weatherProfile.environment.coverageOffset = 0.025f;
                weatherProfile.environment.sunlight *= new Color(0.6f, 0.8f, 1f);
                weatherProfile.environment.shadowTint *= new Color(0.8f, 0.7f, 0.6f);
                weather.TransitionTo(weatherProfile, 0f);
                CloudEnvironment evaluated = time.Evaluate(new Vector3(100f, 3200f, 400f));
                report.weatherDensityPreserved = Mathf.Abs(evaluated.densityMultiplier - 0.67f) <= Tolerance
                    && Mathf.Abs(evaluated.skyDensityMultiplier - 0.39f) <= Tolerance
                    && Mathf.Abs(evaluated.coverageOffset - 0.025f) <= Tolerance;
                Require(report.weatherDensityPreserved, "Night composition changed weather density.");

                report.weatherTintsMoonAndPalette = evaluated.lighting.moonColor.r < neutral.moonColor.r
                    && evaluated.lighting.cloudLightColor.r < neutral.cloudLightColor.r
                    && evaluated.lighting.cloudMidColor.r < neutral.cloudMidColor.r
                    && evaluated.lighting.cloudShadowColor.r < neutral.cloudShadowColor.r
                    && StateDifference(time.CurrentLighting, neutral) <= Tolerance;
                Require(report.weatherTintsMoonAndPalette, "Weather did not reach the moon and palette, or changed global sky state.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(profile);
                UnityEngine.Object.DestroyImmediate(weatherProfile);
            }
        }

        /// <summary>원본 에셋을 수정하지 않고 검사용 네 시간대 프로필을 생성합니다.</summary>
        private static CloudLightingProfile CreateConceptProfile()
        {
            CloudLightingProfile profile = ScriptableObject.CreateInstance<CloudLightingProfile>();
            profile.hideFlags = HideFlags.HideAndDontSave;
            profile.enableMoonlitNight = true;
            profile.day = CloudLightingState.ConceptDay();
            profile.sunset = CloudLightingState.ConceptSunset();
            profile.twilight = CloudLightingState.ConceptTwilight();
            profile.night = CloudLightingState.MoonlitNight();
            return profile;
        }

        /// <summary>고유한 초기값을 가진 임시 비활성 방향광을 생성합니다.</summary>
        private static Light CreateLight(Transform parent, string name, float intensity)
        {
            GameObject host = new GameObject(name);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.transform.SetParent(parent, false);
            Light light = host.AddComponent<Light>();
            light.enabled = false;
            light.type = LightType.Directional;
            light.lightUnit = LightUnit.Lux;
            light.intensity = intensity;
            light.color = new Color(0.62f, 0.73f, 0.84f);
            light.useColorTemperature = true;
            light.transform.rotation = Quaternion.Euler(17f, 29f, 3f);
            return light;
        }

        /// <summary>향후 공개 조명 필드가 추가되어도 전환 비교에서 누락되지 않도록 전체 상태를 비교합니다.</summary>
        private static float StateDifference(CloudLightingState first, CloudLightingState second)
        {
            float maximum = 0f;
            FieldInfo[] fields = typeof(CloudLightingState).GetFields(BindingFlags.Instance | BindingFlags.Public);
            foreach (FieldInfo field in fields)
            {
                if (field.FieldType == typeof(float))
                {
                    float difference = Mathf.Abs((float)field.GetValue(first) - (float)field.GetValue(second));
                    Require(!float.IsNaN(difference) && !float.IsInfinity(difference), "Lighting contains a non-finite scalar.");
                    maximum = Mathf.Max(maximum, difference);
                }
                else if (field.FieldType == typeof(Color))
                {
                    maximum = Mathf.Max(maximum, ColorDifference((Color)field.GetValue(first), (Color)field.GetValue(second)));
                }
                else if (field.FieldType == typeof(bool) && (bool)field.GetValue(first) != (bool)field.GetValue(second))
                {
                    maximum = Mathf.Max(maximum, 1f);
                }
            }

            return maximum;
        }

        /// <summary>색의 모든 채널에서 최대 차이를 구하고 비정상 입력을 검출합니다.</summary>
        private static float ColorDifference(Color first, Color second)
        {
            float maximum = Mathf.Max(Mathf.Abs(first.r - second.r), Mathf.Abs(first.g - second.g),
                Mathf.Abs(first.b - second.b), Mathf.Abs(first.a - second.a));
            Require(!float.IsNaN(maximum) && !float.IsInfinity(maximum), "Lighting contains a non-finite color.");
            return maximum;
        }

        /// <summary>실패 원인을 보고서에 보존할 수 있도록 조건이 맞지 않으면 예외를 발생시킵니다.</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
