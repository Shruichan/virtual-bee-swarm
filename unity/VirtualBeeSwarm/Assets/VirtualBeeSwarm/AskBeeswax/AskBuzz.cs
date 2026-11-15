using UnityEngine;

namespace VirtualBeeSwarm.Ask
{
    /// <summary>
    /// Beeswax's thinking buzz: a soft wing-buzz loop on his body while the Hive works on a reply. The clip is built once
    /// (0.6 s, 22 kHz, procedural: a filtered 210 Hz saw with a 25 Hz wing flutter, both whole cycles so the loop is
    /// seamless); nothing is allocated per frame. Low volume, 3D, muted while the mic listens (the Hive must not hear it).
    /// The Satie scene has no thinking cue, so this is its own AudioSource; tuning ask.buzz=false or sound.on=false turns
    /// it off.
    /// </summary>
    public class AskBuzz : MonoBehaviour
    {
        public float volume = 0.12f;

        static AudioClip _clip;
        AudioSource _src;
        GameNet _net;
        bool _want;

        public bool Playing => _want;

        public void Init(GameNet net)
        {
            _net = net;
            if (_src != null) return;
            _src = gameObject.AddComponent<AudioSource>();
            _src.clip = Clip();
            _src.loop = true;
            _src.playOnAwake = false;
            _src.volume = volume;
            _src.spatialBlend = 1f;
            _src.rolloffMode = AudioRolloffMode.Linear;
            _src.minDistance = 0.3f;
            _src.maxDistance = 4f;
            _src.dopplerLevel = 0f;
            _src.priority = 200;
        }

        public void Play()
        {
            _want = true;
        }

        public void Stop()
        {
            _want = false;
            if (_src != null && _src.isPlaying) _src.Stop();
        }

        void Update()
        {
            if (_src == null) return;
            bool on = _want && !(_net != null && (_net.MicListening || _net.IsSpeaking));
            if (on && !_src.isPlaying) _src.Play();
            else if (!on && _src.isPlaying) _src.Pause();
            if (on) _src.pitch = 1f + 0.035f * Mathf.Sin(Time.time * 2.7f);
        }

        void OnDisable() => Stop();

        static AudioClip Clip()
        {
            if (_clip != null) return _clip;
            const int rate = 22050;
            const float seconds = 0.6f, f0 = 210f, flutter = 25f; // 126 and 15 whole cycles: a seamless loop
            int n = Mathf.RoundToInt(rate * seconds);
            var data = new float[n];
            float lp = 0f;
            uint seed = 12345u;
            for (int pass = 0; pass < 2; pass++) // the second pass starts the filter from the loop's own end state
            {
                for (int i = 0; i < n; i++)
                {
                    float t = i / (float)rate;
                    float saw = 2f * ((t * f0) % 1f) - 1f;
                    seed = seed * 1664525u + 1013904223u;
                    float noise = ((seed >> 9) / 8388608f - 1f) * 0.08f;
                    float am = 0.55f + 0.45f * Mathf.Sin(2f * Mathf.PI * flutter * t);
                    lp += 0.18f * ((saw + noise) * am - lp);
                    data[i] = lp * 0.6f;
                }
            }
            _clip = AudioClip.Create("[VBS] ask_thinking_buzz", n, 1, rate, false);
            _clip.SetData(data, 0);
            return _clip;
        }
    }
}
