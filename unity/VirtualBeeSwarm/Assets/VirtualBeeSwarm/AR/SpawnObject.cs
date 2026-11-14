using UnityEngine;

namespace VirtualBeeSwarm.AR
{
    public class SpawnObject : MonoBehaviour
    {
        [Header("Raycasting")]
        [SerializeField] private Transform rightHandRayStart;
        [SerializeField] private Transform leftHandRayStart;
        [SerializeField] private float rayLength = 1f;
        [SerializeField, Range(0f, 90f)] private float maxHorizontalAngle = 20f;

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
            if (raycaster == null || !raycaster.Enabled) return;

            if (TryHandSpawn(leftHandRayStart, raycaster)) return;
            TryHandSpawn(rightHandRayStart, raycaster);
        }

        bool TryHandSpawn(Transform handRayStart, IEnvRaycaster raycaster)
        {
            if (handRayStart == null) return false;

            Ray ray = new Ray(handRayStart.position, handRayStart.forward);
            if (!raycaster.Raycast(ray, rayLength, out ArHit hit)) return false;

            float angle = Vector3.Angle(hit.normal, Vector3.up);
            if (angle > maxHorizontalAngle) return false;

            Vector3 direction = targetLookPosition != null
                ? targetLookPosition.position - hit.point
                : (Camera.main != null ? Camera.main.transform.position - hit.point : -ray.direction);
            direction = Vector3.ProjectOnPlane(direction, Vector3.up);
            if (direction.sqrMagnitude < 1e-6f)
            {
                direction = Vector3.ProjectOnPlane(-ray.direction, Vector3.up);
            }
            if (direction.sqrMagnitude < 1e-6f) direction = Vector3.forward;

            Quaternion rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
            Instantiate(prefabToSpawn, hit.point, rotation);
            isSpawning = false;
            return true;
        }
    }
}
