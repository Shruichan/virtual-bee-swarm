using UnityEngine;

namespace VirtualBeeSwarm.AR
{
    /// <summary>
    /// Aim-and-pinch placement (PLAN.md V7): in `pre`, a translucent DELIVER HERE plate follows the hand ray (right
    /// hand first, then left, then head gaze when no hand is tracked) onto real surfaces found by the environment
    /// raycast (Meta EnvironmentRaycastManager.PlaceBox, 0.6 x 0.45 m footprint, 0.3 m clearance). Green = it fits
    /// on a flat top (normal.y > 0.9), red = it doesn't. A pinch of the aiming hand puts the hive rig there, facing
    /// away from the player. The old placement (grab the base plate, two-point edge, C key) stays as an override.
    /// Desktop: hold L to aim with the mouse, release L to place.
    /// Switch: ar.placement = raycast (default) | manual.
    /// </summary>
    [DefaultExecutionOrder(40)] // after GamePointer (-10) and HivePlacement (0): sees their state for this frame
    public class InstantPlacement : MonoBehaviour
    {
        [SerializeField] Vector3 boxSize = new Vector3(0.6f, 0.45f, 0.3f);
        [SerializeField] float maxDistance = 4f;
        [SerializeField] KeyCode desktopAimKey = KeyCode.L;
        [SerializeField] Color fitColor = new Color(0.3f, 1f, 0.5f, 0.45f);
        [SerializeField] Color noFitColor = new Color(1f, 0.3f, 0.25f, 0.4f);

        ArRig _rig;
        GameObject _ghost;
        Renderer _ghostRenderer;
        TextMesh _ghostText;
        GamePointer[] _pointers = new GamePointer[0];
        bool _lastFit;
        public bool Aiming { get; private set; }
        public ArHitStatus LastStatus { get; private set; } = ArHitStatus.NotReady;
        public int Placements { get; private set; }

        public void Init(ArRig rig)
        {
            _rig = rig;
            BuildGhost();
            _pointers = FindObjectsByType<GamePointer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        }

        void BuildGhost()
        {
            _ghost = new GameObject("DeliverHereGhost");
            _ghost.transform.SetParent(transform, false);
            var plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plate.name = "Plate";
            Destroy(plate.GetComponent<Collider>());
            plate.transform.SetParent(_ghost.transform, false);
            plate.transform.localPosition = new Vector3(0f, 0.004f, 0f);
            plate.transform.localScale = new Vector3(0.44f, 0.008f, 0.38f);
            _ghostRenderer = plate.GetComponent<Renderer>();
            var mat = GameMaterials.Create("VBS_Ghost (runtime)", fitColor, GameMaterials.Blend.Alpha);
            ArMaterials.MarkNeverOccluded(mat);
            _ghostRenderer.sharedMaterial = mat;
            _ghostRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var label = new GameObject("Label");
            label.transform.SetParent(_ghost.transform, false);
            label.transform.localPosition = new Vector3(0f, 0.01f, 0f);
            label.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            _ghostText = label.AddComponent<TextMesh>();
            _ghostText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_ghostText.font != null) label.GetComponent<MeshRenderer>().sharedMaterial = _ghostText.font.material;
            _ghostText.fontSize = 64;
            _ghostText.characterSize = 0.004f;
            _ghostText.anchor = TextAnchor.MiddleCenter;
            _ghostText.alignment = TextAlignment.Center;
            _ghostText.text = "DELIVER HERE\npinch";
            _ghost.SetActive(false);
        }

        bool Active =>
            _rig != null && _rig.Placement != null && _rig.Raycaster != null && _rig.Raycaster.IsSupported &&
            ArTuning.PlacementMode == ArTuning.Placement.Raycast && _rig.Placement.Allowed && !_rig.Placement.Holding;

        void Update()
        {
            if (!Active)
            {
                Aiming = false;
                if (_ghost != null && _ghost.activeSelf) _ghost.SetActive(false);
                return;
            }

            bool desktop = _rig.DesktopMode;
            bool haveRay = desktop ? DesktopRay(out Ray ray, out bool place) : HeadsetRay(out ray, out place);
            Aiming = haveRay;
            if (!haveRay)
            {
                if (_ghost.activeSelf) _ghost.SetActive(false);
                return;
            }

            Vector3 fwd = Vector3.ProjectOnPlane(ray.direction, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(_rig.HeadForward(), Vector3.up);
            fwd.Normalize();
            bool meadow = Bloom.MeadowLayout.Active; // meadow: the big rig needs open FLOOR (never a table, bed or sofa)
            bool fits = _rig.Raycaster.PlaceBox(ray, meadow ? boxSize * Bloom.MeadowLayout.Scale : boxSize, fwd, out var hit);
            LastStatus = hit.status;
            bool show = hit.status == ArHitStatus.Hit && (hit.point - ray.origin).sqrMagnitude <= maxDistance * maxDistance;
            fits = fits && show && hit.normal.y > 0.9f;
            if (meadow && Mathf.Abs(hit.point.y - Bloom.MeadowLayout.FloorY) > 0.1f) fits = false;
            if (_ghost.activeSelf != show) _ghost.SetActive(show);
            if (!show) return;

            _ghost.transform.SetPositionAndRotation(hit.point, Quaternion.LookRotation(fwd, Vector3.up));
            if (fits != _lastFit || _ghostText.text.Length == 0)
            {
                _lastFit = fits;
                GameMaterials.Tint(_ghostRenderer, fits ? fitColor : noFitColor);
                _ghostText.text = fits ? (desktop ? "DELIVER HERE\nrelease L" : "DELIVER HERE\npinch") : "doesn't fit here";
            }
            if (place && fits)
            {
                Placements++;
                if (!desktop) _rig.ConsumeCalibrationPinch(); // this pinch isn't also a two-point calibration point
                _rig.PlaceFromRaycast(new Pose(hit.point, Quaternion.LookRotation(fwd, Vector3.up)));
                _ghost.SetActive(false);
            }
        }

        bool DesktopRay(out Ray ray, out bool place)
        {
            ray = default;
            place = false;
            bool held = GameInput.KeyHeld(desktopAimKey);
            bool up = GameInput.KeyUp(desktopAimKey);
            if (!held && !up) return false;
            var cam = Camera.main;
            if (cam == null || !GameInput.TryMousePosition(out var mp)) return false;
            ray = cam.ScreenPointToRay(mp);
            place = up;
            return true;
        }

        bool HeadsetRay(out Ray ray, out bool place)
        {
            place = false;
            if (PointerBusy()) { ray = default; return false; }
            for (int h = 1; h >= 0; h--) // right first
            {
                var hand = (GameHand)h;
                if (!GameHands.IsTracked(hand) || !GameHands.TryGetPointerRay(hand, out ray)) continue;
                place = GameHands.PinchDown(hand);
                return true;
            }
            var head = Camera.main;
            if (head == null) { ray = default; return false; }
            ray = new Ray(head.transform.position, head.transform.forward);
            place = GameHands.PinchDown(GameHand.Left) || GameHands.PinchDown(GameHand.Right);
            return true;
        }

        bool PointerBusy()
        {
            for (int i = 0; i < _pointers.Length; i++)
            {
                var p = _pointers[i];
                if (p == null || p.PointerSource == GamePointer.Source.Mouse) continue;
                if (p.IsHoveringTarget || p.IsGrabbing) return true;
            }
            return false;
        }
    }
}
