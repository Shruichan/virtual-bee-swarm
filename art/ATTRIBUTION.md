# Virtual Bee Swarm: art attribution

Temp stylized art set picked on 2026-11-14 (art-director pass). Files live in `pop-os:~/immerse-the-bay/art-staging/final/`. Details per item are in `MANIFEST.json`.

## Summary

- **All 20 models are original**: generated for this project by procedural Blender scripts (`final/_source/scripts/`). No attribution needed.
- **The only third-party files** are Kenney's CC0 particle sprites, used as VFX textures for the smoker smoke and the fire embers. CC0 means public domain, so credit is optional (a thank-you is listed below).
- **No CC-BY asset is used**, so the game has no mandatory credit line from this art set.
- If you swap in one of the downloaded alternates listed in `MANIFEST.json` (`downloaded_alternates_not_used`), add its `attribution_text` here. The CC-BY ones REQUIRE it in the game credits.

## Models (all original)

| Item | File | Author / source | License |
|---|---|---|---|
| beeswax | `beeswax/beeswax.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |
| worker_bee | `worker_bee/worker_bee.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |
| hive | `hive/hive.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |
| hexagon_tile | `hexagon_tile/hexagon_tile.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |
| portal_frame | `portal_frame/portal_frame.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |
| smoker | `smoker/smoker.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |
| honey_jar | `honey_jar/honey_jar.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |
| syrup_bottle | `syrup_bottle/syrup_bottle.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |
| deep_bowl | `deep_bowl/deep_bowl.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |
| pebble_dish | `pebble_dish/pebble_dish.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |
| wasp | `wasp/wasp.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |
| giant_hornet | `giant_hornet/giant_hornet.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |
| ember | `ember/ember.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |
| flowers | `flowers/flowers.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |
| grass_tuft | `grass_tuft/grass_tuft.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |
| rocket | `rocket/rocket.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |
| moon | `moon/moon.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |
| earth | `earth/earth.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |
| clipboard | `clipboard/clipboard.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |
| seed_packet | `seed_packet/seed_packet.glb` (+ .fbx, .blend) | original, generated for this project | original (no attribution needed) |

Also original, generated for this project: `_shared/VBS_Palette.png` (palette texture), `portal_frame/meadow_fallback.png` (stand-in meadow picture for the portal's fallback pane), `ember/ember_sprite.png` (radial glow sprite), and every `*_preview.png` / `*_turntable.png` / `contact_sheet.png` render.

## Third-party assets

### Particle Pack (Kenney)

- **Name:** Particle Pack
- **Author:** Kenney (www.kenney.nl)
- **License:** CC0 1.0 Universal (public domain dedication). `License.txt` is copied next to the files.
- **URL:** https://kenney.nl/assets/particle-pack
- **Attribution text (optional):** Particle Pack by Kenney (www.kenney.nl), CC0 1.0
- **Files used:**
  - `smoker/vfx_kenney_cc0/`: smoke_01.png, smoke_02.png, smoke_03.png, smoke_04.png, smoke_05.png, smoke_06.png, smoke_07.png, smoke_08.png, smoke_09.png, smoke_10.png
  - `ember/vfx_kenney_cc0/`: circle_05.png, fire_01.png, fire_02.png, flame_01.png, flame_02.png, flame_03.png, flame_04.png, flame_05.png, flame_06.png, light_01.png, scorch_01.png, spark_01.png, spark_02.png, spark_03.png, spark_04.png, spark_05.png, spark_06.png, spark_07.png, star_01.png

### Font outlines baked into meshes

- The printed words on the models (HONEY, STORE BRAND, SUGAR SYRUP, LOCAL 20,000, HONEYMOON-1, SEEDS, MEADOW MIX) were set in **Fira Sans ExtraBold** (Mozilla / Carrois Apostrophe, SIL Open Font License 1.1, https://github.com/mozilla/Fira) and converted to mesh geometry. No font file is shipped. The OFL allows using the font this way; no credit is required.
- The contact sheet labels use Fira Sans Bold / Book (same license).

## Downloaded but NOT used

Everything in `art-staging/downloaded/` (111 entries, CC0 or CC-BY 3.0) stays available. Nothing from it is in this final set except the Kenney sprites above. Each entry's license, author, URL and credit text is in `art-staging/downloaded/MANIFEST.json`.

## Tools

Built and rendered with Blender 4.5.16 LTS (GPL; Blender's license does not apply to its output).
