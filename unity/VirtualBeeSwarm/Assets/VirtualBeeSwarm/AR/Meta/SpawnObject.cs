using UnityEngine;
using Meta.XR.MRUtilityKit;
using VirtualBeeSwarm;

public class SpawnObject : MonoBehaviour
{
    [Header("Raycasting")]
    [SerializeField] private Transform rightHandRayStart;
    [SerializeField] private Transform leftHandRayStart;
    [SerializeField] private float rayLength = 1;
    [SerializeField] private float maxHorizontalAngle = 20f;
    
    [Header("Spawning")]
    [SerializeField] private bool isSpawning = false;
    [SerializeField] GameObject[] gameObjectsToSpawn;
    [SerializeField] private MRUKAnchor.SceneLabels labelFilter;

    private int _nextSpawnIndex = 0;
    private bool _warnedRoomUnavailable;
    private bool _warnedCameraUnavailable;
    private bool _hasPlaced;
    private bool _waitForPinchRelease;
    private GameObject _relocationControl;
    private Material _relocationMaterial;

    public void SetSpawningStatus(bool status)
    {
        isSpawning = status;
        if (!status) _waitForPinchRelease = false;
        if (_relocationControl != null) _relocationControl.SetActive(!status && _hasPlaced);
    }

    void OnDisable()
    {
        if (_relocationControl != null) _relocationControl.SetActive(false);
    }

    void OnDestroy()
    {
        if (_relocationControl != null) Destroy(_relocationControl);
        if (_relocationMaterial != null) Destroy(_relocationMaterial);
    }

    void Update()
    {
        if (_hasPlaced && !isSpawning && _relocationControl == null) ShowRelocationControl();
        if (_relocationControl != null && !_relocationControl.activeSelf && _hasPlaced && !isSpawning)
            _relocationControl.SetActive(true);
        if(!isSpawning) return;

        if (_waitForPinchRelease)
        {
            if (GameHands.AnyPinchHeld()) return;
            _waitForPinchRelease = false;
        }

        MRUKRoom room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
        if (room == null)
        {
            if (!_warnedRoomUnavailable)
            {
                Debug.LogWarning("SpawnObject: waiting for an MRUK current room before placing an object.", this);
                _warnedRoomUnavailable = true;
            }
            return;
        }
        _warnedRoomUnavailable = false;

        if (leftHandRayStart != null) TryHandSpawn(leftHandRayStart, room);
        if(!isSpawning) return;
        if (rightHandRayStart != null) TryHandSpawn(rightHandRayStart, room);
    }

    void TryHandSpawn(Transform handRayStart, MRUKRoom room)
    {
        Ray ray = new Ray(handRayStart.position, handRayStart.forward);
        bool hasHit = room.Raycast(
            ray,
            rayLength,
            new LabelFilter(labelFilter),
            out RaycastHit hit,
            out MRUKAnchor anchor
        );
        if (!hasHit) return;

        Vector3 hitPoint = hit.point;
        Vector3 hitNormal = hit.normal;

        // Horizontal check
        float angle = Vector3.Angle(hitNormal, Vector3.up);
        if (angle > maxHorizontalAngle) return;

        PlaceObject(hitPoint);
    }

    void PlaceObject(Vector3 hitPoint)
    {
        if (gameObjectsToSpawn == null || gameObjectsToSpawn.Length == 0 ||
            _nextSpawnIndex >= gameObjectsToSpawn.Length || gameObjectsToSpawn[_nextSpawnIndex] == null)
        {
            Debug.LogError("SpawnObject: assign a valid object to place.", this);
            SetSpawningStatus(false);
            return;
        }

        GameObject gameObjectToSpawn = gameObjectsToSpawn[_nextSpawnIndex];
        gameObjectToSpawn.transform.position = hitPoint;
        gameObjectToSpawn.SetActive(true);
        if (!gameObjectToSpawn.activeInHierarchy)
        {
            Debug.LogError("SpawnObject: the placed object has an inactive parent. Assign the object you want to enable, or enable its parent.", gameObjectToSpawn);
            SetSpawningStatus(false);
            return;
        }

        if (_nextSpawnIndex != 0)
        {
            gameObjectsToSpawn[_nextSpawnIndex-1].SetActive(false);
        }

        _hasPlaced = true;
        SetSpawningStatus(false);
        ShowRelocationControl();
        UpdateNextSpawnIndex();
    }

    void UpdateNextSpawnIndex()
    {
        _nextSpawnIndex++;
        if(_nextSpawnIndex >= gameObjectsToSpawn.Length) _nextSpawnIndex = 0;
    }

    void ShowRelocationControl()
    {
        if (_relocationControl != null) return;

        var rig = leftHandRayStart != null ? leftHandRayStart.GetComponentInParent<OVRCameraRig>() : null;
        if (rig == null && rightHandRayStart != null) rig = rightHandRayStart.GetComponentInParent<OVRCameraRig>();
        Transform head = rig != null ? rig.centerEyeAnchor : (Camera.main != null ? Camera.main.transform : null);
        if (head == null)
        {
            if (!_warnedCameraUnavailable)
            {
                Debug.LogWarning("SpawnObject: waiting for the player camera to show the relocation control.", this);
                _warnedCameraUnavailable = true;
            }
            return;
        }
        _warnedCameraUnavailable = false;

        _relocationControl = new GameObject("RelocateHiveControl");
        _relocationControl.transform.SetParent(head, false);
        _relocationControl.transform.localPosition = new Vector3(0.35f, -0.25f, 1.1f);
        var collider = _relocationControl.AddComponent<BoxCollider>();
        collider.size = new Vector3(0.24f, 0.075f, 0.03f);
        collider.isTrigger = true;

        var face = GameObject.CreatePrimitive(PrimitiveType.Cube);
        face.name = "Face";
        face.transform.SetParent(_relocationControl.transform, false);
        face.transform.localScale = new Vector3(0.24f, 0.075f, 0.015f);
        Destroy(face.GetComponent<Collider>());
        _relocationMaterial = GameMaterials.Create("RelocateHive (runtime)", new Color(1f, 0.78f, 0.2f));
        VirtualBeeSwarm.AR.ArMaterials.MarkNeverOccluded(_relocationMaterial);
        var renderer = face.GetComponent<Renderer>();
        renderer.sharedMaterial = _relocationMaterial;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        var label = new GameObject("Label");
        label.transform.SetParent(_relocationControl.transform, false);
        label.transform.localPosition = new Vector3(0f, 0f, -0.01f);
        var text = label.AddComponent<TextMesh>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (text.font != null) label.GetComponent<MeshRenderer>().sharedMaterial = text.font.material;
        text.fontSize = 64;
        text.characterSize = 0.002f;
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;
        text.color = new Color(0.1f, 0.07f, 0.02f);
        text.text = "RELOCATE HIVE";

        var button = _relocationControl.AddComponent<RelocateHiveButton>();
        button.Initialize(this, renderer);
    }

    public void BeginRelocation()
    {
        _waitForPinchRelease = true;
        SetSpawningStatus(true);
    }
}

public class RelocateHiveButton : MonoBehaviour, IGameTarget
{
    private SpawnObject _spawner;
    private Renderer _face;

    public bool Interactable => isActiveAndEnabled && _spawner != null && _spawner.isActiveAndEnabled;

    public void Initialize(SpawnObject spawner, Renderer face)
    {
        _spawner = spawner;
        _face = face;
    }

    public void SetHover(bool hovered)
    {
        GameMaterials.Tint(_face, hovered ? new Color(1f, 0.92f, 0.45f) : new Color(1f, 0.78f, 0.2f));
    }

    public void Click()
    {
        if (Interactable) _spawner.BeginRelocation();
    }
}