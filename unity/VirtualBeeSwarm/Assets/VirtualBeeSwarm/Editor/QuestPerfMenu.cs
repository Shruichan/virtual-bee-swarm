using UnityEditor;

namespace VirtualBeeSwarm.Editor
{
    /// <summary>Play mode's headset profile (QuestPerf): automatic (on while an XR headset runs), always on, or always off.</summary>
    static class QuestPerfMenu
    {
        const string Root = "Virtual Bee Swarm/Headset Profile In Play Mode/";

        [MenuItem(Root + "Auto (on with a headset)")] static void Auto() => EditorPrefs.DeleteKey(QuestPerf.Key);
        [MenuItem(Root + "Always On")] static void ForceOn() => EditorPrefs.SetString(QuestPerf.Key, "1");
        [MenuItem(Root + "Always Off (full look)")] static void ForceOff() => EditorPrefs.SetString(QuestPerf.Key, "0");

        [MenuItem(Root + "Auto (on with a headset)", true)] static bool AutoCheck() => Check("");
        [MenuItem(Root + "Always On", true)] static bool OnCheck() => Check("1");
        [MenuItem(Root + "Always Off (full look)", true)] static bool OffCheck() => Check("0");

        static bool Check(string v)
        {
            Menu.SetChecked(Root + (v == "1" ? "Always On" : v == "0" ? "Always Off (full look)" : "Auto (on with a headset)"),
                EditorPrefs.GetString(QuestPerf.Key, "") == v);
            return true;
        }
    }
}
