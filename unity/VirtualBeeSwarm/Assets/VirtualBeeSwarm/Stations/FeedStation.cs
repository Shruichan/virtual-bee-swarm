using System.Collections;
using UnityEngine;
using VirtualBeeSwarm.HandsOn;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Station 2: "Is there enough food in there?" The player lifts and rocks the hive by hand or controller
    /// (hold G in the editor). It feels too light. "We're starving." Then store honey (wrong: the colony gets
    /// sick, the honey mistake) or sugar syrup (right). Keys 1-2.
    /// requireMistakeFirst = syrup is locked until honey was tried (for filming the honey mistake).
    /// </summary>
    public class FeedStation : StationBase
    {
        [SerializeField] Transform honeyJar;
        [SerializeField] Transform syrupBottle;
        [Tooltip("Where a jar goes to pour (above the hive).")]
        [SerializeField] Transform pourPoint;
        [SerializeField] bool requireMistakeFirst;
        [Tooltip("Repeat the weight-check instruction after this many seconds of inactivity.")]
        [SerializeField] float liftHintSeconds = 8f;
        [Tooltip("If no one completes the weight check, Beeswax explains the empty hive and continues the demo.")]
        [SerializeField] float liftGiveUpSeconds = 18f;

        protected override GameLine Prompt => GameLines.FeedPrompt;
        protected override GameLine Hint => RideLines.HintFeed;

        bool _rocked, _triedHoney;
        float _retryUntil = -1f, _retrySpokenAt = -100f;
        const float RetryVoiceCooldown = 12f; // the red card can flash on every early let-go; his line can't
        HandsOnPrompt _weightPrompt;
        public bool WarningVisible => _weightPrompt != null && _weightPrompt.Visible && Time.time < _retryUntil;
        Transform _emptyCard;
        Vector3 _honeyHome, _syrupHome;
        Quaternion _honeyRot, _syrupRot;

        protected override void Awake()
        {
            station = GameStation.Feed;
            base.Awake();
            if (honeyJar != null) { _honeyHome = honeyJar.localPosition; _honeyRot = honeyJar.localRotation; }
            if (syrupBottle != null) { _syrupHome = syrupBottle.localPosition; _syrupRot = syrupBottle.localRotation; }
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            if (hive != null)
            {
                hive.Rocked += HandleRocked;
                hive.RockReleasedEarly += HandleEarlyRelease;
                hive.Shaken += HandleShaken;
            }
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (hive != null)
            {
                hive.Rocked -= HandleRocked;
                hive.RockReleasedEarly -= HandleEarlyRelease;
                hive.Shaken -= HandleShaken;
            }
        }

        // dartiros: shaking the box stirs the colony (a short buzz-up that settles back to calm on its own)
        void HandleShaken()
        {
            if (swarm != null) swarm.Agitation = Mathf.Max(swarm.Agitation, 0.3f); // capped: repeated shakes don't stack
        }

        void HandleRocked()
        {
            if (!IsActive) return;
            _rocked = true;
            _weightPrompt?.Hide();
        }

        void HandleEarlyRelease()
        {
            if (!IsActive || _rocked || Time.time < _retryUntil) return;
            _retryUntil = Time.time + 3f;
            ShowWeightPrompt("! PICK UP AND ROCK", HandsOnKit.Bad);
            GameSound.Cue("stamp_grievance", hive.transform);
            // dartiros: never cut Beeswax off mid-line, and say the retry at most once every 12 s
            if (net != null && !net.IsSpeaking && Time.time - _retrySpokenAt >= RetryVoiceCooldown)
            {
                _retrySpokenAt = Time.time;
                net.Say(GameLines.RoomFeedRockRetry);
            }
        }

        void ShowWeightPrompt(string text, Color color)
        {
            if (hive == null) return;
            if (_weightPrompt == null) _weightPrompt = HandsOnPrompt.Build(transform, "Hive weight check");
            var head = HandsOnKit.Head;
            Vector3 right = head != null ? head.right : Vector3.right;
            _weightPrompt.Show(text, hive.SwarmCenter, Vector3.up * 0.23f + right * 0.17f, color: color);
        }

        protected override void OnStationEnter()
        {
            _rocked = false;
            _retryUntil = -1f;
            _retrySpokenAt = -100f;
            if (swarm != null) swarm.SetMode(SwarmMode.Calm);
            BuildEmptyHive(); // [polish] made hidden now, so the theme has restyled it before it shows (it popped in plain, then jumped)
        }

        /// <summary>Let the player feel the weight, then explain the empty hive before the feeding choices arrive.</summary>
        protected override IEnumerator StationIntro()
        {
            if (beeswax != null && hive != null) beeswax.FlyTo(hive.transform.position + Vector3.up * 0.32f); // raps the roof
            if (hive != null) hive.LiftEnabled = true;
            ShowWeightPrompt("PICK UP THE HIVE", HandsOnKit.Honey);
            yield return SayAndWait(Prompt, 0.8f, 5.5f);
            // dartiros: no hint straight after the prompt (it doubled up); the lift hint comes after liftHintSeconds below
            float start = Time.time;
            bool hinted = false;
            while (!_rocked && hive != null && Time.time - start < liftGiveUpSeconds + 5f)
            {
                bool warning = Time.time < _retryUntil;
                if (Time.time - start >= liftGiveUpSeconds && !warning && !hive.Held) break;
                if (warning && hive.Held) ShowWeightPrompt("! ROCK IT BACK AND FORTH", HandsOnKit.Bad);
                if (!warning)
                    ShowWeightPrompt(hive.Held ? "ROCK IT BACK AND FORTH" : "PICK UP THE HIVE", HandsOnKit.Honey);
                if (!hinted && !warning && Time.time - start > liftHintSeconds && (net == null || !net.IsSpeaking))
                {
                    hinted = true;
                    if (net != null) net.Say(GameLines.FeedLiftHint);
                }
                yield return null;
            }
            EndWeightCheck();
            if (beeswax != null) { beeswax.WhipAround(); beeswax.GoPresent(); } // he tumbles off the roof
            if (_rocked)
            {
                ShowEmptyHive();
                yield return SayLine(RideLines.LiftLight, 1f);
            }
            else Debug.Log("[VBS][Stations] feed: weight check assisted");
            yield return SayAndWait(GameLines.FeedTooLight, 1f, 3f, unlessHive: false);
            HideEmptyHive();
            yield return SayLine(GameLines.RoomFeedRocketFuel);
            if (delivery != null) delivery.ParkChoices(); // syrup zips in, the jar labours in
            yield return SayAndWait(GameLines.FeedChoose, 1f, 4f, unlessHive: false);
            if (delivery != null && delivery.HeavyInbound)
            {
                // V17: Beeswax yells at the jar's crew once, over its flight
                Debug.Log("[VBS][Stations] feed: " + GameLines.RoomCrewHeavy.id + " over the jar's flight");
                yield return SayAndWait(GameLines.RoomCrewHeavy, 1f, 3f);
            }
            OfferChoices();
        }

        /// <summary>v2: right first time, the hero fact still gets said (room_feed_why).</summary>
        protected override IEnumerator AfterSolved(string choice)
        {
            if (_triedHoney) yield break;
            yield return Wait(0.3f);
            yield return SayLine(RideLines.FeedWhy);
        }

        protected override bool IsChoiceAllowed(string choice) =>
            !requireMistakeFirst || _triedHoney || choice != GameIds.Choice.SugarSyrup;

        protected override IEnumerator ShowOutcome(string choice, bool correct)
        {
            EndWeightCheck();
            bool honey = choice == GameIds.Choice.StoreHoney;
            if (honey) _triedHoney = true;
            Transform prop = honey ? honeyJar : syrupBottle;
            if (prop != null && pourPoint != null && !(correct && SetupByHand)) // HANDSON hook: the player just poured
            {
                Vector3 home = prop.position;
                Quaternion rot = prop.rotation;
                yield return ArcTo(prop, PourTarget(prop), 0.7f);
                GameSound.Cue("jar_tip", prop);
                float t0 = Time.time;
                while (Time.time - t0 < 0.5f)
                {
                    prop.rotation = rot * Quaternion.Euler(0f, 0f, Mathf.Lerp(0f, 110f, (Time.time - t0) / 0.5f));
                    yield return null;
                }
                yield return Wait(0.8f);
                if (hive != null) hive.SetSick(honey);
                if (swarm != null) swarm.SetMode(SwarmMode.Calm);
                prop.rotation = rot;
                yield return ArcTo(prop, home, 0.6f, 0.06f);
            }
            else if (hive != null) hive.SetSick(honey);
        }

        /// <summary>dartiros: the pour spot, lifted clear of the hive roof (PourPoint at 0.40 m sat in the roof on a scaled
        /// rig, so the jar clipped through it). Uses the hive's rendered top plus the prop's own height.</summary>
        Vector3 PourTarget(Transform prop)
        {
            Vector3 target = pourPoint.position;
            if (hive == null) return target;
            bool any = false;
            Bounds b = default;
            foreach (var r in hive.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || r is ParticleSystemRenderer || r is TrailRenderer) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            if (!any) return target;
            float propHeight = 0.12f;
            var pr = prop.GetComponentInChildren<Renderer>();
            if (pr != null) propHeight = Mathf.Max(0.05f, pr.bounds.size.y);
            target.y = Mathf.Clamp(b.max.y + propHeight + 0.06f, target.y, target.y + 0.5f);
            return target;
        }

        /// <summary>A short hop up and over onto the pour spot (it used to slide straight through the roof's edge).</summary>
        IEnumerator ArcTo(Transform t, Vector3 target, float seconds, float hop = 0.1f)
        {
            Vector3 from = t.position;
            float start = Time.time;
            while (Time.time - start < seconds)
            {
                float k = Mathf.SmoothStep(0f, 1f, (Time.time - start) / seconds);
                t.position = Vector3.LerpUnclamped(from, target, k) + Vector3.up * (Mathf.Sin(k * Mathf.PI) * hop);
                yield return null;
            }
            t.position = target;
        }

        void BuildEmptyHive()
        {
            if (_emptyCard == null)
            {
                _emptyCard = new GameObject("Empty hive label").transform;
                _emptyCard.SetParent(transform, false);
                var plate = GameObject.CreatePrimitive(PrimitiveType.Quad);
                plate.transform.SetParent(_emptyCard, false);
                plate.transform.localScale = new Vector3(0.28f, 0.09f, 1f);
                Destroy(plate.GetComponent<Collider>());
                plate.GetComponent<MeshRenderer>().sharedMaterial = GameMaterials.Create(
                    "Empty hive label", new Color(0.15f, 0.08f, 0.03f), doubleSided: true);
                var label = new GameObject("Empty").AddComponent<TextMesh>();
                label.transform.SetParent(_emptyCard, false);
                label.transform.localPosition = new Vector3(0f, 0f, -0.003f);
                label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                label.GetComponent<MeshRenderer>().sharedMaterial = label.font.material;
                label.text = "EMPTY HIVE";
                label.anchor = TextAnchor.MiddleCenter;
                label.alignment = TextAlignment.Center;
                label.fontSize = 48;
                label.characterSize = 0.01f;
                label.color = new Color(1f, 0.94f, 0.76f);
                _emptyCard.gameObject.SetActive(false);
            }
        }

        void ShowEmptyHive()
        {
            var camera = Camera.main;
            if (hive == null || camera == null) return;
            BuildEmptyHive();
            var eyes = camera.transform;
            Vector3 front = Vector3.ProjectOnPlane(eyes.forward, Vector3.up).normalized;
            if (front.sqrMagnitude < 0.5f) front = Vector3.forward;
            Vector3 position = eyes.position + front * 0.8f - Vector3.up * 0.42f + eyes.right * 0.22f; // final audit: off the caption // [polish] was 0.65 / 0.17: out of the face, down by the hive
            _emptyCard.SetPositionAndRotation(position, Quaternion.LookRotation(position - eyes.position, Vector3.up));
            _emptyCard.gameObject.SetActive(true);
        }

        void HideEmptyHive()
        {
            if (_emptyCard != null) _emptyCard.gameObject.SetActive(false);
        }

        void EndWeightCheck()
        {
            _weightPrompt?.Hide();
            if (hive != null) hive.LiftEnabled = false;
        }

        protected override void OnStationExit()
        {
            EndWeightCheck();
            HideEmptyHive();
            if (hive != null)
            {
                if (Solved) hive.SetSick(false);
            }
            RestoreProps();
        }

        protected override void OnStationReset()
        {
            HideEmptyHive();
            EndWeightCheck();
            _rocked = false;
            _triedHoney = false;
            if (hive != null)
            {
                hive.SetSick(false);
            }
            RestoreProps();
        }

        void RestoreProps()
        {
            if (honeyJar != null) { honeyJar.localPosition = _honeyHome; honeyJar.localRotation = _honeyRot; }
            if (syrupBottle != null) { syrupBottle.localPosition = _syrupHome; syrupBottle.localRotation = _syrupRot; }
        }
    }
}
