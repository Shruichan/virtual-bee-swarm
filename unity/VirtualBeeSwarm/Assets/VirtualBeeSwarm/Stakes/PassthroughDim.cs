using UnityEngine;

namespace VirtualBeeSwarm.Stakes
{
    /// <summary>Where the dim goes. 0 = the real room as is, 1 = black. The Meta one is StakesPassthroughDimmer.</summary>
    public interface IStakesDimOutput
    {
        void ApplyDim(float amount);
    }

    /// <summary>
    /// The dim arbiter (V13): anyone asks for a dim under a key, the room gets the largest open request.
    ///   PassthroughDim.Request("stakes", 0.25f, 1.5f);   // ease to 25 % over 1.5 s
    ///   PassthroughDim.Release("stakes", 2f);            // ease back to nothing over 2 s, then forget the key
    /// Each key eases on its own (smoothstep), so a T-0 "t0" request and the "stakes" request never fight.
    /// The output is whatever registered as <see cref="Output"/> (StakesPassthroughDimmer on the Quest). Without one,
    /// e.g. on the desktop, it is a no-op that still logs and reports <see cref="Applied"/> (the autopilot reads it).
    /// No allocations per frame; at most 8 keys.
    /// </summary>
    public static class PassthroughDim
    {
        const int MaxKeys = 8;
        static readonly string[] _keys = new string[MaxKeys];
        static readonly float[] _from = new float[MaxKeys], _to = new float[MaxKeys], _start = new float[MaxKeys], _dur = new float[MaxKeys];
        static readonly bool[] _releasing = new bool[MaxKeys];
        static int _n;
        static float _lastSent = -1f;
        static Runner _runner;

        /// <summary>Set by the platform output (StakesPassthroughDimmer registers itself).</summary>
        public static IStakesDimOutput Output { get; set; }

        /// <summary>The dim applied this frame (max of the open requests; 0 when StakesFlags.Dim is off).</summary>
        public static float Applied { get; private set; }

        /// <summary>Ask for `amount` (0..1) under `key`, eased from where that key is now over `seconds`.</summary>
        public static void Request(string key, float amount, float seconds = 1f)
        {
            if (string.IsNullOrEmpty(key)) return;
            int i = IndexOf(key);
            if (i < 0)
            {
                if (_n >= MaxKeys) { Debug.LogWarning("[VBS][Stakes] dim: too many keys, '" + key + "' ignored"); return; }
                i = _n++;
                _keys[i] = key;
                _from[i] = 0f;
            }
            else _from[i] = ValueAt(i, Now());
            _to[i] = Mathf.Clamp01(amount);
            _start[i] = Now();
            _dur[i] = Mathf.Max(0f, seconds);
            _releasing[i] = false;
            Debug.Log("[VBS][Stakes] dim " + key + " -> " + _to[i].ToString("0.00") + " over " + _dur[i].ToString("0.0") + " s");
            EnsureRunner();
        }

        /// <summary>Ease `key` back to 0 over `seconds`, then drop it.</summary>
        public static void Release(string key, float seconds = 0.5f)
        {
            int i = IndexOf(key);
            if (i < 0) return;
            _from[i] = ValueAt(i, Now());
            _to[i] = 0f;
            _start[i] = Now();
            _dur[i] = Mathf.Max(0f, seconds);
            _releasing[i] = true;
            Debug.Log("[VBS][Stakes] dim " + key + " released over " + _dur[i].ToString("0.0") + " s");
            EnsureRunner();
        }

        /// <summary>Drop every key at once and put the room back (reset).</summary>
        public static void ReleaseAll()
        {
            _n = 0;
            for (int i = 0; i < MaxKeys; i++) _keys[i] = null;
            Applied = 0f;
            Send(0f, force: true);
        }

        /// <summary>The value one key contributes right now (0 if it isn't open).</summary>
        public static float Get(string key)
        {
            int i = IndexOf(key);
            return i < 0 ? 0f : ValueAt(i, Now());
        }

        /// <summary>The value `key` is easing toward (0 if it isn't open).</summary>
        public static float Target(string key)
        {
            int i = IndexOf(key);
            return i < 0 ? 0f : _to[i];
        }

        /// <summary>Send the current value again next frame (after something else touched the passthrough layer).</summary>
        public static void Resend() => _lastSent = -1f;

        static float Now() => Time.time;

        static int IndexOf(string key)
        {
            for (int i = 0; i < _n; i++)
                if (_keys[i] == key) return i;
            return -1;
        }

        static float ValueAt(int i, float now)
        {
            if (_dur[i] <= 0f) return _to[i];
            float k = Mathf.Clamp01((now - _start[i]) / _dur[i]);
            return Mathf.Lerp(_from[i], _to[i], k * k * (3f - 2f * k));
        }

        static void Tick()
        {
            float now = Now();
            float max = 0f;
            for (int i = 0; i < _n; i++)
            {
                float v = ValueAt(i, now);
                if (_releasing[i] && now - _start[i] >= _dur[i])
                {
                    // drop it: move the last key into this slot
                    _n--;
                    _keys[i] = _keys[_n]; _from[i] = _from[_n]; _to[i] = _to[_n];
                    _start[i] = _start[_n]; _dur[i] = _dur[_n]; _releasing[i] = _releasing[_n];
                    _keys[_n] = null;
                    i--;
                    continue;
                }
                if (v > max) max = v;
            }
            if (!StakesFlags.Dim) max = 0f;
            Applied = max;
            Send(max, force: false);
        }

        static void Send(float v, bool force)
        {
            if (!force && _lastSent >= 0f && Mathf.Abs(v - _lastSent) < 0.002f && !(v == 0f && _lastSent != 0f)) return;
            _lastSent = v;
            var o = Output;
            if (o == null) return;
            if (o is Object uo && uo == null) { Output = null; return; } // destroyed with its scene
            o.ApplyDim(v);
        }

        static void EnsureRunner()
        {
            if (!Application.isPlaying || _runner != null) return;
            var go = new GameObject("PassthroughDim (runner)");
            _runner = go.AddComponent<Runner>();
        }

        [DefaultExecutionOrder(1000)] // after the beats and the director, so a same-frame Request applies at once
        sealed class Runner : MonoBehaviour
        {
            void LateUpdate() => Tick();
            void OnDestroy()
            {
                if (_runner == this) _runner = null;
            }
        }
    }
}
