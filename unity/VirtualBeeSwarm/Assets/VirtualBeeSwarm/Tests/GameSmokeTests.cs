using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using VirtualBeeSwarm.Editor;

namespace VirtualBeeSwarm.Tests
{
    /// <summary>
    /// Builds the OFFLINE test scene (GameNet forced offline, the bus never connects, no mic), enters play mode
    /// and drives a whole run: start, every station (a wrong choice, then the right one), the hornet, the what-if
    /// fire + put-out, escape, T-0, map. Fails on any error/exception logged on the way (Unity Test Framework default).
    /// Silent: the listener volume is 0.
    /// Run: Window > General > Test Runner > EditMode, or
    ///   Unity -batchmode -nographics -projectPath P -runTests -testPlatform EditMode -testResults results.xml
    /// </summary>
    public class GameSmokeTests
    {
        const float TimeScale = 4f;

        bool _prevOptionsEnabled;
        EnterPlayModeOptions _prevOptions;

        [SetUp]
        public void SetUp()
        {
            // No domain reload: the test keeps running straight into play mode.
            _prevOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _prevOptions = EditorSettings.enterPlayModeOptions;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;
        }

        [TearDown]
        public void TearDown()
        {
            Time.timeScale = 1f;
            EditorSettings.enterPlayModeOptionsEnabled = _prevOptionsEnabled;
            EditorSettings.enterPlayModeOptions = _prevOptions;
        }

        /// <summary>Hive unreachable (the bus exists but never connects): BeeswaxStations plays outcomes locally.</summary>
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator FullRun_HiveUnreachable() => FullRun(forceOffline: false);

        /// <summary>GameNet forced offline (key O): GameLines plays the outcomes.</summary>
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator FullRun_ForcedOffline() => FullRun(forceOffline: true);

        [Test]
        public void ReplayingASolvedStationDoesNotCountItTwice()
        {
            var gameObject = new GameObject("Station progress test");
            try
            {
                var director = gameObject.AddComponent<GameDirector>();
                director.NotifyStationSolved(GameStation.Smoker);
                director.NotifyStationSolved(GameStation.Feed);
                director.NotifyStationSolved(GameStation.Smoker);
                Assert.AreEqual(2, director.SolvedStations);

                director.NotifyStationSolved(GameStation.Wasps);
                director.NotifyStationSolved(GameStation.Heat);
                director.NotifyStationSolved(GameStation.Feed);
                Assert.AreEqual(4, director.SolvedStations);

                director.ResetToPre();
                Assert.AreEqual(0, director.SolvedStations);
                director.NotifyStationSolved(GameStation.Smoker);
                Assert.AreEqual(1, director.SolvedStations);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        IEnumerator FullRun(bool forceOffline)
        {
            string path = BuildGameScene.BuildScene("OfflineTest.unity", offline: true);
            EditorSceneManager.OpenScene(path);
            yield return new EnterPlayMode();
            yield return PlayRun(forceOffline);
            Time.timeScale = 1f;
            yield return new ExitPlayMode();
        }

        /// <summary>
        /// The run itself; expects play mode with OfflineTest.unity loaded. Ends back in pre, so it can run
        /// again in the same session. Also driven by GameSmokeRunner (-executeMethod, no Test Runner needed).
        /// </summary>
        public static IEnumerator PlayRun(bool forceOffline)
        {
            AudioListener.volume = 0f;
            Time.timeScale = TimeScale;
            yield return null;
            yield return null;

            var director = UnityEngine.Object.FindAnyObjectByType<GameDirector>();
            var net = UnityEngine.Object.FindAnyObjectByType<GameNet>();
            Assert.IsNotNull(director, "director");
            Assert.IsNotNull(net, "net");
            net.ForceOffline = forceOffline;
            yield return null;
            Assert.IsFalse(net.Online, "the offline test scene must never be online");
            Assert.AreEqual(GameBeat.Pre, director.Beat);
            if (ArtSlots.Enabled) AssertAuthoredSceneArt();
            director.Hornet = GameDirector.HornetMode.Always;

            // Start through the same prompt as the player and let the opening run in order.
            var intro = UnityEngine.Object.FindAnyObjectByType<IntroSequence>();
            var beeswax = UnityEngine.Object.FindAnyObjectByType<BeeswaxController>();
            Assert.IsNotNull(intro, "intro");
            Assert.IsNotNull(beeswax, "Beeswax");
            if (BeeDelivery.Mode == DeliveryMode.Off)
            {
                director.Next("test");
            }
            else
            {
                Assert.IsTrue(intro.SignHereShown, "clipboard is the start prompt");
                yield return WaitUntil(() => intro.SignHereReady, 10f, "clipboard parked and ready");
                Assert.IsFalse(beeswax.Visible, "the courier brings the clipboard before Beeswax appears");
                Assert.IsFalse(net.IsSpeaking, "the clipboard delivery is wordless");
                Assert.IsTrue(intro.SignHereNow("smoke test"), "signature starts the show");
                Assert.IsFalse(beeswax.Visible, "Beeswax stays in the hive during the opening delivery");
            }
            Assert.AreEqual(GameBeat.Hook, director.Beat);
            yield return WaitForBeat(director, GameBeat.Meet, 30f);
            yield return WaitUntil(() => beeswax.Visible && !beeswax.Inside, 10f, "Beeswax emerges");
            yield return WaitForBeat(director, GameBeat.Smoker, 60f);

            if (Bloom.MeadowLayout.Active && HandsOn.HandsOnTuning.Mode == HandsOn.HandsOnMode.Full &&
                BeeDelivery.Mode != DeliveryMode.Off)
            {
                var orderBoard = UnityEngine.Object.FindAnyObjectByType<HandsOn.OrderBoard>(FindObjectsInactive.Include);
                Assert.IsNotNull(orderBoard, "order board");
                yield return WaitUntil(() => orderBoard.Visible, 15f, "first station tickets");
                var ticket = orderBoard.transform.Find("Ticket0");
                Assert.IsNotNull(ticket, "first ticket");
                Assert.That(ticket.position.y - Bloom.MeadowLayout.Anchor.position.y,
                    Is.EqualTo(Bloom.MeadowTuning.ChoiceHeight).Within(0.02f), "tickets are at the meadow's hand height");
                Assert.That(orderBoard.transform.localPosition.z,
                    Is.EqualTo(-Bloom.MeadowLayout.RowOffset(Bloom.MeadowLayout.Scale)).Within(0.001f),
                    "the order board follows the meadow's reachable choice row");
            }

            yield return PlayStation<SmokerStation>(director, GameIds.Choice.Swat, GameIds.Choice.Smoker);
            yield return WaitForBeat(director, GameBeat.Feed, 30f);
            // the feed station waits for a lift (or gives up after 20 s): lift through the hive's grab API
            var hive = UnityEngine.Object.FindAnyObjectByType<HiveBody>();
            yield return WaitUntil(() => hive.LiftEnabled, 20f, "hive lift enabled");
            hive.BeginGrab(hive.transform.position);
            hive.UpdateGrab(hive.transform.position + Vector3.up * 0.05f);
            hive.EndGrab();
            yield return PlayStation<FeedStation>(director, GameIds.Choice.StoreHoney, GameIds.Choice.SugarSyrup);
            yield return WaitForBeat(director, GameBeat.Wasps, 30f);
            yield return PlayStation<WaspsStation>(director, GameIds.Choice.BiggerDoor, GameIds.Choice.SmallerDoor);

            // hornet (forced on): fist times out by itself; add heat until it's cooked
            yield return WaitForBeat(director, GameBeat.Hornet, 30f);
            var hornet = UnityEngine.Object.FindAnyObjectByType<HornetBeat>();
            float t0 = Time.realtimeSinceStartup;
            while (director.Beat == GameBeat.Hornet && Time.realtimeSinceStartup - t0 < 60f)
            {
                hornet.AddHeat(0.02f);
                yield return null;
            }
            Assert.IsTrue(hornet.Cooked, "hornet cooked");

            yield return WaitForBeat(director, GameBeat.Heat, 30f);
            yield return PlayStation<HeatStation>(director, GameIds.Choice.DeepBowl, GameIds.Choice.PebbleDish);

            // what-if: the offline keys' code path
            yield return WaitForBeat(director, GameBeat.WhatIf, 30f);
            Assert.AreEqual(4, director.SolvedStations);
            Assert.IsTrue(director.RunFinished, "the rescue clock stops after the four fixes");
            float scoreTime = director.RunSeconds;
            var whatIf = UnityEngine.Object.FindAnyObjectByType<WhatIfBeat>();
            Assert.IsNotNull(whatIf, "what-if");
            yield return WaitUntil(() => whatIf.PromptDone, 15f, "what-if prompt");
            whatIf.LocalWhatIfFire();
            Assert.AreEqual(GameIds.Scene.WhatIfFire, net.CurrentScene);
            var embers = UnityEngine.Object.FindAnyObjectByType<EmbersFx>();
            yield return WaitUntil(() => embers.Emitting, 10f, "embers emitting");
            whatIf.LocalPutOut();
            Assert.IsFalse(embers.Emitting, "embers stopped");
            Assert.IsTrue(whatIf.FireOut, "fire out");

            yield return WaitForBeat(director, GameBeat.Escape, 30f);
            Assert.AreEqual(scoreTime, director.RunSeconds, "the fire sequence does not add to the rescue time");
            var hex = UnityEngine.Object.FindAnyObjectByType<HexagonPanel>();
            Assert.AreEqual(4, hex.LitCount, "all four hexagons lit");
            var escape = UnityEngine.Object.FindAnyObjectByType<EscapeSequence>();
            yield return WaitUntil(() => escape.LastScore.Length > 0, 10f, "score");
            Debug.Log("[VBSTest] " + escape.LastScore + " (scaled time x" + TimeScale + ")");

            yield return WaitForBeat(director, GameBeat.T0, 40f);
            var launch = UnityEngine.Object.FindAnyObjectByType<T0Sequence>();
            Assert.IsNotNull(launch, "launch sequence");
            var rocket = new SerializedObject(launch).FindProperty("rocket").objectReferenceValue as Transform;
            Assert.IsNotNull(rocket, "rocket");
            var launchPosition = rocket.position;
            var launchRotation = rocket.rotation;
            yield return WaitUntil(() => net.CurrentScene == GameIds.Scene.Moon, 15f, "Moon after liftoff");
            yield return WaitForBeat(director, GameBeat.Map, 90f);
            var map = UnityEngine.Object.FindAnyObjectByType<LevelMap>();
            Assert.IsNotNull(map, "level map");
            yield return WaitUntil(() => map.UnloadDone, 20f, "final contract");
            Assert.IsTrue(escape.EndCardShown, "contract card shown");

            Assert.That(Quaternion.Angle(launchRotation, rocket.rotation), Is.GreaterThan(10f), "crash tilt stays through the contract");
            director.GoTo(GameBeat.T0, "test replay");
            Assert.That(Vector3.Distance(launchPosition, rocket.position), Is.LessThan(0.01f), "T-0 replay returns the rocket to the pad");
            Assert.That(Quaternion.Angle(launchRotation, rocket.rotation), Is.LessThan(0.5f), "T-0 replay clears the crash tilt");

            // Rehearsal controls can revisit a station without changing the score for this group.
            director.GoTo(GameBeat.Heat, "test replay");
            Assert.IsTrue(director.RunFinished, "replaying a solved station keeps the score frozen");
            director.GoTo(GameBeat.Escape, "test replay");
            Assert.AreEqual(scoreTime, director.RunSeconds, "re-entering escape keeps the same rescue score");

            // group reset puts everything back
            director.ResetToPre();
            yield return null;
            Assert.AreEqual(GameBeat.Pre, director.Beat);
            Assert.AreEqual(0, director.SolvedStations);
            Assert.AreEqual(0, hex.LitCount);
            Debug.Log("[VBSTest] full run OK (forceOffline=" + forceOffline + ")");
        }

        static void AssertAuthoredSceneArt()
        {
            var clipboard = UnityEngine.Object.FindAnyObjectByType<SignHereClipboard>(FindObjectsInactive.Include);
            var hive = UnityEngine.Object.FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include);
            var launch = UnityEngine.Object.FindAnyObjectByType<T0Sequence>();
            var wasps = UnityEngine.Object.FindAnyObjectByType<WaspsStation>();
            var swarm = UnityEngine.Object.FindAnyObjectByType<SwarmSystem>(FindObjectsInactive.Include);
            Assert.IsNotNull(clipboard, "clipboard");
            Assert.IsNotNull(hive, "hive");
            Assert.IsNotNull(launch, "launch sequence");
            Assert.IsNotNull(wasps, "wasps station");
            Assert.IsNotNull(swarm, "worker swarm");

            AssertAuthoredMeshes("SignHere", clipboard.transform);
            AssertAuthoredMeshes("Hive", hive.transform);
            AssertAuthoredMeshes("Rocket", new SerializedObject(launch).FindProperty("rocket").objectReferenceValue as Transform);
            var waspRoots = new SerializedObject(wasps).FindProperty("wasps");
            Assert.Greater(waspRoots.arraySize, 0, "the wasps station has wasps");
            for (int i = 0; i < waspRoots.arraySize; i++)
                AssertAuthoredMeshes("Wasp", waspRoots.GetArrayElementAtIndex(i).objectReferenceValue as Transform);

            var worker = AssetDatabase.LoadAssetAtPath<Mesh>(ArtSlots.VbsRoot + "/Art/Bee/Bee_unit.asset");
            var rigBake = Resources.Load<Mesh>(BeeVat.MeshResource);
            Assert.IsNotNull(worker, "authored worker bee mesh");
            Assert.IsNotNull(rigBake, "animated worker rig bake");
            CollectionAssert.AreEqual(rigBake.vertices, worker.vertices, "the static worker uses the animated worker's rest pose");
            Assert.AreEqual(worker, swarm.WorkerBeeMesh, "the swarm retains the authored static mesh for low-detail bees");
            Debug.Log("[VBSTest] authored clipboard, hive, rocket, wasps and worker meshes verified");
        }

        static void AssertAuthoredMeshes(string slot, Transform root)
        {
            var prefab = ArtSlots.Prefab(slot);
            Assert.IsNotNull(prefab, slot + " authored prefab");
            Assert.IsNotNull(root, slot + " scene object");
            var expected = new HashSet<Mesh>(VisualMeshes(prefab.transform));
            Assert.IsTrue(VisualMeshes(root).Any(expected.Contains), slot + " uses the authored mesh in the generated scene");
        }

        static IEnumerable<Mesh> VisualMeshes(Transform root) =>
            root.GetComponentsInChildren<MeshFilter>(true)
                .Where(filter => filter.TryGetComponent(out MeshRenderer renderer) && renderer.enabled)
                .Select(filter => filter.sharedMesh)
                .Concat(root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(renderer => renderer.enabled)
                    .Select(renderer => renderer.sharedMesh))
                .Where(mesh => mesh != null);

        static IEnumerator PlayStation<T>(GameDirector director, string wrong, string right) where T : StationBase
        {
            var station = UnityEngine.Object.FindAnyObjectByType<T>();
            Assert.IsNotNull(station, typeof(T).Name);
            yield return WaitUntil(() => station.Choosing, 40f, typeof(T).Name + " choosing");
            station.Choose(wrong);
            yield return WaitUntil(() => station.Choosing, 40f, typeof(T).Name + " retry");
            Assert.IsFalse(station.Solved, "a wrong choice must not solve " + typeof(T).Name);
            station.Choose(right);
            yield return WaitUntil(() => station.Solved, 40f, typeof(T).Name + " solved");
        }

        static IEnumerator WaitForBeat(GameDirector director, GameBeat beat, float realSeconds) =>
            WaitUntil(() => director.Beat == beat, realSeconds, "beat " + beat + " (at " + director.Beat + ")");

        static IEnumerator WaitUntil(Func<bool> cond, float realSeconds, string what)
        {
            float start = Time.realtimeSinceStartup;
            while (!cond())
            {
                if (Time.realtimeSinceStartup - start > realSeconds) Assert.Fail("timed out waiting for " + what);
                yield return null;
            }
        }
    }
}
