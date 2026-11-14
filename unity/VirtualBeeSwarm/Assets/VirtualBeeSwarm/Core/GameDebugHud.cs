using System.Text;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Desktop test overlay (screen-space IMGUI, not visible in the headset): beat, clocks, Hive link, window scene,
    /// caption, hum heat, and the key list. Rebuilt 4x per second only. F1 toggles it.
    /// </summary>
    public class GameDebugHud : MonoBehaviour
    {
        [SerializeField] GameDirector director;
        [SerializeField] GameNet net;
        [SerializeField] bool showInEditor = true;
        [SerializeField] bool showInPlayer;
        [SerializeField] KeyCode toggleKey = KeyCode.F1;

        const string Keys =
            "N/Space next beat   B previous   Shift+R reset   K hornet mode   O offline\n" +
            "1-3 choose   click buttons   G hold = lift hive   C place rig   RMB+WASD/QE look/move\n" +
            "H hold = fist   U hold = hum   T hold = talk   F fire   P put it out   F2 autopilot   F1 hide";

        readonly StringBuilder _sb = new StringBuilder(512);
        string _text = "";
        float _nextBuild;
        bool _visible;
        GUIStyle _style;

        void Awake()
        {
            if (director == null) director = FindAnyObjectByType<GameDirector>();
            if (net == null) net = FindAnyObjectByType<GameNet>();
            _visible = Application.isEditor ? showInEditor : showInPlayer;
#if UNITY_ANDROID && !UNITY_EDITOR
            // Headset: no screen to show IMGUI on, and an OnGUI method costs per-frame work/GC even when hidden.
            enabled = false;
#endif
        }

        void Update()
        {
            if (GameInput.KeyDown(toggleKey)) _visible = !_visible;
            if (!_visible || Time.unscaledTime < _nextBuild) return;
            _nextBuild = Time.unscaledTime + 0.25f;
            _sb.Length = 0;
            if (director != null)
            {
                _sb.Append("Beat ").Append((int)director.Beat).Append(' ').Append(GameIds.BeatName(director.Beat))
                   .Append("  (").Append(director.BeatSeconds.ToString("0")).Append(" s)   run ")
                   .Append(GameMaterials.Clock(director.RunSeconds))
                   .Append("   solved ").Append(director.SolvedStations).Append("/4   hornet ").Append(director.Hornet).Append('\n');
            }
            if (net != null)
            {
                _sb.Append(net.Online ? "Hive ONLINE " + net.HostName : (net.ForceOffline ? "Hive OFFLINE (forced, O)" : "Hive offline"))
                   .Append("   scene ").Append(string.IsNullOrEmpty(net.CurrentScene) ? "-" : net.CurrentScene)
                   .Append(net.OrbisIsLive ? " (live)" : net.OrbisTexture != null ? " (clip)" : " (local)")
                   .Append("   hum ").Append(net.HumHeat.ToString("0.00"))
                   .Append(net.Talking ? "   TALKING" : "").Append('\n');
                string cap = net.CurrentCaption;
                if (!string.IsNullOrEmpty(cap)) _sb.Append("Beeswax: ").Append(cap).Append('\n');
            }
            _sb.Append(Keys);
            _text = _sb.ToString();
        }

        void OnGUI()
        {
            if (!_visible) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, wordWrap = true };
                _style.normal.textColor = Color.white;
            }
            GUI.Box(new Rect(8, 8, Mathf.Min(760, Screen.width - 16), 118), _text, _style);
        }
    }
}
