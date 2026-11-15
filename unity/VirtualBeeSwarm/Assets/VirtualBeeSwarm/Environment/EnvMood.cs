using UnityEngine;

namespace VirtualBeeSwarm.EnvironmentArt
{
    /// <summary>
    /// What the window's scene does to the room: the island and the vines take the window's health (tint, how open
    /// the flowers are, the full-bloom extras), and the window throws its light (shafts, motes, petals) in its colour.
    /// "The window shows what your choices cause": the table answers too.
    /// </summary>
    public struct EnvMood
    {
        public Color tint;     // multiplies the island / flowers / vines vertex colours
        public float bloom;    // flower size (1 = as modelled); below ~0.7 they droop
        public float extra;    // the full-bloom extras (sunflowers...) 0..1
        public float sunny;    // window light: shafts, petals, motes (0..1.4)
        public Color light;    // its colour
        public bool fire;      // flicker

        static EnvMood M(Color tint, float bloom, float extra, float sunny, Color light, bool fire = false) =>
            new EnvMood { tint = tint, bloom = bloom, extra = extra, sunny = sunny, light = light, fire = fire };

        static readonly Color Warm = new Color(1f, 0.92f, 0.7f);

        /// <summary>The look for a window scene. "" = before the window has a scene (the hive is sick: a bit faded).</summary>
        public static bool TryFor(string scene, out EnvMood m)
        {
            switch (scene)
            {
                case "": m = M(new Color(0.94f, 0.92f, 0.84f), 0.75f, 0f, 0.55f, Warm); return true;
                case GameIds.Scene.Meadow: m = M(Color.white, 0.9f, 0f, 1f, Warm); return true;
                case GameIds.Scene.ForecastPeaceful: m = M(Color.white, 0.9f, 0f, 0.9f, new Color(1f, 0.95f, 0.85f)); return true;
                case GameIds.Scene.ForecastRavaged: m = M(new Color(1f, 0.93f, 0.84f), 0.8f, 0f, 0.9f, new Color(1f, 0.45f, 0.2f)); return true; // Paris burns, not our table (yet)
                case GameIds.Scene.SmokerAngry: m = M(new Color(1f, 0.86f, 0.72f), 0.8f, 0f, 0.35f, new Color(1f, 0.6f, 0.35f)); return true;
                case GameIds.Scene.SmokerSettle: m = M(Color.white, 0.95f, 0f, 1f, Warm); return true;
                case GameIds.Scene.FeedSick: m = M(new Color(0.95f, 0.78f, 0.55f), 0.42f, 0f, 0.15f, new Color(0.8f, 0.8f, 0.7f)); return true;
                case GameIds.Scene.FeedRecover: m = M(new Color(1f, 1.04f, 1f), 1f, 0f, 1f, Warm); return true;
                case GameIds.Scene.WaspsRaid: m = M(new Color(0.95f, 0.85f, 0.7f), 0.8f, 0f, 0.3f, new Color(0.9f, 0.75f, 0.4f)); return true;
                case GameIds.Scene.WaspsLeave: m = M(Color.white, 1f, 0f, 1f, Warm); return true;
                case GameIds.Scene.HeatStress: m = M(new Color(1.15f, 0.98f, 0.58f), 0.58f, 0f, 1.4f, new Color(1f, 0.97f, 0.88f)); return true;
                case GameIds.Scene.HeatDrink: m = M(new Color(0.95f, 1.02f, 1.05f), 1f, 0f, 0.9f, new Color(0.85f, 0.95f, 1f)); return true;
                case GameIds.Scene.WhatIfFire: m = M(new Color(1.05f, 0.46f, 0.3f), 0.25f, 0f, 1.1f, new Color(1f, 0.42f, 0.12f), true); return true;
                case GameIds.Scene.FireOut: m = M(new Color(0.64f, 0.66f, 0.68f), 0.35f, 0f, 0.25f, new Color(0.7f, 0.75f, 0.8f)); return true;
                case GameIds.Scene.WhatIfRain: m = M(new Color(0.86f, 1.05f, 1f), 1f, 0f, 0.3f, new Color(0.7f, 0.8f, 1f)); return true;
                case GameIds.Scene.WhatIfFlowers: m = M(Color.white, 1.15f, 1f, 1f, new Color(1f, 0.9f, 0.85f)); return true;
                case GameIds.Scene.WhatIfSpray: m = M(new Color(0.9f, 0.84f, 0.7f), 0.4f, 0f, 0.4f, new Color(0.85f, 0.85f, 0.75f)); return true;
                case GameIds.Scene.WhatIfWinter: m = M(new Color(1.35f, 1.2f, 1.45f), 0.2f, 0f, 0.4f, new Color(0.85f, 0.92f, 1f)); return true;
                case GameIds.Scene.FullBloom: m = M(new Color(1.02f, 1.05f, 1f), 1.15f, 1f, 1.1f, Warm); return true;
                default: m = default; return false; // moon (MeadowIsland's own stage), whatif_custom: keep the current look
            }
        }

        public static EnvMood Lerp(EnvMood a, EnvMood b, float t) => new EnvMood
        {
            tint = Color.LerpUnclamped(a.tint, b.tint, t),
            bloom = Mathf.Lerp(a.bloom, b.bloom, t),
            extra = Mathf.Lerp(a.extra, b.extra, t),
            sunny = Mathf.Lerp(a.sunny, b.sunny, t),
            light = Color.LerpUnclamped(a.light, b.light, t),
            fire = t < 0.5f ? a.fire : b.fire,
        };
    }
}
