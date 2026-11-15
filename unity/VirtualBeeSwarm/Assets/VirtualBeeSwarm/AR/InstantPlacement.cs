using UnityEngine;

namespace VirtualBeeSwarm.AR
{
    /// <summary>
    /// Manual placement before signing. In the meadow, aim at the real floor and use a pinch or controller trigger
    /// to place the hive. The floor plane keeps placement available while room depth is unavailable.
    /// The tabletop layout still uses the environment's box fit check.
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
        const float FloorHeightTolerance = 0.18f;
        const float HiveCentreForward = 0.15f;
        static readonly Vector2 HiveBaseSize = new Vector2(0.36f, 0.33f);
        float _lastShowAt = -10f, _fitSince, _confirmedUntil;
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
            _rig != null && _rig.Placement != null &&
            (Bloom.MeadowLayout.Active || (_rig.Raycaster != null && _rig.Raycaster.IsSupported)) &&
            ArTuning.PlacementMode == ArTuning.Placement.Raycast && _rig.Placement.Allowed && !_rig.Placement.Holding;

        void Update()
        {
            if (!Active)
            {
                Aiming = false;
                if (_ghost != null && _ghost.activeSelf) _ghost.SetActive(false);
                return;
            }

            if (Time.time < _confirmedUntil) return;
            if (_confirmedUntil > 0f)
            {
                _confirmedUntil = 0f;
                _ghost.SetActive(false);
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
            bool meadow = Bloom.MeadowLayout.Active;
            float now = Time.time;
            bool show, fits;
            Vector3 point;
            Vector2 footprint;
            if (meadow)
            {
                if (now >= _nextScanRead)
                {
                    _nextScanRead = now + HoldMiss;
                    _scene = Bloom.MeadowField.Instance != null ? Bloom.MeadowField.Instance.CurrentScene() : null;
                }
                footprint = HiveBaseSize * Bloom.MeadowLayout.Scale;
                float floorY = Bloom.MeadowLayout.FloorY;
                var raycaster = _rig.Raycaster;
                ArHit hit = new ArHit { status = ArHitStatus.NotSupported };
                bool hitEnvironment = raycaster != null && raycaster.IsSupported && raycaster.Raycast(ray, maxDistance, out hit);
                LastStatus = hit.status;
                var camera = Camera.main;
                // A missing depth frame is common on Quest. Use the tracked floor plane, but do not guess
                // if the headset's tracking origin is still settling or a real surface blocked the ray.
                float eyeHeight = camera != null ? camera.transform.position.y - floorY : 0f;
                bool floorKnown = desktop || _scene != null || (eyeHeight >= 0.35f && eyeHeight <= 2.6f);
                show = TryFloorPoint(ray, hitEnvironment, hit, floorY, maxDistance, floorKnown, out point);
                fits = show && IsClearFloor(_scene, point, fwd, footprint);
            }
            else
            {
                var raycaster = _rig.Raycaster;
                fits = raycaster.PlaceBox(ray, boxSize, fwd, out var hit);
                LastStatus = hit.status;
                if (hit.status != ArHitStatus.Hit && raycaster.Raycast(ray, maxDistance, out var rejected)) hit = rejected;
                point = hit.point;
                show = hit.status == ArHitStatus.Hit && (point - ray.origin).sqrMagnitude <= maxDistance * maxDistance;
                fits = fits && show && hit.normal.y > 0.9f;
                footprint = new Vector2(boxSize.x, boxSize.y);
            }
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
            if (!wasShown || (point - _ghost.transform.position).sqrMagnitude > 0.5f * 0.5f) _ghost.transform.SetPositionAndRotation(point, rot);
            else
            {
                float k = 1f - Mathf.Exp(-Time.deltaTime * 14f);
                _ghost.transform.SetPositionAndRotation(Vector3.Lerp(_ghost.transform.position, point, k), Quaternion.Slerp(_ghost.transform.rotation, rot, k));
            }
            if (fits != _rawFit) { _rawFit = fits; _fitSince = now; }
            bool shownFit = fits && (!wasShown || _lastFit || now - _fitSince >= HoldFit);
            if (shownFit != _lastFit || _ghostText.text.Length == 0)
            {
                _lastFit = shownFit;
                GameMaterials.Tint(_ghostRenderer, shownFit ? fitColor : noFitColor);
            }
            string action = desktop ? "release L" : "pinch / trigger";
            string label = shownFit ? (meadow ? "PLACE HIVE HERE\n" : "DELIVER HERE\n") + action : "AIM AT A CLEAR SPOT";
            if (_ghostText.text != label) _ghostText.text = label;
            if (place && fits)
            {
                Placements++;
                if (!desktop) _rig.ConsumeCalibrationPinch();
                // The plate marks the hive itself. The layout anchor sits just behind its centre.
                Vector3 anchorPoint = meadow ? point - fwd * (HiveCentreForward * Bloom.MeadowLayout.Scale) : point;
                _rig.PlaceFromRaycast(new Pose(anchorPoint, rot));
                _ghost.transform.SetPositionAndRotation(point, rot);
                _ghostText.text = "HIVE PLACED";
                GameMaterials.Tint(_ghostRenderer, fitColor);
                _lastFit = true;
                _confirmedUntil = now + 0.55f;
                GameSound.Cue("touchdown_light", point);
            }
        }

        public static bool TryFloorPoint(Ray ray, bool hitEnvironment, ArHit hit, float floorY,
            float maxDistance, bool floorKnown, out Vector3 point)
        {
            point = default;
            if (ray.direction.y >= -0.12f || float.IsNaN(floorY) || float.IsInfinity(floorY)) return false;
            if (hitEnvironment || hit.status == ArHitStatus.Hit)
            {
                if (hit.normal.y <= 0.9f || Mathf.Abs(hit.point.y - floorY) > FloorHeightTolerance ||
                    (hit.point - ray.origin).sqrMagnitude > maxDistance * maxDistance) return false;
                point = new Vector3(hit.point.x, floorY, hit.point.z);
                return true;
            }
            if (!floorKnown || hit.status == ArHitStatus.Occluded) return false;
            float distance = (floorY - ray.origin.y) / ray.direction.y;
            if (distance <= 0f || distance > maxDistance) return false;
            point = ray.GetPoint(distance);
            return true;
        }

        public static bool IsClearFloor(Bloom.BloomScene scene, Vector3 point, Vector3 forward, Vector2 footprint)
        {
            if (scene == null) return true;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++)
                {
                    Vector3 sample = point + right * (x * footprint.x * 0.5f) + forward * (z * footprint.y * 0.5f);
                    if (!scene.InFloor(sample) || scene.InVolume(sample, 0.04f)) return false;
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
            if (haveIdleRay && !freePinch) return true;
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
