"""Card name OCR with Apple's Vision framework (on-device, no model download), mapped to booth labels.

    from train.ocr import CardOCR
    ocr = CardOCR(level="fast")
    hits = ocr.read(rgb)   # [{label, keyword, kind, strong, full, text, conf, x, y, w, h}] normalized, x,y top-left
    texts = ocr.last_texts # every string Vision read in that frame (debug)

    uv run python -m train.ocr <image>...    # print what it reads and the label it maps to

Strong (a full card name, fuzzy, so "SUNFL0WER" or "BEEHlVE" still count): SUNFLOWER -> native_flower,
HONEYCOMB -> honey_jar, BUG SPRAY -> bug_spray, BEE HOTEL -> bee_hotel, (BEE) WATER DISH -> pebble_dish,
BEEHIVE -> beehive. Weak (one word: HIVE, HOTEL, SPRAY, WATER, DISH, HONEY, FLOWER): only with picture support.
"""

from __future__ import annotations

import difflib
import re
import sys
import time

import numpy as np

# Full card names: a fuzzy read of one of these alone is strong evidence (letters only, spaces dropped).
PHRASES: list[tuple[str, str]] = [
    ("SUNFLOWER", "native_flower"),
    ("HONEYCOMB", "honey_jar"),
    ("BEEHIVE", "beehive"),
    ("BEEHOTEL", "bee_hotel"),
    ("BUGSPRAY", "bug_spray"),
    ("WATERDISH", "pebble_dish"),
]
PHRASE_MIN = 0.85
FULL_CONF = 0.5  # Vision confidence a full-name read needs to fire instantly (no vote)
# Single words: weak evidence (a water bottle says WATER), counted only when the picture matcher agrees.
WORDS: list[tuple[str, str]] = [("HIVE", "beehive"), ("HOTEL", "bee_hotel"), ("SPRAY", "bug_spray"), ("WATER", "pebble_dish"),
                                ("DISH", "pebble_dish"), ("HONEY", "honey_jar"), ("FLOWER", "native_flower")]
WORD_MIN = 0.8


def _ratio_in(hay: str, needle: str) -> float:
    """Best fuzzy similarity of needle against any window of hay of length len(needle) +- 1."""
    n = len(needle)
    if len(hay) <= n + 1:
        return difflib.SequenceMatcher(None, hay, needle).ratio()
    best = 0.0
    for L in (n - 1, n, n + 1):
        for i in range(0, len(hay) - L + 1):
            best = max(best, difflib.SequenceMatcher(None, hay[i:i + L], needle).ratio())
    return best


def label_for(text: str) -> tuple[str | None, str | None, str]:
    """A recognized string -> (label, keyword, kind). kind: "full" (a whole card name), "partial" (a cut-off name,
    >= 6 letters), "word" (one weak word), "" (nothing)."""
    up = re.sub(r"[^A-Z ]", " ", text.upper().replace("0", "O").replace("1", "I").replace("5", "S").replace("8", "B"))
    joined = up.replace(" ", "")
    if len(joined) < 4:
        return None, None, ""
    best = (0.0, None, None)
    for ph, lb in PHRASES:
        r = _ratio_in(joined, ph)
        if r >= PHRASE_MIN and r > best[0]:
            best = (r, lb, ph)
    if best[1]:
        return best[1], best[2], "full"
    # a cut-off read of a full name ("HONEYC", "YCOMB" at the frame edge): >= 6 letters, exact prefix/suffix of one name
    for w in [joined] + up.split():
        if len(w) >= 6:
            for ph, lb in PHRASES:
                if len(w) < len(ph) and (ph.startswith(w) or ph.endswith(w)):
                    return lb, ph, "partial"
                if len(w) >= 7 and len(w) <= len(ph) and (ph.startswith(w[:-1]) or ph.endswith(w[1:])):  # one bad edge letter
                    return lb, ph, "partial"
    for w in up.split():
        for kw, lb in WORDS:
            r = difflib.SequenceMatcher(None, w, kw).ratio()
            if r >= WORD_MIN and r > best[0]:
                best = (r, lb, kw)
    return best[1], best[2], "word" if best[1] else ""


class CardOCR:
    def __init__(self, level: str = "fast", min_height: float = 0.0, min_px: float = 10.0) -> None:
        import Vision  # noqa: F401 - pyobjc-framework-Vision

        self.level = level
        self.min_px = min_px  # ignore keyword reads shorter than this (px): small print on bottles and screens
        self.frame_h = 720
        self.errors = 0
        self.last_err = 0.0
        self.min_height = min_height
        self.last_texts: list[dict] = []

    def _request(self):
        import Vision

        req = Vision.VNRecognizeTextRequest.alloc().init()
        req.setRecognitionLevel_(Vision.VNRequestTextRecognitionLevelFast if self.level == "fast" else Vision.VNRequestTextRecognitionLevelAccurate)
        req.setUsesLanguageCorrection_(False)
        req.setRecognitionLanguages_(["en-US"])
        if self.min_height:
            req.setMinimumTextHeight_(self.min_height)
        return req

    def texts(self, rgb: np.ndarray) -> list[dict]:
        """Every string Vision reads: [{text, conf, x, y, w, h}] normalized with x,y = top-left."""
        import Quartz
        import Vision

        h, w = rgb.shape[:2]
        self.frame_h = h
        rgba = np.ascontiguousarray(np.dstack([rgb, np.full((h, w), 255, np.uint8)]))
        data = rgba.tobytes()
        provider = Quartz.CGDataProviderCreateWithData(None, data, len(data), None)
        cs = Quartz.CGColorSpaceCreateDeviceRGB()
        img = Quartz.CGImageCreate(w, h, 8, 32, w * 4, cs, Quartz.kCGImageAlphaNoneSkipLast, provider, None, False, Quartz.kCGRenderingIntentDefault)
        handler = Vision.VNImageRequestHandler.alloc().initWithCGImage_options_(img, None)
        req = self._request()
        ok, err = handler.performRequests_error_([req], None)
        out = []
        if not ok:
            self.errors += 1
            if time.time() - self.last_err > 5:
                print(f"[ocr] Vision failed ({self.errors} so far): {err}", file=sys.stderr, flush=True)
                self.last_err = time.time()
            return out
        for obs in req.results() or []:
            cands = obs.topCandidates_(1)
            if not cands:
                continue
            c = cands[0]
            bb = obs.boundingBox()  # normalized, origin bottom-left
            x, y, bw, bh = bb.origin.x, bb.origin.y, bb.size.width, bb.size.height
            out.append({"text": str(c.string()), "conf": round(float(c.confidence()), 3), "x": round(float(x), 4),
                        "y": round(float(1 - y - bh), 4), "w": round(float(bw), 4), "h": round(float(bh), 4)})
        return out

    def read(self, rgb: np.ndarray) -> list[dict]:
        self.last_texts = self.texts(rgb)
        hits = []
        for t in self.last_texts:
            lb, kw, kind = label_for(t["text"])
            if lb and t["h"] * self.frame_h >= self.min_px:
                hits.append({**t, "label": lb, "keyword": kw, "kind": kind, "strong": kind in ("full", "partial"),
                             "full": kind == "full" and t["conf"] >= FULL_CONF})
        return hits


def main() -> None:
    import cv2

    level = "fast"
    paths = sys.argv[1:]
    if paths and paths[0] in ("fast", "accurate"):
        level, paths = paths[0], paths[1:]
    ocr = CardOCR(level)
    for p in paths:
        rgb = cv2.cvtColor(cv2.imread(p), cv2.COLOR_BGR2RGB)
        t0 = time.perf_counter()
        hits = ocr.read(rgb)
        ms = (time.perf_counter() - t0) * 1000
        print(f"{p}: {ms:.0f} ms  texts={[t['text'] for t in ocr.last_texts]}  hits={[(h['label'], h['text'], h['strong']) for h in hits]}")


if __name__ == "__main__":
    main()
