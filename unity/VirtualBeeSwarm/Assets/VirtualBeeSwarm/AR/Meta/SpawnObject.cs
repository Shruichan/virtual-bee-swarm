using UnityEngine;
using Meta.XR.MRUtilityKit;

namespace VirtualBeeSwarm.AR
{
    public class SpawnObject : MonoBehaviour
    {
        [Header("Raycasting")]
        [SerializeField] private Transform rightHandRayStart;
        [SerializeField] private Transform leftHandRayStart;
        [SerializeField] private float rayLength = 1f;
        [SerializeField, Range(0f, 90f)] private float maxHorizontalAngle = 20f;
        [SerializeField] private MRUKAnchor.SceneLabels labelFilter = MRUKAnchor.SceneLabels.TABLE;

        [Header("Spawning")]
        [SerializeField] private bool isSpawning;
        [SerializeField] private GameObject[] gameObjectsToSpawn;
        [SerializeField] private Transform targetLookPosition;
        [SerializeField] private ArRig arRig;

        bool _configurationErrorLogged;
        int _nextSpawnIndex;
        int _totalNumSpawns;

        void Awake()
        {
            _totalNumSpawns = gameObjectsToSpawn != null ? gameObjectsToSpawn.Length : 0;
        }

        public void SetSpawningStatus(bool status)
        {
            isSpawning = status;
            _totalNumSpawns = gameObjectsToSpawn != null ? gameObjectsToSpawn.Length : 0;
            if (status && _totalNumSpawns == 0)
            {
                Debug.LogError($"{nameof(SpawnObject)} needs at least one scene object assigned.", this);
                isSpawning = false;
                _configurationErrorLogged = true;
            }
        }

        void Update()
        {
            if (!isSpawning) return;

            if (_totalNumSpawns == 0)
            {
                if (!_configurationErrorLogged)
                {
                    Debug.LogError($"{nameof(SpawnObject)} needs at least one scene object assigned.", this);
                    _configurationErrorLogged = true;
                }

                isSpawning = false;
                return;
            }

            if (arRig == null) arRig = ArRig.Instance;
            if (arRig != null)
            {
                IEnvRaycaster raycaster = arRig.Raycaster;
                if (raycaster == null || !raycaster.Enabled || arRig.Placement == null ||
                    !arRig.Placement.Allowed || !arRig.Room.HasTable) return;

                if (TryHandSpawn(GameHand.Left, raycaster)) return;
                TryHandSpawn(GameHand.Right, raycaster);
                return;
            }

            if (leftHandRayStart == null) leftHandRayStart = GameObject.Find("LeftHandAnchor")?.transform;
            if (rightHandRayStart == null) rightHandRayStart = GameObject.Find("RightHandAnchor")?.transform;
            MRUKRoom room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
            if (room == null) return;

            if (TryHandSpawn(leftHandRayStart, room)) return;
            TryHandSpawn(rightHandRayStart, room);
        }

        bool TryHandSpawn(GameHand hand, IEnvRaycaster raycaster)
        {
            if (!GameHands.TryGetPointerRay(hand, out Ray ray)) return false;

            if (!raycaster.Raycast(ray, rayLength, out ArHit hit) || hit.status != ArHitStatus.Hit) return false;
            float angle = Vector3.Angle(hit.normal, Vector3.up);
            if (angle > maxHorizontalAngle || !arRig.Room.InsideTable(hit.point)) return false;

            float tableY = arRig.Room.Table.Y;
            if (Mathf.Abs(hit.point.y - tableY) > 0.05f) return false;
            hit.point.y = tableY;
            return SpawnAt(hit.point, ray.direction);
        }

        bool TryHandSpawn(Transform handRayStart, MRUKRoom room)
        {
            if (handRayStart == null) return false;

            Ray ray = new Ray(handRayStart.position, handRayStart.forward);
            if (!room.Raycast(
                    ray,
                    rayLength,
                    new LabelFilter(labelFilter),
                    out RaycastHit hit,
                    out _))
            {
                return false;
            }

            if (Vector3.Angle(hit.normal, Vector3.up) > maxHorizontalAngle) return false;
            return SpawnAt(hit.point, ray.direction);
        }

        bool SpawnAt(Vector3 hitPoint, Vector3 rayDirection)
        {
            Vector3 direction = targetLookPosition != null
                ? targetLookPosition.position - hitPoint
                : (Camera.main != null ? Camera.main.transform.position - hitPoint : -rayDirection);
            direction.y = 0f;
            if (direction.sqrMagnitude < 1e-6f) direction = Vector3.ProjectOnPlane(-rayDirection, Vector3.up);
            if (direction.sqrMagnitude < 1e-6f) direction = Vector3.forward;
            Quaternion rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);

            GameObject gameObjectToSpawn = gameObjectsToSpawn[_nextSpawnIndex];
            if (gameObjectToSpawn == null)
            {
                Debug.LogError($"{nameof(SpawnObject)} has no scene object assigned at index {_nextSpawnIndex}.", this);
                isSpawning = false;
                return false;
            }

            gameObjectToSpawn.SetActive(true);
            gameObjectToSpawn.transform.position = hitPoint;
            gameObjectToSpawn.transform.rotation = rotation;
            if (arRig != null && arRig.TableAnchor != null)
                gameObjectToSpawn.transform.SetParent(arRig.TableAnchor, true);

            if (_nextSpawnIndex != 0)
            {
                GameObject previous = gameObjectsToSpawn[_nextSpawnIndex - 1];
                if (previous != null) previous.SetActive(false);
            }

            isSpawning = false;
            _nextSpawnIndex++;
            if (_nextSpawnIndex >= _totalNumSpawns) _nextSpawnIndex = 0;
            return true;
        }
    }
}
