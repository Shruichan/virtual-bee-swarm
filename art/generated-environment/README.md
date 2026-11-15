# Bee Scout meadow environment pack

Three standalone low-poly meshes for optional AR meadow dressing:

- `meadow_flowers.obj` — a three-flower clump with a daisy, lilac bloom, pink flower, stems, and leaves; about 0.4 m wide by 0.6 m tall.
- `soft_grass_tuft.obj` — a rounded fan of thirteen broad grass blades; about 0.4 m tall.
- `flowering_vine.obj` — a gently winding stem, alternating leaves, and three flower heads; about 0.85 m tall.
- `pollinator_water_dish.obj` — a shallow blue dish with five raised landing stones; about 0.2 m wide.
- `solitary_bee_nest_block.obj` — a small timber cavity-nesting block with visible tunnel openings; about 0.2 m tall. It illustrates one nesting type, not a universal bee home.

Each OBJ has a matching MTL palette. Models use meters, +Y up, and +Z toward their flower faces. They are intentionally standalone meshes so they can be brought into Unity without touching the claimed `Art/<Slot>/` production assets. Copy each OBJ and its matching MTL into a new Unity art folder; Unity will generate import metadata when it first imports the model. They are not yet placed in a scene or wired to the environment builder.

Regenerate and preview the flower pack with:

```sh
python3 art/generated-environment/make_environment.py
python3 art/generated-environment/render_preview.py
```

Regenerate and preview the habitat props with:

```sh
python3 art/generated-environment/make_habitat_props.py
python3 art/generated-environment/render_habitat_preview.py
```

The models are deliberately standalone and are not yet placed in the Unity scene. Audio-first copy and factual notes are in `field-guide-cue-cards.md`.
