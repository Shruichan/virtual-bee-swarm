using UnityEngine;

namespace VirtualBeeSwarm.AR
{
    /// <summary>
    /// Optional generic scene-object placement on the already calibrated table during pre. HivePlacement remains responsible
    /// for the hive rig's two-point calibration and grab-and-drop fallback.
    /// </summary>
    public class SpawnObject : MonoBehaviour
    {
        [Header("Raycasting")]
        [SerializeField] private float rayLength = 1f;
        [SerializeField, Range(0f, 90f)] private float maxHorizontalAngle = 20f;

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
            IEnvRaycaster raycaster = arRig != null ? arRig.Raycaster : null;
            if (raycaster == null || !raycaster.Enabled || arRig.Placement == null ||
                !arRig.Placement.Allowed || !arRig.Room.HasTable) return;

            if (TryHandSpawn(GameHand.Left, raycaster)) return;
            TryHandSpawn(GameHand.Right, raycaster);
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

            Vector3 direction = targetLookPosition != null
                ? targetLookPosition.position - hit.point
                : (Camera.main != null ? Camera.main.transform.position - hit.point : -ray.direction);
            direction.y = 0f;
            if (direction.sqrMagnitude < 1e-6f) direction = Vector3.ProjectOnPlane(-ray.direction, Vector3.up);
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
            gameObjectToSpawn.transform.position = hit.point;
            gameObjectToSpawn.transform.rotation = rotation;
            if (arRig.TableAnchor != null) gameObjectToSpawn.transform.SetParent(arRig.TableAnchor, true);

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
