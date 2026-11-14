using System.Collections;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// If ahead: a giant hornet forces its way in. Make a FIST (any hand, grip on a controller, or hold H) and the
    /// swarm balls up around it. "Now HUM!": the hum (HumDetector via GameNet, the Board's hum.set slider, or
    /// hold U on the desktop) heats the ball. 46 °C cooks the hornet; reaching 48 °C within a moment is the
    /// overshoot: "STOP, you're cooking the girls!". Then hornet.cooked{overshoot} goes to the Hive.
    /// (Japanese honeybees really do this: Ono et al., Nature 1995.)
    /// </summary>
    public class HornetBeat : BeatBehaviour
    {
        [SerializeField] Transform hornet;
        [SerializeField] Transform hornetStart;
        [SerializeField] Renderer[] hornetRenderers = new Renderer[0];
        [SerializeField] Color cookedColor = new Color(0.35f, 0.25f, 0.2f, 1f);
        [SerializeField] SwarmSystem swarm;
        [SerializeField] BeeswaxController beeswax;
        [SerializeField] HiveBody hive;

        [Header("Thermometer")]
        [SerializeField] GameObject thermometer;
        [Tooltip("Scaled on Y from 0 to 1 (pivot at the bottom).")]
        [SerializeField] Transform thermoFill;
        [SerializeField] Renderer thermoFillRenderer;
        [SerializeField] TextMesh thermoLabel;
        [SerializeField] float tempStart = 35f;
        [SerializeField] float tempMax = 50f;
        [SerializeField] float cookAt = 46f;
        [SerializeField] float overshootAt = 48f;
        [SerializeField] float overshootWindowSeconds = 2.5f;

        [Header("Fallbacks")]
        [SerializeField] KeyCode humKey = KeyCode.U;
        [SerializeField] float keyHeatPerSecond = 0.14f;
        [Tooltip("No fist after this long: ball up anyway.")]
        [SerializeField] float fistTimeoutSeconds = 9f;

        enum Phase { Idle, Arrive, Fist, Hum, Cooked }
        Phase _phase;
        float _keyHeat, _cookedAt;
        bool _cooked, _overshoot, _sent, _saidLouder;
        int _shownTemp = int.MinValue;

        public bool Cooked => _cooked;
        public bool Overshoot => _overshoot;

        public override bool Handles(GameBeat beat) => beat == GameBeat.Hornet;

        /// <summary>Operator/debug/test: add heat as if they hummed (0..1 of the thermometer).</summary>
        public void AddHeat(float amount) => _keyHeat = Mathf.Clamp01(_keyHeat + amount);

        protected override void Awake()
        {
            base.Awake();
            if (swarm == null) swarm = FindAnyObjectByType<SwarmSystem>();
            if (beeswax == null) beeswax = FindAnyObjectByType<BeeswaxController>();
            if (hive == null) hive = FindAnyObjectByType<HiveBody>();
            HideAll();
        }

        protected override void OnEnter(GameBeat beat)
        {
            _phase = Phase.Arrive;
            _keyHeat = 0f;
            _cooked = _overshoot = _sent = _saidLouder = false;
            _shownTemp = int.MinValue;
            if (net != null)
            {
                net.ResetHum();
                net.WarmUpHum(); // open the mic early; we only listen at "Now HUM!"
            }
            if (hornet != null)
            {
                hornet.gameObject.SetActive(true);
                if (hornetStart != null) hornet.position = hornetStart.position;
                GameMaterials.ClearAll(hornetRenderers);
            }
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            if (beeswax != null) { beeswax.GoPresent(); beeswax.WhipAround(); }
            if (swarm != null) swarm.SetMode(SwarmMode.Guard);
            if (hornet != null && hive != null)
                StartCoroutine(MoveTo(hornet, hive.DoorPoint.position - hive.DoorPoint.forward * 0.1f + Vector3.up * 0.03f, 2.5f));
            _phase = Phase.Fist;
            yield return SayAndWait(GameLines.HornetWarning, 1.5f, 4f); // "...Make a FIST!"
            float start = Time.time;
            while (!GameHands.AnyFist() && Time.time - start < fistTimeoutSeconds && !_sent) yield return null;
            if (_sent) yield break; // timed out / finished meanwhile: don't re-open the hum

            if (swarm != null)
            {
                swarm.SetBallTarget(hornet);
                swarm.SetMode(SwarmMode.Ball);
            }
            SetActive(thermometer, true);
            yield return SayAndWait(GameLines.HornetHum, 1f, 3f);
            if (_sent) yield break;
            if (net != null) net.SetHumListening(true);
            _phase = Phase.Hum;
        }

        void Update()
        {
            if (!IsActive || (_phase != Phase.Hum && _phase != Phase.Cooked)) return;

            if (GameInput.KeyHeld(humKey)) _keyHeat = Mathf.Clamp01(_keyHeat + keyHeatPerSecond * Time.deltaTime);
            float heat = Mathf.Max(net != null ? net.HumHeat : 0f, _keyHeat);
            float temp = Mathf.Lerp(tempStart, tempMax, heat);
            if (swarm != null) swarm.BallHeat = heat;
            ShowTemp(temp, heat);
            if (!_saidLouder && heat >= 0.35f && _phase == Phase.Hum)
            {
                _saidLouder = true;
                if (net != null) net.Say(GameLines.HornetLouder);
            }

            if (_phase == Phase.Hum && temp >= cookAt)
            {
                _phase = Phase.Cooked;
                _cooked = true;
                _cookedAt = Time.time;
                GameMaterials.TintAll(hornetRenderers, cookedColor);
            }
            if (_phase == Phase.Cooked && !_sent)
            {
                if (temp >= overshootAt) Finish(true);
                else if (Time.time - _cookedAt >= overshootWindowSeconds) Finish(false);
            }
        }

        void Finish(bool overshoot)
        {
            _sent = true;
            _overshoot = overshoot;
            if (net != null)
            {
                net.SetHumListening(false);
                net.SendHornetCooked(overshoot);
            }
            StartCoroutine(Outro(overshoot));
        }

        IEnumerator Outro(bool overshoot)
        {
            if (overshoot && net != null) net.StopVoice(); // cut him off: this one is urgent
            yield return SayAndWait(overshoot ? GameLines.HornetOvershoot : GameLines.HornetDone, 2f, 6f);
            if (swarm != null)
            {
                swarm.BallHeat = 0f;
                swarm.SetMode(SwarmMode.Calm);
            }
            if (hornet != null && hive != null)
                yield return MoveTo(hornet, new Vector3(hornet.position.x, hive.transform.position.y + 0.01f, hornet.position.z), 0.5f);
            Complete(1f);
        }

        void ShowTemp(float temp, float heat)
        {
            if (thermoFill != null)
            {
                var s = thermoFill.localScale;
                s.y = Mathf.Max(0.02f, heat);
                thermoFill.localScale = s;
            }
            int whole = Mathf.FloorToInt(temp);
            if (whole == _shownTemp) return;
            _shownTemp = whole;
            if (thermoLabel != null) thermoLabel.text = whole + " °C";
            if (thermoFillRenderer != null)
                GameMaterials.Tint(thermoFillRenderer, temp >= overshootAt ? Color.red : temp >= cookAt ? new Color(1f, 0.5f, 0.1f) : new Color(1f, 0.85f, 0.2f));
        }

        protected override void OnBeatTimeout()
        {
            if (_sent) { Complete(); return; }
            _cooked = true;
            Finish(false); // the Board can still drive hum.set; on timeout we call it cooked
        }

        protected override void OnExit(GameBeat beat)
        {
            if (net != null)
            {
                net.SetHumListening(false);
                if (!_sent) { _sent = true; net.SendHornetCooked(false); }
            }
            if (swarm != null && swarm.Mode == SwarmMode.Ball) swarm.SetMode(SwarmMode.Calm);
            HideAll();
            _phase = Phase.Idle;
        }

        protected override void OnResetState()
        {
            _phase = Phase.Idle;
            if (net != null) net.SetHumListening(false);
            HideAll();
        }

        void HideAll()
        {
            if (hornet != null) hornet.gameObject.SetActive(false);
            SetActive(thermometer, false);
        }
    }
}
