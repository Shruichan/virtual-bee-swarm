using System.Collections;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Base for everything that runs during one or more beats (stations, hornet, what-if, escape, T-0, map, intro).
    /// GameDirector calls Enter/Exit in a fixed order; a behaviour calls Complete() when its beat is done, and
    /// OnBeatTimeout() is its last chance to wrap up before the director moves on anyway.
    /// </summary>
    public abstract class BeatBehaviour : MonoBehaviour
    {
        [SerializeField] protected GameDirector director;
        [SerializeField] protected GameNet net;

        public bool IsActive { get; private set; }
        public GameBeat ActiveBeat { get; private set; }

        /// <summary>Does this behaviour run in that beat?</summary>
        public abstract bool Handles(GameBeat beat);

        protected virtual void Awake()
        {
            if (director == null) director = FindAnyObjectByType<GameDirector>();
            if (net == null) net = FindAnyObjectByType<GameNet>();
        }

        internal void Enter(GameBeat beat)
        {
            IsActive = true;
            ActiveBeat = beat;
            OnEnter(beat);
        }

        internal void Exit(GameBeat beat)
        {
            if (!IsActive) return;
            IsActive = false;
            StopAllCoroutines();
            OnExit(beat);
        }

        internal void Timeout() { if (IsActive) OnBeatTimeout(); }

        internal void ResetForGroup()
        {
            IsActive = false;
            StopAllCoroutines();
            OnResetState();
        }

        protected abstract void OnEnter(GameBeat beat);
        protected virtual void OnExit(GameBeat beat) { }
        /// <summary>Max duration reached: wrap up quickly (default: complete now).</summary>
        protected virtual void OnBeatTimeout() => Complete();
        /// <summary>group.reset / back to pre: put everything back where the scene builder left it.</summary>
        protected virtual void OnResetState() { }

        /// <summary>This beat is done; the director advances after `delay` seconds.</summary>
        protected void Complete(float delay = 0f)
        {
            if (IsActive && director != null) director.NotifyComplete(ActiveBeat, delay);
        }

        // ------------------------------------------------------------ helpers for sequences
        protected IEnumerator Wait(float seconds)
        {
            float end = Time.time + seconds;
            while (Time.time < end) yield return null;
        }

        /// <summary>
        /// Say a line now and wait until Beeswax stops talking (min..max s). unlessHive = true only for a line the Hive
        /// also answers with (a station outcome after choice.made): then the Hive's line wins if it comes within
        /// GameNet's wait. The Hive does NOT voice beat changes (hive/src/server.ts beatEntered only sets the
        /// window scene), so beat lines play at once; a Hive copy of the same line id is suppressed by the voice.
        /// </summary>
        protected IEnumerator SayAndWait(GameLine line, float minSeconds, float maxSeconds, bool unlessHive = false)
        {
            if (net != null)
            {
                if (unlessHive) net.SayUnlessHive(line);
                else net.Say(line);
            }
            float start = Time.time;
            yield return null;
            // Give the Hive / the voice a moment to start the line, then wait for the end.
            while (Time.time - start < maxSeconds)
            {
                bool talking = net != null && net.IsSpeaking;
                float t = Time.time - start;
                if (t >= minSeconds && !talking && t > 1.5f) break;
                yield return null;
            }
        }

        /// <summary>Wait for a line someone else starts (the Hive, BeeswaxStations) to finish (min..max s).</summary>
        protected IEnumerator WaitForTalk(float minSeconds, float maxSeconds)
        {
            float start = Time.time;
            yield return null;
            while (Time.time - start < maxSeconds)
            {
                float t = Time.time - start;
                bool talking = net != null && net.IsSpeaking;
                if (t >= minSeconds && !talking && t > 2f) break; // > 2 s: BeeswaxStations waits 1.5 s for the Hive
                yield return null;
            }
        }

        /// <summary>Lerp a transform's local scale with an ease-out; no allocations per frame.</summary>
        protected IEnumerator ScaleTo(Transform t, Vector3 target, float seconds)
        {
            if (t == null) yield break;
            Vector3 from = t.localScale;
            float start = Time.time;
            while (Time.time - start < seconds)
            {
                float k = Mathf.Clamp01((Time.time - start) / seconds);
                k = 1f - (1f - k) * (1f - k);
                t.localScale = Vector3.LerpUnclamped(from, target, k);
                yield return null;
            }
            t.localScale = target;
        }

        protected IEnumerator MoveTo(Transform t, Vector3 worldTarget, float seconds)
        {
            if (t == null) yield break;
            Vector3 from = t.position;
            float start = Time.time;
            while (Time.time - start < seconds)
            {
                float k = Mathf.SmoothStep(0f, 1f, (Time.time - start) / seconds);
                t.position = Vector3.LerpUnclamped(from, worldTarget, k);
                yield return null;
            }
            t.position = worldTarget;
        }

        protected static void SetActive(GameObject go, bool on)
        {
            if (go != null && go.activeSelf != on) go.SetActive(on);
        }
    }
}
