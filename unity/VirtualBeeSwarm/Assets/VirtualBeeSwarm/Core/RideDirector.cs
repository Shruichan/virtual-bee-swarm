using System;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Ride script v2 glue that belongs to no single beat. Installs itself into any scene with a GameDirector.
    ///   * Ride clock log: every beat change and every line Beeswax starts, on the script's clock (0:00 = the signature):
    ///     "[VBS][Ride] +0:25.21 line room_hook_1 (clip 5.25 s)", "[VBS][Ride] beat smoker at +0:40.02 (meet took 31.4 s)".
    ///   * room_live_filler before every live answer: the Hive's intent "ask" (a question, not a what-if) arrives before
    ///     Claude's answer streams, so Beeswax covers the wait ("I could drone on about that. Short version:").
    ///   * room_clock_zero once per run when the boarding clock hits 0:00 with problems left (no fail state).
    ///   * ASK BEESWAX after the contract (map beat): the operator opens it from the Board (Say a line: room_ask_anything)
    ///     or with J on the desktop; a small card parks by the window and a pinch talks to the Hive (the existing what-if
    ///     voice path; the Hive's router answers questions live from the vetted facts).
    ///   * RidePacing's stalls reset with every run.
    /// </summary>
    [DefaultExecutionOrder(-20)]
    public class RideDirector : MonoBehaviour
    {
        public static RideDirector Instance { get; private set; }

        [SerializeField] GameDirector director;
        [SerializeField] GameNet net;
        [SerializeField] KeyCode askKey = KeyCode.J;
        [SerializeField] bool logLines = true;

        public bool AskShown => _askCard != null && _askCard.activeSelf;

        float _beatAt = -1f;
        GameBeat _beat = GameBeat.Pre;
        bool _clockZeroSaid, _subscribed;
        float _lastFillerAt = -100f;
        GameObject _askCard;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            var gd = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (gd == null || FindAnyObjectByType<RideDirector>() != null) return;
            var go = new GameObject("Ride");
            go.transform.SetParent(gd.transform.parent, false);
            go.AddComponent<RideDirector>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { enabled = false; return; }
            Instance = this;
            if (director == null) director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (net == null) net = GameNet.Instance != null ? GameNet.Instance : FindAnyObjectByType<GameNet>();
            Debug.Log("[VBS][Ride] ride script v2 staging on (optional lines: " + RidePacing.Setting + ")");
        }

        void OnEnable()
        {
            if (Instance != this) return;
            if (director != null) director.BeatChanged += HandleBeat;
            if (net != null) { net.VoiceLineStarted += HandleLine; net.IntentReceived += HandleIntent; }
            _subscribed = true;
        }

        void OnDisable()
        {
            if (!_subscribed) return;
            _subscribed = false;
            if (director != null) director.BeatChanged -= HandleBeat;
            if (net != null) { net.VoiceLineStarted -= HandleLine; net.IntentReceived -= HandleIntent; }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            WhatIfBeat.AskOpen = false;
        }

        string Clock()
        {
            float t = director != null ? director.ShowSeconds : -1f;
            if (t < 0f) return "pre";
            int m = Mathf.FloorToInt(t / 60f);
            return "+" + m + ":" + (t - m * 60f).ToString("00.00");
        }

        void HandleBeat(GameBeat prev, GameBeat beat)
        {
            float now = Time.time;
            if (beat != _beat || prev == beat)
            {
                Debug.Log("[VBS][Ride] beat " + GameIds.BeatName(beat) + " at " + Clock() +
                          (_beatAt >= 0f && _beat != GameBeat.Pre ? " (" + GameIds.BeatName(_beat) + " took " + (now - _beatAt).ToString("0.0") + " s)" : "") +
                          (beat >= GameBeat.Smoker && director != null ? " score clock " + GameMaterials.Clock(director.RunSeconds) : ""));
            }
            _beat = beat;
            _beatAt = now;
            if (beat == GameBeat.Pre)
            {
                RidePacing.ResetRun();
                _clockZeroSaid = false;
                ShowAsk(false, "");
            }
            if (prev == GameBeat.Map && beat != GameBeat.Map) ShowAsk(false, "");
        }

        void HandleLine(GameNet.VoiceLineInfo info)
        {
            if (info.id == "room_ask_anything" && _beat == GameBeat.Map && !AskShown) ShowAsk(true, "the Board's room_ask_anything");
            if (!logLines) return;
            Debug.Log("[VBS][Ride] " + Clock() + " line " + (info.id.Length > 0 ? info.id : "(live)") + " (" +
                      (info.stream ? "stream" : info.hasClip ? "clip " + info.clipSeconds.ToString("0.00") + " s" : "caption only") + ") [" + GameIds.BeatName(_beat) + "]");
        }

        void HandleIntent(string name, string topic, string text)
        {
            if (name != GameIds.Intent.Ask || net == null || _beat == GameBeat.Pre || _beat == GameBeat.Hook) return;
            if (Time.time - _lastFillerAt < 3f) return; // the Hive (or we) already covered this answer
            var filler = RideLines.LiveFiller;
            if (net.CurrentCaption == filler.caption) return;
            _lastFillerAt = Time.time;
            Debug.Log("[VBS][Ride] ask intent: " + filler.id + " covers the live answer");
            net.Say(filler);
        }

        void Update()
        {
            if (director == null) return;
            var beat = director.Beat;

            // room_clock_zero: the boarding clock hits 0:00 with problems left
            if (!_clockZeroSaid && beat >= GameBeat.Smoker && beat <= GameBeat.Heat && !director.RunFinished &&
                director.RunSeconds >= director.TimeLimitSeconds && director.SolvedStations < 4)
            {
                _clockZeroSaid = true;
                RidePacing.NoteStall("the boarding clock hit 0:00");
                if (net != null) net.Say(RideLines.ClockZero);
            }

            if (beat == GameBeat.Map && GameInput.KeyDown(askKey))
            {
                if (AskShown) ShowAsk(false, "J");
                else
                {
                    ShowAsk(true, "J");
                    if (net != null) net.Say(RideLines.AskAnything);
                }
            }
        }

        // ------------------------------------------------------------ ASK BEESWAX
        void ShowAsk(bool on, string why)
        {
            WhatIfBeat.AskOpen = on;
            if (!on)
            {
                if (_askCard != null && _askCard.activeSelf)
                {
                    var bd = FindAnyObjectByType<BeeDelivery>();
                    if (bd != null) bd.Release(_askCard.transform, hide: true);
                    _askCard.SetActive(false);
                    Debug.Log("[VBS][Ride] ASK BEESWAX closed");
                }
                return;
            }
            if (_askCard == null) _askCard = BuildAskCard();
            var delivery = FindAnyObjectByType<BeeDelivery>();
            Pose pose = AskPose(delivery);
            if (delivery != null && BeeDelivery.Mode != DeliveryMode.Off) delivery.Park(_askCard.transform, pose, 4);
            else { _askCard.transform.SetPositionAndRotation(pose.position, pose.rotation); _askCard.SetActive(true); }
            Debug.Log("[VBS][Ride] ASK BEESWAX open (" + why + "): pinch, hold and ask; the Hive answers live");
        }

        Pose AskPose(BeeDelivery delivery)
        {
            Transform head = delivery != null ? delivery.Head : (Camera.main != null ? Camera.main.transform : null);
            Vector3 eye = head != null ? head.position : new Vector3(0f, 1.6f, 0f);
            Vector3 pane = delivery != null ? delivery.PanePosition() : eye + Vector3.forward * 1.5f;
            // by the window: a third of the way from the window to the player, at chest height
            Vector3 pos = Vector3.Lerp(pane, eye, 0.35f);
            pos.y = eye.y - 0.25f;
            Vector3 toHead = eye - pos;
            toHead.y = 0f;
            if (toHead.sqrMagnitude < 1e-4f) toHead = Vector3.back;
            return new Pose(pos, Quaternion.LookRotation(-toHead.normalized, Vector3.up) * Quaternion.Euler(10f, 0f, 0f));
        }

        GameObject BuildAskCard()
        {
            var card = new GameObject("AskBeeswaxCard");
            card.transform.SetParent(transform, false);
            var back = GameObject.CreatePrimitive(PrimitiveType.Cube);
            back.name = "Backing";
            var col = back.GetComponent<Collider>();
            if (col != null) Destroy(col);
            back.transform.SetParent(card.transform, false);
            back.transform.localPosition = new Vector3(0f, 0f, 0.006f);
            back.transform.localScale = new Vector3(0.3f, 0.2f, 0.01f);
            Font font = null;
            foreach (var tm in FindObjectsByType<TextMesh>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (tm.font != null) { font = tm.font; break; }
            var br = back.GetComponent<Renderer>();
            br.sharedMaterial = GameMaterials.Create("VBS_AskCard", new Color(0.13f, 0.11f, 0.08f));
            br.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var go = new GameObject("Text");
            go.transform.SetParent(card.transform, false);
            var text = go.AddComponent<TextMesh>();
            if (font != null) { text.font = font; go.GetComponent<MeshRenderer>().sharedMaterial = font.material; }
            text.fontSize = 64;
            text.characterSize = 0.0024f;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.color = new Color(1f, 0.9f, 0.55f);
            text.text = RideLines.AskCard;
            card.SetActive(false);
            return card;
        }
    }
}
