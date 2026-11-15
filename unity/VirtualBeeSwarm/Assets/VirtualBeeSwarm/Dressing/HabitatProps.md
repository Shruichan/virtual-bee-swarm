# Habitat props

`Resources/Habitat/` holds the water-dish and cavity-nesting-block OBJ/MTL files as `TextAsset`s. `DressingHabitatMesh` reads each pair once when `DressingDirector` starts, builds a small mesh and unlit palette materials, and adds both props beneath the live `TableAnchor`. They have no colliders and cast no shadows.

Set `dressing.habitat=false` in `tuning.json` to hide both props for an A/B run. They are separate from the articulated wild-bee nest and do not replace any `Art/<Slot>` prefab.
