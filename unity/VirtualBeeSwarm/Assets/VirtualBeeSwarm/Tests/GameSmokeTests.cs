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
                bool beeswaxBringsClipboard = IntroSequence.BringClipboard != null;
                Assert.AreEqual(beeswaxBringsClipboard, beeswax.Visible, "the selected messenger brings the clipboard");
                if (!beeswaxBringsClipboard)
                    Assert.IsFalse(net.IsSpeaking, "the crew's clipboard delivery is wordless");
                Assert.IsTrue(intro.SignHereNow("smoke test"), "signature starts the show");
                if (!beeswaxBringsClipboard)
                    Assert.IsFalse(beeswax.Visible, "Beeswax stays in the hive during the crew delivery");
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

            yield return PlayStation<SmokerStation>(director, forceOffline ? GameIds.Choice.SprayWater : GameIds.Choice.Swat, GameIds.Choice.Smoker);
            yield return WaitForBeat(director, GameBeat.Feed, 30f);
            var hive = UnityEngine.Object.FindAnyObjectByType<HiveBody>();
            var feed = UnityEngine.Object.FindAnyObjectByType<FeedStation>();
            Assert.IsNotNull(hive, "hive");
            Assert.IsNotNull(feed, "feed station");
            yield return RockHive(hive, feed);
            yield return PlayStation<FeedStation>(director, GameIds.Choice.StoreHoney, GameIds.Choice.SugarSyrup);
            yield return WaitForBeat(director, GameBeat.Wasps, 30f);
            var wasps = UnityEngine.Object.FindAnyObjectByType<WaspsStation>();
            Assert.IsNotNull(wasps, "wasps station");
            yield return WaitUntil(() => wasps.WarningVisible || wasps.WaspsArrived, 10f, "wasp warning");
            Assert.IsTrue(wasps.WarningVisible, "a visible warning comes before the wasps");
            Assert.IsFalse(wasps.WaspsArrived, "the wasps stay out during the warning");
            yield return WaitUntil(() => !wasps.WarningVisible || wasps.WaspsArrived, 10f, "wasp warning to clear");
            Assert.IsFalse(wasps.WarningVisible, "the warning clears before the wasps arrive");
            yield return WaitUntil(() => wasps.WaspsArrived, 10f, "wasps after their warning");
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
            yield return PlayStation<HeatStation>(director, forceOffline ? GameIds.Choice.Nothing : GameIds.Choice.DeepBowl, GameIds.Choice.PebbleDish);

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
            embers.Play(delay: 1f);
            Assert.IsTrue(embers.Scheduled, "the delayed ember cue is queued");
            embers.enabled = false;
            embers.enabled = true;
            Assert.IsFalse(embers.Scheduled, "a disabled ember effect forgets the previous cue");

            yield return WaitForBeat(director, GameBeat.Escape, 30f);
            Assert.AreEqual(scoreTime, director.RunSeconds, "the fire sequence does not add to the rescue time");
            var hex = UnityEngine.Object.FindAnyObjectByType<HexagonPanel>();
            Assert.AreEqual(4, hex.LitCount, "all four hexagons lit");
            var escape = UnityEngine.Object.FindAnyObjectByType<EscapeSequence>();
            yield return WaitUntil(() => escape.LastScore.Length > 0, 10f, "score");
            Debug.Log("[VBSTest] " + escape.LastScore + " (scaled time x" + TimeScale + ")");

            bool moonLineStarted = false;
            net.VoiceLineStarted += line =>
            {
                if (line.id == GameLines.RoomT0FakeMoon.id && director.Beat == GameBeat.T0)
                    moonLineStarted = true;
            };
            yield return WaitForBeat(director, GameBeat.T0, 40f);
            var launch = UnityEngine.Object.FindAnyObjectByType<T0Sequence>();
            Assert.IsNotNull(launch, "launch sequence");
            var rocket = new SerializedObject(launch).FindProperty("rocket").objectReferenceValue as Transform;
            Assert.IsNotNull(rocket, "rocket");
            Assert.IsTrue(rocket.gameObject.activeInHierarchy, "the rocket is visible at the launch pad");
            var launchPosition = rocket.position;
            var launchRotation = rocket.rotation;
            var restingScale = rocket.localScale;
            var rocketItem = rocket.GetComponent<DeliveryItem>();
            Assert.IsNotNull(rocketItem, "the rocket has measured bounds and a launch pad");
            Assert.IsTrue(rocketItem.hasHome, "the original launch pad is recorded");
            Assert.That(rocketItem.localBounds.size.y * Mathf.Abs(rocket.lossyScale.y), Is.InRange(0.16f, 0.28f),
                "the upright rocket is about 22 cm tall beside the hive");
            Vector3 padOffset = Vector3.ProjectOnPlane(launchPosition - hive.transform.position, Vector3.up);
            Vector3 padRight = Vector3.ProjectOnPlane(rocket.parent != null ? rocket.parent.right : Vector3.right, Vector3.up).normalized;
            Assert.That(Mathf.Abs(Vector3.Dot(padOffset, padRight)), Is.GreaterThan(0.14f),
                "the rocket starts to one side of the hive");
            Assert.That(padOffset.magnitude, Is.LessThan(0.9f), "the rocket starts next to the hive");
            float padHeight = Bloom.MeadowLayout.Active ? Bloom.MeadowLayout.FloorY : hive.transform.position.y;
            Assert.That(Mathf.Abs(launchPosition.y - padHeight), Is.LessThan(0.22f),
                "the small rocket starts on the floor or tabletop beside the hive");
            var revealPosition = launchPosition;
            Assert.IsFalse(HiveClearance.Push(rocket, ref revealPosition, launchRotation, rocketItem),
                "the revealed rocket is clear of the hive");

            yield return WaitUntil(() => net.CurrentScene == GameIds.Scene.Moon, 15f, "Moon after liftoff");
            Assert.That(rocket.localScale.magnitude / restingScale.magnitude, Is.GreaterThan(3f),
                "the rocket briefly grows to flight size for liftoff");
            yield return WaitUntil(() => launch.MoonAwardVisible || director.Beat != GameBeat.T0, 80f, "Moon award after the crash");
            Assert.AreEqual(GameBeat.T0, director.Beat, "the Moon award is shown before the map");
            Assert.IsTrue(launch.MoonAwardVisible, "the rocket scene reveals the Moon award");
            Assert.That(Vector3.Distance(rocket.localScale, restingScale), Is.LessThan(0.001f),
                "the crashed rocket is back to its small resting size");
            yield return WaitForBeat(director, GameBeat.Map, 90f);
            Assert.IsTrue(moonLineStarted, "Beeswax explains the award during the rocket scene");
            var map = UnityEngine.Object.FindAnyObjectByType<LevelMap>();
            Assert.IsNotNull(map, "level map");
            yield return WaitUntil(() => map.UnloadDone, 20f, "final contract");
            Assert.IsTrue(escape.EndCardShown, "contract card shown");

            var rainCloud = UnityEngine.Object.FindAnyObjectByType<DeliveryRainCloud>();
            Assert.IsNotNull(rainCloud, "rain cloud controller");
            net.RequestScene(GameIds.Scene.FireOut, sendToHive: false);
            Assert.IsFalse(rainCloud.Busy, "the finale's window preview cannot bring the room's rain crew");

            Assert.That(Quaternion.Angle(launchRotation, rocket.rotation), Is.GreaterThan(10f), "crash tilt stays through the contract");
            if (Bloom.MeadowLayout.Active)
                Assert.That(Vector3.ProjectOnPlane(rocket.position - rocketItem.HomePosition, Vector3.up).magnitude,
                    Is.LessThan(0.01f), "the rocket crashes back on the default meadow launch pad");
            var crashPosition = rocket.position;
            Assert.IsFalse(HiveClearance.Push(rocket, ref crashPosition, rocket.rotation, rocketItem),
                "the crashed rocket remains clear of the hive");

            // Rehearsal controls can revisit a station without changing the score or finishing an abandoned flight.
            bool checkFlight = beeswax.RootDriver is BeeswaxMotion && beeswax.Visible;
            if (checkFlight)
            {
                Vector3 flightStart = beeswax.transform.position;
                beeswax.FlyTo(flightStart + Vector3.right * 0.5f);
                yield return WaitUntil(() => !beeswax.Arrived && Vector3.Distance(beeswax.transform.position, flightStart) > 0.01f,
                    5f, "Beeswax takes off before rehearsal jump");
            }
            Vector3 beforeJump = beeswax.transform.position;
            director.GoTo(GameBeat.Heat, "test replay");
            if (checkFlight)
                Assert.That(Vector3.Distance(beeswax.transform.position, beforeJump), Is.LessThan(0.01f),
                    "the rehearsal jump never teleports Beeswax to his abandoned flight destination");
            Assert.IsTrue(director.RunFinished, "replaying a solved station keeps the score frozen");

            director.GoTo(GameBeat.T0, "test replay");
            Assert.That(Vector3.Distance(launchPosition, rocket.position), Is.LessThan(0.01f), "T-0 replay returns the rocket to the pad");
            Assert.That(Quaternion.Angle(launchRotation, rocket.rotation), Is.LessThan(0.5f), "T-0 replay clears the crash tilt");

            director.GoTo(GameBeat.Escape, "test replay");
            Assert.AreEqual(scoreTime, director.RunSeconds, "re-entering escape keeps the same rescue score");

            // group reset puts everything back
            var lid = hive.GetComponentsInChildren<ArtAnimProp>(true).Single(prop => prop.trigger == ArtAnimProp.Trigger.Beat && prop.beat == GameBeat.Escape);
            var mapPreview = new SerializedObject(map).FindProperty("mapRoot").objectReferenceValue as GameObject;
            AR.Touchdown.Burst(rocket);
            var crashDust = GameObject.Find("Touchdown (runtime)").GetComponentInChildren<ParticleSystem>();
            Assert.Greater(crashDust.particleCount, 0, "landing effects are active before replay");
            var meadow = Bloom.MeadowField.Instance;
            escape.DeliverEndCard();
            Assert.IsTrue(escape.EndCardShown, "the previous contract is visible before reset");
            net.RequestScene(GameIds.Scene.FullBloom, sendToHive: false);
            director.ResetToPre();
            Assert.AreEqual(GameBeat.Pre, director.Beat);
            Assert.AreEqual(GameIds.Scene.Meadow, net.CurrentScene, "the previous finale scene is replaced at reset");
            Assert.IsFalse(map.UnloadDone || LevelMap.MapsMenuOpen, "the previous map and its menu are closed");
            Assert.IsFalse(mapPreview != null && mapPreview.activeSelf, "the map preview is hidden");
            Assert.IsFalse(escape.EndCardShown, "the previous contract is hidden");
            Assert.IsFalse(launch.MoonAwardVisible, "the previous Moon award is cleared");
            Assert.That(Vector3.Distance(rocket.position, rocketItem.HomePosition), Is.LessThan(0.01f), "the rocket is returned to its pad");
            Assert.That(Vector3.Distance(rocket.localScale, restingScale), Is.LessThan(0.001f), "the rocket returns to its resting size");
            Assert.AreEqual(0, crashDust.particleCount, "reset clears crash dust");
            if (Bloom.MeadowLayout.Active && meadow != null)
                Assert.That(meadow.Health, Is.EqualTo(0.3f).Within(0.001f), "the meadow immediately starts fresh");
            yield return null;
            Assert.IsNull(GameObject.Find("[VBS] Moon Award"), "the old award card is no longer visible");
            Assert.IsNull(GameObject.Find("[VBS] MoonTrophy"), "the old Moon trophy is gone");
            if (BeeDelivery.Mode != DeliveryMode.Off)
            {
                Assert.IsTrue(intro.SignHereShown, "the new ride invites the player to sign");
                yield return WaitUntil(() => intro.SignHereReady, 10f, "fresh SIGN HERE clipboard");
            }
            float resetAt = Time.time;
            yield return WaitUntil(() => Time.time - resetAt > lid.delay, 4f, "delayed lid reset");
            Assert.AreEqual(0, director.SolvedStations);
            Assert.AreEqual(0, hex.LitCount);
            Assert.IsFalse(lid.GetComponent<Animation>().IsPlaying(lid.clip), "the escape lid cannot open after reset");

            director.GoTo(GameBeat.Escape, "test suspended opening");
            lid.enabled = false;
            director.ResetToPre();
            lid.enabled = true;
            resetAt = Time.time;
            yield return WaitUntil(() => Time.time - resetAt > lid.delay, 4f, "suspended lid cue");
            Assert.IsFalse(lid.GetComponent<Animation>().IsPlaying(lid.clip), "a disabled lid cue cannot play after reset");
            Debug.Log("[VBSTest] full run OK (forceOffline=" + forceOffline + ")");
        }

        [Test]
        public void QuestWorkerLodKeepsTheAuthoredPaletteAndWingHinges()
        {
            var worker = AssetDatabase.LoadAssetAtPath<Mesh>(ArtSlots.VbsRoot + "/Art/Bee/Bee_unit.asset");
            Assert.IsNotNull(worker, "authored worker input");
            var far = BeeVat.AuthoredWorkerFarMesh(worker);
            Assert.IsNotNull(far);
            Assert.AreNotSame(worker, far, "the high-count pools need the reduced rest mesh");
            Assert.That(far.GetIndexCount(0) / 3, Is.InRange(75, 140), "stay near the original Quest budget");
            CollectionAssert.AreEquivalent(worker.colors32.Distinct(), far.colors32.Distinct(), "same colors at distance");
            Assert.IsTrue(far.uv3.Any(flag => flag.x < -0.5f), "left wing still flaps");
            Assert.IsTrue(far.uv3.Any(flag => flag.x > 0.5f), "right wing still flaps");
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

        static IEnumerator RockHive(HiveBody hive, FeedStation feed)
        {
            yield return WaitUntil(() => hive.LiftEnabled, 20f, "hive rock enabled");
            int earlyReleases = 0;
            int completedRocks = 0;
            void OnEarlyRelease() => earlyReleases++;
            void OnRocked() => completedRocks++;
            hive.RockReleasedEarly += OnEarlyRelease;
            hive.Rocked += OnRocked;
            float previousTimeScale = Time.timeScale;
            float previousCaptureDelta = Time.captureDeltaTime;
            try
            {
                Time.timeScale = 1f;
                // Supply one stable tracking sample per frame even when the editor is busy.
                Time.captureDeltaTime = 1f / 72f;
                Vector3 origin = hive.transform.position;
                hive.BeginGrab(origin);
                yield return new WaitForSeconds(0.05f);
                hive.UpdateGrab(origin + Vector3.up * 0.025f);
                hive.EndGrab();
                Assert.AreEqual(1, earlyReleases, "letting go early sends the retry event");
                Assert.IsFalse(hive.RockCompleted, "a single lift does not count as rocking");
                Assert.AreEqual(0, completedRocks, "an early release cannot advance the feed station");
                yield return WaitUntil(() => feed.WarningVisible, 5f, "early release warning");
                Assert.IsTrue(hive.LiftEnabled, "the player can retry the rock");

                origin = hive.transform.position;
                Vector3 raised = origin + Vector3.up * 0.09f;
                Vector3 side = Vector3.ProjectOnPlane(hive.transform.right, Vector3.up).normalized * 0.05f;
                hive.BeginGrab(origin);
                yield return SampleGrabStroke(hive, origin, raised, 12);
                Assert.That(hive.LiftHeight, Is.GreaterThanOrEqualTo(0.05f), "the hive is high enough to rock");
                yield return SampleGrabStroke(hive, raised, raised + side, 12);
                Assert.IsFalse(hive.RockCompleted, "the first stroke alone is not a rock");
                yield return SampleGrabStroke(hive, raised + side, raised - side, 28);
                Assert.IsTrue(hive.RockCompleted,
                    $"the complete rock counts before release (held {hive.Held}, enabled {hive.LiftEnabled}, height {hive.LiftHeight:F3})");
                Assert.AreEqual(1, completedRocks, "the successful rock sends one event");
                hive.EndGrab();
                Assert.AreEqual(1, earlyReleases, "releasing a successful rock does not send a retry");
            }
            finally
            {
                Time.captureDeltaTime = previousCaptureDelta;
                Time.timeScale = previousTimeScale;
                hive.RockReleasedEarly -= OnEarlyRelease;
                hive.Rocked -= OnRocked;
                if (hive.Held) hive.EndGrab();
            }
        }

        static IEnumerator SampleGrabStroke(HiveBody hive, Vector3 from, Vector3 to, int frames)
        {
            for (int frame = 1; frame <= frames; frame++)
            {
                yield return null;
                hive.UpdateGrab(Vector3.Lerp(from, to, (float)frame / frames));
            }
        }

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
