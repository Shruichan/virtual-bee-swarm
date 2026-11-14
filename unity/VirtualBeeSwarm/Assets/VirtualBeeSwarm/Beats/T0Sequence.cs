using System.Collections;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// T-0: "...I'm still going. Ticket's non-refundable." Beeswax boards HONEYMOON-1, "TAKE ME TO THE MOON!",
    /// 3-2-1, launch (through the ceiling), passthrough dims (optional IPassthroughDimmer), the window swells into the
    /// Moon while Moon + Earth placeholders appear in the room (Earth rising). "...Not one flower." He crashes back
    /// onto the real table, dusty. "Same time tomorrow? There are six more hives."
    /// </summary>
    public class T0Sequence : BeatBehaviour
    {
        [Header("Rocket")]
        [SerializeField] Transform rocket;
        [SerializeField] Transform rocketSeat;
        [SerializeField] GameObject flame;
        [SerializeField] TextMesh rocketDisplay;
        [SerializeField] float launchHeight = 3f;
        [SerializeField] float launchSeconds = 3f;
        [Tooltip("Where it crashes back (on the table).")]
        [SerializeField] Transform crashSpot;
        [SerializeField] ParticleSystem dustPuff;

        [Header("Moon")]
        [SerializeField] GameObject moon;
        [SerializeField] Transform earth;
        [SerializeField] float earthRise = 0.45f;
        [SerializeField, Range(0f, 1f)] float passthroughDim = 0.75f;

        [Header("Others")]
        [SerializeField] BeeswaxController beeswax;
        [SerializeField] FutureWindow window;
        [SerializeField] SwarmSystem swarm;

        IPassthroughDimmer _dimmer;
        Vector3 _rocketHomePos, _earthHome, _moonScale = Vector3.one;
        Quaternion _rocketHomeRot;
        float _dim;
        bool _launched, _landed;

        public override bool Handles(GameBeat beat) => beat == GameBeat.T0;

        protected override void Awake()
        {
            base.Awake();
            if (beeswax == null) beeswax = FindAnyObjectByType<BeeswaxController>();
            if (window == null) window = FindAnyObjectByType<FutureWindow>();
            if (swarm == null) swarm = FindAnyObjectByType<SwarmSystem>();
            if (rocket != null) { _rocketHomePos = rocket.localPosition; _rocketHomeRot = rocket.localRotation; }
            if (earth != null) _earthHome = earth.localPosition;
            if (moon != null) _moonScale = moon.transform.localScale;
            SetActive(flame, false);
            SetActive(moon, false);
        }

        void Start()
        {
            // Optional passthrough dimmer (VirtualBeeSwarm.Meta/OvrPassthroughDimmer or your own).
            var all = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length && _dimmer == null; i++) _dimmer = all[i] as IPassthroughDimmer;
        }

        protected override void OnEnter(GameBeat beat)
        {
            _launched = _landed = false;
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            if (swarm != null && swarm.Mode == SwarmMode.PourOut) swarm.SetMode(SwarmMode.Calm);
            if (beeswax != null) beeswax.GoPresent();
            yield return SayAndWait(GameLines.T0Going, 2f, 6f);

            // board
            if (beeswax != null && rocketSeat != null)
            {
                beeswax.FlyTo(rocketSeat);
                float t0 = Time.time;
                while (!beeswax.Arrived && Time.time - t0 < 3f) yield return null;
                beeswax.AttachTo(rocketSeat);
            }
            if (net != null) net.Say(GameLines.T0Launch);
            for (int n = 3; n >= 1; n--)
            {
                if (rocketDisplay != null) rocketDisplay.text = n.ToString();
                yield return Wait(0.9f);
            }
            if (rocketDisplay != null) rocketDisplay.text = "LIFTOFF";

            // launch
            _launched = true;
            SetActive(flame, true);
            if (window != null) window.SwellToMoon();
            if (net != null) net.RequestScene(GameIds.Scene.Moon, sendToHive: true);
            if (moon != null)
            {
                moon.transform.localScale = Vector3.zero;
                SetActive(moon, true);
                StartCoroutine(ScaleTo(moon.transform, _moonScale, 2.5f));
            }
            if (rocket != null)
            {
                Vector3 from = rocket.position;
                float start = Time.time;
                while (Time.time - start < launchSeconds)
                {
                    float k = (Time.time - start) / launchSeconds;
                    rocket.position = from + Vector3.up * (k * k * launchHeight) + Vector3.right * (Mathf.Sin(Time.time * 25f) * 0.003f);
                    SetDim(Mathf.Lerp(0f, passthroughDim, Mathf.Clamp01(k * 1.5f)));
                    if (earth != null) earth.localPosition = _earthHome + Vector3.up * (earthRise * k * 0.5f);
                    yield return null;
                }
                rocket.gameObject.SetActive(false);
                if (beeswax != null) beeswax.SetVisible(false); // he's up there with it
            }

            // the Moon
            float moonStart = Time.time;
            yield return SayAndWait(GameLines.T0Moon, 2.5f, 6f, unlessHive: false);
            while (Time.time - moonStart < 4f)
            {
                if (earth != null) earth.localPosition = _earthHome + Vector3.up * (earthRise * (0.5f + 0.5f * (Time.time - moonStart) / 4f));
                yield return null;
            }

            // crash back
            if (rocket != null)
            {
                rocket.gameObject.SetActive(true);
                Vector3 land = crashSpot != null ? crashSpot.position
                    : rocket.parent != null ? rocket.parent.TransformPoint(_rocketHomePos) : _rocketHomePos;
                Quaternion tilt = (crashSpot != null ? crashSpot.rotation : rocket.rotation) * Quaternion.Euler(0f, 0f, 28f);
                rocket.rotation = tilt * Quaternion.Euler(0f, 0f, 150f);
                Vector3 from = land + Vector3.up * 1.2f;
                float start = Time.time;
                while (Time.time - start < 0.6f)
                {
                    float k = (Time.time - start) / 0.6f;
                    rocket.position = Vector3.Lerp(from, land, k * k);
                    rocket.rotation = Quaternion.Slerp(rocket.rotation, tilt, k);
                    yield return null;
                }
                rocket.position = land;
                rocket.rotation = tilt;
            }
            _landed = true;
            SetActive(flame, false);
            if (dustPuff != null) { dustPuff.transform.position = rocket != null ? rocket.position : dustPuff.transform.position; dustPuff.Emit(30); }
            if (beeswax != null)
            {
                beeswax.Detach();
                beeswax.SetVisible(true);
                beeswax.SetDusty(true);
                beeswax.GoPresent();
            }
            SetActive(moon, false);
            if (window != null) window.NormalSize();
            if (net != null) net.RequestScene(GameIds.Scene.FullBloom, sendToHive: true);
            float undimStart = Time.time;
            float dim0 = _dim;
            while (Time.time - undimStart < 1f)
            {
                SetDim(Mathf.Lerp(dim0, 0f, Time.time - undimStart));
                yield return null;
            }
            SetDim(0f);
            if (rocketDisplay != null) rocketDisplay.text = "HONEYMOON-1";

            yield return SayAndWait(GameLines.T0Back, 2.5f, 7f);
            Complete(1f);
        }

        void SetDim(float amount)
        {
            _dim = amount;
            if (_dimmer != null) _dimmer.SetDim(amount);
        }

        protected override void OnExit(GameBeat beat)
        {
            SetDim(0f);
            SetActive(flame, false);
            SetActive(moon, false);
            if (rocket != null)
            {
                if (!rocket.gameObject.activeSelf) rocket.gameObject.SetActive(true);
                if (_launched && !_landed) PutRocketDown(); // skipped mid-flight: don't leave it hanging in the air
            }
            if (window != null && _launched && !_landed) window.NormalSize();
            if (beeswax != null) { beeswax.Detach(); beeswax.SetVisible(true); }
            if (rocketDisplay != null) rocketDisplay.text = "HONEYMOON-1";
        }

        void PutRocketDown()
        {
            if (crashSpot != null) rocket.SetPositionAndRotation(crashSpot.position, crashSpot.rotation * Quaternion.Euler(0f, 0f, 28f));
            else { rocket.localPosition = _rocketHomePos; rocket.localRotation = _rocketHomeRot; }
            _landed = true;
        }

        protected override void OnResetState()
        {
            SetDim(0f);
            SetActive(flame, false);
            SetActive(moon, false);
            if (rocket != null)
            {
                rocket.gameObject.SetActive(true);
                rocket.localPosition = _rocketHomePos;
                rocket.localRotation = _rocketHomeRot;
            }
            if (earth != null) earth.localPosition = _earthHome;
            if (rocketDisplay != null) rocketDisplay.text = "HONEYMOON-1";
        }
    }
}
