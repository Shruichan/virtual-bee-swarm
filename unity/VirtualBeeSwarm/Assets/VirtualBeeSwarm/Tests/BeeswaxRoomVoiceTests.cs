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
