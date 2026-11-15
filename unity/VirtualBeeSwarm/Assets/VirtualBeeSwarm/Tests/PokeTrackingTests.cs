using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VirtualBeeSwarm.Tests
{
    public class PokeTrackingTests
    {
        sealed class HandSource : IHandPoseSource, IControllerPoseSource
        {
            public bool Tracked = true;
            public bool Controller, Trigger, Aiming;
            public Vector3 Origin;
            public bool IsController(GameHand hand) => hand == GameHand.Right && Controller;
            public bool IsTracked(GameHand hand) => hand == GameHand.Right && Tracked;
            public bool IsPinching(GameHand hand) => IsTracked(hand) && Trigger;
            public bool IsFist(GameHand hand) => false;
            public bool IsTalkHeld(GameHand hand) => false;
            public bool TryGetPointerRay(GameHand hand, out Ray ray)
            {
                ray = new Ray(Origin, Vector3.forward);
                return Aiming && IsTracked(hand);
            }
            public bool TryGetIndexTip(GameHand hand, out Vector3 position)
            {
                position = Origin;
                return IsTracked(hand);
            }
        }

        [UnityTest]
        public IEnumerator ControllerTipRequiresTriggerToSelect()
        {
            yield return new EnterPlayMode();

            var source = new HandSource { Controller = true, Aiming = true, Origin = new Vector3(8f, 5f, 8f) };
            GameHands.Register(source, 1000);
            var finger = new GameObject("Controller Test Fingertip").AddComponent<GameFingertip>();
            var clipboard = new GameObject("Controller Test Clipboard").AddComponent<SignHereClipboard>();
            var pointer = new GameObject("Controller Test Pointer").AddComponent<GamePointer>();
            try
            {
                pointer.Configure(GamePointer.Source.RightHand, null);
                clipboard.transform.position = source.Origin + Vector3.forward * 0.5f;
                var button = clipboard.gameObject.AddComponent<BoxCollider>();
                button.size = new Vector3(0.2f, 0.2f, 0.02f);
                clipboard.Arm(true);
                var signatures = 0;
                clipboard.Signed += (_, _) => signatures++;
                Physics.SyncTransforms();
                yield return null;
                Assert.That(pointer.IsHoveringTarget, Is.True);
                var tip = finger.GetComponent<SphereCollider>();
                Assert.That(tip.enabled, Is.False);
                clipboard.SendMessage("OnTriggerEnter", tip);
                Assert.That(signatures, Is.Zero);

                source.Trigger = true;
                yield return null;
                Assert.That(signatures, Is.EqualTo(1));
            }
            finally
            {
                GameHands.Unregister(source);
                Object.Destroy(pointer.gameObject);
                Object.Destroy(finger.gameObject);
                Object.Destroy(clipboard.gameObject);
            }
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator ControllerTriggerCanDragTheFaceOfAGrabbablePanel()
        {
            yield return new EnterPlayMode();

            var source = new HandSource { Controller = true, Aiming = true, Origin = new Vector3(8f, 5f, 8f) };
            GameHands.Register(source, 1000);
            var panel = new GameObject("Controller Test Panel");
            var pointer = new GameObject("Panel Test Pointer").AddComponent<GamePointer>();
            try
            {
                pointer.Configure(GamePointer.Source.RightHand, null);
                panel.transform.position = source.Origin + Vector3.forward * 0.5f;
                var start = panel.transform.position;
                var grip = PanelGrip.Add(panel.transform, panel.transform, Vector3.up * 0.12f,
                    panelArea: new Vector2(0.25f, 0.18f));
                Physics.SyncTransforms();
                yield return null;
                Assert.That(grip.Held, Is.False);

                source.Trigger = true;
                yield return null;
                Assert.That(grip.Held, Is.True);
                source.Origin += Vector3.right * 0.15f;
                yield return null;
                Assert.That(panel.transform.position.x, Is.EqualTo(start.x + 0.15f).Within(0.005f));

                // Meta can swap modalities inside the same pose provider while its button edge is held.
                source.Controller = false;
                source.Origin += Vector3.right * 0.5f;
                yield return null;
                Assert.That(grip.Held, Is.False);
                Assert.That(panel.transform.position.x, Is.EqualTo(start.x + 0.15f).Within(0.005f));

                source.Trigger = false;
                yield return null;
                Assert.That(grip.Held, Is.False);
                Assert.That(grip.ManuallyPlaced, Is.True);

                // The new hand can pinch the nearby handle even before its pointer ray becomes available.
                source.Aiming = false;
                source.Origin = grip.transform.position;
                Physics.SyncTransforms();
                source.Trigger = true;
                yield return null;
                Assert.That(grip.Held, Is.True);
            }
            finally
            {
                GameHands.Unregister(source);
                Object.Destroy(pointer.gameObject);
                Object.Destroy(panel);
            }
            yield return new ExitPlayMode();
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
