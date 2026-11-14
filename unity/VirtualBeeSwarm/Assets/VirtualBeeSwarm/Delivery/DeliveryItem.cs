using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Per-item data for BeeDelivery: weight (sets the crew size, the dips and the thunk), its measured size (crown
    /// ring, hang point, contact shadow) and its rest scale. BeeDelivery adds one to any transform it is asked to
    /// move, so the builder only needs to add it to set a weight. The pivot of an item is its base (props are built
    /// standing on y = 0), and it hangs upright under its crew.
    /// </summary>
    [DisallowMultipleComponent]
    public class DeliveryItem : MonoBehaviour
    {
        [Tooltip("Placeholder weight in kg: crew = 3 + round(weight x 10), clamped 3..12. >= heavyFrom: slow, two dips, 8-bee repo that drags first.")]
        public float weight = 0.1f;
        [Tooltip("Crew size override (0 = by weight).")]
        public int crewOverride;
        [Tooltip("Blob shadow under it while delivered (off for things that never touch the table).")]
        public bool castShadow = true;

        public const float HeavyFrom = 0.45f;

        public bool Heavy => weight >= HeavyFrom;
        public int Crew => crewOverride > 0 ? Mathf.Clamp(crewOverride, 1, BeeDelivery.MaxCrew) : CrewFor(weight);

        public static int CrewFor(float kg) => Mathf.Clamp(3 + Mathf.RoundToInt(kg * 10f), 3, 12);

        // ---- runtime state owned by BeeDelivery (not serialized)
        [System.NonSerialized] public int job = -1;
        [System.NonSerialized] public bool measured;
        [System.NonSerialized] public Vector3 restScale = Vector3.one;
        /// <summary>Local-space bounds (item space, before its own scale).</summary>
        [System.NonSerialized] public Bounds localBounds;
        [System.NonSerialized] public Renderer shadow;
        [System.NonSerialized] public float shadowAlpha = -1f;
        [System.NonSerialized] public bool delivered;       // touched down / swirled in and not taken away since
        [System.NonSerialized] public bool parked;          // parked in the room (arc slot) and not taken away since
        [System.NonSerialized] public Transform stamp;
        [System.NonSerialized] public TextMesh stampText;
        [System.NonSerialized] public Renderer stampBacking;
        [System.NonSerialized] public float stampAt = -1f;
        [System.NonSerialized] public float squashUntil = -1f;
        [System.NonSerialized] public bool hasHome;
        [System.NonSerialized] public Transform homeParent;
        [System.NonSerialized] public Vector3 homeLocalPos;
        [System.NonSerialized] public Quaternion homeLocalRot = Quaternion.identity;

        /// <summary>Remember where the item sits now (under its parent) as its home: DeliverHome() lands it there.</summary>
        public void RecordHome()
        {
            homeParent = transform.parent;
            homeLocalPos = transform.localPosition;
            homeLocalRot = transform.localRotation;
            hasHome = true;
        }

        public Vector3 HomePosition => homeParent != null ? homeParent.TransformPoint(homeLocalPos) : homeLocalPos;
        public Quaternion HomeRotation => homeParent != null ? homeParent.rotation * homeLocalRot : homeLocalRot;

        /// <summary>Height of the top above the pivot, world metres, at rest scale.</summary>
        public float Top => localBounds.max.y * Mathf.Abs(LossyY());
        /// <summary>Horizontal half-size, world metres.</summary>
        public float Radius
        {
            get
            {
                Vector3 s = transform.lossyScale;
                float x = Mathf.Max(Mathf.Abs(localBounds.min.x), Mathf.Abs(localBounds.max.x)) * Mathf.Abs(s.x);
                float z = Mathf.Max(Mathf.Abs(localBounds.min.z), Mathf.Abs(localBounds.max.z)) * Mathf.Abs(s.z);
                return Mathf.Max(0.02f, Mathf.Max(x, z));
            }
        }

        float LossyY()
        {
            // the item may be mid-squash / mid-shrink: measure against the rest scale
            Vector3 l = transform.localScale;
            float k = Mathf.Abs(l.y) > 1e-6f ? restScale.y / l.y : 1f;
            return transform.lossyScale.y * k;
        }

        /// <summary>Measure the meshes under the item (works while inactive). Skips BeeDelivery's own children ([VBS] names).</summary>
        public void Measure()
        {
            restScale = transform.localScale;
            var filters = GetComponentsInChildren<MeshFilter>(true);
            Matrix4x4 toLocal = transform.worldToLocalMatrix;
            bool any = false;
            var b = new Bounds(Vector3.zero, Vector3.zero);
            for (int i = 0; i < filters.Length; i++)
            {
                var mf = filters[i];
                if (mf.sharedMesh == null || IsOwnChild(mf.transform)) continue;
                Bounds mb = mf.sharedMesh.bounds;
                Matrix4x4 m = toLocal * mf.transform.localToWorldMatrix;
                Vector3 c = mb.center, e = mb.extents;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 corner = c + new Vector3((k & 1) == 0 ? -e.x : e.x, (k & 2) == 0 ? -e.y : e.y, (k & 4) == 0 ? -e.z : e.z);
                    Vector3 p = m.MultiplyPoint3x4(corner);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                    else b.Encapsulate(p);
                }
            }
            if (!any) b = new Bounds(new Vector3(0f, 0.05f, 0f), new Vector3(0.1f, 0.1f, 0.1f));
            localBounds = b;
            measured = true;
        }

        bool IsOwnChild(Transform t)
        {
            while (t != null && t != transform)
            {
                if (t.name.StartsWith("[VBS]", System.StringComparison.Ordinal)) return true;
                t = t.parent;
            }
            return false;
        }

        void OnDisable()
        {
            if (shadow != null && shadow.enabled) shadow.enabled = false;
            shadowAlpha = -1f;
        }

        void OnDestroy()
        {
            if (shadow == null) return;
            if (Application.isPlaying) Destroy(shadow.gameObject);
            else DestroyImmediate(shadow.gameObject);
        }
    }
}
