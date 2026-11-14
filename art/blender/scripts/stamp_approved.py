"""APPROVED stamp: slammed onto a choice the moment the player pokes the right one (V15: scale 0 -> 1.2 -> 1 in
120 ms). Green ink. Pivot = center, faces the player. One mesh."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402
import stamp_common as S  # noqa: E402

ITEM = "stamp_approved"
BUDGET = 3000

if __name__ == "__main__":
    C.run(ITEM, lambda: S.build_stamp("StampApproved", "APPROVED", "stamp_green", 0.0205), BUDGET,
          pivot="center", hero_az=-15)
