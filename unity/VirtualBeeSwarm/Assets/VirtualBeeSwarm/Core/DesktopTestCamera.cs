using UnityEngine;
using UnityEngine.XR;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Lets teammates test without a headset. When no XR device is active (editor without Link, desktop build) this
    /// camera takes over: it becomes the MainCamera, other MainCamera-tagged cameras (e.g. the OVRCameraRig's center
    /// eye) and extra AudioListeners are switched off, and the desktop-only props (a fake table) show.
    /// With an XR device active (or on Android) it switches itself off and hides the desktop props, so the real
    /// table in passthrough is what you see. Hold the right mouse button to look, WASD to move, Q/E down/up.
    /// </summary>
    [DefaultExecutionOrder(-90)]
    public class DesktopTestCamera : MonoBehaviour
    {
        [SerializeField] Camera cam;
        [SerializeField] AudioListener listener;
        [Tooltip("Only for desktop testing: the fake table etc. Hidden in the headset.")]
        [SerializeField] GameObject[] desktopOnly = new GameObject[0];
        [SerializeField] float moveSpeed = 0.8f;
        [SerializeField] float lookSpeed = 2.5f;
        [Tooltip("Keep checking this long for an XR device that comes up late.")]
        [SerializeField] float xrGraceSeconds = 3f;

        public bool DesktopMode { get; private set; }

        Camera[] _disabledCams = new Camera[0];
        AudioListener[] _disabledListeners = new AudioListener[0];
        float _yaw, _pitch;

        void Awake()
        {
            if (cam == null) cam = GetComponent<Camera>();
            if (listener == null) listener = GetComponent<AudioListener>();
#if UNITY_ANDROID && !UNITY_EDITOR
            SetDesktop(false);
#else
            SetDesktop(!XRSettings.isDeviceActive);
#endif
            var e = transform.eulerAngles;
            _yaw = e.y;
            _pitch = e.x > 180f ? e.x - 360f : e.x;
        }

        void SetDesktop(bool on)
        {
            DesktopMode = on;
            if (cam != null) cam.enabled = on;
            if (listener != null) listener.enabled = on;
            for (int i = 0; i < desktopOnly.Length; i++)
                if (desktopOnly[i] != null) desktopOnly[i].SetActive(on);

            if (on)
            {
                gameObject.tag = "MainCamera";
                var cams = FindObjectsByType<Camera>(FindObjectsSortMode.None);
                int n = 0;
                for (int i = 0; i < cams.Length; i++) if (cams[i] != cam && cams[i].enabled && cams[i].CompareTag("MainCamera")) n++;
                _disabledCams = new Camera[n];
                n = 0;
                for (int i = 0; i < cams.Length; i++)
                    if (cams[i] != cam && cams[i].enabled && cams[i].CompareTag("MainCamera")) { cams[i].enabled = false; _disabledCams[n++] = cams[i]; }

                var ls = FindObjectsByType<AudioListener>(FindObjectsSortMode.None);
                n = 0;
                for (int i = 0; i < ls.Length; i++) if (ls[i] != listener && ls[i].enabled) n++;
                _disabledListeners = new AudioListener[n];
                n = 0;
                for (int i = 0; i < ls.Length; i++)
                    if (ls[i] != listener && ls[i].enabled) { ls[i].enabled = false; _disabledListeners[n++] = ls[i]; }
            }
            else
            {
                gameObject.tag = "Untagged";
                for (int i = 0; i < _disabledCams.Length; i++) if (_disabledCams[i] != null) _disabledCams[i].enabled = true;
                for (int i = 0; i < _disabledListeners.Length; i++) if (_disabledListeners[i] != null) _disabledListeners[i].enabled = true;
                _disabledCams = new Camera[0];
                _disabledListeners = new AudioListener[0];
            }
        }

        void Update()
        {
            if (DesktopMode && Time.realtimeSinceStartup < xrGraceSeconds + 5f && XRSettings.isDeviceActive)
            {
                SetDesktop(false); // a headset came up after all
                return;
            }
            if (!DesktopMode) return;

            float dt = Time.unscaledDeltaTime;
            if (GameInput.MouseHeld(1))
            {
                Vector2 d = GameInput.MouseDelta();
                _yaw += d.x * lookSpeed;
                _pitch = Mathf.Clamp(_pitch - d.y * lookSpeed, -85f, 85f);
                transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            }
            Vector3 move = Vector3.zero;
            if (GameInput.KeyHeld(KeyCode.W)) move += transform.forward;
            if (GameInput.KeyHeld(KeyCode.S)) move -= transform.forward;
            if (GameInput.KeyHeld(KeyCode.D)) move += transform.right;
            if (GameInput.KeyHeld(KeyCode.A)) move -= transform.right;
            if (GameInput.KeyHeld(KeyCode.E)) move += Vector3.up;
            if (GameInput.KeyHeld(KeyCode.Q)) move -= Vector3.up;
            if (move != Vector3.zero) transform.position += move * (moveSpeed * dt);
        }
    }
}
