using UnityEngine;
#if VBS_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Keyboard + mouse for desktop testing, working under either input backend:
    /// the legacy Input Manager when it is enabled, otherwise the Input System package.
    /// (Player Settings → Active Input Handling = "Input System Package" makes UnityEngine.Input throw.)
    /// All reads are allocation-free. On the Quest there is usually no keyboard and every read returns false.
    /// </summary>
    public static class GameInput
    {
        /// <summary>Global kill switch (e.g. turn off desktop keys in a kiosk build).</summary>
        public static bool Enabled = true;

        public static bool KeyDown(KeyCode k)
        {
            if (!Enabled) return false;
#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(k);
#elif VBS_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            var key = ToKey(k);
            return kb != null && key != Key.None && kb[key].wasPressedThisFrame;
#else
            return false;
#endif
        }

        public static bool KeyHeld(KeyCode k)
        {
            if (!Enabled) return false;
#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKey(k);
#elif VBS_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            var key = ToKey(k);
            return kb != null && key != Key.None && kb[key].isPressed;
#else
            return false;
#endif
        }

        public static bool KeyUp(KeyCode k)
        {
            if (!Enabled) return false;
#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyUp(k);
#elif VBS_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            var key = ToKey(k);
            return kb != null && key != Key.None && kb[key].wasReleasedThisFrame;
#else
            return false;
#endif
        }

        public static bool Shift => KeyHeld(KeyCode.LeftShift) || KeyHeld(KeyCode.RightShift);

        /// <summary>Digit n (1..9) pressed this frame, top row or keypad.</summary>
        public static bool DigitDown(int n)
        {
            if (n < 1 || n > 9) return false;
            return KeyDown(KeyCode.Alpha0 + n) || KeyDown(KeyCode.Keypad0 + n);
        }

        // ------------------------------------------------------------ mouse (0 = left, 1 = right, 2 = middle)
        public static bool MouseDown(int button)
        {
            if (!Enabled) return false;
#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetMouseButtonDown(button);
#elif VBS_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var b = MouseButton(button);
            return b != null && b.wasPressedThisFrame;
#else
            return false;
#endif
        }

        public static bool MouseHeld(int button)
        {
            if (!Enabled) return false;
#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetMouseButton(button);
#elif VBS_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var b = MouseButton(button);
            return b != null && b.isPressed;
#else
            return false;
#endif
        }

        public static bool MouseUp(int button)
        {
            if (!Enabled) return false;
#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetMouseButtonUp(button);
#elif VBS_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var b = MouseButton(button);
            return b != null && b.wasReleasedThisFrame;
#else
            return false;
#endif
        }

        /// <summary>Screen position in pixels; false if there is no mouse.</summary>
        public static bool TryMousePosition(out Vector2 position)
        {
            position = default;
            if (!Enabled) return false;
#if ENABLE_LEGACY_INPUT_MANAGER
            if (!Input.mousePresent) return false;
            position = Input.mousePosition;
            return true;
#elif VBS_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            if (m == null) return false;
            position = m.position.ReadValue();
            return true;
#else
            return false;
#endif
        }

        /// <summary>Mouse movement this frame, roughly in legacy "Mouse X/Y" units.</summary>
        public static Vector2 MouseDelta()
        {
            if (!Enabled) return Vector2.zero;
#if ENABLE_LEGACY_INPUT_MANAGER
            return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));
#elif VBS_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            return m == null ? Vector2.zero : m.delta.ReadValue() * 0.05f;
#else
            return Vector2.zero;
#endif
        }

#if !ENABLE_LEGACY_INPUT_MANAGER && VBS_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        static UnityEngine.InputSystem.Controls.ButtonControl MouseButton(int button)
        {
            var m = Mouse.current;
            if (m == null) return null;
            switch (button)
            {
                case 0: return m.leftButton;
                case 1: return m.rightButton;
                case 2: return m.middleButton;
                default: return null;
            }
        }

        static Key ToKey(KeyCode k)
        {
            if (k >= KeyCode.A && k <= KeyCode.Z) return Key.A + (k - KeyCode.A);
            if (k >= KeyCode.Alpha1 && k <= KeyCode.Alpha9) return Key.Digit1 + (k - KeyCode.Alpha1);
            if (k >= KeyCode.Keypad1 && k <= KeyCode.Keypad9) return Key.Numpad1 + (k - KeyCode.Keypad1);
            switch (k)
            {
                case KeyCode.Alpha0: return Key.Digit0;
                case KeyCode.Keypad0: return Key.Numpad0;
                case KeyCode.Space: return Key.Space;
                case KeyCode.Return: return Key.Enter;
                case KeyCode.KeypadEnter: return Key.NumpadEnter;
                case KeyCode.Backspace: return Key.Backspace;
                case KeyCode.Tab: return Key.Tab;
                case KeyCode.Escape: return Key.Escape;
                case KeyCode.LeftShift: return Key.LeftShift;
                case KeyCode.RightShift: return Key.RightShift;
                case KeyCode.LeftControl: return Key.LeftCtrl;
                case KeyCode.RightControl: return Key.RightCtrl;
                case KeyCode.LeftAlt: return Key.LeftAlt;
                case KeyCode.RightAlt: return Key.RightAlt;
                case KeyCode.UpArrow: return Key.UpArrow;
                case KeyCode.DownArrow: return Key.DownArrow;
                case KeyCode.LeftArrow: return Key.LeftArrow;
                case KeyCode.RightArrow: return Key.RightArrow;
                case KeyCode.F1: return Key.F1;
                case KeyCode.F2: return Key.F2;
                case KeyCode.F3: return Key.F3;
                default: return Key.None;
            }
        }
#endif
    }
}
