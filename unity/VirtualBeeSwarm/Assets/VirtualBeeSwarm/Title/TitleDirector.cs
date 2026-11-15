using UnityEngine;
using VirtualBeeSwarm.AR;

namespace VirtualBeeSwarm.Title
{
    /// <summary>
    /// The title screen is a little scene in the player's room (tuning title.on, default on), shown during pre:
    ///   the logo  floats about a metre ahead at chest-to-eye height (over the table when that is in front of the
    ///             player), life-size, in layers a few cm apart, Daniel's sticker bees flying loops around it, swarm bees
    ///             drifting by; a bed of flowers and grass sways on the table or floor under it, with bees visiting.
    ///   signed  the logo's bees scatter and the logo flies toward the opening window.
    /// The intro runs the clipboard delivery independently of the title.
    /// </summary>
    [DefaultExecutionOrder(-25)]
    public class TitleDirector : MonoBehaviour
    {
        /// <summary>Install into every scene that has a GameDirector (off: only scenes that add it themselves).</summary>
        public static bool AutoInstall = true;
        public static TitleDirector Instance { get; private set; }

        [SerializeField] GameDirector director;
        [SerializeField] BeeDelivery delivery;
        [SerializeField] TitleArt art;

        [Header("Logo")]
        [Tooltip("Width of the logo's canvas (m). The honeycomb is 0.78 of it; with the bees' loops about 0.7 m.")]
        [SerializeField] float canvasWidth = 0.8f;
        [Tooltip("Ahead of the player when there is no table in front of them (m).")]
        [SerializeField] float distance = 1.2f;
        [Tooltip("Over the table: this far from the player, min and max (m).")]
        [SerializeField] Vector2 tableDistance = new Vector2(1.0f, 1.4f);
        [SerializeField] float belowEye = 0.25f;
        [Tooltip("Lowest logo centre above the table or floor: with the clipboard, and with the START button's text.")]
        [SerializeField] Vector2 minAboveSurface = new Vector2(0.52f, 0.72f);
        [Tooltip("Top leans away from the player (degrees).")]
        [SerializeField] float tilt = 8f;
        [SerializeField] float bob = 0.012f;
        [SerializeField] float popSeconds = 0.6f;
        [SerializeField] float flyOffSeconds = 0.6f;

        [Tooltip("Nothing of the title comes nearer to the eyes than this (m).")]
        [SerializeField] float keepClear = 0.5f;
        [SerializeField] bool log = true;

        enum Phase { Hidden, Showing, Leaving }

        Phase _phase = Phase.Hidden;
        TitleLogo _logo;
        TitleMeadow _meadow;
        FutureWindow _window;
        bool _subscribed, _built;

        // placement
        Vector3 _logoPos, _logoGoal, _right, _away, _surface, _anchorAt, _anchorSeen, _headAt;
        Quaternion _logoRot = Quaternion.identity;
        bool _placed, _onTable;
        float _shownAt, _leftAt = -1f, _nextCheck, _hiddenCheckAt;
        Vector3 _flyFrom, _flyTo;

        // the pre text ("Put the hive on the real table ...") moves under the logo while the title shows
        Transform _text, _textBack;
        Renderer _textRenderer;
        Vector3 _textPos;
        Quaternion _textRot;
        Material _textBackMat;
        Texture2D _textBackTex;

        bool Delivering => delivery != null && BeeDelivery.Mode != DeliveryMode.Off;
        Transform Head => delivery != null ? delivery.Head : Camera.main != null ? Camera.main.transform : null;

        // ------------------------------------------------------------------ install
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (!AutoInstall) return;
            var gd = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (gd == null || FindAnyObjectByType<TitleDirector>() != null) return;
            var go = new GameObject("Title");
            go.transform.SetParent(gd.transform.parent, false);
            go.AddComponent<TitleDirector>();
            Debug.Log("[VBS][Title] installed (auto), title.on=" + (TitleFlags.On ? "on" : "off") + " (" + TitleFlags.Source + ")");
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[VBS][Title] a second TitleDirector was disabled.");
                enabled = false;
                return;
            }
            Instance = this;
            if (director == null) director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (delivery == null) delivery = BeeDelivery.Instance != null ? BeeDelivery.Instance : FindAnyObjectByType<BeeDelivery>();
            _window = FindAnyObjectByType<FutureWindow>(FindObjectsInactive.Include);
        }

        void OnEnable()
        {
            if (Instance != this) return;
            if (director != null) director.BeatChanged += HandleBeat;
            _subscribed = true;
        }

        void Start()
        {
            // installed after the director's first BeatChanged: catch up
            if (_phase == Phase.Hidden && director != null && director.Beat == GameBeat.Pre) Show("start");
        }

        void OnDisable()
        {
            if (!_subscribed) return;
            _subscribed = false;
            if (director != null) director.BeatChanged -= HandleBeat;
            HideNow("disabled");
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            _logo?.Destroy();
            _meadow?.Destroy();
            if (_textBackMat != null) Destroy(_textBackMat);
            if (_textBackTex != null) Destroy(_textBackTex);
        }

        bool Build()
        {
            if (_built) return _logo != null;
            _built = true;
            if (art == null) art = TitleArt.Load();
            if (art == null || (art.plate == null && art.flat == null))
            {
                Debug.LogWarning("[VBS][Title] no Resources/" + TitleArt.ResourceName + " (Art/Logo): no title scene, pre as before");
                return false;
            }
            _logo = new TitleLogo(transform, art, canvasWidth);
            _meadow = new TitleMeadow(transform, art);
            foreach (var tm in FindObjectsByType<TextMesh>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (tm.name == "Instructions" && tm.transform.parent != null && tm.transform.parent.name == "PreUI")
                {
                    _text = tm.transform;
                    _textPos = _text.localPosition;
                    _textRot = _text.localRotation;
                    _textRenderer = tm.GetComponent<Renderer>();
                    break;
                }
            if (_text != null)
            {
                // a dark plate behind the white text, so it reads on any wall
                var go = new GameObject("TitleTextPlate");
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
                _textBackMat = GameMaterials.Create("VBS_TitleTextPlate", new Color(0.165f, 0.09f, 0.04f, 0.78f), GameMaterials.Blend.Alpha, doubleSided: true);
                _textBackMat.mainTexture = _textBackTex = PlateTexture(160, 56, 16);
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _textBackMat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.sortingOrder = -5; // under the text, over the logo
                _textBack = go.transform;
                go.SetActive(false);
            }
            return true;
        }

        // ------------------------------------------------------------------ beats
        void HandleBeat(GameBeat prev, GameBeat beat)
        {
            if (beat == GameBeat.Pre) Show(prev == GameBeat.Pre ? "pre" : "back to pre");
            else if (_phase == Phase.Showing)
            {
                if (beat == GameBeat.Hook) Leave();
                else HideNow("jump to " + GameIds.BeatName(beat));
            }
            else if (_phase == Phase.Leaving && beat != GameBeat.Hook) HideNow("jump to " + GameIds.BeatName(beat));
        }

        void Show(string why)
        {
            if (!TitleFlags.On || !Build())
            {
                _phase = Phase.Hidden;
                return;
            }
            _phase = Phase.Showing;
            _shownAt = Time.time;
            _leftAt = -1f;
            _placed = false;
            _logo.SetScatter(0f);
            _logo.Show(false);
            _meadow.Hide();
            Log("up (" + why + "), delivery " + BeeDelivery.ModeName(BeeDelivery.Mode));
        }

        /// <summary>Signed (or started another way): the bees scatter, the logo leaves through the window.</summary>
        void Leave()
        {
            _phase = Phase.Leaving;
            _leftAt = Time.time;
            _flyFrom = _logoPos;
            _flyTo = WindowPoint();
            RestoreText();
            Log("signed: the logo flies toward the window");
        }

        /// <summary>Hide the title when the intro is skipped or the title is switched off.</summary>
        void HideNow(string why)
        {
            if (_phase == Phase.Hidden) return;
            _phase = Phase.Hidden;
            if (_logo != null) _logo.Show(false);
            if (_meadow != null) _meadow.Hide();
            RestoreText();
            Log("down (" + why + ")");
        }

        Vector3 WindowPoint()
        {
            if (delivery != null) return delivery.PanePosition();
            if (_window != null) return _window.PaneTransform.position;
            return _logoPos + _away * 1.5f + Vector3.up * 0.6f;
        }

        // ------------------------------------------------------------------ placement
        void Place(Transform head, bool snap)
        {
            Vector3 eye = head.position;
            Vector3 fwd = Flat(head.forward, Vector3.forward);
            var rig = ArRig.Instance;
            var room = rig != null ? rig.Room : null;
            Transform anchor = delivery != null && delivery.TableAnchor != null ? delivery.TableAnchor : rig != null ? rig.TableAnchor : null;

            Vector3 dir = fwd;
            float dist = distance;
            bool overTable = false;
            float tableY = 0f;
            if (anchor != null)
            {
                bool hasTable = room != null && room.HasTable;
                Vector3 centre = hasTable ? room.Table.center : anchor.position;
                tableY = hasTable ? room.Table.Y : anchor.position.y;
                Vector3 to = centre - eye;
                to.y = 0f;
                float d = to.magnitude;
                if (d > 0.3f && d < 1.9f && Vector3.Angle(to, fwd) < 65f && tableY < eye.y - 0.3f)
                {
                    dir = to / d;
                    dist = Mathf.Clamp(d + 0.15f, tableDistance.x, tableDistance.y);
                    overTable = true;
                }
                _anchorAt = anchor.position;
            }
            Vector3 p = eye + dir * dist;
            if (overTable && room != null && room.HasTable && !room.InsideTable(p, 0.05f))
            {
                // keep it over the top: pull it back toward the table's middle
                Vector3 c = room.Table.center;
                for (int i = 0; i < 8 && !room.InsideTable(p, 0.05f); i++) p = Vector3.Lerp(p, new Vector3(c.x, p.y, c.z), 0.25f);
                Vector3 pd = p - eye;
                pd.y = 0f;
                if (pd.magnitude < tableDistance.x - 0.15f) overTable = false; // the table is too near: float it ahead instead
                if (!overTable) p = eye + fwd * distance;
                else dir = pd.normalized;
            }
            _onTable = overTable;
            float floorY = room != null ? room.FloorY : 0f;
            float surfaceY = overTable ? tableY : Mathf.Min(floorY, eye.y - 0.9f);
            float min = surfaceY + (Delivering ? minAboveSurface.x : minAboveSurface.y);
            p.y = Mathf.Max(eye.y - belowEye, min);
            // AR: a real wall or shelf nearer than that would hide it (depth occlusion): come in front of it
            float room1 = ClearAhead(eye, p);
            if (room1 < Vector3.Distance(eye, p) + 0.2f)
            {
                Vector3 to = (p - eye).normalized;
                p = eye + to * Mathf.Max(keepClear + 0.25f, room1 - 0.2f);
                p.y = Mathf.Max(p.y, min);
            }
            _right = Vector3.Cross(Vector3.up, dir).normalized;
            _away = dir;
            _surface = new Vector3(p.x, surfaceY, p.z);
            _logoGoal = p;
            _logoRot = Quaternion.LookRotation(dir, Vector3.up) * Quaternion.Euler(tilt, 0f, 0f);
            _headAt = eye;
            if (snap || !_placed) _logoPos = p;
            _placed = true;
            _meadow.LayOut(_surface, _right, _away, overTable, room, !Delivering, eye, Time.time);
            if (snap) _meadow.ScatterBees(p);
            Log("logo " + Vector3.Distance(eye, p).ToString("0.00") + " m from the eyes, " + (p.y - surfaceY).ToString("0.00") + " m over the " +
                (overTable ? "table" : "floor") + ", " + _meadow.FlowerCount + " flowers + " + (_meadow.PlantCount - _meadow.FlowerCount) + " grass");
        }

        /// <summary>How far the real room is clear from the eye toward `to` (the environment raycast; +inf when unknown).</summary>
        static float ClearAhead(Vector3 eye, Vector3 to)
        {
            var rig = ArRig.Instance;
            var rc = rig != null ? rig.Raycaster : null;
            if (rc == null || !rc.IsSupported || !rc.Enabled) return float.PositiveInfinity;
            Vector3 d = to - eye;
            float len = d.magnitude;
            if (len < 1e-3f) return float.PositiveInfinity;
            return rc.Raycast(new Ray(eye, d / len), len + 0.5f, out ArHit hit) && hit.status == ArHitStatus.Hit
                ? Vector3.Distance(eye, hit.point) : float.PositiveInfinity;
        }

        static Vector3 Flat(Vector3 v, Vector3 fallback)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-4f ? v.normalized : fallback;
        }

        void CheckPlacement(Transform head)
        {
            if (Time.time < _nextCheck) return;
            _nextCheck = Time.time + 0.25f;
            var rig = ArRig.Instance;
            Transform anchor = delivery != null && delivery.TableAnchor != null ? delivery.TableAnchor : rig != null ? rig.TableAnchor : null;
            Vector3 toLogo = _logoGoal - head.position;
            toLogo.y = 0f;
            // the table was placed (wait until the anchor has stopped moving)
            bool still = anchor == null || (anchor.position - _anchorSeen).sqrMagnitude < 1e-6f;
            if (anchor != null) _anchorSeen = anchor.position;
            bool moved = still && anchor != null && (anchor.position - _anchorAt).sqrMagnitude > 0.03f * 0.03f;
            bool near = toLogo.magnitude < keepClear + 0.1f;                                                     // walked into it
            bool gone = (head.position - _headAt).magnitude > 1.2f && Vector3.Angle(toLogo, head.forward) > 70f; // walked off
            bool height = Mathf.Abs(head.position.y - _headAt.y) > 0.25f;                                        // sat down, stood up, tracking came in
            bool hidden = Time.time >= _hiddenCheckAt &&
                          ClearAhead(head.position, _logoGoal) < Vector3.Distance(head.position, _logoGoal) - 0.05f; // behind something real
            if (hidden) _hiddenCheckAt = Time.time + 3f;
            if (moved || near || gone || height || hidden) Place(head, false);
        }

        // ------------------------------------------------------------------ frame
        void Update()
        {
            if (_phase == Phase.Hidden) return;
            if (!TitleFlags.On) { HideNow("title.on=off"); return; }
            Transform head = Head;
            if (head == null) return;
            float t = Time.time;
            if (!_placed)
            {
                if (t - _shownAt < 0.15f) return; // the head pose and a reset's snaps settle first
                Place(head, true);
                _logo.Show(true);
                PlaceText();
            }

            float shown = Mathf.Clamp01((t - _shownAt - 0.15f) / popSeconds), scale = 1f, leave = 0f;
            Vector3 pos;
            Quaternion rot = _logoRot * Quaternion.Euler(0f, Mathf.Sin(t * 0.5f) * 3f, Mathf.Sin(t * 0.7f + 1f) * 1.5f);
            if (_phase == Phase.Showing)
            {
                CheckPlacement(head);
                _logoPos = Vector3.Lerp(_logoPos, _logoGoal, 1f - Mathf.Exp(-4f * Time.deltaTime));
                pos = _logoPos + Vector3.up * (Mathf.Sin(t * 0.9f) * bob);
                if (_text != null) PlaceText();
            }
            else
            {
                // the bees scatter at once; the logo dips, then shoots out through the window, shrinking
                float k = Mathf.Clamp01((t - _leftAt) / flyOffSeconds);
                leave = Mathf.Clamp01((t - _leftAt) / 1.1f);
                _logo.SetScatter(Mathf.Clamp01((t - _leftAt) / 0.35f));
                float e = k * k * (3f - 2f * k);
                e *= e;
                pos = Vector3.Lerp(_flyFrom, _flyTo, e) + Vector3.up * (Mathf.Sin(k * Mathf.PI) * 0.1f - Mathf.Sin(Mathf.Min(1f, k * 4f) * Mathf.PI) * 0.03f);
                scale = Mathf.Lerp(1f, 0.08f, e);
                rot = rot * Quaternion.Euler(0f, 0f, -200f * e);
                if (k >= 1f) _logo.Show(false);
                if (leave >= 1f)
                {
                    _phase = Phase.Hidden;
                    _meadow.Hide();
                    Log("gone");
                    return;
                }
            }
            if (_logo.Root.gameObject.activeSelf) _logo.Tick(pos, rot, shown, scale, t, head.position);
            _meadow.Tick(t, Time.deltaTime, pos, _right, _away, head.position, leave, keepClear + 0.08f, gameObject.layer);
        }

        void PlaceText()
        {
            if (_text == null) return;
            bool on = _text.gameObject.activeInHierarchy && _logo.Shown > 0.6f;
            if (_textBack.gameObject.activeSelf != on) _textBack.gameObject.SetActive(on);
            if (!_text.gameObject.activeInHierarchy) return;
            // centred under the drip, facing the player (builder text reads along +Z)
            Quaternion rot = Quaternion.LookRotation(_away, Vector3.up);
            Vector3 p = _logoPos - Vector3.up * (canvasWidth * 0.25f + 0.125f) - _away * 0.02f;
            _text.SetPositionAndRotation(p, rot);
            if (!on || _textRenderer == null) return;
            Vector3 size = Vector3.Scale(_textRenderer.localBounds.size, _text.lossyScale);
            _textBack.SetPositionAndRotation(p + _away * 0.006f, rot);
            _textBack.localScale = new Vector3(Mathf.Abs(size.x) + 0.05f, Mathf.Abs(size.y) + 0.035f, 1f);
        }

        void RestoreText()
        {
            if (_text == null) return;
            _text.localPosition = _textPos;
            _text.localRotation = _textRot;
            if (_textBack != null && _textBack.gameObject.activeSelf) _textBack.gameObject.SetActive(false);
        }

        /// <summary>A white rounded rectangle with a soft edge (tinted by the material).</summary>
        static Texture2D PlateTexture(int w, int h, int radius)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "VBS_TitlePlate", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float dx = Mathf.Max(0f, Mathf.Max(radius - x, x - (w - 1 - radius)));
                float dy = Mathf.Max(0f, Mathf.Max(radius - y, y - (h - 1 - radius)));
                float a = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy));
                px[y * w + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        void Log(string msg)
        {
            if (log) Debug.Log("[VBS][Title] " + msg);
        }
    }
}
