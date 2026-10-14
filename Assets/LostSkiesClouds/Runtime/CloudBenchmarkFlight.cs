using System;
using UnityEngine;

namespace ClouDream.LostSkies
{
    /// <summary>실시간 비행의 자세·태양·바람·중단 가능한 날씨를 하나의 경과 시간에서 결정합니다.</summary>
    public sealed class CloudBenchmarkFlight : IDisposable
    {
        public const float Speed = 720f;
        public const float TurnRate = 45f;

        private readonly LostSkiesCloudPass pass;
        private readonly CloudTimeOfDayController clock;
        private readonly CloudEnvironmentSource originalWeather;
        private readonly float originalHour;
        private readonly bool originalAdvance;
        private readonly float originalWind;
        private readonly bool originalOverride;
        private readonly float originalSimulationTime;
        private readonly Vector3 center;
        private readonly float secondsPerLeg;
        private readonly float dayCycleSeconds;
        private readonly GameObject weatherHost;
        private readonly CloudWeatherController weather;
        private readonly CloudWeatherProfile neutral;
        private readonly CloudWeatherProfile overcast;
        private readonly CloudWeatherProfile clearing;
        private readonly float[] weatherEvents;
        private float previousSeconds;
        private int weatherEvent;

        /// <summary>원래 환경 공급자를 보존하고 벤치마크 전용 날씨 공급자를 연결합니다.</summary>
        public CloudBenchmarkFlight(LostSkiesCloudPass target, CloudTimeOfDayController timeOfDay, Vector3 routeCenter, float legSeconds, float daySeconds = 0f)
        {
            pass = target;
            clock = timeOfDay;
            center = routeCenter;
            secondsPerLeg = legSeconds;
            dayCycleSeconds = secondsPerLeg * 3f;
            if (float.IsFinite(daySeconds) && daySeconds > 0f)
            {
                dayCycleSeconds = daySeconds;
            }
            weatherEvents = new[] { secondsPerLeg * 0.5f, secondsPerLeg * 0.8f, secondsPerLeg * 1.8f };
            originalWind = pass.windSpeed;
            originalOverride = pass.useSimulationTimeOverride;
            originalSimulationTime = pass.simulationTimeOverride;
            pass.windSpeed = 5f;
            pass.useSimulationTimeOverride = true;
            pass.simulationTimeOverride = 0f;
            neutral = ScriptableObject.CreateInstance<CloudWeatherProfile>();
            overcast = ScriptableObject.CreateInstance<CloudWeatherProfile>();
            clearing = ScriptableObject.CreateInstance<CloudWeatherProfile>();
            if (clock != null)
            {
                originalHour = clock.TimeOfDay;
                originalAdvance = clock.autoAdvance;
                originalWeather = clock.weatherSource;
                clock.autoAdvance = false;
                if (originalWeather != null)
                {
                    neutral.environment = originalWeather.Evaluate(center);
                }
            }

            overcast.environment = neutral.environment;
            overcast.environment.densityMultiplier *= 1.25f;
            overcast.environment.skyDensityMultiplier *= 1.3f;
            overcast.environment.coverageOffset = 0.035f;
            overcast.environment.brightness *= 0.8f;
            clearing.environment = neutral.environment;
            clearing.environment.densityMultiplier *= 0.85f;
            clearing.environment.skyDensityMultiplier *= 0.75f;
            clearing.environment.coverageOffset = -0.02f;
            weatherHost = new GameObject("Cloud benchmark temporary weather");
            weatherHost.hideFlags = HideFlags.HideAndDontSave;
            weather = weatherHost.AddComponent<CloudWeatherController>();
            // 게임 시간은 벤치마크가 정지하므로 Update는 0초만 진행하며 공급자 활성 상태는 유지합니다.
            weather.TransitionTo(neutral, 0f);
            if (clock != null)
            {
                clock.weatherSource = weather;
            }
        }

        /// <summary>초당 720m 이동과 첫 구간 초당 45도 회전을 FPS와 독립적으로 적용합니다.</summary>
        public void Apply(float seconds, out Vector3 position, out Quaternion rotation)
        {
            seconds = Mathf.Clamp(seconds, 0f, secondsPerLeg * 3f - 0.0001f);
            int leg = Mathf.FloorToInt(seconds / secondsPerLeg);
            float local = seconds - leg * secondsPerLeg;
            float halfDistance = Speed * secondsPerLeg * 0.5f;
            if (leg == 0)
            {
                position = center + new Vector3(-halfDistance + Speed * local, 1000f, -6800f);
                rotation = Quaternion.Euler(8f, TurnRate * local, 0f);
            }
            else if (leg == 1)
            {
                position = center + new Vector3(0f, 0f, -halfDistance + Speed * local);
                rotation = Quaternion.Euler(-6f + local, -15f + local * 1.5f, 0f);
            }
            else
            {
                position = new Vector3(1222f, 3300f, -6800f) + new Vector3(5000f, 0f, 11000f).normalized * (Speed * local);
                rotation = Quaternion.Euler(15f, 15f + local * 12f, 0f);
            }

            AdvanceWeather(seconds);
            pass.simulationTimeOverride = seconds;
            if (clock != null)
            {
                clock.SetTime(Mathf.Repeat(12f + seconds / dayCycleSeconds * 24f, 24f));
            }
        }

        /// <summary>이벤트를 건너뛴 느린 프레임도 정확한 중단 시점부터 다시 보간합니다.</summary>
        private void AdvanceWeather(float seconds)
        {
            while (weatherEvent < weatherEvents.Length && seconds >= weatherEvents[weatherEvent])
            {
                weather.Advance(weatherEvents[weatherEvent] - previousSeconds);
                previousSeconds = weatherEvents[weatherEvent];
                if (weatherEvent == 0)
                {
                    weather.TransitionTo(overcast, secondsPerLeg);
                }
                else if (weatherEvent == 1)
                {
                    weather.TransitionTo(clearing, secondsPerLeg * 0.7f);
                }
                else
                {
                    weather.TransitionTo(neutral, secondsPerLeg * 0.8f);
                }

                weatherEvent++;
            }

            weather.Advance(seconds - previousSeconds);
            previousSeconds = seconds;
        }

        /// <summary>계측의 임시 공급자와 시간·바람 설정을 복원하고 소유한 객체만 해제합니다.</summary>
        public void Dispose()
        {
            pass.windSpeed = originalWind;
            pass.useSimulationTimeOverride = originalOverride;
            pass.simulationTimeOverride = originalSimulationTime;
            if (clock != null)
            {
                clock.weatherSource = originalWeather;
                clock.autoAdvance = originalAdvance;
                clock.SetTime(originalHour);
            }

            UnityEngine.Object.Destroy(weatherHost);
            UnityEngine.Object.Destroy(neutral);
            UnityEngine.Object.Destroy(overcast);
            UnityEngine.Object.Destroy(clearing);
        }
    }
}
