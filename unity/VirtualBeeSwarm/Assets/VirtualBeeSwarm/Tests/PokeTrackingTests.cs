using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VirtualBeeSwarm.Tests
{
    public class PokeTrackingTests
    {
        sealed class HandSource : IHandPoseSource
        {
            public bool Tracked = true;
            public bool IsTracked(GameHand hand) => hand == GameHand.Right && Tracked;
            public bool IsPinching(GameHand hand) => false;
            public bool IsFist(GameHand hand) => false;
            public bool IsTalkHeld(GameHand hand) => false;
            public bool TryGetPointerRay(GameHand hand, out Ray ray) { ray = default; return false; }
            public bool TryGetIndexTip(GameHand hand, out Vector3 position)
            {
                position = Vector3.zero;
                return IsTracked(hand);
            }
        }

        [UnityTest]
        public IEnumerator ClipboardAcceptsPokeAfterFingertipTrackingLossWithoutAnExit()
        {
            yield return new EnterPlayMode();

            var source = new HandSource();
            GameHands.Register(source, 1000);
            var finger = new GameObject("Poke Test Fingertip").AddComponent<GameFingertip>();
            var clipboard = new GameObject("Poke Test Clipboard").AddComponent<SignHereClipboard>();
            try
            {
                var collider = finger.GetComponent<SphereCollider>();
                var signatures = 0;
                clipboard.Signed += (_, _) => signatures++;

                // A fingertip can enter while the clipboard is still on its way to the player.
                clipboard.SendMessage("OnTriggerEnter", collider);
                Assert.That(signatures, Is.Zero);

                source.Tracked = false;
                finger.SendMessage("Update");
                Assert.That(collider.enabled, Is.False);

                // The lost contact produces no trigger-exit callback.
                source.Tracked = true;
                finger.SendMessage("Update");
                Assert.That(collider.enabled, Is.True);
                clipboard.Arm(true);
                clipboard.SendMessage("OnTriggerEnter", collider);
                Assert.That(signatures, Is.EqualTo(1));
            }
            finally
            {
                GameHands.Unregister(source);
                Object.Destroy(finger.gameObject);
                Object.Destroy(clipboard.gameObject);
            }
            yield return new ExitPlayMode();
        }
    }
}
