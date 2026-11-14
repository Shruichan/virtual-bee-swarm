using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace Beeswax
{
    /// <summary>
    /// The one microphone recording shared by everything that listens (HumDetector, BeeswaxVoiceInput).
    /// Unity has a single recording per device: a second Microphone.Start restarts it under the first user, and
    /// Microphone.End stops it for everyone. So components never call those directly; they Acquire/Release here
    /// (reference-counted) and each reads the looping clip with its own MicCursor. Main thread only.
    /// The first Acquire picks the device and rate; later ones join whatever is running.
    /// </summary>
    public static class BeeswaxMic
    {
        /// <summary>Length of the looping clip. Readers that fall behind by more than half of it skip ahead.</summary>
        public const int ClipSeconds = 4;

        static readonly HashSet<object> s_users = new HashSet<object>();
        static bool s_permissionAsked, s_warnedDevice;
        static int s_checkedFrame = -1;

        public static AudioClip Clip { get; private set; }
        /// <summary>Device in use (null = the default mic).</summary>
        public static string Device { get; private set; }
        public static int Frequency => Clip != null ? Clip.frequency : 0;
        public static bool IsRecording => Clip != null;
        public static int UserCount => s_users.Count;
        /// <summary>Write position in Clip (Microphone.GetPosition), or -1 when not recording.</summary>
        public static int Position => Clip != null ? Microphone.GetPosition(Device) : -1;

        // Enter Play Mode without a domain reload keeps statics: start clean.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_users.Clear();
            Clip = null;
            Device = null;
            s_permissionAsked = s_warnedDevice = false;
            s_checkedFrame = -1;
        }

        public static bool HasPermission
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return Permission.HasUserAuthorizedPermission(Permission.Microphone);
#else
                return true;
#endif
            }
        }

        /// <summary>
        /// Start or join the recording. False while there is no mic or no permission (Android: asks once, then
        /// keeps returning false until granted); just call it again later (e.g. every Update while you need audio).
        /// </summary>
        public static bool Acquire(object user, string deviceName = null, int sampleRate = BeeswaxProtocol.MicSampleRate)
        {
            if (user == null) return false;
            EnsureRecording();
            if (Clip != null)
            {
                if (!string.IsNullOrEmpty(deviceName) && deviceName != Device && !s_warnedDevice)
                {
                    s_warnedDevice = true;
                    Debug.LogWarning($"[Beeswax] mic '{deviceName}' requested but '{Device ?? "default"}' is already recording; sharing it.");
                }
                s_users.Add(user);
                return true;
            }
            if (!HasPermission)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (!s_permissionAsked) { s_permissionAsked = true; Permission.RequestUserPermission(Permission.Microphone); }
#endif
                return false;
            }
            if (Microphone.devices.Length == 0) return false;
            if (!Start(string.IsNullOrEmpty(deviceName) ? null : deviceName, sampleRate)) return false;
            s_users.Add(user);
            return true;
        }

        /// <summary>Stop using the mic. The recording stops when the last user releases it.</summary>
        public static void Release(object user)
        {
            if (user == null || !s_users.Remove(user) || s_users.Count > 0) return;
            Stop();
        }

        /// <summary>
        /// Restart the recording if the OS stopped it (app paused, device lost) while someone still holds it.
        /// Readers call it every Update; it does the work at most once per frame. A restart makes a new Clip, so
        /// readers compare Clip to the one they had and reset their cursor (MicCursor does that by itself).
        /// </summary>
        public static void EnsureRecording()
        {
            if (Clip == null || s_checkedFrame == Time.frameCount) return;
            s_checkedFrame = Time.frameCount;
            if (Microphone.IsRecording(Device)) return;
            Debug.LogWarning("[Beeswax] the microphone stopped recording; restarting it.");
            int rate = Clip.frequency;
            string device = Device;
            DestroyClip();
            if (s_users.Count > 0 && Microphone.devices.Length > 0) Start(device, rate);
        }

        static bool Start(string device, int sampleRate)
        {
            Microphone.GetDeviceCaps(device, out int minFreq, out int maxFreq);
            int rate = sampleRate;
            if (minFreq > 0 && rate < minFreq) rate = minFreq;
            if (maxFreq > 0 && rate > maxFreq) rate = maxFreq;
            AudioClip clip;
            try { clip = Microphone.Start(device, true, ClipSeconds, rate); }
            catch (Exception e) { Debug.LogWarning("[Beeswax] Microphone.Start threw: " + e.Message); clip = null; }
            if (clip == null) { Debug.LogWarning("[Beeswax] Microphone.Start failed."); return false; }
            Clip = clip;
            Device = device;
            s_checkedFrame = Time.frameCount;
            return true;
        }

        static void Stop()
        {
            if (Clip == null) return;
            try { Microphone.End(Device); } catch (Exception) { /* device gone */ }
            DestroyClip();
        }

        static void DestroyClip()
        {
            var clip = Clip;
            Clip = null;
            if (clip == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(clip);
            else UnityEngine.Object.DestroyImmediate(clip);
        }
    }

    /// <summary>
    /// One reader's position in a looping recording clip. Pull() hands over every whole 10 ms block recorded since
    /// the last call, never reading across the clip's wrap point. Also works on any looping AudioClip whose write
    /// position you supply (tests).
    /// </summary>
    public sealed class MicCursor
    {
        AudioClip _clip;
        int _readPos = -1, _block;
        float[] _buf = Array.Empty<float>();

        public int SampleRate => _clip != null ? _clip.frequency : 0;
        /// <summary>Samples skipped because the reader fell more than half a clip behind (hitch, pause).</summary>
        public long SkippedSamples { get; private set; }

        public void Reset()
        {
            _clip = null;
            _readPos = -1;
        }

        /// <summary>
        /// Calls onBlock(samples, count) for each new block (mono clips; count = clip rate / 100, or a divisor of the
        /// clip length close to it). position = the clip's write position. Returns the samples delivered.
        /// The first call after a (re)start only syncs to position: old audio in the clip is never delivered.
        /// </summary>
        public int Pull(AudioClip clip, int position, Action<float[], int> onBlock)
        {
            if (clip == null) { Reset(); return 0; }
            int len = clip.samples;
            if (clip != _clip)
            {
                _clip = clip;
                _readPos = -1;
                _block = Mathf.Max(1, clip.frequency / 100);
                while (_block > 1 && len % _block != 0) _block--;
                if (_buf.Length != _block * clip.channels) _buf = new float[_block * clip.channels];
            }
            if (position < 0 || position >= len) return 0;
            if (_readPos < 0)
            {
                if (position <= 0) return 0; // Android reports 0 until the first audio arrives
                _readPos = position / _block * _block;
                return 0;
            }
            int avail = (position - _readPos + len) % len;
            if (avail > len / 2)
            {
                int skipTo = ((position / _block - 1) * _block + len) % len;
                SkippedSamples += (skipTo - _readPos + len) % len;
                _readPos = skipTo;
                avail = (position - _readPos + len) % len;
            }
            int delivered = 0;
            while (avail >= _block)
            {
                clip.GetData(_buf, _readPos);
                _readPos = (_readPos + _block) % len;
                avail -= _block;
                onBlock(_buf, _block); // mono; a multi-channel mic would hand interleaved frames (Unity mics are mono)
                delivered += _block;
            }
            return delivered;
        }
    }

    /// <summary>
    /// Streaming resampler to a fixed output rate: area-averaging (box filter) when downsampling, which also keeps
    /// most aliasing out of 16 kHz speech; linear interpolation when upsampling; a copy at equal rates.
    /// State carries across calls, so chunk boundaries don't click.
    /// </summary>
    public sealed class StreamResampler
    {
        public int InRate { get; private set; }
        public int OutRate { get; }
        double _ratio, _acc, _filled, _t;
        float _prev;
        bool _primed;
        float[] _out = new float[256];

        public StreamResampler(int outRate) { OutRate = Math.Max(1, outRate); }

        /// <summary>Set the input rate (resets the filter state when it changes).</summary>
        public void Configure(int inRate)
        {
            if (inRate == InRate) return;
            InRate = Math.Max(1, inRate);
            _ratio = (double)InRate / OutRate;
            Reset();
        }

        public void Reset() { _acc = _filled = _t = 0; _prev = 0f; _primed = false; }

        /// <summary>Resample input[0..count) and call sink(output, n) once (n may be 0). The output array is reused.</summary>
        public void Process(float[] input, int count, Action<float[], int> sink)
        {
            if (InRate == 0) Configure(OutRate);
            int maxOut = (int)(count / _ratio) + 4;
            if (_out.Length < maxOut) _out = new float[Math.Max(maxOut, _out.Length * 2)];
            int n = 0;
            if (InRate == OutRate)
            {
                Array.Copy(input, _out, count);
                n = count;
            }
            else if (_ratio > 1.0)
            {
                for (int i = 0; i < count; i++)
                {
                    double x = input[i], remaining = 1.0;
                    while (_filled + remaining >= _ratio)
                    {
                        double take = _ratio - _filled;
                        _acc += x * take;
                        _out[n++] = (float)(_acc / _ratio);
                        remaining -= take;
                        _acc = 0;
                        _filled = 0;
                    }
                    _acc += x * remaining;
                    _filled += remaining;
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    float x = input[i];
                    if (!_primed) { _primed = true; _prev = x; _t = 0; continue; }
                    while (_t < 1.0)
                    {
                        _out[n++] = (float)(_prev + (x - _prev) * _t);
                        _t += _ratio;
                    }
                    _t -= 1.0;
                    _prev = x;
                }
            }
            sink(_out, n);
        }
    }
}
