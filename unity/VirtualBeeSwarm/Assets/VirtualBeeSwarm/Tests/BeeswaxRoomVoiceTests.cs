using System.Collections.Generic;
using System.Reflection;
using Beeswax;
using NUnit.Framework;
using UnityEngine;

namespace VirtualBeeSwarm.Tests
{
    public class BeeswaxRoomVoiceTests
    {
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void QuestGainRaisesQuietAudioAndLimitsLoudAudioWithoutFlatClipping()
        {
            var gain = typeof(BeeswaxRoomVoice).GetMethod("ApplyDialogueGain", BindingFlags.Static | BindingFlags.NonPublic);
            var quiet = new[] { .2f, -.4f, .5f };
            gain.Invoke(null, new object[] { quiet, 1.41f });
            Assert.That(quiet[0], Is.EqualTo(.282f).Within(.0001f));
            Assert.That(quiet[1], Is.EqualTo(-.564f).Within(.0001f));
            Assert.That(quiet[2], Is.EqualTo(.705f).Within(.0001f));
            var loud = new[] { .9f, -1f, .2f };
            gain.Invoke(null, new object[] { loud, 1.41f });
            Assert.That(loud[0], Is.EqualTo(.882f).Within(.0001f));
            Assert.That(loud[1], Is.EqualTo(-.98f).Within(.0001f));
            Assert.That(loud[2], Is.EqualTo(.196f).Within(.0001f));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void EmptyTtsStillShowsItsCaption(bool receivedEnd)
        {
            var root = new GameObject("Empty TTS test");
            try
            {
                var voice = root.AddComponent<BeeswaxRoomVoice>();
                var shown = new List<string>();
                voice.OnLineStarted += shown.Add;
                const string answer = "A waggle dance gives directions to food.";
                Invoke(voice, "HandleTtsStart", new TtsStart { tts_id = "empty", caption = answer });
                if (receivedEnd) Invoke(voice, "HandleTtsEnd", new TtsEnd { tts_id = "empty" });
                else typeof(BeeswaxRoomVoice).GetField("streamStallSeconds", PrivateInstance).SetValue(voice, -1f);

                Invoke(voice, "Update");
                Invoke(voice, "Update");
                Invoke(voice, "Update");

                Assert.That(shown, Is.EqualTo(new[] { answer }));
                Assert.That(voice.CurrentCaption, Is.EqualTo(answer));
                Assert.That(voice.CurrentTtsId, Is.Empty, "The client presents a caption instead of an audio stream.");
                Assert.That(voice.IsSpeaking, Is.True, "The caption stays up for reading time.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        static void Invoke(BeeswaxRoomVoice voice, string method, params object[] args) =>
            typeof(BeeswaxRoomVoice).GetMethod(method, PrivateInstance).Invoke(voice, args);
    }
}
