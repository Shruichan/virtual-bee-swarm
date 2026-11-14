using System.Collections;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// pre (fitting: place the hive, START), hook ("fix it in four minutes or we move to the Moon", Beeswax on his
    /// rocket) and meet (Beeswax flies in, the future window opens on the meadow). Entering pre also puts every
    /// world object back to its start state, so group.reset / Shift+R give a clean run.
    /// </summary>
    public class IntroSequence : BeatBehaviour
    {
        [SerializeField] BeeswaxController beeswax;
        [SerializeField] FutureWindow window;
        [SerializeField] SwarmSystem swarm;
        [SerializeField] HexagonPanel hexagons;
        [SerializeField] HiveBody hive;
        [SerializeField] EmbersFx embers;
        [Tooltip("Shown during pre: instructions + the START button.")]
        [SerializeField] GameObject preUi;
        [SerializeField] ChoiceButton startButton;
        [Tooltip("Where Beeswax sits on HONEYMOON-1 during pre/hook.")]
        [SerializeField] Transform rocketPerch;
        [Tooltip("v3.1: rings the window, flies the hive in on bee chains and the rocket in nose-up. Empty = today's hook.")]
        [SerializeField] BeeDelivery delivery;
        [Tooltip("HONEYMOON-1 (empty: rocketPerch's parent).")]
        [SerializeField] Transform rocket;

        bool Delivering => delivery != null && BeeDelivery.Mode != DeliveryMode.Off;
        Transform Rocket => rocket != null ? rocket : rocketPerch != null ? rocketPerch.parent : null;
        GameDirector _beats;

        public override bool Handles(GameBeat beat) => beat == GameBeat.Pre || beat == GameBeat.Hook || beat == GameBeat.Meet;

        protected override void Awake()
        {
            base.Awake();
            if (beeswax == null) beeswax = FindAnyObjectByType<BeeswaxController>();
            if (window == null) window = FindAnyObjectByType<FutureWindow>();
            if (swarm == null) swarm = FindAnyObjectByType<SwarmSystem>();
            if (hexagons == null) hexagons = FindAnyObjectByType<HexagonPanel>();
            if (hive == null) hive = FindAnyObjectByType<HiveBody>();
            if (embers == null) embers = FindAnyObjectByType<EmbersFx>();
            if (delivery == null) delivery = FindAnyObjectByType<BeeDelivery>();
        }

        void Start()
        {
            _beats = director != null ? director : GameDirector.Instance;
            if (_beats != null) _beats.BeatChanged += HandleBeatChanged;
        }

        void OnDestroy()
        {
            if (_beats != null) _beats.BeatChanged -= HandleBeatChanged;
        }

        // A Board jump / skip past the hook: the hive and the rocket are simply there (no flight holds a beat).
        void HandleBeatChanged(GameBeat prev, GameBeat beat)
        {
            if (beat <= GameBeat.Hook || delivery == null) return;
            if (hive != null) delivery.EnsureHome(hive.transform, DeliveryStyle.Festoon, 0, HiveLanded);
            if (Rocket != null) delivery.EnsureHome(Rocket, DeliveryStyle.Crown, 12);
        }

        void HiveLanded()
        {
            if (swarm != null && swarm.Mode == SwarmMode.Hidden)
            {
                swarm.Scatter();
                swarm.SetMode(SwarmMode.Idle);
            }
        }

        void OnEnable() { if (startButton != null) startButton.Pressed += HandleStart; }
        void OnDisable() { if (startButton != null) startButton.Pressed -= HandleStart; }

        void HandleStart(ChoiceButton b)
        {
            if (IsActive && ActiveBeat == GameBeat.Pre && director != null) director.Next("start button");
        }

        protected override void OnEnter(GameBeat beat)
        {
            switch (beat)
            {
                case GameBeat.Pre:
                    if (net != null) net.PreloadVoice(); // every clip loads while the hive is being placed (S8.2)
                    ResetWorld();
                    SetActive(preUi, true);
                    if (startButton != null) startButton.SetInteractable(true);
                    break;
                case GameBeat.Hook:
                    if (net != null) net.PreloadVoice(); // no-op once done; covers a jump straight to hook
                    SetActive(preUi, false);
                    StartCoroutine(Hook());
                    break;
                case GameBeat.Meet:
                    StartCoroutine(Meet());
                    break;
            }
        }

        protected override void OnExit(GameBeat beat)
        {
            if (beat == GameBeat.Pre) SetActive(preUi, false);
            if (beat == GameBeat.Meet && window != null && !window.IsOpen) window.Open(); // skipped ahead: still open it
        }

        protected override void OnResetState() => SetActive(preUi, false);

        void ResetWorld()
        {
            if (hexagons != null) hexagons.ResetAll();
            if (hive != null) hive.ResetHive();
            if (window != null) window.ResetWindow();
            if (swarm != null) swarm.ResetSwarm();
            if (embers != null) embers.ResetFx();
            if (beeswax != null)
            {
                beeswax.ResetBeeswax();
                if (rocketPerch != null) beeswax.AttachTo(rocketPerch);
            }
            if (delivery == null) return;
            if (Delivering)
            {
                // v3.1: nothing is here until the bees bring it (Beeswax rides in at the hive door: V14 refines that)
                if (hive != null) delivery.SnapHome(hive.transform, hide: true);
                if (Rocket != null) delivery.SnapHome(Rocket, hide: true);
                if (swarm != null) swarm.SetMode(SwarmMode.Hidden);
                if (beeswax != null && hive != null) beeswax.AttachTo(hive.DoorPoint);
            }
            else
            {
                if (hive != null) { delivery.SnapHome(hive.transform, hide: false); hive.gameObject.SetActive(true); }
                if (Rocket != null) { delivery.SnapHome(Rocket, hide: false); Rocket.gameObject.SetActive(true); }
            }
        }

        IEnumerator Hook()
        {
            bool delivering = Delivering && hive != null;
            if (delivering)
            {
                // 20 bees cut the ring, the window opens, the hive swings in on 6 bee chains (~2 s + 4.5 s)
                bool cut = false, landed = false;
                delivery.CutRing(() => cut = true);
                float t0 = Time.time;
                while (!cut && Time.time - t0 < 3f) yield return null;
                if (window != null) window.Open();
                delivery.DeliverHome(hive.transform, DeliveryStyle.Festoon, 0, () => { landed = true; HiveLanded(); });
                t0 = Time.time;
                while (!landed && Time.time - t0 < 6f) yield return null;
            }
            if (beeswax != null)
            {
                if (!delivering && rocketPerch != null) beeswax.AttachTo(rocketPerch);
                beeswax.WhipAround();
            }
            if (swarm != null) swarm.SetMode(SwarmMode.Idle);
            yield return SayAndWait(GameLines.Hook, 3f, 8f);
            if (delivering && Rocket != null) delivery.DeliverHome(Rocket, DeliveryStyle.Crown, 12); // flown in nose-up
            yield return SayAndWait(GameLines.Hook2, 1.5f, 6f);
            Complete(0.4f);
        }

        IEnumerator Meet()
        {
            if (beeswax != null)
            {
                beeswax.Detach();
                beeswax.GoPresent();
            }
            yield return Wait(0.8f);
            if (window != null) window.Open();
            if (net != null) net.RequestScene(GameIds.Scene.Meadow, sendToHive: true);
            yield return SayAndWait(GameLines.MeetIntro, 2f, 8f);
            yield return SayAndWait(GameLines.Meet, 2f, 6f);
            yield return SayAndWait(GameLines.MeetWindow, 1.5f, 7f);
            Complete(0.4f);
        }
    }
}
