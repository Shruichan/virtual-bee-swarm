using UnityEngine;
using UnityEngine.Rendering;
using VirtualBeeSwarm.HandsOn;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// One pointer: the mouse (desktop testing) or one hand/controller ray (GameHands). Hovers IGameTarget,
    /// clicks it on mouse-down / pinch / trigger, and drags IGameGrabTarget (ray grab at a fixed distance, or a
    /// direct grab when a hand pinches right next to the target). Allocation-free per frame.
    /// </summary>
    [DefaultExecutionOrder(-10)] // before HivePlacement, so a pinch that grabs/clicks isn't also a calibration point
    public class GamePointer : MonoBehaviour
    {
        public enum Source { Mouse, LeftHand, RightHand }

        [SerializeField] Source source = Source.Mouse;
        [Tooltip("Mouse rays come from this camera (empty = Camera.main).")]
        [SerializeField] Camera mouseCamera;
        [SerializeField] float maxDistance = 4f;
        [SerializeField] LayerMask layers = Physics.DefaultRaycastLayers;
        [Tooltip("A hand pinching within this distance of a grab target grabs it directly (no ray).")]
        [SerializeField] float directGrabRadius = 0.07f;
        [Tooltip("Optional ray visual for hands/controllers (2 positions, world space).")]
        [SerializeField] LineRenderer rayLine;
        [SerializeField] float idleRayLength = 0.25f;

        public Source PointerSource => source;
        public bool IsHoveringTarget => _hover != null;
        public bool IsGrabbing => _grab != null;
        public bool HasRay { get; private set; }

        IGameTarget _hover;
        IGameGrabTarget _grab;
        Collider _lastCol;
        IGameTarget _lastTarget;
        IGameGrabTarget _lastGrabTarget;
        float _grabDistance;
        bool _directGrab;
        bool _panelGripHeld, _grabHeldByGrip;
        readonly Collider[] _overlap = new Collider[8];
        readonly RaycastHit[] _hitsBehindLever = new RaycastHit[16];
        static int _pickupDrawFrame = -1;
        Component _pickupTarget;
        MeshFilter[] _pickupMeshes;
        Material _pickupMaterial;
        MaterialPropertyBlock _pickupColors;
        float _nextPickupSearch;

        void OnDestroy()
        {
            if (_pickupMaterial != null) Destroy(_pickupMaterial);
        }

        public void Configure(Source s, LineRenderer line)
        {
            source = s;
            rayLine = line;
        }

        void OnDisable()
        {
            SetHover(null);
            if (_grab != null) { _grab.EndGrab(); _grab = null; }
            _panelGripHeld = false;
            if (rayLine != null) rayLine.enabled = false;
        }

        void Update()
        {
            HasRay = TryGetRay(out Ray ray);
            bool down = PressDown(), held = PressHeld();
            var hand = source == Source.LeftHand ? GameHand.Left : GameHand.Right;
            bool controller = source != Source.Mouse && GameHands.For(hand) is IControllerPoseSource input && input.IsController(hand);
            bool gripHeld = controller ? GameHands.IsFist(hand) : held;
            bool gripDown = controller ? gripHeld && !_panelGripHeld : down;
            _panelGripHeld = gripHeld;

            // ---- holding something
            if (_grab != null)
            {
                bool stillHeld = _grab is PanelGrip panel ? (_grabHeldByGrip ? gripHeld : held) && panel.Held && panel.Ready : held && _grab.Grabbable;
                if (!stillHeld)
                {
                    _grab.EndGrab();
                    _grab = null;
                }
                else if (_directGrab)
                {
                    if (TryGetHandPoint(out var hp)) UpdateCurrentGrab(hp);
                }
                else if (HasRay) UpdateCurrentGrab(ray.GetPoint(_grabDistance));
                DrawRay(HasRay, ray, _grabDistance);
                return;
            }

            // A locked lever can provide feedback, but must lose to an active target.
            HoneyLever lockedLever = null;
            Vector3 lockedLeverPoint = default;

            // ---- direct hand grab
            if ((down || gripDown) && source != Source.Mouse && TryGetHandPoint(out var handPoint))
            {
                int n = Physics.OverlapSphereNonAlloc(handPoint, directGrabRadius, _overlap, layers, QueryTriggerInteraction.Collide);
                for (int i = 0; i < n; i++)
                {
                    var g = _overlap[i].GetComponentInParent<IGameGrabTarget>();
                    if (g == null || !g.Grabbable) continue;
                    if (g is PanelGrip)
                    {
                        // The nearby tab must be touched deliberately, rather than winning an overlapping chip's poke.
                        if ((!gripDown && !down) || (_overlap[i].ClosestPoint(handPoint) - handPoint).sqrMagnitude > 0.028f * 0.028f) continue;
                    }
                    else if (!down) continue;
                    if (g is HoneyLever lever && !lever.Live)
                    {
                        lockedLever = lever;
                        lockedLeverPoint = handPoint;
                        continue;
                    }
                    BeginDirectGrab(g, handPoint, controller && gripDown && g is PanelGrip);
                    return;
                }
            }

            // ---- ray
            IGameTarget target = null;
            IGameGrabTarget grabTarget = null;
            float hitDistance = 0f;
            if (HasRay && Physics.Raycast(ray, out RaycastHit hit, maxDistance, layers, QueryTriggerInteraction.Collide))
            {
                // A delivered prop can become grabbable while the player is already pointing at it.
                if (hit.collider != _lastCol || down || gripDown) CacheHit(hit.collider);
                if (_lastGrabTarget is HoneyLever lever && !lever.Live && TryHitBehindLever(ray, hit.collider, out var behind))
                {
                    hit = behind;
                    if (hit.collider != _lastCol) CacheHit(hit.collider);
                }
                target = _lastTarget != null && _lastTarget.Interactable ? _lastTarget : null;
                grabTarget = _lastGrabTarget != null && _lastGrabTarget.Grabbable ? _lastGrabTarget : null;
                hitDistance = hit.distance;
            }
            else _lastCol = null;

            SetHover(target);
            DrawRay(HasRay, ray, target != null || grabTarget != null ? hitDistance : idleRayLength);

            if (!down && !gripDown) return;
            if (grabTarget is PanelGrip ? gripDown || down : grabTarget != null && down)
            {
                SetHover(null);
                _grab = grabTarget;
                _directGrab = false;
                _grabDistance = hitDistance;
                _grabHeldByGrip = controller && gripDown && grabTarget is PanelGrip;
                grabTarget.BeginGrab(ray.GetPoint(hitDistance));
                Feedback(0.20f, 0.05f);
            }
            else if (down && target != null)
            {
                target.Click();
                Feedback(0.30f, 0.055f);
            }
            else if (down && lockedLever != null) BeginDirectGrab(lockedLever, lockedLeverPoint);
        }

        void LateUpdate()
        {
            // The same objective is visible to both pointers, with only one overlay per frame.
            if (_pickupDrawFrame == Time.frameCount) return;
            _pickupDrawFrame = Time.frameCount;
            var director = GameDirector.Instance;
            if (director == null || director.Beat < GameBeat.Smoker || director.Beat > GameBeat.Heat)
            {
                _pickupTarget = null;
                _pickupMeshes = null;
                _nextPickupSearch = 0f;
                return;
            }

            if (!PickupReady(_pickupTarget) && Time.unscaledTime >= _nextPickupSearch)
            {
                _nextPickupSearch = Time.unscaledTime + 0.2f;
                Component next = null;
                foreach (var prop in FindObjectsByType<CarryProp>(FindObjectsSortMode.None))
                {
                    if (!PickupReady(prop)) continue;
                    next = prop;
                    break;
                }
                if (next == null && director.Beat == GameBeat.Feed)
                {
                    var hive = FindAnyObjectByType<HiveBody>();
                    if (PickupReady(hive)) next = hive;
                }
                if (next != _pickupTarget)
                {
                    _pickupTarget = next;
                    _pickupMeshes = next != null ? next.GetComponentsInChildren<MeshFilter>(true) : null;
                }
            }

            // Runs after input so a successful grab removes the flash that very frame.
            if (!PickupReady(_pickupTarget) || _pickupMeshes == null) return;
            if (_pickupMaterial == null)
            {
                _pickupMaterial = GameMaterials.Create("VBS Objective Flash", Color.white, GameMaterials.Blend.Additive);
                _pickupColors = new MaterialPropertyBlock();
            }
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 4.5f);
            var white = new Color(1f, 1f, 1f, 0.14f + 0.86f * pulse * pulse * pulse);
            _pickupColors.SetColor(GameMaterials.ColorId, white);
            _pickupColors.SetColor(GameMaterials.BaseColorId, white);
            foreach (var filter in _pickupMeshes)
            {
                if (filter == null || !filter.gameObject.activeInHierarchy || !filter.TryGetComponent<MeshRenderer>(out var renderer) ||
                    !renderer.enabled || renderer.forceRenderingOff || filter.sharedMesh == null) continue;
                Mesh mesh = filter.sharedMesh;
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                    Graphics.DrawMesh(mesh, renderer.localToWorldMatrix, _pickupMaterial,
                        renderer.gameObject.layer, null, submesh, _pickupColors, ShadowCastingMode.Off, false);
            }
        }

        static bool PickupReady(Component target)
        {
            if (target == null || !target.gameObject.activeInHierarchy) return false;
            return target is CarryProp prop && prop.Grabbable && !prop.Held ||
                   target is HiveBody hive && hive.Grabbable && !hive.Held;
        }

        void UpdateCurrentGrab(Vector3 point)
        {
            var hive = source != Source.Mouse ? _grab as HiveBody : null;
            bool waitingForLift = hive != null && !hive.LiftReported;
            int priorShakes = hive != null ? hive.ShakeCount : 0;
            _grab.UpdateGrab(point);
            if (hive == null) return;
            if (hive.ShakeCount != priorShakes) Feedback(0.34f, 0.10f);
            else if (waitingForLift && hive.LiftReported) Feedback(0.25f, 0.09f);
        }

        void Feedback(float strength, float seconds)
        {
            if (source != Source.Mouse)
                GameHaptics.Pulse(source == Source.LeftHand ? GameHand.Left : GameHand.Right, strength, seconds);
        }

        void BeginDirectGrab(IGameGrabTarget target, Vector3 point, bool byGrip = false)
        {
            SetHover(null);
            _grab = target;
            _directGrab = true;
            _grabHeldByGrip = byGrip;
            target.BeginGrab(point);
            Feedback(0.20f, 0.05f);
        }

        void CacheHit(Collider hit)
        {
            _lastCol = hit;
            _lastTarget = hit.GetComponentInParent<IGameTarget>();
            _lastGrabTarget = hit.GetComponentInParent<IGameGrabTarget>();
        }

        bool TryHitBehindLever(Ray ray, Collider leverCollider, out RaycastHit hit)
        {
            hit = default;
            float nearest = maxDistance;
            int count = Physics.RaycastNonAlloc(ray, _hitsBehindLever, maxDistance, layers, QueryTriggerInteraction.Collide);
            for (int i = 0; i < count; i++)
            {
                RaycastHit other = _hitsBehindLever[i];
                if (other.collider == leverCollider || other.distance >= nearest) continue;
                nearest = other.distance;
                hit = other;
            }
            return hit.collider != null &&
                   (hit.collider.GetComponentInParent<IGameTarget>()?.Interactable == true ||
                    hit.collider.GetComponentInParent<IGameGrabTarget>()?.Grabbable == true);
        }

        void SetHover(IGameTarget t)
        {
            if (ReferenceEquals(t, _hover)) return;
            if (_hover != null && !(_hover is Object o && o == null)) _hover.SetHover(false);
            _hover = t;
            if (_hover != null) _hover.SetHover(true);
        }

        bool TryGetRay(out Ray ray)
        {
            switch (source)
            {
                case Source.Mouse:
                    var cam = mouseCamera != null ? mouseCamera : Camera.main;
                    if (cam != null && GameInput.TryMousePosition(out var mp))
                    {
                        ray = cam.ScreenPointToRay(mp);
                        return true;
                    }
                    ray = default;
                    return false;
                case Source.LeftHand: return GameHands.TryGetPointerRay(GameHand.Left, out ray);
                default: return GameHands.TryGetPointerRay(GameHand.Right, out ray);
            }
        }

        bool TryGetHandPoint(out Vector3 p)
        {
            if (source == Source.Mouse) { p = default; return false; }
            return GameHands.TryGetIndexTip(source == Source.LeftHand ? GameHand.Left : GameHand.Right, out p);
        }

        bool PressDown()
        {
            switch (source)
            {
                case Source.Mouse: return GameInput.MouseDown(0);
                case Source.LeftHand: return GameHands.PinchDown(GameHand.Left);
                default: return GameHands.PinchDown(GameHand.Right);
            }
        }

        bool PressHeld()
        {
            switch (source)
            {
                case Source.Mouse: return GameInput.MouseHeld(0);
                case Source.LeftHand: return GameHands.PinchHeld(GameHand.Left);
                default: return GameHands.PinchHeld(GameHand.Right);
            }
        }

        void DrawRay(bool show, Ray ray, float length)
        {
            if (rayLine == null) return;
            show &= source != Source.Mouse;
            if (rayLine.enabled != show) rayLine.enabled = show;
            if (!show) return;
            rayLine.SetPosition(0, ray.origin);
            rayLine.SetPosition(1, ray.GetPoint(length));
        }
    }
}
