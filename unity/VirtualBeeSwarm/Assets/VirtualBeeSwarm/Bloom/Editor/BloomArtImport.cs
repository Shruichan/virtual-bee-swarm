using UnityEditor;

namespace VirtualBeeSwarm.Bloom.Editor
{
    /// <summary>Models under Art/Bloom/ import readable: the bloom merges the kit's flowers into one clump mesh and the
    /// vines' leaves into one vine mesh on the CPU (BloomMeshes / Vines), which needs Read/Write. An FBX imported
    /// before this script existed needs one reimport (or isReadable: 1 in its .meta).</summary>
    public class BloomArtImport : AssetPostprocessor
    {
        // after the team's ArtImportSettings (which sets isReadable = false for everything under Art/)
        public override int GetPostprocessOrder() => 100;

        void OnPreprocessModel()
        {
            if (!assetPath.Replace('\\', '/').Contains("/Art/Bloom/")) return;
            if (assetImporter is ModelImporter mi && !mi.isReadable) mi.isReadable = true;
        }
    }
}
