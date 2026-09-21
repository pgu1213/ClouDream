using UnityEngine;

namespace ClouDream.LostSkies
{
    [CreateAssetMenu(menuName = "ClouDream/Clouds/Time Of Day Lighting")]
    public sealed class CloudLightingProfile : ScriptableObject
    {
        [Header("세 기준 시각 (시)")]
        public float dayHour = 12f;
        public float sunsetHour = 18f;
        public float twilightHour = 19.25f;

        [Header("시간대별 공유 조명")]
        public CloudLightingState day = CloudLightingState.Day();

        public CloudLightingState sunset = CloudLightingState.Sunset();

        public CloudLightingState twilight = CloudLightingState.Twilight();

        public float StartHour
        {
            get { return dayHour; }
        }

        public float EndHour
        {
            get { return Mathf.Max(dayHour + 0.02f, twilightHour); }
        }

        /// <summary>기준 시각 사이를 부드럽게 보간하여 하늘과 구름에 사용할 동일 상태를 반환합니다.</summary>
        public CloudLightingState Evaluate(float hours)
        {
            float middle = Mathf.Clamp(sunsetHour, StartHour + 0.01f, EndHour - 0.01f);
            if (hours <= middle)
            {
                float amount = Mathf.InverseLerp(StartHour, middle, hours);
                return CloudLightingState.Blend(day, sunset, Mathf.SmoothStep(0f, 1f, amount));
            }

            float lateAmount = Mathf.InverseLerp(middle, EndHour, hours);
            return CloudLightingState.Blend(sunset, twilight, Mathf.SmoothStep(0f, 1f, lateAmount));
        }
    }
}
