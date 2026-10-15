using UnityEngine;

namespace ClouDream.LostSkies
{
    /// <summary>절대 위치·시선·회전 속도로 다음 생성 지연 동안 필요한 방향을 예측합니다.</summary>
    internal sealed class CloudResidencyPrediction
    {
        private Vector3 previousPosition;

        private Vector3 previousForward;

        private double previousTime;

        private int previousFrame = -1;

        public Vector3 Direction { get; private set; }

        public float Speed { get; private set; }

        public float Turn { get; private set; }

        /// <summary>순간이동은 속도로 오인하지 않고 방향 예측을 재시작합니다. 원점 이동에는 절대 위치를 씁니다.</summary>
        public void Update(Vector3 position, Vector3 forward)
        {
            if (previousFrame == Time.frameCount)
            {
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            float delta = (float)(now - previousTime);
            Vector3 velocity = Vector3.zero;
            Turn = 0f;
            if (previousFrame >= 0 && delta > 0.0001f && delta < 1f)
            {
                Vector3 movement = position - previousPosition;
                if (movement.magnitude < 2500f)
                {
                    velocity = movement / delta;
                }
                Turn = Mathf.Clamp01(Vector3.Angle(previousForward, forward) / delta / 120f);
            }

            Speed = Mathf.Min(2000f, velocity.magnitude);
            Direction = forward;
            if (Speed > 10f)
            {
                Direction = (forward + velocity.normalized * 1.5f).normalized;
            }
            previousPosition = position;
            previousForward = forward;
            previousTime = now;
            previousFrame = Time.frameCount;
        }

        /// <summary>뒤 4km·옆 6km·앞 12~16km를 기준으로 회전 중에는 측면 준비 영역도 넓힙니다.</summary>
        public float Margin(Vector3 camera, Vector3 closest, CloudHybridQualityProfile quality)
        {
            Vector3 direction = closest - camera;
            if (direction.sqrMagnitude < 1f)
            {
                return quality.predictionForward;
            }
            float alignment = Vector3.Dot(Direction, direction.normalized);
            float margin = Mathf.Lerp(quality.predictionSide, quality.predictionForward, Mathf.Max(0f, alignment));
            if (alignment < 0f)
            {
                margin = Mathf.Lerp(quality.predictionSide, quality.predictionRear, -alignment);
            }
            return Mathf.Min(16000f, margin + Speed * quality.predictionSeconds + Turn * 3000f);
        }
    }
}
