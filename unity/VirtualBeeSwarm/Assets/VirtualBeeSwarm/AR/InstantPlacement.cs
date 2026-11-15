using UnityEngine;

namespace VirtualBeeSwarm.AR
{
    /// <summary>
    /// Aim-and-pinch placement (PLAN.md V7): in `pre`, a translucent DELIVER HERE plate uses the pinching hand's ray,
    /// then an idle hand, then head gaze. Meta EnvironmentRaycastManager.PlaceBox checks real surfaces using the
    /// meadow rig's scaled footprint and clearance. Green = it fits
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
        // steadiness: depth-sensor hits are noisy frame to frame, so the plate holds through short misses, eases to the
        // new spot and only flips green/red once a result has held (it used to blink on/off and red/green every frame)
        const float HoldMiss = 0.35f, HoldFit = 0.2f;
        float _lastShowAt = -10f, _fitSince;
        bool _rawFit;
        Bloom.BloomScene _scene;
        float _nextScanRead;
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
            _ghostText.text = ""; // Paint the first visible preview from its fit result.
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
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            bool meadow = Bloom.MeadowLayout.Active; // meadow: the big rig needs open FLOOR (never a table, bed or sofa)
            float now = Time.time;
            if (meadow && !desktop && now >= _nextScanRead)
            {
                _nextScanRead = now + HoldMiss;
                _scene = Bloom.MeadowField.Instance != null ? Bloom.MeadowField.Instance.CurrentScene() : null;
            }
            Vector3 footprint = meadow ? boxSize * Bloom.MeadowLayout.Scale : boxSize;
            bool fits = _rig.Raycaster.PlaceBox(ray, footprint, fwd, out var hit);
            LastStatus = hit.status;
            // A rejected box can still have a ray hit. Keep the red footprint visible at the actual surface.
            if (hit.status != ArHitStatus.Hit && _rig.Raycaster.Raycast(ray, maxDistance, out var rejected))
                hit = rejected;
            bool show = hit.status == ArHitStatus.Hit && (hit.point - ray.origin).sqrMagnitude <= maxDistance * maxDistance;
            fits = fits && show && hit.normal.y > 0.9f;
            bool aboveFloor = meadow && Mathf.Abs(hit.point.y - (_scene?.FloorY ?? Bloom.MeadowLayout.FloorY)) > 0.1f;
            if (aboveFloor || (fits && meadow && _scene != null && !OpenFloor(hit.point, fwd, footprint))) fits = false;
            if (!show)
            {
                if (_ghost.activeSelf && now - _lastShowAt > HoldMiss) _ghost.SetActive(false);
                return;
            }
            bool wasShown = _ghost.activeSelf;
            _lastShowAt = now;
            if (!wasShown) _ghost.SetActive(true);
            _ghostRenderer.transform.localScale = new Vector3(footprint.x, 0.008f, footprint.y);

            var rot = Quaternion.LookRotation(fwd, Vector3.up);
            if (!wasShown || (hit.point - _ghost.transform.position).sqrMagnitude > 0.5f * 0.5f) _ghost.transform.SetPositionAndRotation(hit.point, rot);
            else
            {
                float k = 1f - Mathf.Exp(-Time.deltaTime * 14f);
                _ghost.transform.SetPositionAndRotation(Vector3.Lerp(_ghost.transform.position, hit.point, k), Quaternion.Slerp(_ghost.transform.rotation, rot, k));
            }
            if (fits != _rawFit) { _rawFit = fits; _fitSince = now; }
            bool shownFit = !wasShown || now - _fitSince >= HoldFit ? fits : _lastFit;
            if (shownFit != _lastFit || _ghostText.text.Length == 0)
            {
                _lastFit = shownFit;
                GameMaterials.Tint(_ghostRenderer, shownFit ? fitColor : noFitColor);
            }
            string label = shownFit ? (desktop ? "DELIVER HERE\nrelease L" : "DELIVER HERE\npinch") :
                aboveFloor ? "AIM AT OPEN FLOOR" : "MORE SPACE NEEDED";
            if (_ghostText.text != label) _ghostText.text = label;
            if (place && fits)
            {
                Placements++;
                if (!desktop) _rig.ConsumeCalibrationPinch(); // this pinch isn't also a two-point calibration point
                _rig.PlaceFromRaycast(new Pose(hit.point, Quaternion.LookRotation(fwd, Vector3.up)));
                _ghost.SetActive(false);
            }
        }

        bool OpenFloor(Vector3 point, Vector3 forward, Vector3 footprint)
        {
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            // Use the existing collision query as the first gate, then veto edges and furniture from the room scan.
            for (int x = -2; x <= 2; x++)
                for (int z = -2; z <= 2; z++)
                {
                    Vector3 sample = point + right * (x * footprint.x * 0.25f) + forward * (z * footprint.y * 0.25f);
                    if (!_scene.InFloor(sample) || _scene.InVolume(sample, 0.04f)) return false;
                }
            return true;
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
            ray = default;
            place = false;
            bool haveIdleRay = false, freePinch = false, anyBusy = false;
            for (int h = 1; h >= 0; h--) // right first when neither available hand pinches
            {
                var hand = (GameHand)h;
                if (PointerBusy(hand))
                {
                    anyBusy = true;
                    continue;
                }

                bool pressed = GameHands.PinchDown(hand);
                freePinch |= pressed;
                if (!GameHands.IsTracked(hand) || !GameHands.TryGetPointerRay(hand, out var handRay)) continue;
                if (pressed)
                {
                    ray = handRay;
                    place = true;
                    return true;
                }
                if (haveIdleRay) continue;
                ray = handRay;
                haveIdleRay = true;
            }
            if (haveIdleRay) return true;
            if (anyBusy && !freePinch) return false;

            var head = Camera.main;
            if (head == null) return false;
            ray = new Ray(head.transform.position, head.transform.forward);
            place = freePinch;
            return true;
        }

        bool PointerBusy(GameHand hand)
        {
            var source = hand == GameHand.Left ? GamePointer.Source.LeftHand : GamePointer.Source.RightHand;
            for (int i = 0; i < _pointers.Length; i++)
            {
                var p = _pointers[i];
                if (p == null || p.PointerSource != source) continue;
                if (p.IsHoveringTarget || p.IsGrabbing) return true;
            }
            return false;
        }
    }
}
