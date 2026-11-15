using UnityEngine;

namespace VirtualBeeSwarm.Title
{
    /// <summary>
    /// What the title scene shows: the game logo in layers (Art/Logo, built from Daniel's sticker bees) and the meadow
    /// art. One asset, Art/Logo/Resources/TitleArt.asset, written by Title/Editor/TitleArtBuilder.
    /// </summary>
    public class TitleArt : ScriptableObject
    {
        public const string ResourceName = "TitleArt";

        [Tooltip("Honeycomb plate, peeking bees and the drip. Same 2:1 canvas as the wordmark.")]
        public Texture2D plate;
        [Tooltip("\"Virtual Bee Swarm\" with the bee on the i.")]
        public Texture2D wordmark;
        [Tooltip("The whole logo on one layer (used when a layer is missing).")]
        public Texture2D flat;
        [Tooltip("Daniel's sticker bees that fly loops around the logo.")]
        public Texture2D[] stickerBees = new Texture2D[0];
        [Tooltip("+1: the sticker looks right, -1: left (so it can face the way it flies).")]
        public int[] stickerFacing = new int[0];
        [Tooltip("Art/Flowers/Flowers.prefab: every mesh in it is one kind of flower.")]
        public GameObject flowers;
        public GameObject grass;

        public static TitleArt Load() => Resources.Load<TitleArt>(ResourceName);
    }
}
