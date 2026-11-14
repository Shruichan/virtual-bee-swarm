using UnityEditor;
using UnityEngine;

namespace VirtualBeeSwarm.SoundEditor
{
    /// <summary>
    /// Import settings for Satie's takes, Assets/VirtualBeeSwarm/Sound/Resources/SatieTakes/*.wav (the .meta files carry the
    /// same, this keeps a re-baked or added take right): Force To Mono (Normalize off: the bake evened the loudness), Vorbis
    /// 0.6, 3D; loops (loop_*) Streaming, one-shots Decompress On Load + Preload Audio Data; Load In Background.
    /// </summary>
    public class SatieTakeImporter : AssetPostprocessor
    {
        public const string Folder = "Assets/VirtualBeeSwarm/Sound/Resources/SatieTakes/";
        const uint Version = 1;

        public override uint GetVersion() => Version;

        void OnPreprocessAudio()
        {
            if (!assetPath.StartsWith(Folder, System.StringComparison.Ordinal)) return;
            var imp = assetImporter as AudioImporter;
            if (imp == null) return;
            bool loop = System.IO.Path.GetFileName(assetPath).StartsWith("loop_", System.StringComparison.Ordinal);
            imp.forceToMono = true;
            imp.loadInBackground = true;
            imp.ambisonic = false;
            var s = imp.defaultSampleSettings;
            s.loadType = loop ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad;
            s.compressionFormat = AudioCompressionFormat.Vorbis;
            s.quality = 0.6f;
            s.preloadAudioData = !loop;
            s.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
            imp.defaultSampleSettings = s;
            var so = new SerializedObject(imp);
            var normalize = so.FindProperty("m_Normalize");
            if (normalize != null && normalize.boolValue)
            {
                normalize.boolValue = false;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }
    }
}
