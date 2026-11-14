"""Write final/MANIFEST.json and final/ATTRIBUTION.md for the picked art set.

    python3 -P build_manifest.py STAGING_DIR
Reads final/_work/out/stats/<item>.json (tris, objects, pivots), final/_work/bounds.json (from contact_sheet.py)
and downloaded/MANIFEST.json (for the downloaded alternates that were NOT picked).
"""
import glob
import json
import os
import sys

st = os.path.abspath(sys.argv[1])
fin = os.path.join(st, "final")
stats_dir = os.path.join(fin, "_work", "out", "stats")
with open(os.path.join(fin, "_work", "bounds.json")) as fh:
    BOUNDS = json.load(fh)
with open(os.path.join(st, "downloaded", "MANIFEST.json")) as fh:
    DL = json.load(fh)

ORIGINAL = "original, generated for this project (procedural Blender script; no attribution needed)"
KENNEY = dict(name="Particle Pack", author="Kenney (www.kenney.nl)", license="CC0 1.0 (public domain)",
              url="https://kenney.nl/assets/particle-pack",
              attribution_text="Particle Pack by Kenney (www.kenney.nl), CC0 1.0 (credit optional)")

# what moves / how to hook it up in Unity, per item
INFO = {
    "beeswax": dict(
        animate="Body (empty): BeeswaxController scales it while talking. Mouth: pivot on its top edge, scale local Y "
                "to open (Y=0 closed). WingL / WingR: pivot at the wing root, rotate on Unity Z to flap. Empties: "
                "HandR (holds the Clipboard), HandL, CaptionAnchor.",
        notes="No skeleton; everything animates by transforms, like the placeholder. Names match "
              "BuildHoneymoonScene.cs (Mouth, WingL, WingR). For BeeswaxController.dustyRenderers assign the Torso and "
              "Head renderers (as the placeholder does). Clipboard is a separate child mesh (hide/swap freely). "
              "~0.38 m tall incl. antennae. Bee material is slightly emissive for passthrough."),
    "worker_bee": dict(
        animate="None as transforms: ONE mesh, ONE material/submesh for GPU instancing (x150). The two wings are part "
                "of the mesh; flap them in a vertex shader if wanted (wing verts are the sky-blue palette cell).",
        notes="~3.5 cm long, +Z forward, pivot = body center. FBX carries vertex colours (Honeymoon/Unlit); "
              "GLB uses the palette texture. Meets S11.3 (<=300 tris)."),
    "hive": dict(
        animate="LiftRoot: rotate/raise to tip or heft the hive (everything below hangs off it). Lid: pivot on the "
                "back-bottom edge, rotate on X to open or move up to lift. Door: pivot at the entrance, scale.x = "
                "entrance width (HiveBody 'Door widths (scale.x)'). EntranceReducer: separate wooden cleat resting on "
                "the landing board, pivot at its base: slide it into the slot at station 3. HeatGlow: soft glow box, "
                "disabled by default, enable for the heat wave. HexSlot1-4 (under Hexagons): parent hexagon_tile "
                "instances here. DoorPoint, SwarmCenter: empties kept from the placeholder.",
        notes="Entrance faces +Z. The placeholder scene's door faces -Z, so if the slot loader keeps the placeholder's "
              "orientation, rotate the prefab 180 deg on Y. BoxUpper is open on top with frames + comb inside "
              "(visible when the Lid lifts). 'LOCAL 20,000' plaque on the brood box (small text)."),
    "smoker": dict(
        animate="BellowsLever: pivot on its bottom hinge, rotate on X to squeeze (Bellows stays put). Lid: pivot on "
                "the back hinge, rotate on X to open. SmokePoint: empty at the spout tip, +Z points out of the spout: "
                "parent the smoke particle system here.",
        notes="Art-director pass: darker steel can, copper lid + spout, red leather bellows with accordion pleats "
              "(no longer reads as a book). Smoke sprites: smoker/vfx_kenney_cc0/smoke_01..10.png (CC0)."),
    "honey_jar": dict(
        animate="Lid: pivot at its base, unscrew (rotate Y + lift). Label: separate mesh.",
        notes="WRONG choice at the feeding station. Big 'HONEY' label; 'STORE BRAND' is small text."),
    "syrup_bottle": dict(
        animate="Cap: pivot at its base (flip/squeeze gesture). Label: separate mesh.",
        notes="RIGHT choice at the feeding station. 'SUGAR SYRUP' label readable at arm's length."),
    "deep_bowl": dict(
        animate="Water: pivot at the water-surface center (bob / ripple / scale).",
        notes="WRONG choice at the heat-wave station (bees drown in deep water)."),
    "pebble_dish": dict(
        animate="Water: separate mesh. LandingSpot1-9: empties on the pebble tops where bees land to drink.",
        notes="RIGHT choice at the heat-wave station. Shallow terracotta saucer."),
    "wasp": dict(
        animate="WingL / WingR: pivot at the wing root, flap on Unity Z. Body is static.",
        notes="~6 cm long, lemon-yellow + black, angry brows. Emissive bee material."),
    "giant_hornet": dict(
        animate="Mandibles: pivot at the jaw hinge, rotate on X to chomp. WingL / WingR: pivot at the wing root, "
                "flap on Unity Z.",
        notes="Art-director pass: scaled to ~0.26 m long (was 0.33) and the mandibles now hang below the chin like "
              "pincers instead of reading as a moustache. Faceted/flat-shaded so it reads harder than the bees."),
    "rocket": dict(
        animate="Flame: pivot at the nozzle exit, disabled by default; enable and scale Y for the launch. Seat (where "
                "Beeswax sits), Perch, CountdownDisplay: empties kept from the placeholder (CountdownDisplay is where "
                "the placeholder's 'T-4:00' text goes).",
        notes="HONEYMOON-1. Hull is 0.595 m tall; bounds incl. the hidden Flame are 0.733 m (the flame hangs below "
              "the base). Name painted on the hull is small text."),
    "portal_frame": dict(
        animate="LivePane: front pane for the live Orbis video (put the video texture here; disabled in previews). "
                "FallbackPane: same size, 4 mm behind, with meadow_fallback.png. Glow: soft halo behind the frame "
                "(alpha 0.35, disabled in previews). Embers: empty at the pane center, +Z points out of the window "
                "toward the player: parent the ember VFX here.",
        notes="Hexagonal honeycomb ring on two feet, ~0.79 m wide x 0.80 m tall (0.94 m wide incl. the Glow halo). "
              "Pane UVs show the centered 65% of a 16:9 frame (u 0.175..0.825), no stretching. The placeholder used a "
              "0.52 x 0.30 m quad."),
    "hexagon_tile": dict(
        animate="Two states side by side: HexTile (cream Face = not done) and HexTile_Lit (glowing Face_Lit with the "
                "Glow material = done). Toggle between them, or swap the Face material, when a station is solved.",
        notes="Pointy-top, 4.4 cm across, faces +Z, pivot = center of the back face. Fits HexSlot1-4 on the hive. "
              "Each state is its own root object with its own pivot, 3 cm left/right of the file origin: zero its "
              "position when you make the prefab."),
    "moon": dict(
        animate="None (one mesh). Pivot = center.",
        notes="Art-director pass: deeper crater bowls, light rims and dark floors so the craters read (was flat grey "
              "patches). 0.5 m across."),
    "earth": dict(
        animate="Clouds: child mesh, spin slowly on Y.",
        notes="0.2 m globe: ocean, raised continents, ice caps. Pivot = center."),
    "flowers": dict(
        animate="Three separate meshes: Flower_Purple, Flower_Blue, Flower_Lavender, pivot at each stem base "
                "(sway by rotating a few degrees).",
        notes="They sit side by side in the file; split them into 3 prefabs or scatter copies."),
    "grass_tuft": dict(animate="None (one mesh).", notes="~8 cm tall, two greens."),
    "ember": dict(
        animate="Ember (1.2 cm glowing ball) and EmberFlake (shard): meshes for a mesh particle system, Glow "
                "material, pivot = center of each.",
        notes="ember/ember_sprite.png = 64 px radial glow sprite (original). ember/vfx_kenney_cc0/ = Kenney "
              "spark/flame/fire/light sprites (CC0, 512 px, white/greyscale: tint to fire #FF6B2C in the particle "
              "system)."),
    "clipboard": dict(
        animate="None (one mesh).",
        notes="Standalone copy of Beeswax's spreadsheet clipboard (beeswax.glb already contains its own)."),
    "seed_packet": dict(animate="None (one mesh).", notes="Optional prop. Flat card with crimped top."),
    # ---- v3.1 props (2026-11-14): delivered by bee crews; V15 normalizes every choice prop to a 12 cm box
    "swatter": dict(
        animate="None (one mesh).",
        notes="WRONG choice at the smoker station. Red head with a hole grid on a long handle, ~0.24 m tall; pivot "
              "at the bottom of the grip."),
    "spray_bottle": dict(
        animate="Sprayer: pivot at its base on the neck. SprayPoint (empty) = nozzle tip, +Z = spray direction "
                "(aim the WaterPuff particles from it).",
        notes="WRONG choice at the smoker station: plain WATER, blue bottle, white trigger sprayer."),
    "crowbar": dict(
        animate="None (one mesh).",
        notes="WRONG choice at the wasps station (BIGGER door). Red hex bar, gooseneck hook toward +X, steel tips."),
    "reducer": dict(
        animate="None (one mesh).",
        notes="RIGHT choice at the wasps station (SMALLER door). Standalone, chunkier copy of hive.fbx's "
              "EntranceReducer (0.20 m long) with arrows pointing at the notch."),
    "rain_cloud": dict(
        animate="Drops: child mesh, hide it when it isn't raining. RainPoint (empty) = bottom center of the cloud; "
                "emit rain from it straight down.",
        notes="The fire brigade (V18, SHOULD): flown in by a crown of 6. Pivot = cloud center (it floats)."),
    "stamp_approved": dict(
        animate="Scale pop 0 -> 1.2 -> 1 in 120 ms on the pick (V15).",
        notes="Green rubber-stamp mark, 0.11 x 0.042 m, pivot = center, faces +Z."),
    "stamp_grievance": dict(
        animate="Scale pop 0 -> 1.2 -> 1 in 120 ms on the pick (V15).",
        notes="Red rubber-stamp mark, 0.11 x 0.042 m, pivot = center, faces +Z."),
    "clipboard_sign": dict(
        animate="None. PokePoint (empty) sits on the signature line: put the START collider there.",
        notes="The SIGN HERE clipboard = START (V14), held still by 4 bees. Same board as clipboard.fbx, "
              "0.12 x 0.16 m."),
    "score_card": dict(
        animate="None. TextAnchor (empty, just in front of the face): parent the result TextMesh to it.",
        notes="Flown in at the escape (V16). Dark certificate 0.46 x 0.14 m with an OFFICIAL RESULT tab; pivot = "
              "card center."),
}

# downloaded options NOT picked (kept on pop-os for darter to swap in; add their credit if used)
ALT = {
    "beeswax": ["beeswax/armabee_42djT5zJnx.glb", "beeswax/bee_enemy_6HsU6GQt3a.glb"],
    "worker_bee": ["worker_bee/bee_HvfIku26CK_dec300.glb"],
    "wasp": ["wasp/wasp_3aQgc75sUR.glb"],
    "giant_hornet": ["giant_hornet/wasp_3aQgc75sUR.glb"],
    "rocket": ["rocket/oga_retro-toy-rocket/retro_toy_rocket_dec2900.glb", "rocket/rocketship_ah8WSBbizA6.glb"],
    "moon": ["moon/moon_63c8LKpoXTO.glb"],
    "earth": ["earth/earth_1I5ip-3VOfv.glb"],
    "honey_jar": ["honey_jar/jar_of_jam_3jQoqeTyeLC.glb", "honey_jar/kenney_food-kit/honey.glb"],
    "syrup_bottle": ["syrup_bottle/ketchup_qBZO8zHrN7.glb"],
    "deep_bowl": ["deep_bowl/bowl_c8KfkHCEnu.glb"],
    "pebble_dish": ["pebble_dish/kenney_food-kit/plate-deep.glb"],
    "flowers": ["flowers/pastel_plume_flowers_eLVv17bTyB-.glb", "flowers/kenney_nature-kit/flower_purpleA.glb"],
    "grass_tuft": ["grass_tuft/kenney_nature-kit/grass_large.glb"],
    "hexagon_tile": ["hexagon_tile/honey_comb_atnjipGFNO.glb"],
    "portal_frame": ["portal_frame/honey_comb_atnjipGFNO.glb"],
    "clipboard": ["clipboard/clipboard_8H5SDxwMnEA.glb"],
    "ember": ["ember/flame_decal_8DEKYKfH2h.glb"],
}
dl_index = {}
for k, v in DL.items():
    if isinstance(v, list):
        for e in v:
            if isinstance(e, dict) and "path" in e:  # skips the _rejected / _sources_checked notes
                dl_index[e["path"]] = e

ORDER = ["beeswax", "worker_bee", "hive", "hexagon_tile", "portal_frame", "smoker", "honey_jar", "syrup_bottle",
         "deep_bowl", "pebble_dish", "wasp", "giant_hornet", "ember", "flowers", "grass_tuft", "rocket", "moon",
         "earth", "clipboard", "seed_packet",
         "swatter", "spray_bottle", "crowbar", "reducer", "rain_cloud", "stamp_approved", "stamp_grievance",
         "clipboard_sign", "score_card"]

items = {}
for it in ORDER:
    with open(os.path.join(stats_dir, it + ".json")) as fh:
        s = json.load(fh)
    d = os.path.join(fin, it)
    files = sorted(os.path.relpath(p, fin) for p in glob.glob(os.path.join(d, "**", "*"), recursive=True)
                   if os.path.isfile(p))
    b = BOUNDS[it]
    objs = [dict(name=o["name"], type=o["type"].lower(), parent=o["parent"], tris=o["tris"],
                 pivot_unity_m=o["pivot_unity"], material=o["material"],
                 **({"disabled_by_default": True} if o["name"] in b["hidden_by_default"] else {}))
            for o in s["objects"]]
    alts = []
    for p in ALT.get(it, []):
        e = dl_index.get(p)
        if e:
            alts.append(dict(path="art-staging/downloaded/" + p, license=e["license"], author=e["author"],
                             source_url=e["source_url"], tris=e.get("tris"),
                             suggested_uniform_scale=e.get("suggested_uniform_scale"),
                             animations=e.get("animations") or [], attribution_text=e["attribution_text"]))
    vfx = None
    if it in ("smoker", "ember"):
        vfx = dict(folder=it + "/vfx_kenney_cc0", **KENNEY)
    items[it] = dict(
        file=it + "/" + it + ".glb",
        fbx=it + "/" + it + ".fbx",
        blend=it + "/" + it + ".blend",
        preview=it + "/" + it + "_preview.png",
        all_files=files,
        source="procedural",
        license=ORIGINAL,
        tris=s["tris"],
        tri_budget=s["budget"],
        bounds_m=dict(unity_space="meters, X right, Y up, Z = front (glTFast / Unity FBX import)",
                      visible=b["visible"], including_disabled_parts=b["all_meshes"]),
        size_m=b["visible"]["size"],
        pivot=s["pivot"],
        objects=objs,
        animate=INFO[it]["animate"],
        notes=INFO[it]["notes"],
        **({"vfx_sprites": vfx} if vfx else {}),
        downloaded_alternates_not_used=alts,
    )

manifest = dict(
    project="Virtual Bee Swarm (Immerse the Bay 2026) - temp stylized art set",
    generated="2026-11-14 (art-director pass; v3.1 props added ~07:30 PT)",
    location="pop-os:~/immerse-the-bay/art-staging/final/",
    conventions=dict(
        units="meters", up="+Y", front="+Z (Unity)", transforms="no rotation or scale on any node (all applied)",
        formats="GLB (glTF 2.0) + FBX 7.4 (UnitScaleFactor 100, Y-up; import with Convert Units on) + .blend",
        materials="VBS_Palette (emission 0.08), VBS_Palette_Bee (emission 0.45, bees/wasps/hornet/Beeswax, reads "
                  "in passthrough), VBS_Glow (emission 1.0: embers, flame, lit hex), VBS_GlowSoft (alpha 0.35 halo), "
                  "VBS_PaneLive / VBS_PaneFallback (portal panes, real UVs)",
        colour="Every face has BOTH a UV at the center of one 8x8-cell 64 px palette texture "
               "(_shared/VBS_Palette.png, use Point filtering, no mips needed) AND an sRGB vertex colour (FBX only, "
               "for the Honeymoon/Unlit shader). GLB has no COLOR_0 on purpose: glTF multiplies it with the texture.",
        palette="amber #F2A900, honey #FFC845, cream #FFF3D6, charcoal #2B2B2B, leaf #5BAA4A, sky #7EC8E3, fire "
                "#FF6B2C (+ helpers; full list in _source/scripts/common.py PALETTE)",
        rebuild="blender -b --factory-startup --python _source/scripts/build_all.py -- --out OUT_DIR (about 20 s for "
                "all of them); contact sheet: _source/tools/contact_sheet.py",
        art_director_changes="smoker (steel/copper, pleated red leather bellows), giant_hornet (pincer mandibles, "
                             "scaled 0.8 to ~0.26 m), moon (readable craters), worker_bee (eyes spread apart), "
                             "5 palette colours appended (steel_mid, copper, leather, leather_dark, moon_light); "
                             "v3.1 props: 3 more appended (stamp_green, cloud, cloud_dark)",
    ),
    summary=dict(items=len(items), procedural=len(items), downloaded_meshes_used=0,
                 third_party_files="Kenney Particle Pack sprites (CC0) in smoker/vfx_kenney_cc0 and "
                                   "ember/vfx_kenney_cc0 only",
                 missing=[]),
    items=items,
)
with open(os.path.join(fin, "MANIFEST.json"), "w") as fh:
    json.dump(manifest, fh, indent=1, ensure_ascii=False)

# ---------------------------------------------------------------------------------------------- ATTRIBUTION.md
smoke = sorted(os.path.basename(p) for p in glob.glob(os.path.join(fin, "smoker", "vfx_kenney_cc0", "*.png")))
emb = sorted(os.path.basename(p) for p in glob.glob(os.path.join(fin, "ember", "vfx_kenney_cc0", "*.png")))
L = []
L.append("# Virtual Bee Swarm: art attribution\n")
L.append("Temp stylized art set picked on 2026-11-14 (art-director pass; the nine v3.1 props from swatter to "
         "score_card were added the same morning). Files live in "
         "`pop-os:~/immerse-the-bay/art-staging/final/`. Details per item are in `MANIFEST.json`.\n")
L.append("## Summary\n")
L.append("- **All %d models are original**: generated for this project by procedural Blender scripts "
         "(`final/_source/scripts/`). No attribution needed." % len(ORDER))
L.append("- **The only third-party files** are Kenney's CC0 particle sprites, used as VFX textures for the smoker "
         "smoke and the fire embers. CC0 means public domain, so credit is optional (a thank-you is listed below).")
L.append("- **No CC-BY asset is used**, so the game has no mandatory credit line from this art set.")
L.append("- If you swap in one of the downloaded alternates listed in `MANIFEST.json` "
         "(`downloaded_alternates_not_used`), add its `attribution_text` here. The CC-BY ones REQUIRE it in the game "
         "credits.\n")
L.append("## Models (all original)\n")
L.append("| Item | File | Author / source | License |")
L.append("|---|---|---|---|")
for it in ORDER:
    L.append("| %s | `%s/%s.glb` (+ .fbx, .blend) | original, generated for this project | "
             "original (no attribution needed) |" % (it, it, it))
L.append("")
L.append("Also original, generated for this project: `_shared/VBS_Palette.png` (palette texture), "
         "`portal_frame/meadow_fallback.png` (stand-in meadow picture for the portal's fallback pane), "
         "`ember/ember_sprite.png` (radial glow sprite), and every `*_preview.png` / `*_turntable.png` / "
         "`contact_sheet.png` render.\n")
L.append("## Third-party assets\n")
L.append("### Particle Pack (Kenney)\n")
L.append("- **Name:** Particle Pack")
L.append("- **Author:** Kenney (www.kenney.nl)")
L.append("- **License:** CC0 1.0 Universal (public domain dedication). `License.txt` is copied next to the files.")
L.append("- **URL:** https://kenney.nl/assets/particle-pack")
L.append("- **Attribution text (optional):** Particle Pack by Kenney (www.kenney.nl), CC0 1.0")
L.append("- **Files used:**")
L.append("  - `smoker/vfx_kenney_cc0/`: " + ", ".join(smoke))
L.append("  - `ember/vfx_kenney_cc0/`: " + ", ".join(emb))
L.append("")
L.append("### Font outlines baked into meshes\n")
L.append("- The printed words on the models (HONEY, STORE BRAND, SUGAR SYRUP, LOCAL 20,000, HONEYMOON-1, SEEDS, "
         "MEADOW MIX, WATER, APPROVED, GRIEVANCE, SIGN HERE, OFFICIAL RESULT, BEE WORKERS UNION) were set in **Fira Sans ExtraBold** (Mozilla / Carrois Apostrophe, SIL Open Font License 1.1, "
         "https://github.com/mozilla/Fira) and converted to mesh geometry. No font file is shipped. The OFL allows "
         "using the font this way; no credit is required.")
L.append("- The contact sheet labels use Fira Sans Bold / Book (same license).\n")
L.append("## Downloaded but NOT used\n")
L.append("Everything in `art-staging/downloaded/` (111 entries, CC0 or CC-BY 3.0) stays available. Nothing from it "
         "is in this final set except the Kenney sprites above. Each entry's license, author, URL and credit text is "
         "in `art-staging/downloaded/MANIFEST.json`.\n")
L.append("## Tools\n")
L.append("Built and rendered with Blender 4.5.16 LTS (GPL; Blender's license does not apply to its output).")
with open(os.path.join(fin, "ATTRIBUTION.md"), "w") as fh:
    fh.write("\n".join(L) + "\n")
print("MANIFEST + ATTRIBUTION written")
