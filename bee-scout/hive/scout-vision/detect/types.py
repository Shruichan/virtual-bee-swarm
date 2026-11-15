"""ScoutBox, mirroring hive/src/scout-protocol.ts (coordinates normalized 0..1, x,y = top-left)."""

from __future__ import annotations

from dataclasses import asdict, dataclass

LABELS = ["beehive", "native_flower", "honey_jar", "pebble_dish", "bug_spray", "bee_hotel"]
BACKGROUND = "background"
CLASSES = [*LABELS, BACKGROUND]


@dataclass
class ScoutBox:
    label: str
    score: float
    x: float
    y: float
    w: float
    h: float
    via: str  # "detector" | "marker" | "refs"
    tag_id: int | None = None

    @property
    def cx(self) -> float:
        """Box centre, 0 = left edge of the view, 1 = right edge."""
        return self.x + self.w / 2

    @property
    def bearing(self) -> str:
        """Coarse left/right cue for the wearer."""
        return "left" if self.cx < 0.38 else "right" if self.cx > 0.62 else "ahead"

    def to_json(self) -> dict:
        """The ScoutBox wire shape (scout-protocol.ts): label, score, x, y, w, h, via."""
        d = asdict(self)
        d.pop("tag_id")
        for k in ("score", "x", "y", "w", "h"):
            d[k] = round(d[k], 4)
        return d
