using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VirtualBeeSwarm;

public class SpawnObjectRelocationTests
{
    sealed class PinchingHand : IHandPoseSource
    {
        public bool Pinching = true;
        public bool IsTracked(GameHand hand) => hand == GameHand.Right;
        public bool IsPinching(GameHand hand) => Pinching;
        public bool IsFist(GameHand hand) => false;
        public bool IsTalkHeld(GameHand hand) => false;
        public bool TryGetPointerRay(GameHand hand, out Ray ray) { ray = default; return false; }
        public bool TryGetIndexTip(GameHand hand, out Vector3 position) { position = default; return false; }
    }

    static FieldInfo Field(string name) =>
        typeof(SpawnObject).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);

    [UnityTest]
    public IEnumerator PlacementShowsControlAndRelocationRearmsWithoutMovingOnTheSelectionPinch()
    {
        yield return new EnterPlayMode();

        var cameraObject = new GameObject("Relocation test camera");
        cameraObject.tag = "MainCamera";
        cameraObject.AddComponent<Camera>();
        var spawnerObject = new GameObject("Relocation test spawner");
        var spawner = spawnerObject.AddComponent<SpawnObject>();
        var hive = new GameObject("Relocation test hive");
        var rotation = Quaternion.Euler(12f, 34f, 56f);
        hive.transform.rotation = rotation;
        hive.SetActive(false);
        Field("gameObjectsToSpawn").SetValue(spawner, new[] { hive });
        var hand = new PinchingHand();
        GameHands.Register(hand, 1000);

        try
        {
            yield return null;
            var firstPosition = new Vector3(1f, 2f, 3f);
            spawner.SendMessage("PlaceObject", firstPosition);
            var control = (GameObject)Field("_relocationControl").GetValue(spawner);
            Assert.That(hive.activeSelf, Is.True);
            Assert.That(hive.transform.position, Is.EqualTo(firstPosition));
            Assert.That(Quaternion.Angle(hive.transform.rotation, rotation), Is.LessThan(0.01f));
            Assert.That((bool)Field("isSpawning").GetValue(spawner), Is.False);
            Assert.That(control, Is.Not.Null);
            Assert.That(control.activeSelf, Is.True);

            control.GetComponent<RelocateHiveButton>().Click();
            Assert.That((bool)Field("isSpawning").GetValue(spawner), Is.True);
            Assert.That(control.activeSelf, Is.False);
            spawner.enabled = false;
            spawner.SendMessage("Update");
            Assert.That((bool)Field("_waitForPinchRelease").GetValue(spawner), Is.True);
            Assert.That(hive.transform.position, Is.EqualTo(firstPosition));

            hand.Pinching = false;
            yield return null;
            spawner.SendMessage("Update");
            Assert.That((bool)Field("_waitForPinchRelease").GetValue(spawner), Is.False);
            Assert.That((bool)Field("isSpawning").GetValue(spawner), Is.True);
            spawner.enabled = true;

            var secondPosition = new Vector3(3f, 2f, 1f);
            spawner.SendMessage("PlaceObject", secondPosition);
            Assert.That(hive.transform.position, Is.EqualTo(secondPosition));
            Assert.That(Quaternion.Angle(hive.transform.rotation, rotation), Is.LessThan(0.01f));
            Assert.That((bool)Field("isSpawning").GetValue(spawner), Is.False);
            Assert.That(control.activeSelf, Is.True);
        }
        finally
        {
            GameHands.Unregister(hand);
            Object.Destroy(spawnerObject);
            Object.Destroy(hive);
            Object.Destroy(cameraObject);
        }
        yield return new ExitPlayMode();
    }
}
