using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ClouDream.LostSkies
{
    /// <summary>공유 시간 상태를 태양/하늘에 적용하고 기존 날씨 공급자의 결과와 합성합니다.</summary>
    [ExecuteAlways]
    public sealed class CloudTimeOfDayController : CloudEnvironmentSource
    {
        [Header("공유 조명과 기존 환경")]
        public CloudLightingProfile profile;
        public CloudEnvironmentSource weatherSource;

        [Header("장면 연결")]
        public Light sun;
        public Light moon;
        public Volume skyVolume;

        [Header("미리보기와 실행 시간")]
        [SerializeField, Range(0f, 24f)] private float timeOfDay = 12f;
        public bool autoAdvance = true;
        [Min(1f)] public float playbackSeconds = 180f;

        private bool transitioning;
        private float transitionStartHour;
        private float transitionTargetHour;
        private float transitionElapsed;
        private float transitionDuration;

        private CloudLightingState currentLighting;
        private Volume attachedVolume;
        private VolumeProfile previousProfile;
        private VolumeProfile ownedProfile;
        private PhysicallyBasedSky sky;
        private Exposure exposure;

        private readonly DirectionalLightOwnership sunOwnership = new DirectionalLightOwnership();
        private readonly DirectionalLightOwnership moonOwnership = new DirectionalLightOwnership();

        /// <summary>태양과 달에 공통으로 사용하는 광원 상태 저장과 복원 단위입니다.</summary>
        private sealed class DirectionalLightOwnership
        {
            private Light light;
            private Quaternion savedRotation;
            private Color savedColor;
            private float savedIntensity;
            private LightUnit savedUnit;
            private bool savedTemperature;
            private bool savedEnabled;

            public Light Target
            {
                get { return light; }
            }

            /// <summary>광원을 제어하기 전에 외부에서 설정한 원래 상태를 보관합니다.</summary>
            public void Attach(Light target)
            {
                light = target;
                if (light == null)
                {
                    return;
                }

                savedRotation = light.transform.rotation;
                savedColor = light.color;
                savedIntensity = light.intensity;
                savedUnit = light.lightUnit;
                savedTemperature = light.useColorTemperature;
                savedEnabled = light.enabled;
            }

            /// <summary>공유 천체 상태를 적용하고 달만 광량에 따라 활성화합니다.</summary>
            public void Apply(float elevation, float azimuth, Color color, float lux, bool controlEnabled)
            {
                if (light == null)
                {
                    return;
                }

                light.transform.rotation = Quaternion.Euler(elevation, azimuth + 180f, 0f);
                light.color = color;
                light.useColorTemperature = false;
                light.lightUnit = LightUnit.Lux;
                light.intensity = Mathf.Max(0f, lux);
                if (controlEnabled)
                {
                    light.enabled = light.intensity > 0f;
                }
            }

            /// <summary>광원의 방향, 색, 광량, 단위, 색온도와 활성 상태를 모두 복원합니다.</summary>
            public void Restore()
            {
                if (light != null)
                {
                    light.transform.rotation = savedRotation;
                    light.color = savedColor;
                    light.lightUnit = savedUnit;
                    light.intensity = savedIntensity;
                    light.useColorTemperature = savedTemperature;
                    light.enabled = savedEnabled;
                }

                light = null;
            }
        }

        public float TimeOfDay
        {
            get { return timeOfDay; }
        }

        public CloudLightingState CurrentLighting
        {
            get
            {
                if (profile != null)
                {
                    return profile.Evaluate(timeOfDay);
                }

                return default(CloudLightingState);
            }
        }

        /// <summary>에디터 미리보기와 Play 시작 시 현재 시간의 장면 조명을 맞춥니다.</summary>
        private void OnEnable()
        {
            ApplyCurrent();
        }

        /// <summary>실행 중 시간을 진행하고 편집 중에는 Inspector 변경을 미리 보여줍니다.</summary>
        private void Update()
        {
            if (Application.isPlaying)
            {
                Advance(Time.deltaTime);
            }
            else
            {
                ApplyCurrent();
            }
        }

        /// <summary>태양과 하늘에 적용한 조명을 기존 날씨/바이옴의 밀도 값에 결합합니다.</summary>
        public override CloudEnvironment Evaluate(Vector3 worldPosition)
        {
            CloudEnvironment environment = CloudEnvironment.ClearDay();
            if (weatherSource != null && weatherSource != this && weatherSource.isActiveAndEnabled)
            {
                environment = weatherSource.Evaluate(worldPosition);
            }

            if (profile != null)
            {
                environment.lighting = environment.TintLighting(CurrentLighting);
            }

            return environment;
        }

        /// <summary>지정 시각을 즉시 적용하고 진행 중이던 시간 전환을 종료합니다.</summary>
        public void SetTime(float hours)
        {
            timeOfDay = ClampHour(hours);
            transitioning = false;
            ApplyCurrent();
        }

        /// <summary>현재 시각부터 지정 시각으로 전환하여 재호출 시에도 조명이 이어지게 합니다.</summary>
        public void TransitionTo(float hours, float seconds)
        {
            if (seconds <= 0f)
            {
                SetTime(hours);
                return;
            }

            transitionStartHour = timeOfDay;
            transitionTargetHour = ClampHour(hours);
            transitionElapsed = 0f;
            transitionDuration = seconds;
            transitioning = true;
        }

        /// <summary>시간 전환 또는 프로필 범위의 데모 재생을 진행합니다. 황혼 또는 밤의 끝에서 정지합니다.</summary>
        public void Advance(float deltaTime)
        {
            if (profile == null)
            {
                ApplyCurrent();
                return;
            }

            float delta = Mathf.Max(0f, deltaTime);
            if (transitioning)
            {
                transitionElapsed = Mathf.Min(transitionDuration, transitionElapsed + delta);
                float amount = Mathf.SmoothStep(0f, 1f, transitionElapsed / transitionDuration);
                timeOfDay = Mathf.Lerp(transitionStartHour, transitionTargetHour, amount);
                if (transitionElapsed >= transitionDuration)
                {
                    transitioning = false;
                }
            }
            else if (autoAdvance)
            {
                float hoursPerSecond = (profile.EndHour - profile.StartHour) / Mathf.Max(1f, playbackSeconds);
                timeOfDay = ClampHour(timeOfDay + delta * hoursPerSecond);
                if (timeOfDay >= profile.EndHour)
                {
                    autoAdvance = false;
                }
            }

            ApplyCurrent();
        }

        /// <summary>현재 프로필의 낮~황혼 또는 낮~밤 구간으로 시간을 제한합니다. 자정 순환은 제공하지 않습니다.</summary>
        private float ClampHour(float hours)
        {
            if (profile == null)
            {
                return Mathf.Clamp(hours, 0f, 24f);
            }

            return Mathf.Clamp(hours, profile.StartHour, profile.EndHour);
        }

        /// <summary>동일한 조명 값을 태양과 달, HDRP 하늘, 노출에 적용합니다.</summary>
        public void ApplyCurrent()
        {
            if (profile == null || !isActiveAndEnabled)
            {
                ReleaseSky();
                RestoreLights();
                currentLighting = default(CloudLightingState);
                return;
            }

            timeOfDay = ClampHour(timeOfDay);
            currentLighting = profile.Evaluate(timeOfDay);
            PrepareLights();
            PrepareSky();

            sunOwnership.Apply(currentLighting.sunElevation, currentLighting.sunAzimuth,
                currentLighting.sunColor, currentLighting.sunLux, false);
            moonOwnership.Apply(currentLighting.moonElevation, currentLighting.moonAzimuth,
                currentLighting.moonColor, currentLighting.moonLux, true);

            if (sky != null)
            {
                sky.horizonTint.Override(currentLighting.horizonTint);
                sky.zenithTint.Override(currentLighting.zenithTint);
                sky.exposure.Override(currentLighting.skyExposure);
                sky.updateMode.Override(EnvironmentUpdateMode.OnChanged);
            }

            if (exposure != null)
            {
                exposure.mode.Override(ExposureMode.Fixed);
                exposure.fixedExposure.Override(currentLighting.exposureEV);
            }
        }

        /// <summary>원본 공유 에셋을 수정하지 않는 소유 프로필을 만들어 미리보기에 연결합니다.</summary>
        private void PrepareSky()
        {
            if (attachedVolume == skyVolume && ownedProfile != null && attachedVolume != null
                && attachedVolume.HasInstantiatedProfile() && attachedVolume.profile == ownedProfile)
            {
                return;
            }

            // 다른 시스템이 프로필을 교체했다면 이전 복제본을 해제하고 새 연결을 기준으로 다시 준비합니다.
            ReleaseSky();
            if (skyVolume == null)
            {
                return;
            }

            VolumeProfile source = skyVolume.sharedProfile;
            if (skyVolume.HasInstantiatedProfile())
            {
                previousProfile = skyVolume.profile;
                source = previousProfile;
            }

            if (source == null)
            {
                previousProfile = null;
                return;
            }

            attachedVolume = skyVolume;
            ownedProfile = ScriptableObject.CreateInstance<VolumeProfile>();
            ownedProfile.name = "Time Of Day - owned preview";
            ownedProfile.hideFlags = HideFlags.HideAndDontSave;
            foreach (VolumeComponent component in source.components)
            {
                VolumeComponent copy = Instantiate(component);
                copy.hideFlags = HideFlags.HideAndDontSave;
                ownedProfile.components.Add(copy);
            }

            ownedProfile.TryGet(out sky);
            ownedProfile.TryGet(out exposure);
            attachedVolume.profile = ownedProfile;
        }

        /// <summary>광원 연결 교체 시 두 광원을 먼저 복원하여 태양과 달의 역할 교환도 안전하게 처리합니다.</summary>
        private void PrepareLights()
        {
            Light desiredMoon = null;
            if (profile.enableMoonlitNight && moon != sun)
            {
                desiredMoon = moon;
            }

            if (sunOwnership.Target == sun && moonOwnership.Target == desiredMoon)
            {
                return;
            }

            RestoreLights();
            sunOwnership.Attach(sun);
            moonOwnership.Attach(desiredMoon);
        }

        /// <summary>컴포넌트가 꺼질 때 하늘 복제본을 해제하고 태양과 달의 원래 값을 복원합니다.</summary>
        private void OnDisable()
        {
            ReleaseSky();
            RestoreLights();
        }

        /// <summary>자신이 연결한 프로필만 복원하고 생성한 컴포넌트를 해제합니다.</summary>
        private void ReleaseSky()
        {
            if (attachedVolume != null && attachedVolume.HasInstantiatedProfile() && attachedVolume.profile == ownedProfile)
            {
                attachedVolume.profile = previousProfile;
            }

            if (ownedProfile != null)
            {
                foreach (VolumeComponent component in ownedProfile.components)
                {
                    CoreUtils.Destroy(component);
                }

                ownedProfile.components.Clear();
                CoreUtils.Destroy(ownedProfile);
            }

            attachedVolume = null;
            previousProfile = null;
            ownedProfile = null;
            sky = null;
            exposure = null;
        }

        /// <summary>태양과 달의 제어권을 돌려줄 때 각각 저장한 상태를 복원합니다.</summary>
        private void RestoreLights()
        {
            sunOwnership.Restore();
            moonOwnership.Restore();
        }

        /// <summary>컴포넌트 메뉴에서 낮 기준을 확인합니다.</summary>
        [ContextMenu("Preview/Day")]
        public void PreviewDay()
        {
            if (profile != null)
            {
                SetTime(profile.StartHour);
            }
        }

        /// <summary>컴포넌트 메뉴에서 일몰 기준을 확인합니다.</summary>
        [ContextMenu("Preview/Sunset")]
        public void PreviewSunset()
        {
            if (profile != null)
            {
                SetTime(profile.sunsetHour);
            }
        }

        /// <summary>컴포넌트 메뉴에서 해질녘 기준을 확인합니다.</summary>
        [ContextMenu("Preview/Twilight")]
        public void PreviewTwilight()
        {
            if (profile != null)
            {
                SetTime(profile.TwilightHour);
            }
        }

        /// <summary>밤을 지원하는 프로필에서 달빛 밤 기준을 확인합니다.</summary>
        [ContextMenu("Preview/Moonlit Night")]
        public void PreviewMoonlitNight()
        {
            if (profile != null && profile.enableMoonlitNight)
            {
                SetTime(profile.EndHour);
            }
        }
    }
}
