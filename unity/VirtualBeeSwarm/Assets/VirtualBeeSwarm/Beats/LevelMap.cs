using System.Collections;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// "Six more hives": a map of locked levels (farmland, city rooftop, desert, ...) around the hive you just saved.
    /// Pops up in the map beat and stays through field. Modeler: replace the tiles; keep the order (0 = this hive).
    /// Ride script v2 (UNLOAD: the truth and your contract): room_truth, room_meet_stat, room_map as the map lands
    /// (cut-if-behind #3: then the map lands silently), room_end_contract as a crew delivers the score card again with
    /// the contract on it and holds it in front of the player. UnloadDone after that; ASK BEESWAX can follow (RideDirector).
    /// </summary>
    public class LevelMap : BeatBehaviour
    {
        [SerializeField] GameObject mapRoot;
        [SerializeField] Transform[] tiles = new Transform[0];
        [SerializeField] BeeswaxController beeswax;
        [SerializeField] float tileStagger = 0.12f;

        Vector3 _rootScale = Vector3.one;
        EscapeSequence _escape;

        /// <summary>The unload's lines are done (room_end_contract said, the end card is out).</summary>
        public bool UnloadDone { get; private set; }
        Vector3[] _tileScale;

        public override bool Handles(GameBeat beat) => beat == GameBeat.Map || beat == GameBeat.Field;

        protected override void Awake()
        {
            base.Awake();
            if (beeswax == null) beeswax = FindAnyObjectByType<BeeswaxController>();
            _escape = FindAnyObjectByType<EscapeSequence>();
            if (mapRoot != null) _rootScale = mapRoot.transform.localScale;
            _tileScale = new Vector3[tiles.Length];
            for (int i = 0; i < tiles.Length; i++) if (tiles[i] != null) _tileScale[i] = tiles[i].localScale;
            SetActive(mapRoot, false);
        }

        protected override void OnEnter(GameBeat beat)
        {
            if (beat == GameBeat.Field && mapRoot != null && mapRoot.activeSelf) return;
            if (beat == GameBeat.Map) StartCoroutine(Unload());
            else StartCoroutine(Show(false));
        }

        IEnumerator Unload()
        {
            UnloadDone = false;
            if (beeswax != null) beeswax.GoPresent(); // drops the act: Talk only
            yield return Say(RideLines.Truth);
            yield return Say(RideLines.MeetStat);
            var map = GameLines.Map;
            bool cut = RidePacing.Cut(3, map);
            StartCoroutine(Show(true)); // the map of six more hives lands
            if (!cut) yield return Say(map);
            SetActive(mapRoot, false);
            if (_escape != null) _escape.DeliverEndCard();
            yield return Say(RideLines.EndContract);
            UnloadDone = true;
            Debug.Log("[VBS][Ride] unload done: the end card is out (ASK BEESWAX: J or the Board's room_ask_anything)");
        }

        void LateUpdate()
        {
            var ride = RideDirector.Instance;
            if (ride == null || !ride.AskShown) return;
            SetActive(mapRoot, false);
            if (_escape != null) _escape.HideEndCard();
        }

        IEnumerator Say(GameLine line)
        {
            float len = LineTime.Seconds(net, line);
            yield return SayAndWait(line, Mathf.Min(1f, len), len + 1.5f);
        }

        void ShowTitle(bool on)
        {
            var title = mapRoot != null ? mapRoot.transform.Find("Title") : null;
            if (title != null) title.gameObject.SetActive(on);
        }

        IEnumerator Show(bool animate)
        {
            SetActive(mapRoot, true);
            ShowTitle(true);
            if (beeswax != null) beeswax.GoPresent();
            if (!animate || mapRoot == null) yield break;
            for (int i = 0; i < tiles.Length; i++) if (tiles[i] != null) tiles[i].localScale = Vector3.zero;
            mapRoot.transform.localScale = _rootScale * 0.2f;
            yield return ScaleTo(mapRoot.transform, _rootScale, 0.4f);
            for (int i = 0; i < tiles.Length; i++)
            {
                if (tiles[i] != null) StartCoroutine(ScaleTo(tiles[i], _tileScale[i], 0.3f));
                yield return Wait(tileStagger);
            }
        }

        protected override void OnExit(GameBeat beat)
        {
            Restore(false);
            if (beat == GameBeat.Map && _escape != null) _escape.HideEndCard();
        }

        protected override void OnResetState()
        {
            UnloadDone = false;
            Restore(false);
            if (_escape != null) _escape.HideEndCard();
        }

        void Restore(bool visible)
        {
            SetActive(mapRoot, visible);
            if (mapRoot != null) mapRoot.transform.localScale = _rootScale;
            for (int i = 0; i < tiles.Length; i++) if (tiles[i] != null) tiles[i].localScale = _tileScale[i];
        }
    }
}
