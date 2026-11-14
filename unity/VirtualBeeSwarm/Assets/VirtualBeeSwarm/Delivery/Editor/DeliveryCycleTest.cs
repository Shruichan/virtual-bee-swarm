using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VirtualBeeSwarm.DeliveryEditor
{
    /// <summary>
    /// Headless delivery cycle test, no play mode, no Test Runner, no sound (BeeDelivery has its own clock: the test
    /// steps it at 72 fps, i.e. real 1x timing):
    ///   Unity -batchmode -nographics -projectPath P -executeMethod VirtualBeeSwarm.DeliveryEditor.DeliveryCycleTest.Run -logFile log
    /// In each delivery.mode (on, swirl, off), in a throwaway scene with a fake table, window pane, hive, rocket and the
    /// smoker station's props: ring cut, hive festoon delivery, rocket crown delivery, park the three choices in the
    /// arc (spacing, height, gaze, stillness), stamp, repo haul of the wrong pick, set-down of the right pick on its
    /// spot with the leftovers returned, station exit, then a Board jump mid-hive-flight and mid-repo (FinishAll) and a
    /// group reset (CancelAll): no item left in the air, no bees left. Also: garbage per Step and ms per Step.
    /// Logs [VBSDelivery] lines and "[VBSDelivery] RESULT PASS"; exit code 0 = pass, 1 = fail (batch mode).
    /// </summary>
    public static class DeliveryCycleTest
    {
        const float Dt = 1f / 72f;
        static readonly List<string> Fail = new List<string>();
        static string _mode = "";

        [MenuItem("Virtual Bee Swarm/Delivery/Run Delivery Cycle Test")]
        public static void RunMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            RunAll(exit: false);
        }

        public static void Run() => RunAll(exit: Application.isBatchMode);

        static void RunAll(bool exit)
        {
            Fail.Clear();
            var prev = BeeDelivery.Mode;
            try
            {
                RunMode(DeliveryMode.On);
                RunMode(DeliveryMode.Swirl);
                RunMode(DeliveryMode.Off);
            }
            catch (Exception e)
            {
                Fail.Add(_mode + ": exception " + e);
                Debug.LogException(e);
            }
            finally
            {
                BeeDelivery.Mode = prev;
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            foreach (var f in Fail) Debug.Log("[VBSDelivery] FAIL " + f);
            bool ok = Fail.Count == 0;
            Debug.Log("[VBSDelivery] RESULT " + (ok ? "PASS" : "FAIL (" + Fail.Count + ")"));
            if (exit) EditorApplication.Exit(ok ? 0 : 1);
        }

        // ------------------------------------------------------------ helpers
        static void Check(bool cond, string what)
        {
            if (!cond) Fail.Add(_mode + ": " + what);
        }

        static void Steps(BeeDelivery d, float seconds)
        {
            int n = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < n; i++) d.Step(Dt);
        }

        static GameObject Cube(string name, Transform parent, Vector3 localPos, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            return go;
        }

        static float HorizAngle(Vector3 from, Vector3 a, Vector3 b)
        {
            Vector3 x = a - from, y = b - from;
            x.y = 0f; y.y = 0f;
            return Vector3.Angle(x, y);
        }

        // ------------------------------------------------------------ one mode
        static void RunMode(DeliveryMode mode)
        {
            _mode = BeeDelivery.ModeName(mode);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BeeDelivery.Mode = mode;
            bool on = mode == DeliveryMode.On, off = mode == DeliveryMode.Off;

            // ---- fake room (same layout as BuildGameScene: player at -Z, table top y 0.75)
            var head = new GameObject("Head").transform;
            head.position = new Vector3(0f, 1.38f, -0.8f);
            var table = new GameObject("TableAnchor").transform;
            table.position = new Vector3(0f, 0.75f, 0f);
            var hive = new GameObject("Hive").transform;
            hive.SetParent(table, false);
            hive.localPosition = new Vector3(0f, 0f, 0.15f);
            Cube("Body", hive, new Vector3(0f, 0.15f, 0f), new Vector3(0.26f, 0.3f, 0.24f));
            var pane = new GameObject("Pane").transform;
            pane.SetParent(table, false);
            pane.localPosition = new Vector3(-0.44f, 0.25f, 0.2f);
            pane.localRotation = Quaternion.Euler(0f, -22f, 0f);
            pane.localScale = new Vector3(0.52f, 0.3f, 1f);
            var rocket = new GameObject("HONEYMOON-1").transform;
            rocket.SetParent(table, false);
            rocket.localPosition = new Vector3(0.31f, 0f, 0.2f);
            Cube("Hull", rocket, new Vector3(0f, 0.13f, 0f), new Vector3(0.06f, 0.26f, 0.06f));

            var station = new GameObject("SmokerStation").transform;
            station.SetParent(table, false);
            var props = new GameObject("Props").transform;
            props.SetParent(station, false);
            var smoker = new GameObject("Smoker").transform;
            smoker.SetParent(props, false);
            smoker.localPosition = new Vector3(0.19f, 0f, -0.02f);
            Cube("Can", smoker, new Vector3(0f, 0.045f, 0f), new Vector3(0.05f, 0.09f, 0.05f));
            var row = new GameObject("Buttons").transform;
            row.SetParent(station, false);
            var buttons = new List<ChoiceButton>();
            string[] ids = GameIds.Choices[(int)GameStation.Smoker];
            for (int i = 0; i < ids.Length; i++)
            {
                var b = new GameObject("Button_" + ids[i]);
                b.transform.SetParent(row, false);
                b.transform.localPosition = new Vector3(-0.1f + i * 0.1f, 0f, 0f);
                var col = b.AddComponent<BoxCollider>();
                col.isTrigger = true;
                var cb = b.AddComponent<ChoiceButton>();
                cb.Configure(ids[i], ids[i], i + 1);
                buttons.Add(cb);
            }

            var dGo = new GameObject("BeeDelivery");
            var d = dGo.AddComponent<BeeDelivery>();
            d.Configure(null, pane, head, table, null);
            d.Init();
            var sd = station.gameObject.AddComponent<StationDelivery>();
            sd.Configure(GameStation.Smoker, d, hive, props, null, null);
            sd.SetStampArt(null, null); // checks BeeDelivery's text stamp; the art stamp shows in the smoke run log

            // the builder state: hive and rocket hidden until delivered (Register first: records home)
            d.Register(hive);
            d.Register(rocket);
            Vector3 hiveHome = hive.position, rocketHome = rocket.position;
            Quaternion rocketRot = rocket.rotation;
            hive.gameObject.SetActive(false);
            rocket.gameObject.SetActive(false);

            // ---- 1. ring cut
            int cut = 0;
            d.CutRing(() => cut++);
            d.Step(Dt);
            if (off) Check(cut == 1 && d.ActiveBees == 0, "ring: off calls back at once");
            else Check(d.ActiveBees == 20, "ring: 20 bees (got " + d.ActiveBees + ")");
            Steps(d, 2.6f);
            Check(cut == 1, "ring: onCut once (got " + cut + ")");
            Steps(d, 1.6f);
            Check(d.ActiveBees == 0, "ring: no bees left (" + d.ActiveBees + ")");

            // ---- 2. hive on bee chains
            int hiveLanded = 0;
            d.DeliverHome(hive, DeliveryStyle.Festoon, 0, () => hiveLanded++);
            Steps(d, 1.0f);
            if (on)
            {
                Check(hive.gameObject.activeSelf, "hive: visible in flight");
                Check(Vector3.Distance(hive.position, hiveHome) > 0.05f, "hive: in the air at 1 s");
                Check(d.ActiveBees == 48, "hive: 48 festoon bees (got " + d.ActiveBees + ")");
                Check(d.InFlight == 1, "hive: InFlight 1 (got " + d.InFlight + ")");
                Steps(d, 3.6f);
            }
            Check(hiveLanded == 1, "hive: landed once (got " + hiveLanded + ")");
            Check(Vector3.Distance(hive.position, hiveHome) < 0.001f, "hive: on its plate (" + Vector3.Distance(hive.position, hiveHome).ToString("0.0000") + " m)");
            Check(hive.localScale == Vector3.one, "hive: rest scale after the squash");
            Steps(d, 3.0f);
            Check(d.ActiveBees == 0, "hive: chains gone (" + d.ActiveBees + ")");
            if (!off) Check(hive.GetComponent<DeliveryItem>().shadow.enabled, "hive: contact shadow on");

            // ---- 3. rocket, crown of 12, nose up
            int rocketLanded = 0;
            d.DeliverHome(rocket, DeliveryStyle.Crown, 12, () => rocketLanded++);
            Steps(d, 0.5f);
            if (on) Check(d.ActiveBees == 12, "rocket: crown of 12 (got " + d.ActiveBees + ")");
            if (on) Check(Quaternion.Angle(Quaternion.identity, Quaternion.FromToRotation(Vector3.up, rocket.up)) <= 20.5f, "rocket: nose up within the 20 deg swing");
            Steps(d, 1.3f);
            Check(rocketLanded == 1, "rocket: landed once");
            Check(Vector3.Distance(rocket.position, rocketHome) < 0.001f && Quaternion.Angle(rocket.rotation, rocketRot) < 0.1f, "rocket: planted upright at home");

            // ---- 4. station: park, stamp, repo, set-down, return
            sd.Prepare();
            var swatter = sd.Item(0);
            var spray = sd.Item(1);
            var smokerItem = sd.Item(2);
            Check(swatter != null && spray != null && smokerItem == smoker, "station: items resolved (placeholders built)");
            if (off)
            {
                Check(smoker.gameObject.activeSelf && !swatter.gameObject.activeSelf && !spray.gameObject.activeSelf, "off: legacy prop shown, placeholders hidden");
                Check(buttons[0].transform.parent == row, "off: buttons stay in their row");
                sd.ParkChoices();
                sd.OnChoiceMade(GameIds.Choice.SprayWater, false);
                sd.OnOutcomeShown(GameIds.Choice.SprayWater, false);
                Steps(d, 1f);
                Check(smoker.localPosition == new Vector3(0.19f, 0f, -0.02f) && smoker.localScale == Vector3.one, "off: legacy prop untouched");
                Check(d.ActiveBees == 0, "off: no bees ever");
            }
            else
            {
                Check(!swatter.gameObject.activeSelf && !smoker.gameObject.activeSelf, "station: choices wait outside until the prompt");
                Check(buttons[2].transform.parent != null && buttons[2].transform.parent.parent == smoker, "station: the button became the smoker's price tag");
                sd.ParkChoices();
                Steps(d, 2.0f);
                float tableY = table.position.y;
                var items = new[] { swatter, spray, smokerItem };
                for (int i = 0; i < 3; i++)
                {
                    var di = items[i].GetComponent<DeliveryItem>();
                    Check(items[i].gameObject.activeSelf && di.parked && !d.IsFlying(items[i]), "arc: " + items[i].name + " parked");
                    float h = items[i].position.y - tableY;
                    Check(h >= 0.3f && h <= 0.7f && items[i].position.y <= head.position.y - 0.17f, "arc: " + items[i].name + " 30-70 cm above the table, 18+ cm below the eye (" + h.ToString("0.000") + ")");
                    Check(items[i].position.z < hive.position.z - 0.25f, "arc: " + items[i].name + " in front of the hive's face (z " + items[i].position.z.ToString("0.000") + ")");
                    Check(HorizAngle(head.position, items[i].position, hive.position) <= 25.5f, "arc: " + items[i].name + " within 25 deg of gaze");
                    Vector3 size = Vector3.Scale(di.localBounds.size, items[i].lossyScale);
                    Check(Mathf.Abs(Mathf.Max(size.x, Mathf.Max(size.y, size.z)) - 0.17f) < 0.003f, "arc: " + items[i].name + " normalized to 17 cm");
                    var col = items[i].GetComponent<BoxCollider>();
                    Vector3 cs = Vector3.Scale(col.size, items[i].lossyScale);
                    Check(col.enabled && col.isTrigger && cs.x <= 0.2401f && cs.y <= 0.2401f && cs.z <= 0.2401f, "arc: " + items[i].name + " collider <= 24 cm");
                    for (int k = i + 1; k < 3; k++)
                        Check(Vector3.Distance(items[i].position, items[k].position) >= 0.2f, "arc: " + items[i].name + "/" + items[k].name + " 20+ cm apart");
                }
                Check(d.ActiveBees == (on ? 12 : 0), "arc: crews hovering " + (on ? 12 : 0) + " (got " + d.ActiveBees + ")");
                Vector3 p0 = spray.position, p1 = smokerItem.position;
                Steps(d, 0.5f);
                Check((spray.position - p0).sqrMagnitude < 1e-8f && (smokerItem.position - p1).sqrMagnitude < 1e-8f, "arc: parked items completely still");

                // wrong pick: GRIEVANCE, then the repo crew hauls it out
                sd.OnChoiceMade(GameIds.Choice.SprayWater, false);
                var sdi = spray.GetComponent<DeliveryItem>();
                Check(sdi.stamp != null && sdi.stamp.gameObject.activeSelf && sdi.stampText.text == "GRIEVANCE", "stamp: GRIEVANCE on the spray");
                Steps(d, 0.2f);
                Check(Mathf.Abs(sdi.stamp.lossyScale.x - 1f) < 0.01f, "stamp: settled at scale 1 after 120 ms (" + sdi.stamp.lossyScale.x.ToString("0.000") + ")");
                sd.OnOutcomeShown(GameIds.Choice.SprayWater, false);
                Steps(d, 0.3f);
                Check(spray.gameObject.activeSelf, "repo: spray still here while the crew arrives");
                Steps(d, 3.0f);
                Check(!spray.gameObject.activeSelf, "repo: spray hauled out through the window");
                Check(swatter.gameObject.activeSelf && smokerItem.gameObject.activeSelf, "repo: the other choices still parked");

                // right pick: APPROVED, set down on the spot, leftovers return
                sd.OnChoiceMade(GameIds.Choice.Smoker, true);
                Check(smokerItem.GetComponent<DeliveryItem>().stampText.text == "APPROVED", "stamp: APPROVED on the smoker");
                Steps(d, 2.0f);
                Check(Vector3.Distance(smokerItem.position, sd.Spot.position) < 0.001f, "set-down: smoker on its spot (" + Vector3.Distance(smokerItem.position, sd.Spot.position).ToString("0.0000") + " m)");
                Check(smokerItem.GetComponent<DeliveryItem>().delivered, "set-down: touchdown");
                Check(smokerItem.GetComponent<DeliveryItem>().shadow.enabled, "set-down: contact shadow");
                Steps(d, 0.5f);
                Check(!swatter.gameObject.activeSelf, "return: swatter flew back out");
                Steps(d, 2.0f);
                Check(d.ActiveBees == 0, "station: no bees left (" + d.ActiveBees + ")");
                sd.OnStationExit(true);
                Check(smokerItem.gameObject.activeSelf && Vector3.Distance(smokerItem.position, sd.Spot.position) < 0.001f, "exit: smoker stays on its spot");
            }

            // ---- 5. Board jump mid-hive-flight and mid-repo, then a group reset
            d.SnapHome(hive, hide: true);
            int landed2 = 0;
            d.DeliverHome(hive, DeliveryStyle.Festoon, 0, () => landed2++);
            Steps(d, on ? 1.5f : 0.2f);
            d.FinishAll();
            Check(landed2 == 1, "jump: hive onLanded once on the snap (got " + landed2 + ")");
            Check(Vector3.Distance(hive.position, hiveHome) < 0.001f && hive.gameObject.activeSelf, "jump: hive on its plate, nothing in the air");
            Check(d.ActiveBees == 0 && d.InFlight == 0, "jump: no orphan bees after the hive snap (" + d.ActiveBees + ")");
            Steps(d, 0.1f);
            Check(landed2 == 1, "jump: no second onLanded");

            sd.ResetItems();
            sd.Prepare();
            if (off) Check(smoker.gameObject.activeSelf && smoker.localScale == Vector3.one, "off: after a reset (run 2) the legacy smoker is shown again");
            sd.ParkChoices();
            Steps(d, off ? 0.1f : 0.6f); // mid-flight park
            sd.OnChoiceMade(GameIds.Choice.Swat, false);
            sd.OnOutcomeShown(GameIds.Choice.Swat, false);
            Steps(d, off ? 0.1f : 0.4f); // mid-repo
            d.FinishAll();
            if (!off)
            {
                Check(!swatter.gameObject.activeSelf, "jump: swatter repo snapped out");
                Check(spray.GetComponent<DeliveryItem>().parked && !d.IsFlying(spray), "jump: the others snapped into the arc");
            }
            sd.OnStationExit(false);
            Check(!swatter.gameObject.activeSelf && !spray.gameObject.activeSelf && !smoker.gameObject.activeSelf || off, "exit unsolved: every choice gone");
            Steps(d, Dt);
            Check(d.ActiveBees == 0, "exit: no orphan bees (" + d.ActiveBees + ")");

            // reset in the middle of everything
            d.SnapHome(hive, hide: true);
            int landed3 = 0;
            d.DeliverHome(hive, DeliveryStyle.Festoon, 0, () => landed3++);
            d.CutRing(null);
            Steps(d, off ? 0.05f : 0.3f);
            d.CancelAll();
            Steps(d, Dt);
            Check(d.ActiveBees == 0 && d.InFlight == 0, "reset: no bees, nothing in flight");
            Check(off ? landed3 == 1 : landed3 == 0, "reset: no callback from a cancelled flight");
            Check(Vector3.Distance(hive.position, hiveHome) < 0.001f, "reset: hive not left in the air");

            // ---- 6. budget: garbage and CPU per Step with a hive + rocket + three crowns in the air
            if (on)
            {
                d.SnapHome(hive, hide: true);
                d.SnapHome(rocket, hide: true);
                sd.ResetItems();
                sd.Prepare();
                d.DeliverHome(hive, DeliveryStyle.Festoon);
                d.DeliverHome(rocket, DeliveryStyle.Crown, 12);
                sd.ParkChoices();
                Steps(d, 0.2f);
                int bees = d.ActiveBees;
                for (int i = 0; i < 10; i++) d.Step(Dt); // warm
                long before = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                const int n = 50;
                for (int i = 0; i < n; i++) d.Step(Dt);
                sw.Stop();
                long garbage = GC.GetAllocatedBytesForCurrentThread() - before;
                double ms = sw.Elapsed.TotalMilliseconds / n;
                Debug.Log("[VBSDelivery] budget: " + bees + " bees, " + garbage + " B garbage over " + n + " steps, " + ms.ToString("0.000") + " ms/step (editor, unoptimized)");
                Check(bees == 72, "budget: 48 + 12 + 12 bees in the air (got " + bees + ")");
                Check(garbage == 0, "budget: 0 B garbage per Step (got " + garbage + " B over " + n + ")");
                Check(ms <= 0.5, "budget: <= 0.5 ms per Step in the editor (got " + ms.ToString("0.000") + ")");
            }

            Debug.Log("[VBSDelivery] mode " + _mode + " done, failures so far: " + Fail.Count);
        }
    }
}
