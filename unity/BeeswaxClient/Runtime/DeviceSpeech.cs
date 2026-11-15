using System;
using UnityEngine;

namespace Beeswax
{
    /// <summary>Fallback speech from an installed on-device engine. A missing voice leaves the caption visible.</summary>
    sealed class DeviceSpeech : IDisposable
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        const int Success = 0;
        const string QuestEngine = "com.oculus.systemintelligence";
        AndroidJavaObject _tts;
        volatile int _initialized = -1;
        bool _checkedVoice, _hasVoice;
        bool _warned;

        sealed class InitListener : AndroidJavaProxy
        {
            readonly DeviceSpeech _owner;

            public InitListener(DeviceSpeech owner) : base("android.speech.tts.TextToSpeech$OnInitListener")
            {
                _owner = owner;
            }

            public void onInit(int status) => _owner._initialized = status;
        }

        public void Warm()
        {
            if (_tts != null) return;
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                if (activity == null) return;
                using var context = activity.Call<AndroidJavaObject>("getApplicationContext");
                _tts = new AndroidJavaObject("android.speech.tts.TextToSpeech", context, new InitListener(this), QuestEngine);
            }
            catch (Exception error) { Warn(error); }
        }

        bool SelectOfflineEnglish()
        {
            using var defaultVoice = _tts.Call<AndroidJavaObject>("getVoice");
            if (IsOfflineEnglish(defaultVoice)) return true;
            using var voices = _tts.Call<AndroidJavaObject>("getVoices");
            if (voices == null) return false;
            using var iterator = voices.Call<AndroidJavaObject>("iterator");
            while (iterator.Call<bool>("hasNext"))
            {
                using var voice = iterator.Call<AndroidJavaObject>("next");
                if (IsOfflineEnglish(voice) && _tts.Call<int>("setVoice", voice) == Success) return true;
            }
            return false;
        }

        static bool IsOfflineEnglish(AndroidJavaObject voice)
        {
            if (voice == null || voice.Call<bool>("isNetworkConnectionRequired")) return false;
            using var locale = voice.Call<AndroidJavaObject>("getLocale");
            return locale != null && locale.Call<string>("getLanguage") == "en";
        }

        public bool Speak(string caption)
        {
            if (_tts == null || _initialized != Success || string.IsNullOrWhiteSpace(caption)) return false;
            try
            {
                if (!_checkedVoice)
                {
                    _checkedVoice = true;
                    _hasVoice = SelectOfflineEnglish();
                    if (_hasVoice)
                    {
                        _tts.Call<int>("setSpeechRate", 1.1f);
                        _tts.Call<int>("setPitch", 1f);
                    }
                    else Debug.Log("[Beeswax] No locally installed English voice. Keeping the answer caption.");
                }
                if (!_hasVoice) return false;
                // The String overload works with Unity's JNI bridge and flushes only this client's previous speech.
                using var parameters = new AndroidJavaObject("java.util.HashMap");
                return _tts.Call<int>("speak", caption, 0, parameters) == Success;
            }
            catch (Exception error) { Warn(error); return false; }
        }

        public bool Speaking
        {
            get
            {
                try { return _tts != null && _tts.Call<bool>("isSpeaking"); }
                catch (Exception error) { Warn(error); return false; }
            }
        }

        public void Stop()
        {
            try { _tts?.Call<int>("stop"); }
            catch (Exception error) { Warn(error); }
        }

        public void Dispose()
        {
            if (_tts == null) return;
            try { _tts.Call("shutdown"); }
            catch (Exception error) { Warn(error); }
            finally { _tts.Dispose(); _tts = null; }
        }

        void Warn(Exception error)
        {
            if (_warned) return;
            _warned = true;
            Debug.LogWarning("[Beeswax] Device speech unavailable: " + error.GetType().Name);
        }
#else
        public void Warm() { }
        public bool Speak(string caption) => false;
        public bool Speaking => false;
        public void Stop() { }
        public void Dispose() { }
#endif
    }
}
