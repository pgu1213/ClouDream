using UnityEngine;
using UnityEngine.InputSystem;

namespace ClouDream
{
    [RequireComponent(typeof(Camera))]
    public sealed class CloudSeaFlight : MonoBehaviour
    {
        [Header("이동과 시점")]
        [Min(1f)]
        public float speed = 180f;
        public float boost = 4f;
        public float sensitivity = 0.12f;

        [Header("화면 안내")]
        public bool showHelp = true;
        private Vector3 startPosition;
        private Quaternion startRotation;
        private float yaw;

        private float pitch;

        /// <summary>초기 위치와 회전을 저장하여 R 키로 돌아올 수 있게 합니다.</summary>
        private void Start()
        {
            startPosition = transform.position;
            startRotation = transform.rotation;
            SyncAngles();
        }

        /// <summary>현재 Transform 회전을 마우스 시점의 누적 각도로 맞춥니다.</summary>
        private void SyncAngles()
        {
            yaw = transform.eulerAngles.y;
            pitch = Mathf.DeltaAngle(0f, transform.eulerAngles.x);
        }

        /// <summary>키보드 이동, 시점 북마크, 마우스 회전 입력을 처리합니다.</summary>
        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            UpdateBookmarks(keyboard);
            UpdateMovement(keyboard);
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                UpdateLook(keyboard, mouse);
                speed = Mathf.Clamp(speed * Mathf.Exp(mouse.scroll.ReadValue().y * 0.001f), 10f, 2000f);
            }
        }

        /// <summary>운해, 상공, 내부를 빠르게 확인하는 시점 단축키를 처리합니다.</summary>
        private void UpdateBookmarks(Keyboard keyboard)
        {
            if (keyboard.hKey.wasPressedThisFrame)
            {
                showHelp = !showHelp;
            }

            if (keyboard.rKey.wasPressedThisFrame || keyboard.digit1Key.wasPressedThisFrame)
            {
                SetView(startPosition, startRotation);
            }

            if (keyboard.digit2Key.wasPressedThisFrame)
            {
                SetView(new Vector3(0f, 9000f, -3200f), Quaternion.Euler(25f, 15f, 0f));
            }

            if (keyboard.digit3Key.wasPressedThisFrame)
            {
                SetView(new Vector3(0f, 920f, -3200f), Quaternion.identity);
            }
        }

        /// <summary>지정한 시점으로 이동하고 다음 마우스 입력과 회전을 일치시킵니다.</summary>
        private void SetView(Vector3 position, Quaternion rotation)
        {
            transform.SetPositionAndRotation(position, rotation);
            SyncAngles();
        }

        /// <summary>대각선 속도를 정규화하고 Shift 가속을 적용합니다.</summary>
        private void UpdateMovement(Keyboard keyboard)
        {
            Vector3 direction = Vector3.zero;
            if (keyboard.dKey.isPressed)
            {
                direction += transform.right;
            }

            if (keyboard.aKey.isPressed)
            {
                direction -= transform.right;
            }

            if (keyboard.wKey.isPressed)
            {
                direction += transform.forward;
            }

            if (keyboard.sKey.isPressed)
            {
                direction -= transform.forward;
            }

            if (keyboard.eKey.isPressed)
            {
                direction += Vector3.up;
            }

            if (keyboard.qKey.isPressed)
            {
                direction -= Vector3.up;
            }

            float currentSpeed = speed;
            if (keyboard.leftShiftKey.isPressed)
            {
                currentSpeed *= boost;
            }

            transform.position += Vector3.ClampMagnitude(direction, 1f) * currentSpeed * Time.unscaledDeltaTime;
        }

        /// <summary>우클릭 동안만 커서를 잠그고 pitch를 제한하여 시점을 회전합니다.</summary>
        private void UpdateLook(Keyboard keyboard, Mouse mouse)
        {
            bool looking = mouse.rightButton.isPressed && !keyboard.escapeKey.isPressed;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = !looking;
            if (looking)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Vector2 delta = mouse.delta.ReadValue();
                yaw += delta.x * sensitivity;
                pitch = Mathf.Clamp(pitch - delta.y * sensitivity, -89f, 89f);
                transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
            }
        }

        /// <summary>비행 컴포넌트가 꺼질 때 커서를 풀어 에디터 조작을 복구합니다.</summary>
        private void OnDisable()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        /// <summary>이동 키와 현재 고도, 속도를 작은 안내 패널에 표시합니다.</summary>
        private void OnGUI()
        {
            if (!showHelp)
            {
                return;
            }

            float scale = Mathf.Min(1f, Screen.width / 640f);
            Matrix4x4 previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(Vector3.one * scale);
            GUI.Box(new Rect(20f, 20f, 600f, 102f), "CLOUDREAM  /  CLOUD SEA FLIGHT");
            GUI.Label(new Rect(34f, 47f, 570f, 22f), "WASD move   Q/E altitude   RMB look   Shift boost   Wheel speed");
            GUI.Label(new Rect(34f, 69f, 570f, 22f), "1 sea   2 upper sky   3 inside   R reset   H hide");
            GUI.Label(new Rect(34f, 91f, 570f, 22f), $"Altitude {transform.position.y:0} m   Speed {speed:0} m/s");
            GUI.matrix = previousMatrix;
        }
    }
}
