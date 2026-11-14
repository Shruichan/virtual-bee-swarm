using UnityEngine;

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
        readonly Collider[] _overlap = new Collider[8];

        public void Configure(Source s, LineRenderer line)
        {
            source = s;
            rayLine = line;
        }

        void OnDisable()
        {
            SetHover(null);
            if (_grab != null) { _grab.EndGrab(); _grab = null; }
            if (rayLine != null) rayLine.enabled = false;
        }

        void Update()
        {
            HasRay = TryGetRay(out Ray ray);
            bool down = PressDown(), held = PressHeld();

            // ---- holding something
            if (_grab != null)
            {
                if (!held || !_grab.Grabbable)
                {
                    _grab.EndGrab();
                    _grab = null;
                }
                else if (_directGrab && TryGetHandPoint(out var hp)) _grab.UpdateGrab(hp);
                else if (HasRay) _grab.UpdateGrab(ray.GetPoint(_grabDistance));
                DrawRay(HasRay, ray, _grabDistance);
                return;
            }

            // ---- direct hand grab
            if (down && source != Source.Mouse && TryGetHandPoint(out var handPoint))
            {
                int n = Physics.OverlapSphereNonAlloc(handPoint, directGrabRadius, _overlap, layers, QueryTriggerInteraction.Collide);
                for (int i = 0; i < n; i++)
                {
                    var g = _overlap[i].GetComponentInParent<IGameGrabTarget>();
                    if (g != null && g.Grabbable)
                    {
                        SetHover(null);
                        _grab = g;
                        _directGrab = true;
                        g.BeginGrab(handPoint);
                        return;
                    }
                }
            }

            // ---- ray
            IGameTarget target = null;
            IGameGrabTarget grabTarget = null;
            float hitDistance = 0f;
            if (HasRay && Physics.Raycast(ray, out RaycastHit hit, maxDistance, layers, QueryTriggerInteraction.Collide))
            {
                if (hit.collider != _lastCol)
                {
                    _lastCol = hit.collider;
                    _lastTarget = hit.collider.GetComponentInParent<IGameTarget>();
                    _lastGrabTarget = hit.collider.GetComponentInParent<IGameGrabTarget>();
                }
                target = _lastTarget != null && _lastTarget.Interactable ? _lastTarget : null;
                grabTarget = _lastGrabTarget != null && _lastGrabTarget.Grabbable ? _lastGrabTarget : null;
                hitDistance = hit.distance;
            }
            else _lastCol = null;

            SetHover(target);
            DrawRay(HasRay, ray, target != null || grabTarget != null ? hitDistance : idleRayLength);

            if (!down) return;
            if (grabTarget != null)
            {
                SetHover(null);
                _grab = grabTarget;
                _directGrab = false;
                _grabDistance = hitDistance;
                grabTarget.BeginGrab(ray.GetPoint(hitDistance));
            }
            else if (target != null) target.Click();
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
