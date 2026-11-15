"""Card name OCR + picture matcher, fused so a wrong card never fires.

    from train.fusion import CardScout
    scout = CardScout("detect/card_images", ocr=True, debug_dir=Path("debug"))
    top, scores, boxes = scout.process(rgb, at_ms)

Per frame (OCR and SIFT run in parallel threads):
  1. OCR reads a full card name (strong) -> that label, score 0.95. One-word reads (WATER, HOTEL...) count only
     when SIFT matched the same picture. Several names in view: the biggest text wins.
     Box: the SIFT outline when SIFT agrees, else the text box grown upward over the picture.
  2. No OCR label: SIFT alone fires only with >= sift_alone inliers (25), when no OCR read names any card, and
     when the runner-up picture has less than half its inliers.
  3. OCR and SIFT disagree: OCR's label is reported, never SIFT's.
  4. The final label must win >= 3 of the last 5 frames before its score goes above 0.3 (the Hive needs 0.6).
Debug: frames with any evidence (at most ~3/s) and every voted-label change go to a ring of 50 thumbnails.
"""

from __future__ import annotations

import json
import sys
import threading
import time
from collections import deque
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import cv2
import numpy as np

from train.matcher import BACKGROUND, LABELS, CardMatcher

OCR_SCORE = 0.95
SIFT_ALONE = 25
SIFT_SUPPORT = 10
CAP = 0.3


def _box(label: str, score: float, x0: float, y0: float, x1: float, y1: float) -> dict:
    x0, y0, x1, y1 = (min(1.0, max(0.0, v)) for v in (x0, y0, x1, y1))
    return {"label": label, "score": round(score, 3), "x": round(x0, 4), "y": round(y0, 4), "w": round(x1 - x0, 4), "h": round(y1 - y0, 4), "via": "detector"}


def _text_card_box(t: dict, aspect: float = 16 / 9) -> tuple[float, float, float, float]:
    """Text box (normalized) -> the card around it: the picture sits above the name, about as wide as the page."""
    cx = t["x"] + t["w"] / 2
    w = max(t["w"] * 1.3, t["h"] / aspect * 6)  # in x units (frame width = 1); text height is in y units
    h_px = w * aspect * 0.85  # picture height ~ 0.85 of card width; aspect turns x units into y units
    return cx - w / 2, t["y"] - h_px, cx + w / 2, t["y"] + t["h"] * 1.3


class CardScout:
    def __init__(self, ref_dir: str | Path, ocr: bool = True, ocr_level: str = "accurate", sift_alone: int = SIFT_ALONE,
                 vote_need: int = 3, vote_n: int = 5, debug_dir: Path | None = None, debug_keep: int = 50) -> None:
        self.ref_dir = ref_dir
        self.matcher = CardMatcher(ref_dir, min_inliers=SIFT_SUPPORT)
        self.ocr = None
        if ocr:
            from train.ocr import CardOCR

            self.ocr = CardOCR(ocr_level)
        self.sift_alone = sift_alone
        self.need = vote_need
        self.hist: deque[str] = deque(maxlen=vote_n)
        self.last_at = 0.0
        self.voted = BACKGROUND
        self.pool = ThreadPoolExecutor(2)
        self.debug_dir = Path(debug_dir) if debug_dir else None
        self.debug: deque[dict] = deque(maxlen=debug_keep)
        self.debug_lock = threading.Lock()
        self.last_debug = 0.0
        if self.debug_dir:
            self.debug_dir.mkdir(parents=True, exist_ok=True)
            for old in self.debug_dir.glob("*.jpg"):  # the ring starts empty: drop thumbnails from the last run
                old.unlink(missing_ok=True)

    def reload(self) -> dict:
        self.matcher = CardMatcher(self.ref_dir, min_inliers=SIFT_SUPPORT)
        return self.matcher.files

    # ------------------------------------------------------------------ one frame

    def evidence(self, rgb: np.ndarray) -> tuple[list[dict], list[dict], list[dict], dict]:
        """(ocr keyword hits, every OCR string, SIFT matches >= SIFT_SUPPORT inliers, sift debug)."""
        dbg: dict = {}
        f_sift = self.pool.submit(self.matcher.detect, rgb, dbg)
        hits, texts = [], []
        if self.ocr is not None:
            try:
                hits = self.ocr.read(rgb)
                texts = self.ocr.last_texts
            except Exception as e:  # noqa: BLE001 - OCR trouble must not stop the worker
                print(f"[ocr] {e}", file=sys.stderr, flush=True)
        sift = f_sift.result()
        return hits, texts, sift, dbg

    def decide(self, hits: list[dict], sift: list[dict]) -> tuple[str, float, dict | None, str]:
        """-> (label or background, raw score, box, reason)."""
        by_sift = {b["label"]: b for b in sift}  # best first already, one per label
        usable = [h for h in hits if h["strong"] or h["label"] in by_sift]
        if usable:
            h = max(usable, key=lambda t: t["w"] * t["h"])
            lb = h["label"]
            if lb in by_sift:
                b = by_sift[lb]
                return lb, OCR_SCORE, _box(lb, OCR_SCORE, b["x"], b["y"], b["x"] + b["w"], b["y"] + b["h"]), "ocr+sift"
            return lb, OCR_SCORE, _box(lb, OCR_SCORE, *_text_card_box(h)), "ocr" + ("" if h["strong"] else "-weak")
        if hits:  # OCR named a card but it is unusable (weak, no picture support): stay quiet rather than trust SIFT
            return BACKGROUND, 0.0, None, "ocr-weak-only"
        if sift:
            b = sift[0]
            second = sift[1]["inliers"] if len(sift) > 1 else 0
            if b["inliers"] >= self.sift_alone and second * 2 <= b["inliers"]:
                s = min(0.99, 0.6 + 0.39 * (b["inliers"] - self.sift_alone) / 30)
                return b["label"], s, _box(b["label"], s, b["x"], b["y"], b["x"] + b["w"], b["y"] + b["h"]), "sift"
            return BACKGROUND, 0.0, None, "sift-weak"
        return BACKGROUND, 0.0, None, "none"

    def process(self, rgb: np.ndarray, at: float) -> tuple[list[dict], dict[str, float], list[dict]]:
        hits, texts, sift, dbg = self.evidence(rgb)
        label, raw, box, reason = self.decide(hits, sift)
        if at - self.last_at > 1500:
            self.hist.clear()
        self.last_at = at
        self.hist.append(label)
        voted = label != BACKGROUND and sum(lb == label for lb in self.hist) >= self.need
        score = raw if voted else min(raw, CAP)
        scores = {lb: 0.0 for lb in LABELS}
        if label != BACKGROUND:
            scores[label] = round(score, 4)
        scores[BACKGROUND] = round(1 - score, 4)
        order = sorted(scores, key=lambda c: -scores[c])[:3]
        top = [{"label": c, "score": scores[c]} for c in order]
        boxes = [{**box, "score": round(score, 3)}] if box else []
        new_voted = label if voted else (self.voted if self.voted in self.hist else BACKGROUND)
        changed = new_voted != self.voted
        self.voted = new_voted
        if self.debug_dir and (changed or ((hits or sift) and time.time() - self.last_debug > 0.33)):
            self.last_debug = time.time()
            self._record(rgb, at, hits, texts, sift, dbg, label, score, reason, voted, changed, boxes)
        return top, scores, boxes

    # ------------------------------------------------------------------ debug ring

    def _record(self, rgb, at, hits, texts, sift, dbg, label, score, reason, voted, changed, boxes) -> None:
        try:
            ranked = sorted(dbg.get("inliers", {}).items(), key=lambda kv: -kv[1])[:2]
            entry = {"at": int(at), "time": time.strftime("%H:%M:%S", time.localtime(at / 1000)) + f".{int(at) % 1000:03d}",
                     "final": label, "score": round(score, 3), "voted": voted, "voted_label": self.voted, "changed": changed,
                     "reason": reason, "ocr": [{"text": t["text"], "h_px": round(t["h"] * rgb.shape[0])} for t in texts][:8],
                     "ocr_hits": [{"text": h["text"], "label": h["label"], "strong": h["strong"]} for h in hits],
                     "sift_top2": [{"label": lb, "inliers": n} for lb, n in ranked],
                     "sift_accepted": [{"label": b["label"], "inliers": b["inliers"]} for b in sift]}
            H, W = rgb.shape[:2]
            s = 480 / W
            im = cv2.cvtColor(cv2.resize(rgb, (480, round(H * s)), interpolation=cv2.INTER_AREA), cv2.COLOR_RGB2BGR)
            th, tw = im.shape[:2]
            for b in sift:
                q = (np.array(b["quad"]) * [tw, th]).astype(np.int32)
                cv2.polylines(im, [q], True, (0, 200, 0) if b["label"] == label else (0, 0, 255), 1)
            for t in texts:
                c = (0, 220, 255) if any(t is h or t["text"] == h["text"] for h in hits) else (160, 160, 160)
                cv2.rectangle(im, (int(t["x"] * tw), int(t["y"] * th)), (int((t["x"] + t["w"]) * tw), int((t["y"] + t["h"]) * th)), c, 1)
            for b in boxes:
                cv2.rectangle(im, (int(b["x"] * tw), int(b["y"] * th)), (int((b["x"] + b["w"]) * tw), int((b["y"] + b["h"]) * th)), (255, 255, 255), 2)
            cv2.putText(im, f"{label} {score:.2f} {reason}{' VOTED' if voted else ''}", (5, 18), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 255, 255), 2)
            name = f"{int(at)}.jpg"
            cv2.imwrite(str(self.debug_dir / name), im, [cv2.IMWRITE_JPEG_QUALITY, 75])
            entry["img"] = name
            with self.debug_lock:
                if len(self.debug) == self.debug.maxlen:
                    old = self.debug[0].get("img")
                    if old:
                        (self.debug_dir / old).unlink(missing_ok=True)
                self.debug.append(entry)
                with (self.debug_dir / "log.jsonl").open("a") as f:
                    f.write(json.dumps(entry) + "\n")
        except Exception as e:  # noqa: BLE001
            print(f"[debug] {e}", file=sys.stderr, flush=True)

    def recent(self) -> list[dict]:
        with self.debug_lock:
            return list(reversed(self.debug))
