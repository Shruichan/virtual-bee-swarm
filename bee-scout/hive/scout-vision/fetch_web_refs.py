"""Download freely licensed reference photos from Wikimedia Commons.

    uv run python fetch_web_refs.py              # all labels + background into data/web/<label>/
    uv run python fetch_web_refs.py --per 45     # candidates per label (some get dropped as junk)
    uv run python fetch_web_refs.py --split      # only redo the enrol/test split (refs/ + data/test/)
    uv run python fetch_web_refs.py --sheets     # contact sheets in data/sheets/ for a hand check

Junk found by hand goes in data/web/junk.txt (one "<label>/<file>" per line); --split skips those.
Credits: data/web/credits.csv (label, file, url, license, author, page).
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import html
import random
import re
import shutil
import time
from pathlib import Path

import requests

ROOT = Path(__file__).resolve().parent
WEB = ROOT / "data" / "web"
TEST = ROOT / "data" / "test"
REFS = ROOT / "refs"
UA = "BeeScoutHackathon/0.1 (educational)"
API = "https://commons.wikimedia.org/w/api.php"

# Plain strings are full-text searches; "Category:..." lists that Commons category's files (more on-topic).
QUERIES: dict[str, list[str]] = {
    "beehive": ["Category:Langstroth hives", "Langstroth hive", "beehive box apiary", "bee hive boxes", "Langstroth beehive white", "wooden beehive"],
    "native_flower": ["Eschscholzia californica flower", "California poppy", "Salvia flower purple", "Salvia clevelandii", "Salvia leucantha"],
    "honey_jar": ["Category:Honey jars", "jar of honey", "honey jar", "honey glass jar", "honey bottle supermarket"],
    "pebble_dish": ["Category:Bird baths", "Category:Bird bath", "bee water dish pebbles", "butterfly puddling station", "saucer of water with stones", "bird bath stones", "bee watering station", "bird bath pebbles", "dish of water with pebbles", "shallow dish stones water", "pebbles in water bowl"],
    "bug_spray": ["Category:Insect repellents", "Category:Raid (insecticide)", "Category:Spray cans", "Category:Spray bottles", "mosquito repellent spray bottle", "aerosol can", "insecticide spray can", "bug spray", "insect repellent spray", "aerosol insecticide", "Raid insecticide"],
    "bee_hotel": ["Category:Insect hotels", "insect hotel", "bee hotel", "Insektenhotel", "wild bee nesting aid", "solitary bee house"],
    "background": [
        "office desk laptop", "laptop on table", "conference attendees", "classroom interior", "office hallway",
        "trade show booth", "exhibition hall booth", "white wall room", "people at a hackathon", "coworking space",
        "meeting room table", "computer monitor desk", "conference room", "university lecture hall", "corridor interior",
        "people talking at event", "potted plant office", "lawn grass", "garden shrubs", "water bottle on desk",
        "coffee cup on table", "cardboard boxes", "table with tablecloth", "smartphone in hand", "backpack on floor",
        "carpet floor", "window blinds office",
    ],
}

LICENSE_OK = re.compile(r"^(cc0|cc[- ]by|cc[- ]by[- ]sa|public domain|pd)", re.I)


def strip_tags(s: str) -> str:
    return html.unescape(re.sub(r"<[^>]+>", "", s or "")).strip()


def search(session: requests.Session, query: str, limit: int) -> list[dict]:
    params = {"action": "query", "format": "json", "prop": "imageinfo", "iiprop": "url|extmetadata|mime|size", "iiurlwidth": 640}
    if query.startswith("Category:"):
        params |= {"generator": "categorymembers", "gcmtitle": query, "gcmtype": "file", "gcmlimit": min(limit, 500)}
    else:
        params |= {"generator": "search", "gsrsearch": f"{query} filetype:bitmap", "gsrnamespace": 6, "gsrlimit": limit}
    for attempt in range(4):
        r = session.get(API, params=params, timeout=30)
        if r.status_code == 429:
            time.sleep(5 * (attempt + 1))
            continue
        r.raise_for_status()
        pages = r.json().get("query", {}).get("pages", {})
        return sorted(pages.values(), key=lambda p: p.get("index", 0))
    return []


def fetch(per: int, bg_total: int) -> None:
    session = requests.Session()
    session.headers["User-Agent"] = UA
    WEB.mkdir(parents=True, exist_ok=True)
    credits_path = WEB / "credits.csv"
    seen_rows: dict[str, dict] = {}
    if credits_path.exists():
        with credits_path.open() as f:
            for row in csv.DictReader(f):
                seen_rows[f"{row['label']}/{row['file']}"] = row
    junk = junk_set()
    for label, queries in QUERIES.items():
        # top up to `per` usable photos: hand-dropped junk does not count
        want = (bg_total if label == "background" else per) + sum(1 for j in junk if j.startswith(label + "/"))
        out = WEB / label
        out.mkdir(parents=True, exist_ok=True)
        have = len(list(out.glob("*.jpg")))
        per_query = max(8, want // len(queries) + 6)
        seen_titles: set[str] = set()
        for q in queries:
            if have >= want:
                break
            got_q = 0
            for page in search(session, q, 200 if q.startswith("Category:") else per_query * 2):
                if have >= want or (got_q >= per_query and not q.startswith("Category:")):
                    break
                title = page.get("title", "")
                if title in seen_titles:
                    continue
                seen_titles.add(title)
                info = (page.get("imageinfo") or [{}])[0]
                if info.get("mime") not in ("image/jpeg", "image/png"):
                    continue
                if min(info.get("width", 0), info.get("height", 0)) < 300:
                    continue
                meta = info.get("extmetadata", {})
                lic = strip_tags(meta.get("LicenseShortName", {}).get("value", ""))
                if not LICENSE_OK.match(lic):
                    continue
                url = info.get("thumburl") or info.get("url")
                name = hashlib.sha1(title.encode()).hexdigest()[:12] + ".jpg"
                key = f"{label}/{name}"
                if (out / name).exists():
                    continue
                try:
                    r = session.get(url, timeout=30)
                    if r.status_code == 429:
                        time.sleep(5)
                        continue
                    r.raise_for_status()
                except requests.RequestException as e:
                    print(f"  skip {title}: {e}")
                    continue
                tmp = out / (name + ".part")
                tmp.write_bytes(r.content)
                try:
                    from PIL import Image

                    with Image.open(tmp) as im:
                        im.convert("RGB").save(out / name, "JPEG", quality=90)
                    tmp.unlink()
                except Exception as e:  # noqa: BLE001
                    tmp.unlink(missing_ok=True)
                    print(f"  bad image {title}: {e}")
                    continue
                seen_rows[key] = {
                    "label": label, "file": name, "url": info.get("url", url), "license": lic,
                    "author": strip_tags(meta.get("Artist", {}).get("value", ""))[:120],
                    "page": info.get("descriptionurl", ""), "query": q,
                }
                have += 1
                got_q += 1
                time.sleep(0.15)
            print(f"{label:14s} {q!r:40s} -> {have}/{want}")
    with credits_path.open("w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=["label", "file", "url", "license", "author", "page", "query"])
        w.writeheader()
        for row in seen_rows.values():
            w.writerow({k: row.get(k, "") for k in w.fieldnames})


def junk_set() -> set[str]:
    p = WEB / "junk.txt"
    if not p.exists():
        return set()
    return {ln.split("#")[0].strip() for ln in p.read_text().splitlines() if ln.split("#")[0].strip()}


def split(n_enrol: int, bg_enrol: int, seed: int = 7) -> None:
    junk = junk_set()
    rng = random.Random(seed)
    # refs/ keeps live_*.jpg (booth enrolment); only the web_*.jpg copies are redone.
    for f in [*TEST.glob("*/*.jpg"), *REFS.glob("*/web_*.jpg")]:
        f.unlink()
    for label in QUERIES:
        files = sorted(p for p in (WEB / label).glob("*.jpg") if f"{label}/{p.name}" not in junk)
        rng.shuffle(files)
        k = bg_enrol if label == "background" else min(n_enrol, round(len(files) * 0.65))
        enrol, test = files[:k], files[k:]
        (REFS / label).mkdir(parents=True, exist_ok=True)
        (TEST / label).mkdir(parents=True, exist_ok=True)
        for f in enrol:
            shutil.copy2(f, REFS / label / f"web_{f.name}")
        for f in test:
            shutil.copy2(f, TEST / label / f.name)
        print(f"{label:14s} kept {len(files):3d} (dropped {sum(1 for j in junk if j.startswith(label + '/'))}) enrol {len(enrol)} test {len(test)}")


def sheets() -> None:
    from PIL import Image, ImageDraw

    out = ROOT / "data" / "sheets"
    out.mkdir(parents=True, exist_ok=True)
    for label in QUERIES:
        files = sorted((WEB / label).glob("*.jpg"))
        cols, cell = 8, 200
        for start in range(0, len(files), 48):
            chunk = files[start:start + 48]
            rows = (len(chunk) + cols - 1) // cols
            sheet = Image.new("RGB", (cols * cell, rows * cell), "white")
            draw = ImageDraw.Draw(sheet)
            for i, f in enumerate(chunk):
                im = Image.open(f).convert("RGB")
                im.thumbnail((cell - 4, cell - 22))
                x, y = (i % cols) * cell, (i // cols) * cell
                sheet.paste(im, (x + 2, y + 20))
                draw.text((x + 4, y + 4), f"{start + i} {f.stem[:8]}", fill="red")
            sheet.save(out / f"{label}_{start // 48}.jpg", quality=80)
    print(f"sheets in {out}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--per", type=int, default=40)
    ap.add_argument("--bg", type=int, default=150)
    ap.add_argument("--enrol", type=int, default=20)
    ap.add_argument("--bg-enrol", type=int, default=50)
    ap.add_argument("--split", action="store_true")
    ap.add_argument("--sheets", action="store_true")
    a = ap.parse_args()
    if a.sheets:
        sheets()
    elif a.split:
        split(a.enrol, a.bg_enrol)
    else:
        fetch(a.per, a.bg)
        sheets()
