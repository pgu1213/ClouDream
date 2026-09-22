using UnityEngine;
using UnityEngine.InputSystem;

namespace ClouDream.LostSkies
{
    /// <summary>비행 데모에서 조명 기준과 시간 재생을 확인하는 독립적인 입력 컴포넌트입니다.</summary>
    [RequireComponent(typeof(CloudTimeOfDayController))]
    public sealed class CloudTimeOfDayInput : MonoBehaviour
    {
        [Min(0f)] public float transitionSeconds = 6f;
        public bool showHelp = true;

        private CloudTimeOfDayController time;

        /// <summary>같은 오브젝트의 시간대 컨트롤러를 찾습니다.</summary>
        private void Awake()
        {
            time = GetComponent<CloudTimeOfDayController>();
        }

        /// <summary>4/5/6/7로 낮·석양·황혼·밤에 전환하고 T로 시간 자동 재생을 켜거나 멈춥니다.</summary>
        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || time == null || time.profile == null)
            {
                return;
            }

            if (keyboard.hKey.wasPressedThisFrame)
            {
                showHelp = !showHelp;
            }

            if (keyboard.digit4Key.wasPressedThisFrame)
            {
                SelectTime(time.profile.StartHour);
            }

            if (keyboard.digit5Key.wasPressedThisFrame)
            {
                SelectTime(time.profile.sunsetHour);
            }

            if (keyboard.digit6Key.wasPressedThisFrame)
            {
                SelectTime(time.profile.TwilightHour);
            }

            if (keyboard.digit7Key.wasPressedThisFrame && time.profile.enableMoonlitNight)
            {
                SelectTime(time.profile.EndHour);
            }

            if (keyboard.tKey.wasPressedThisFrame)
            {
                time.autoAdvance = !time.autoAdvance;
                time.SetTime(time.TimeOfDay);
                if (time.autoAdvance && time.TimeOfDay >= time.profile.EndHour)
                {
                    time.SetTime(time.profile.StartHour);
                }
            }
        }

        /// <summary>자동 시간을 멈추고 선택한 조명까지 부드럽게 이동합니다.</summary>
        private void SelectTime(float hours)
        {
            time.autoAdvance = false;
            time.TransitionTo(hours, transitionSeconds);
        }

        /// <summary>시간대 단축키와 현재 시각을 비행 안내 아래에 표시합니다.</summary>
        private void OnGUI()
        {
            if (!showHelp || time == null)
            {
                return;
            }

            float scale = Mathf.Min(1f, Screen.width / 640f);
            Matrix4x4 previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(Vector3.one * scale);
            int hour = Mathf.FloorToInt(time.TimeOfDay);
            int minute = Mathf.FloorToInt((time.TimeOfDay - hour) * 60f);
            string playback = "Preview";
            if (time.autoAdvance)
            {
                playback = "Time running";
            }

            string controls = "4 Day   5 Sunset   6 Twilight   T play / pause time   H hide";
            if (time.profile != null && time.profile.enableMoonlitNight)
            {
                controls = "4 Day   5 Sunset   6 Twilight   7 Moonlit night   T play / pause   H hide";
            }

            GUI.Box(new Rect(20f, 132f, 600f, 68f), "SKY LIGHTING");
            GUI.Label(new Rect(34f, 157f, 570f, 22f), controls);
            GUI.Label(new Rect(34f, 177f, 570f, 22f), $"{hour:00}:{minute:00}   {playback}");
            GUI.matrix = previousMatrix;
        }
    }
}
