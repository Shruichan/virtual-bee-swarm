# Bee Scout bonus props

Two original, low-poly props generated for optional future Unity use:

- `pocket_bee.obj` — tiny honey bee with readable stripes, broad pale-blue wings, antennae, six legs, and a small face.
- `ember_buddy.obj` — crossed firewood, a layered flame character, a pebble ring, and floating sparks.

Both models use meters, +Y up, and +Z forward. The bee is about 0.43 m long. The campfire fits in a roughly 0.55 m tall by 0.55 m wide area. Material colors are in the paired `.mtl` files. The materials are intentionally opaque and matte so they import cleanly as lightweight OBJ meshes.

To preview/import in Unity, copy one `.obj` and its same-named `.mtl` into a new Unity art folder. Unity will create the model import metadata on first import; drag the imported model into a scene and adjust its transform for the desired prop scale. These are standalone model meshes, not production prefabs, and do not alter the claimed `Art/<Slot>/` assets.

Regenerate both meshes with:

```sh
python3 art/generated-props/make_props.py
```

The sticker-sheet art is tracked separately and will use the Penguinbook bee reference when it is available.
