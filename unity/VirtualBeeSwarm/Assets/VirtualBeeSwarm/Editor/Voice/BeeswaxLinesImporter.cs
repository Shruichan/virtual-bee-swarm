using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VirtualBeeSwarm.Editor
{
    /// <summary>
    /// Import settings for Beeswax's pre-rendered lines, Assets/Resources/BeeswaxLines/&lt;line id&gt;.mp3 (PLAN S8.2).
    /// Applied on every import of a clip in that folder, so nobody sets them by hand and a fresh clone gets the same:
    ///   * Force To Mono (Normalize off: keep the render's loudness, S8.7 levels the clips upstream), not ambisonic
    ///   * Vorbis, quality 0.5, Decompress On Load (BeeswaxRoomVoice's mouth envelope reads the samples), Preload Audio Data
    ///   * Load In Background: decoding happens off the main thread while GameNet preloads the clips during the intro
    ///   * default sample rate preserved (44.1 kHz renders); Android override: the same at 22050 Hz
    /// Bump Version when the settings change, so every clip re-imports. BeeswaxVoiceTools.Check verifies them in batch.
    /// </summary>
    public class BeeswaxLinesImporter : AssetPostprocessor
    {
        public const string Folder = "Assets/Resources/BeeswaxLines";
        public const string AndroidPlatform = "Android";
        public const float VorbisQuality = 0.5f;
        public const uint AndroidSampleRate = 22050;
        const uint Version = 1;

        public override uint GetVersion() => Version;

        public static bool IsLineClip(string assetPath) =>
            !string.IsNullOrEmpty(assetPath) && assetPath.StartsWith(Folder + "/", StringComparison.Ordinal);

        void OnPreprocessAudio()
        {
            if (!IsLineClip(assetPath)) return;
            if (assetImporter is AudioImporter imp) Apply(imp);
        }

        public static void Apply(AudioImporter imp)
        {
            imp.forceToMono = true;
            imp.loadInBackground = true;
            imp.ambisonic = false;

            var d = imp.defaultSampleSettings;
            Fill(ref d, false);
            imp.defaultSampleSettings = d;

            var a = imp.GetOverrideSampleSettings(AndroidPlatform); // the default settings if there is no override yet
            Fill(ref a, true);
            if (!imp.SetOverrideSampleSettings(AndroidPlatform, a))
                Debug.LogWarning("[VBSVoice] could not set the Android override on " + imp.assetPath);

            var so = new SerializedObject(imp);
            var normalize = so.FindProperty("m_Normalize");
            if (normalize == null) Debug.LogWarning("[VBSVoice] AudioImporter.m_Normalize not found (Unity changed it?): " + imp.assetPath + " keeps its Normalize setting");
            else if (normalize.boolValue)
            {
                normalize.boolValue = false;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static void Fill(ref AudioImporterSampleSettings s, bool android)
        {
            s.loadType = AudioClipLoadType.DecompressOnLoad;
            s.compressionFormat = AudioCompressionFormat.Vorbis;
            s.quality = VorbisQuality;
            s.preloadAudioData = true;
            if (android)
            {
                s.sampleRateSetting = AudioSampleRateSetting.OverrideSampleRate;
                s.sampleRateOverride = AndroidSampleRate;
            }
            else s.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
        }

        /// <summary>What differs from the settings above (empty = all good). Also checks the imported clip itself.</summary>
        public static List<string> Problems(string assetPath)
        {
            var p = new List<string>();
            var imp = AssetImporter.GetAtPath(assetPath) as AudioImporter;
            if (imp == null)
            {
                p.Add("no AudioImporter");
                return p;
            }
            if (!imp.forceToMono) p.Add("not Force To Mono");
            if (!imp.loadInBackground) p.Add("Load In Background off");
            if (imp.ambisonic) p.Add("ambisonic");
            var normalize = new SerializedObject(imp).FindProperty("m_Normalize");
            if (normalize == null) p.Add("Normalize setting not found");
            else if (normalize.boolValue) p.Add("Normalize on");
            CheckSettings(p, "default", imp.defaultSampleSettings, false);
            if (!imp.ContainsSampleSettingsOverride(AndroidPlatform)) p.Add("no Android override");
            else CheckSettings(p, "Android", imp.GetOverrideSampleSettings(AndroidPlatform), true);

            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
            if (clip == null) p.Add("no AudioClip imported");
            else
            {
                if (clip.channels != 1) p.Add("clip has " + clip.channels + " channels");
                if (clip.loadType != AudioClipLoadType.DecompressOnLoad) p.Add("clip loadType " + clip.loadType);
                if (!clip.preloadAudioData) p.Add("clip preloadAudioData off");
                if (!clip.loadInBackground) p.Add("clip loadInBackground off");
                if (clip.length <= 0.05f) p.Add("clip length " + clip.length.ToString("0.000") + " s");
            }
            return p;
        }

        static void CheckSettings(List<string> p, string label, AudioImporterSampleSettings s, bool android)
        {
            if (s.loadType != AudioClipLoadType.DecompressOnLoad) p.Add(label + ": loadType " + s.loadType);
            if (s.compressionFormat != AudioCompressionFormat.Vorbis) p.Add(label + ": format " + s.compressionFormat);
            if (Mathf.Abs(s.quality - VorbisQuality) > 0.005f) p.Add(label + ": quality " + s.quality.ToString("0.00"));
            if (!s.preloadAudioData) p.Add(label + ": Preload Audio Data off");
            if (android)
            {
                if (s.sampleRateSetting != AudioSampleRateSetting.OverrideSampleRate || s.sampleRateOverride != AndroidSampleRate)
                    p.Add(label + ": sample rate " + s.sampleRateSetting + " " + s.sampleRateOverride);
            }
            else if (s.sampleRateSetting != AudioSampleRateSetting.PreserveSampleRate) p.Add(label + ": sample rate " + s.sampleRateSetting);
        }
    }
}
