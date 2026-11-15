using System;
using UnityEngine;
using VirtualBeeSwarm.HandsOn;

namespace VirtualBeeSwarm
{
    sealed class MapMenu
    {
        readonly Transform _root;
        readonly GameObject _tab, _panel, _maps, _confirm;

        public MapMenu(Transform parent, Transform[] tiles, Action open, Action replay, Action back)
        {
            _root = new GameObject("[VBS] Maps Menu").transform;
            _root.SetParent(parent, true);
            _root.gameObject.SetActive(false);
            _tab = MapMenuButton.Build(_root, "Maps", new Vector3(-0.24f, 0.02f, 0f),
                new Vector2(0.115f, 0.052f), HandsOnKit.Honey, open);
            var panel = new GameObject("Maps Panel").transform;
            panel.SetParent(_root, false);
            _panel = panel.gameObject;
            HandsOnKit.Prim(PrimitiveType.Cube, "Frame", panel, Vector3.zero,
                new Vector3(0.52f, 0.33f, 0.012f), HandsOnKit.Wood);
            HandsOnKit.Prim(PrimitiveType.Cube, "Title Band", panel, new Vector3(0f, 0.126f, -0.006f),
                new Vector3(0.49f, 0.053f, 0.007f), HandsOnKit.Honey);
            HandsOnKit.Text("Title", panel, new Vector3(0f, 0.126f, -0.013f), "MAPS", 0.0048f, HandsOnKit.Ink);

            _maps = new GameObject("Hive List");
            _maps.transform.SetParent(panel, false);
            HandsOnKit.Text("Current Hive", _maps.transform, new Vector3(-0.105f, 0.062f, -0.013f),
                "Current hive\nPlayable", 0.0034f, HandsOnKit.Cream);
            MapMenuButton.Build(_maps.transform, "Replay this hive", new Vector3(0.104f, 0.063f, -0.014f),
                new Vector2(0.204f, 0.046f), HandsOnKit.Honey, () => SetConfirmation(true));
            HandsOnKit.Prim(PrimitiveType.Cube, "Divider", _maps.transform, new Vector3(0f, 0.029f, -0.007f),
                new Vector3(0.46f, 0.0015f, 0.003f), HandsOnKit.HoneyDim);
            HandsOnKit.Text("Preview Label", _maps.transform, new Vector3(0f, 0.007f, -0.013f),
                "FUTURE HIVES · PREVIEW ONLY", 0.003f, HandsOnKit.Cream);
            for (int i = 1; i < tiles.Length; i++)
                BuildPreview(_maps.transform, tiles[i], i - 1);
            MapMenuButton.Build(_maps.transform, "Back", new Vector3(0f, -0.127f, -0.014f),
                new Vector2(0.14f, 0.045f), HandsOnKit.Brass, back);

            _confirm = new GameObject("Confirm Replay");
            _confirm.transform.SetParent(panel, false);
            HandsOnKit.Text("Confirm Title", _confirm.transform, new Vector3(0f, 0.045f, -0.013f),
                "Replay this hive?", 0.0041f, HandsOnKit.Cream);
            HandsOnKit.Text("Confirm Explanation", _confirm.transform, new Vector3(0f, -0.008f, -0.013f),
                "The ride starts over.", 0.0032f, HandsOnKit.Cream);
            MapMenuButton.Build(_confirm.transform, "Cancel", new Vector3(-0.108f, -0.08f, -0.014f),
                new Vector2(0.171f, 0.047f), HandsOnKit.Brass, () => SetConfirmation(false));
            MapMenuButton.Build(_confirm.transform, "Replay", new Vector3(0.108f, -0.08f, -0.014f),
                new Vector2(0.171f, 0.047f), HandsOnKit.Honey, replay);
            SetConfirmation(false);
        }

        static void BuildPreview(Transform parent, Transform source, int index)
        {
            if (source == null) return;
            TextMesh title = source.GetComponentInChildren<TextMesh>(true);
            string label = title != null ? title.text.Replace("LOCKED", "").Replace('\n', ' ').Trim() : "";
            if (label.Length == 0 || label == "???") label = "Unrevealed";
            var position = new Vector3((index % 3 - 1) * 0.155f, -0.035f - index / 3 * 0.040f, -0.012f);
            if (source.TryGetComponent(out MeshFilter mesh) && mesh.sharedMesh != null &&
                source.TryGetComponent(out MeshRenderer art))
            {
                var icon = new GameObject("Preview Hex");
                icon.transform.SetParent(parent, false);
                icon.transform.localPosition = position + Vector3.left * 0.065f + Vector3.forward * 0.003f;
                float size = Mathf.Max(0.001f, mesh.sharedMesh.bounds.size.x);
                icon.transform.localScale = Vector3.one * (0.022f / size);
                icon.AddComponent<MeshFilter>().sharedMesh = mesh.sharedMesh;
                var face = icon.AddComponent<MeshRenderer>();
                face.sharedMaterial = art.sharedMaterial;
                face.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                face.receiveShadows = false;
                GameMaterials.Tint(face, HandsOnKit.Brass);
            }
            HandsOnKit.Text("Preview " + label, parent, position + Vector3.right * 0.014f,
                label, 0.0026f, HandsOnKit.Cream);
        }

        void SetConfirmation(bool on)
        {
            _maps.SetActive(!on);
            _confirm.SetActive(on);
        }

        public void Show(bool visible, bool open)
        {
            if ((!visible || !open) && _confirm.activeSelf) SetConfirmation(false);
            if (visible && (!_root.gameObject.activeSelf || _panel.activeSelf != open))
            {
                Transform head = HandsOnKit.Head;
                if (head == null) return;
                Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
                forward = forward.sqrMagnitude > 0.001f ? forward.normalized : Vector3.forward;
                float distance = open ? 0.63f : 0.59f;
                var position = head.position + forward * distance - Vector3.up * (open ? 0.27f : 0.20f);
                position.y = Mathf.Min(position.y, Bloom.MeadowLayout.FloorY + 1.30f);
                _root.SetPositionAndRotation(position, Quaternion.LookRotation(forward, Vector3.up));
            }
            _tab.SetActive(visible && !open);
            _panel.SetActive(visible && open);
            _root.gameObject.SetActive(visible);
        }
    }

    sealed class MapMenuButton : MonoBehaviour, IGameTarget
    {
        readonly PokeContacts _contacts = new();
        Action _click;
        Renderer _face;
        Color _color;
        float _readyAt;

        public bool Interactable => isActiveAndEnabled && Time.unscaledTime >= _readyAt;

        public static GameObject Build(Transform parent, string label, Vector3 position, Vector2 size, Color color, Action click)
        {
            var button = new GameObject(label);
            button.transform.SetParent(parent, false);
            button.transform.localPosition = position;
            var view = button.AddComponent<MapMenuButton>();
            view._face = HandsOnKit.Prim(PrimitiveType.Cube, "Face", button.transform, Vector3.zero,
                new Vector3(size.x, size.y, 0.007f), color).GetComponent<Renderer>();
            view._color = color;
            view._click = click;
            var target = button.AddComponent<BoxCollider>();
            target.isTrigger = true;
            target.size = new Vector3(size.x, size.y, 0.036f);
            HandsOnKit.Text("Label", button.transform, Vector3.back * 0.006f, label.ToUpperInvariant(), 0.0032f, HandsOnKit.Ink);
            return button;
        }

        public void SetHover(bool hovered) => GameMaterials.Tint(_face, hovered ? Color.Lerp(_color, Color.white, 0.42f) : _color);

        public void Click()
        {
            if (Interactable) _click?.Invoke();
        }

        void OnEnable() => _readyAt = Time.unscaledTime + 0.35f;

        void OnDisable()
        {
            _contacts.Clear();
            SetHover(false);
        }

        void OnTriggerEnter(Collider other)
        {
            if (Interactable && _contacts.Enter(other)) Click();
        }

        void OnTriggerExit(Collider other) => _contacts.Exit(other);
    }
}
