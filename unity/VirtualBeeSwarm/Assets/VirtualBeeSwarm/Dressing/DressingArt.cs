using UnityEngine;

namespace VirtualBeeSwarm.Dressing
{
    /// <summary>
    /// The art the table dressing spawns at runtime (Resources/DressingArt.asset, written by
    /// Dressing/Editor/DressingArtImport.cs). Empty fields = that piece is skipped, the game is unchanged.
    /// </summary>
    public class DressingArt : ScriptableObject
    {
        public const string ResourceName = "DressingArt";

        [Tooltip("Art/WildBeeNest: dartiros' wild bee nest, base on the origin, entrance (+Z) has a DoorPoint.")]
        public GameObject nest;
        [Tooltip("Art/SolitaryBee: rigged (Legacy Animation: Hover, Walk, Blink, Antenna Twitch, Shake).")]
        public GameObject solitaryBee;
        [Tooltip("Art/Bumblebee: rigged, same clips.")]
        public GameObject bumblebee;
        [Tooltip("Art/TinyWasp: rigged, same clips.")]
        public GameObject tinyWasp;
        [Tooltip("Art/SeedPacket: SEEDS MEADOW MIX, delivered with the end card.")]
        public GameObject seedPacket;
        [Tooltip("Grass tuft mesh for the hive base (Bloom's Resources/Bloom_Grass wins when it exists).")]
        public Mesh grass;
        public Material grassMaterial;
        [Tooltip("Art/_Shared/VBS_DartVC: unlit, linear vertex colours, instancing (for Bloom_Grass).")]
        public Material vertexColorMaterial;
    }
}
