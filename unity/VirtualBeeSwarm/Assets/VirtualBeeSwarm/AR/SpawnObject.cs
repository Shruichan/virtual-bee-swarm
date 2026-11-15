using UnityEngine;

namespace VirtualBeeSwarm.AR
{
    /// <summary>
    /// Optional generic prefab placement on the already calibrated table during pre. HivePlacement remains responsible
    /// for the hive rig's two-point calibration and grab-and-drop fallback.
    /// </summary>
    public class SpawnObject : MonoBehaviour
    {
        [Header("Raycasting")]
        [SerializeField] private Transform rightHandRayStart;
        [SerializeField] private Transform leftHandRayStart;
        [SerializeField] private float rayLength = 1f;
        [SerializeField, Range(0f, 90f)] private float maxHorizontalAngle = 20f;
        [Tooltip("Footprint and clearance passed to the environment raycaster; match this to the prefab.")]
        [SerializeField] private Vector3 placementBoxSize = new Vector3(0.6f, 0.45f, 0.3f);

        [Header("Spawning")]
        [SerializeField] private bool isSpawning;
        [SerializeField] private GameObject prefabToSpawn;
        [SerializeField] private Transform targetLookPosition;
        [SerializeField] private ArRig arRig;

        bool _configurationErrorLogged;

        public void SetSpawningStatus(bool status)
        {
            isSpawning = status;
        }

        void Update()
        {
            if (!isSpawning) return;

            if (prefabToSpawn == null)
            {
                if (!_configurationErrorLogged)
                {
                    Debug.LogError($"{nameof(SpawnObject)} needs a prefab assigned.", this);
                    _configurationErrorLogged = true;
                }

                isSpawning = false;
                return;
            }

            if (arRig == null) arRig = ArRig.Instance;
            IEnvRaycaster raycaster = arRig != null ? arRig.Raycaster : null;
            if (raycaster == null || !raycaster.Enabled || arRig.Placement == null ||
                !arRig.Placement.Allowed || !arRig.Room.HasTable) return;

            if (TryHandSpawn(leftHandRayStart, raycaster)) return;
            TryHandSpawn(rightHandRayStart, raycaster);
        }

        bool TryHandSpawn(Transform handRayStart, IEnvRaycaster raycaster)
        {
            if (handRayStart == null) return false;

            Ray ray = new Ray(handRayStart.position, handRayStart.forward);
            Vector3 direction = targetLookPosition != null
                ? targetLookPosition.position - handRayStart.position
                : (Camera.main != null ? Camera.main.transform.position - handRayStart.position : -ray.direction);
            direction = Vector3.ProjectOnPlane(direction, Vector3.up);
            if (direction.sqrMagnitude < 1e-6f)
            {
                direction = Vector3.ProjectOnPlane(-ray.direction, Vector3.up);
            }
            if (direction.sqrMagnitude < 1e-6f) direction = Vector3.forward;

            if (!raycaster.PlaceBox(ray, placementBoxSize, direction.normalized, out ArHit hit) ||
                hit.status != ArHitStatus.Hit || Vector3.Distance(ray.origin, hit.point) > rayLength) return false;

            float angle = Vector3.Angle(hit.normal, Vector3.up);
            if (angle > maxHorizontalAngle || !arRig.Room.InsideTable(hit.point)) return false;

            float tableY = arRig.Room.Table.Y;
            if (Mathf.Abs(hit.point.y - tableY) > 0.05f) return false;
            hit.point.y = tableY;

            Quaternion rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
            GameObject spawned = Instantiate(prefabToSpawn, hit.point, rotation);
            if (arRig.TableAnchor != null) spawned.transform.SetParent(arRig.TableAnchor, true);
            isSpawning = false;
            return true;
        }
    }
}
