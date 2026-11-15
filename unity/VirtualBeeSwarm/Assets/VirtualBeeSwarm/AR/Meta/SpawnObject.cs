using UnityEngine;
using Meta.XR.MRUtilityKit;
using UnityEngine.Serialization;

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
    private int _totalNumSpawns;
    private bool _warnedRoomUnavailable;
    

    public void SetSpawningStatus(bool status)
    {
        isSpawning = status;
        _totalNumSpawns = gameObjectsToSpawn.Length;
    }
    
    void Update()
    {
        if(!isSpawning) return;

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
        
        GameObject gameObjectToSpawn = gameObjectsToSpawn[_nextSpawnIndex];
        gameObjectToSpawn.transform.position = hitPoint;
        gameObjectToSpawn.SetActive(true);
        if (!gameObjectToSpawn.activeInHierarchy)
        {
            Debug.LogError("SpawnObject: the placed object has an inactive parent. Assign the object you want to enable, or enable its parent.", gameObjectToSpawn);
            isSpawning = false;
            return;
        }

        if (_nextSpawnIndex != 0)
        {
            gameObjectsToSpawn[_nextSpawnIndex-1].SetActive(false);
        }

        isSpawning = false;
        UpdateNextSpawnIndex();
    }

    void UpdateNextSpawnIndex()
    {
        _nextSpawnIndex++;
        if(_nextSpawnIndex >= _totalNumSpawns) _nextSpawnIndex = 0;
    }
}