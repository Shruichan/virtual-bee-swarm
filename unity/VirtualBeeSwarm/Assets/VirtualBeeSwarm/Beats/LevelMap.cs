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
        static LevelMap _openMaps;
        MapMenu _menu;
        bool _returnToAsk, _returnToContract, _returnToPreview;

        public static bool MapsMenuOpen => _openMaps != null;

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
            var honey = Pals.HoneyFrame.Instance;
            bool askShown = ride != null && ride.AskShown;
            if (_openMaps == this)
            {
                if (honey != null && honey.Visible) CloseMaps(restore: false);
                else if (Ask.AskBeeswax.Instance is Ask.AskBeeswax ask && ask.IsOpen) ask.Close("Maps open");
            }
            if (askShown || (honey != null && honey.Visible) || _openMaps == this)
            {
                SetActive(mapRoot, false);
                if (_escape != null && _escape.EndCardShown) _escape.HideEndCard();
            }

            bool open = _openMaps == this;
            bool visible = open || CanOpenMaps();
            if (visible && _menu == null) _menu = new MapMenu(transform, OpenMaps, ReplayHive, BackFromMaps);
            _menu?.Show(visible, open);
        }

        bool CanOpenMaps()
        {
            if (!IsActive || !UnloadDone || director == null || !Handles(director.Beat)) return false;
            var honey = Pals.HoneyFrame.Instance;
            if (honey != null && honey.BlocksAsk) return false;
            var ride = RideDirector.Instance;
            var ask = Ask.AskBeeswax.Instance;
            return ride == null || !ride.AskShown || (ask != null && ask.IsOpen);
        }

        void OpenMaps()
        {
            if (MapsMenuOpen || !CanOpenMaps()) return;
            _openMaps = this;
            var ask = Ask.AskBeeswax.Instance;
            _returnToAsk = ask != null && ask.IsOpen;
            _returnToContract = _escape != null && _escape.EndCardShown;
            _returnToPreview = mapRoot != null && mapRoot.activeSelf;
            if (_returnToAsk) ask.Close("Maps open");
            if (_returnToContract) _escape.HideEndCard();
            SetActive(mapRoot, false);
            _menu.Show(true, true);
        }

        void BackFromMaps() => CloseMaps(restore: true);

        void ReplayHive()
        {
            if (_openMaps != this || director == null) return;
            CloseMaps(restore: false);
            director.ResetToPre();
        }

        void CloseMaps(bool restore)
        {
            if (_openMaps != this) return;
            _openMaps = null;
            _menu?.Show(false, false);
            if (restore && IsActive && director != null && Handles(director.Beat))
            {
                if (_returnToAsk) Ask.AskBeeswax.Instance?.Open("Maps back");
                else if (_returnToContract && _escape != null) _escape.DeliverEndCard();
                else if (_returnToPreview) Restore(true);
            }
            _returnToAsk = _returnToContract = _returnToPreview = false;
        }

        void OnDisable()
        {
            CloseMaps(restore: false);
            _menu?.Show(false, false);
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
            CloseMaps(restore: false);
            _menu?.Show(false, false);
            Restore(false);
            if (beat == GameBeat.Map && _escape != null) _escape.HideEndCard();
        }

        protected override void OnResetState()
        {
            CloseMaps(restore: false);
            _menu?.Show(false, false);
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
