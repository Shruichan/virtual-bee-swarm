# Bee Scout meadow environment pack

Three standalone low-poly meshes for optional AR meadow dressing:

- `meadow_flowers.obj` — a three-flower clump with a daisy, lilac bloom, pink flower, stems, and leaves; about 0.4 m wide by 0.6 m tall.
- `soft_grass_tuft.obj` — a rounded fan of thirteen broad grass blades; about 0.4 m tall.
- `flowering_vine.obj` — a gently winding stem, alternating leaves, and three flower heads; about 0.85 m tall.

Each OBJ has a matching MTL palette. Models use meters, +Y up, and +Z toward their flower faces. They are intentionally standalone meshes so they can be brought into Unity without touching the claimed `Art/<Slot>/` production assets. Copy each OBJ and its matching MTL into a new Unity art folder; Unity will generate import metadata when it first imports the model. They are not yet placed in a scene or wired to the environment builder.

Regenerate and preview with:

```sh
python3 art/generated-environment/make_environment.py
python3 art/generated-environment/render_preview.py
```
