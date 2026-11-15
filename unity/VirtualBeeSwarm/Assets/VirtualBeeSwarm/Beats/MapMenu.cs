using System;
using UnityEngine;
using VirtualBeeSwarm.HandsOn;

namespace VirtualBeeSwarm
{
    sealed class MapMenu
    {
        readonly Transform _root;
        readonly GameObject _tab, _panel;

        public MapMenu(Transform parent, Action open, Action replay, Action back)
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
                new Vector3(0.41f, 0.23f, 0.012f), HandsOnKit.Wood);
            HandsOnKit.Prim(PrimitiveType.Cube, "Title Band", panel, new Vector3(0f, 0.071f, -0.006f),
                new Vector3(0.38f, 0.052f, 0.007f), HandsOnKit.Honey);
            HandsOnKit.Text("Title", panel, new Vector3(0f, 0.071f, -0.012f), "MAPS", 0.0048f, HandsOnKit.Ink);
            MapMenuButton.Build(panel, "Replay this hive", new Vector3(0f, 0.004f, -0.014f),
                new Vector2(0.30f, 0.057f), HandsOnKit.Cream, replay);
            MapMenuButton.Build(panel, "Back", new Vector3(0f, -0.062f, -0.014f),
                new Vector2(0.14f, 0.046f), HandsOnKit.Brass, back);
        }

        public void Show(bool visible, bool open)
        {
            if (visible && (!_root.gameObject.activeSelf || _panel.activeSelf != open))
            {
                Transform head = HandsOnKit.Head;
                if (head == null) return;
                Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
                forward = forward.sqrMagnitude > 0.001f ? forward.normalized : Vector3.forward;
                var position = head.position + forward * 0.52f - Vector3.up * 0.22f;
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

        public bool Interactable => isActiveAndEnabled;

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
