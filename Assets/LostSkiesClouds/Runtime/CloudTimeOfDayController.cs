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

        private Light attachedSun;
        private Quaternion savedSunRotation;
        private Color savedSunColor;
        private float savedSunIntensity;
        private LightUnit savedLightUnit;
        private bool savedColorTemperature;

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

        /// <summary>시간 전환 또는 낮부터 해질녘까지의 데모 재생을 진행합니다. 끝에서 정지합니다.</summary>
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

        /// <summary>현재 프로필이 제공하는 낮~해질녘 구간으로 시간을 제한합니다.</summary>
        private float ClampHour(float hours)
        {
            if (profile == null)
            {
                return Mathf.Clamp(hours, 0f, 24f);
            }

            return Mathf.Clamp(hours, profile.StartHour, profile.EndHour);
        }

        /// <summary>동일한 조명 값을 태양, HDRP 하늘, 노출에 적용합니다.</summary>
        public void ApplyCurrent()
        {
            if (profile == null || !isActiveAndEnabled)
            {
                ReleaseSky();
                RestoreSun();
                currentLighting = default(CloudLightingState);
                return;
            }

            timeOfDay = ClampHour(timeOfDay);
            currentLighting = profile.Evaluate(timeOfDay);
            PrepareSun();
            PrepareSky();

            if (attachedSun != null)
            {
                attachedSun.transform.rotation = Quaternion.Euler(currentLighting.sunElevation, currentLighting.sunAzimuth + 180f, 0f);
                attachedSun.color = currentLighting.sunColor;
                attachedSun.useColorTemperature = false;
                attachedSun.lightUnit = LightUnit.Lux;
                attachedSun.intensity = Mathf.Max(0f, currentLighting.sunLux);
            }

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

        /// <summary>새 태양을 제어하기 전에 원래 상태를 저장합니다.</summary>
        private void PrepareSun()
        {
            if (attachedSun == sun)
            {
                return;
            }

            RestoreSun();
            attachedSun = sun;
            if (attachedSun != null)
            {
                savedSunRotation = attachedSun.transform.rotation;
                savedSunColor = attachedSun.color;
                savedSunIntensity = attachedSun.intensity;
                savedLightUnit = attachedSun.lightUnit;
                savedColorTemperature = attachedSun.useColorTemperature;
            }
        }

        /// <summary>컴포넌트가 꺼질 때 하늘 복제본을 해제하고 태양의 원래 값을 복원합니다.</summary>
        private void OnDisable()
        {
            ReleaseSky();
            RestoreSun();
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

        /// <summary>태양 제어권을 돌려줄 때 저장한 방향, 색, 강도를 복원합니다.</summary>
        private void RestoreSun()
        {
            if (attachedSun != null)
            {
                attachedSun.transform.rotation = savedSunRotation;
                attachedSun.color = savedSunColor;
                attachedSun.lightUnit = savedLightUnit;
                attachedSun.intensity = savedSunIntensity;
                attachedSun.useColorTemperature = savedColorTemperature;
            }

            attachedSun = null;
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
                SetTime(profile.EndHour);
            }
        }
    }
}
