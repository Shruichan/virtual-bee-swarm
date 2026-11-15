using System;
using System.Collections.Generic;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// dartiros' ten new props, put where they help the story. Installs itself after the game scene loads (no scene or
    /// builder edit; VBS_NEWPROPS=0 skips it) from Resources/NewPropsKit, and only listens: GameDirector.BeatChanged /
    /// GroupResetDone, GameNet.VoiceLineStarted (line id + caption) and HiveBody.Lifted. Every prop plays its own Blender
    /// clip on a Legacy Animation (ArtAnim style) and logs "[VBS][NewProps] &lt;prop&gt; &lt;clip&gt; (&lt;why&gt;)".
    ///   BadgeAct     union badge pinned on Beeswax's chest (his rig's Body bone); Settle wiggle on his union lines
    ///   BenchAct     the beekeeper who quit left a bench on the table: frame rest, queen cage, sample tray, hive tool;
    ///                tool + comb frame on the hive lift and at the escape, cage cork on "queen", samples on "honey"
    ///   NectarAct    right feed pick (room_feed_sugar_syrup): three nectar drops fall into the hive
    ///   QueenAct     escape: the queen rises out of the open hive and hovers (she's safe); back inside at T-0
    ///   MoonFlagAct  T-0: the union flag is planted on the Moon next to the humans' Earth flag; both flutter
    ///   ContractAct  the contract ("plant flowers"): three pots, the watering can pours, flowers pop up
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class NewProps : MonoBehaviour
    {
        public static NewProps Instance { get; private set; }

        public NewPropsKit Kit { get; private set; }
        public GameDirector Director { get; private set; }
        public GameNet Net { get; private set; }
        public HiveBody Hive { get; private set; }
        public BeeswaxController Beeswax { get; private set; }
        /// <summary>TableAnchor (the hive's parent): +Z away from the player, y = 0 on the table top.</summary>
        public Transform Table { get; private set; }
        /// <summary>The hive's LiftRoot (lifts and tilts with the heft).</summary>
        public Transform LiftRoot { get; private set; }

        readonly List<NewPropAct> _acts = new List<NewPropAct>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (Instance != null) return;
            string env = null;
            try { env = Environment.GetEnvironmentVariable("VBS_NEWPROPS"); } catch (Exception) { }
            if (env == "0") { Debug.Log("[VBS][NewProps] VBS_NEWPROPS=0: not installed"); return; }
            var director = FindAnyObjectByType<GameDirector>(FindObjectsInactive.Include);
            if (director == null) return; // not the game scene
            var kit = NewPropsKit.Load();
            if (kit == null) { Debug.LogWarning("[VBS][NewProps] no Resources/" + NewPropsKit.ResourceName + ": not installed"); return; }
            var go = new GameObject("NewProps");
            var np = go.AddComponent<NewProps>();
            np.Kit = kit;
            np.Director = director;
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (Director != null) { Director.BeatChanged -= OnBeat; Director.GroupResetDone -= OnReset; }
            if (Net != null) Net.VoiceLineStarted -= OnLine;
            if (Hive != null) Hive.Lifted -= OnLifted;
        }

        void Start()
        {
            if (Kit == null) { enabled = false; return; }
            // the hive and Beeswax may still be switched off in pre (they are delivered in the hook)
            Net = FindAnyObjectByType<GameNet>(FindObjectsInactive.Include);
            Hive = FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include);
            Beeswax = FindAnyObjectByType<BeeswaxController>(FindObjectsInactive.Include);
            Table = Hive != null ? Hive.transform.parent : null;
            LiftRoot = Hive != null ? FindDeep(Hive.transform, "LiftRoot") : null;
            if (Table == null) { Debug.LogWarning("[VBS][NewProps] no hive / TableAnchor: not installed"); enabled = false; return; }
            if (LiftRoot == null) LiftRoot = Hive.transform;

            var report = new List<string>();
            Add<BadgeAct>(report);
            Add<BenchAct>(report);
            Add<NectarAct>(report);
            Add<QueenAct>(report);
            Add<MoonFlagAct>(report);
            Add<ContractAct>(report);
            Director.BeatChanged += OnBeat;
            Director.GroupResetDone += OnReset;
            if (Net != null) Net.VoiceLineStarted += OnLine;
            if (Hive != null) Hive.Lifted += OnLifted;
            Debug.Log("[VBS][NewProps] installed: " + string.Join("; ", report));
        }

        void Add<T>(List<string> report) where T : NewPropAct
        {
            var act = gameObject.AddComponent<T>();
            act.Hub = this;
            string what;
            try { what = act.Setup(); }
            catch (Exception e) { what = null; Debug.LogError("[VBS][NewProps] " + typeof(T).Name + " setup FAILED: " + e); }
            if (string.IsNullOrEmpty(what))
            {
                report.Add(typeof(T).Name + " off");
                Destroy(act);
                return;
            }
            report.Add(what);
            _acts.Add(act);
        }

        void OnBeat(GameBeat from, GameBeat to)
        {
            foreach (var a in _acts)
            {
                if (a == null) continue;
                try { a.OnBeat(from, to); } catch (Exception e) { Debug.LogException(e, a); }
            }
        }

        void OnLine(GameNet.VoiceLineInfo info)
        {
            string id = info.id ?? "", caption = info.caption ?? "";
            foreach (var a in _acts)
            {
                if (a == null) continue;
                try { a.OnLine(id, caption); } catch (Exception e) { Debug.LogException(e, a); }
            }
        }

        void OnLifted(float h)
        {
            foreach (var a in _acts)
            {
                if (a == null) continue;
                try { a.OnLifted(); } catch (Exception e) { Debug.LogException(e, a); }
            }
        }

        void OnReset()
        {
            foreach (var a in _acts)
            {
                if (a == null) continue;
                a.StopAllCoroutines();
                try { a.OnReset(); } catch (Exception e) { Debug.LogException(e, a); }
            }
        }

        // ------------------------------------------------------------------------------------------ helpers
        /// <summary>An unpacked copy of prefab under parent (local pose), front (+Z) turned by yaw (180 = to the player).</summary>
        public static GameObject Spawn(GameObject prefab, Transform parent, Vector3 localPos, float yaw, float scale, string name)
        {
            var go = Instantiate(prefab, parent, false);
            go.name = name;
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = Vector3.one * scale;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            return go;
        }

        /// <summary>Breadth-first search by name below root (root itself excluded).</summary>
        public static Transform FindDeep(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name)) return null;
            var q = new Queue<Transform>();
            q.Enqueue(root);
            while (q.Count > 0)
            {
                var t = q.Dequeue();
                if (t != root && t.name == name) return t;
                for (int i = 0; i < t.childCount; i++) q.Enqueue(t.GetChild(i));
            }
            return null;
        }

        /// <summary>Bounds of the meshes below root (skinned or not, active or not) in space's local coordinates.</summary>
        public static Bounds LocalBounds(Transform space, Transform root, Func<Transform, bool> skip = null)
        {
            var b = new Bounds();
            bool any = false;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (skip != null && skip(r.transform)) continue;
                Mesh m = r is SkinnedMeshRenderer s ? s.sharedMesh : (r.TryGetComponent(out MeshFilter mf) ? mf.sharedMesh : null);
                if (m == null) continue;
                var mtx = space.worldToLocalMatrix * r.transform.localToWorldMatrix;
                var mb = m.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = mtx.MultiplyPoint3x4(c);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                    else b.Encapsulate(p);
                }
            }
            return b;
        }

        public static float EaseOutBack(float t)
        {
            t = Mathf.Clamp01(t) - 1f;
            const float s = 1.9f;
            return 1f + t * t * ((s + 1f) * t + s);
        }
    }

    /// <summary>One story moment for one or more of the new props. The hub calls these on the main thread.</summary>
    public abstract class NewPropAct : MonoBehaviour
    {
        public NewProps Hub;
        protected NewPropsKit Kit => Hub.Kit;

        /// <summary>Spawns the props; returns a short report, or null to switch this act off.</summary>
        public abstract string Setup();
        public virtual void OnBeat(GameBeat from, GameBeat to) { }
        public virtual void OnLine(string id, string caption) { }
        public virtual void OnLifted() { }
        /// <summary>Back to pre: everything as Setup left it (coroutines are already stopped).</summary>
        public virtual void OnReset() { }
    }

    /// <summary>Legacy Animation playback of the Blender clips (the importer sets the clip names and loop flags).</summary>
    public static class PropClip
    {
        public static Animation Prepare(GameObject go)
        {
            var a = go != null ? go.GetComponent<Animation>() : null;
            if (a == null) return null;
            a.playAutomatically = false;
            a.cullingType = AnimationCullingType.AlwaysAnimate;
            return a;
        }

        public static bool Has(Animation a, string clip) => a != null && a[clip] != null;

        /// <summary>Holds the clip's pose at normalized time t (0 = rest), not playing.</summary>
        public static void Pose(Animation a, string clip, float t)
        {
            if (!Has(a, clip)) return;
            var s = a[clip];
            a.Play(clip);
            s.normalizedTime = t;
            s.enabled = true;
            s.weight = 1f;
            a.Sample();
            a.Stop();
        }

        public static AnimationState Play(Animation a, string clip, float speed, WrapMode wrap, string who, string why)
        {
            if (!Has(a, clip)) return null;
            var s = a[clip];
            s.speed = speed;
            s.wrapMode = wrap;
            a.Rewind(clip);
            a.Play(clip);
            Debug.Log("[VBS][NewProps] " + who + " " + clip + " (" + why + ")");
            return s;
        }
    }
}
