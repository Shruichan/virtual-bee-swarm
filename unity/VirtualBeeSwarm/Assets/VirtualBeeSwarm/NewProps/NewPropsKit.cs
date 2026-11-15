using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// The prefabs of dartiros' ten new props (Art/&lt;Slot&gt;/&lt;Slot&gt;.prefab, made by NewProps/Editor/NewPropsImport from
    /// "Ten New Assets.blend" via art/blender/scripts/export_dart_props.py) plus the flowers that grow in the pots.
    /// Lives at NewProps/Resources/NewPropsKit.asset so <see cref="NewProps"/> can install itself without a scene edit.
    /// </summary>
    public class NewPropsKit : ScriptableObject
    {
        public const string ResourceName = "NewPropsKit";

        public GameObject unionBadge, hiveTool, frameRest, queenBee, queenCage, nectarDroplet, wateringCan, flowerPots,
            sampleTray, moonFlag, earthFlag;
        [Tooltip("Art/Flowers/Flowers.prefab: one bunch per pot.")]
        public GameObject flowers;

        public static NewPropsKit Load() => Resources.Load<NewPropsKit>(ResourceName);
    }
}
