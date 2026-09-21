using UnityEngine;

namespace ClouDream.LostSkies
{
    public sealed class CloudWeatherController : CloudEnvironmentSource
    {
        [Header("시작 환경")]
        public CloudWeatherProfile initialProfile;

        [Header("실행 중 전환할 환경")]
        public CloudWeatherProfile selectedProfile;
        [Min(0f)]
        public float transitionSeconds = 10f;

        // 전환 도중에도 현재 값을 보존하는 환경 상태입니다.
        private CloudEnvironment current;
        private CloudEnvironment transitionStart;
        private CloudEnvironment transitionTarget;

        private float duration;
        private float elapsed;
        private bool initialized;

        /// <summary>컴포넌트 메뉴에서 선택한 환경으로 부드럽게 전환합니다.</summary>
        [ContextMenu("Transition To Selected Weather")]
        public void TransitionToSelectedWeather()
        {
            TransitionTo(selectedProfile, transitionSeconds);
        }

        /// <summary>컴포넌트를 켤 때 초기 프로필을 한 번 준비합니다.</summary>
        private void OnEnable()
        {
            EnsureInitialized();
        }

        /// <summary>게임 시간으로 전환을 진행합니다. 렌더 카메라 수와 무관하게 한 번만 갱신합니다.</summary>
        private void Update()
        {
            Advance(Time.deltaTime);
        }

        /// <summary>편집 모드는 프로필을 직접 읽고, 실행 중에는 현재 전환 값을 반환합니다.</summary>
        public override CloudEnvironment Evaluate(Vector3 worldPosition)
        {
            if (!Application.isPlaying && initialProfile != null)
            {
                return initialProfile.environment;
            }

            EnsureInitialized();
            return current;
        }

        /// <summary>진행 중인 전환의 현재 화면 값을 출발점으로 삼아 새 날씨로 전환합니다.</summary>
        public void TransitionTo(CloudWeatherProfile profile, float seconds)
        {
            if (profile == null)
            {
                return;
            }

            EnsureInitialized();
            transitionStart = current;
            transitionTarget = profile.environment;
            elapsed = 0f;
            duration = Mathf.Max(0f, seconds);
            if (duration == 0f)
            {
                current = transitionTarget;
            }
        }

        /// <summary>주어진 시간만큼 보간합니다. 테스트에서도 같은 전환 경로를 사용합니다.</summary>
        public void Advance(float deltaTime)
        {
            EnsureInitialized();
            if (duration <= 0f || elapsed >= duration)
            {
                return;
            }

            elapsed = Mathf.Min(duration, elapsed + Mathf.Max(0f, deltaTime));
            current = CloudEnvironment.Blend(transitionStart, transitionTarget, elapsed / duration);
        }

        /// <summary>첫 사용 시 초기 환경을 준비하여 렌더 순서에 대한 의존을 없앱니다.</summary>
        private void EnsureInitialized()
        {
            if (initialized)
            {
                return;
            }

            current = CloudEnvironment.ClearDay();
            if (initialProfile != null)
            {
                current = initialProfile.environment;
            }

            initialized = true;
        }
    }
}
