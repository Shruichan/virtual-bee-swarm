# Pollinator habitat props

Two small low-poly models for the Virtual Bee Swarm table environment:

- `pollinator_water_dish.obj` — shallow blue dish with raised landing stones, about 0.2 m wide.
- `solitary_bee_nest_block.obj` — timber block with visible cavity entrances, about 0.24 m tall. It illustrates one type of bee nesting site, not a home for every bee.

Each OBJ has a matching MTL palette. The runtime copies live as text resources under `unity/VirtualBeeSwarm/Assets/VirtualBeeSwarm/Dressing/Resources/Habitat/`. `DressingHabitatMesh` reads the OBJ/MTL data and builds low-poly meshes at scene start; `DressingDirector` places them beside the existing hive/nest dressing. No scene file or dartiros art slot is replaced.

Regenerate the models and preview:

```sh
python3 art/generated-habitat-props/make_habitat_props.py
python3 art/generated-habitat-props/render_habitat_preview.py
```

Audio-first cue cards and factual notes are in `field-guide-cue-cards.md`.
