using System.Collections;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// "Six more hives": a map of locked levels (farmland, city rooftop, desert, ...) around the hive you just saved.
    /// Pops up in the map beat and stays through field. Modeler: replace the tiles; keep the order (0 = this hive).
    /// </summary>
    public class LevelMap : BeatBehaviour
    {
        [SerializeField] GameObject mapRoot;
        [SerializeField] Transform[] tiles = new Transform[0];
        [SerializeField] BeeswaxController beeswax;
        [SerializeField] float tileStagger = 0.12f;

        Vector3 _rootScale = Vector3.one;
        Vector3[] _tileScale;

        public override bool Handles(GameBeat beat) => beat == GameBeat.Map || beat == GameBeat.Field;

        protected override void Awake()
        {
            base.Awake();
            if (beeswax == null) beeswax = FindAnyObjectByType<BeeswaxController>();
            if (mapRoot != null) _rootScale = mapRoot.transform.localScale;
            _tileScale = new Vector3[tiles.Length];
            for (int i = 0; i < tiles.Length; i++) if (tiles[i] != null) _tileScale[i] = tiles[i].localScale;
            SetActive(mapRoot, false);
        }

        protected override void OnEnter(GameBeat beat)
        {
            if (beat == GameBeat.Field && mapRoot != null && mapRoot.activeSelf) return;
            StartCoroutine(Show(beat == GameBeat.Map));
        }

        IEnumerator Show(bool animate)
        {
            SetActive(mapRoot, true);
            if (beeswax != null) beeswax.GoPresent();
            if (animate && net != null) net.Say(GameLines.Map);
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

        protected override void OnExit(GameBeat beat) => Restore(false);
        protected override void OnResetState() => Restore(false);

        void Restore(bool visible)
        {
            SetActive(mapRoot, visible);
            if (mapRoot != null) mapRoot.transform.localScale = _rootScale;
            for (int i = 0; i < tiles.Length; i++) if (tiles[i] != null) tiles[i].localScale = _tileScale[i];
        }
    }
}
