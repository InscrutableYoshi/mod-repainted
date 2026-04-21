"""
Bake preview sprites for the Repainted decoration shop entries.

For each WallType, overlay a horizontal rainbow gradient onto Wall1.png,
but ONLY inside the ColorBand regions — the uncolored portions pass
through unchanged. The gradient is multiply-blended, matching how the
game's shader combines _BaseColor × _BaseMap, so the stucco texture
shows through underneath.

Output: one PNG per WallType in this directory.
"""

from pathlib import Path
from PIL import Image
import colorsys
import numpy as np

HERE = Path(__file__).resolve().parent
WALL1 = HERE.parent.parent.parent / "materials" / "walls" / "sprites" / "Wall1.png"
assert WALL1.exists(), f"Wall1.png not found at {WALL1}"

# Color band definitions (must mirror DecorationManagerPatch.cs).
# Y is normalized 0..1 from bottom to top.
STRIPE_TOP = 0.224
TOP_TRIM = 0.85

WALLS = {
    "Repainted_Full":            [(0.00, 1.00)],
    "Repainted_BottomStripe":    [(0.00, STRIPE_TOP)],
    "Repainted_BottomTopStripe": [(0.00, STRIPE_TOP), (TOP_TRIM, 1.00)],
    "Repainted_Trim":            [(0.00, 0.10),       (TOP_TRIM, 1.00)],
    "Repainted_HighStripe":      [(0.50, 0.70)],
}

# Rainbow tuning — punchy but not neon.
RAINBOW_SATURATION = 0.85
RAINBOW_VALUE = 0.95
# Hue sweep across the horizontal axis. Start at red (0) and sweep
# through to violet (~0.83) so we don't wrap back around.
HUE_START = 0.00
HUE_END   = 0.83

# The decoration-shop preview UI crops our sprite: the Tile RectTransform
# is 415×416 but its parent TileMask is 281×282, both centered, so only
# the center ~67.7% of the sprite is visible. We inset the bands and the
# rainbow sweep by this margin so they fall entirely inside the visible
# window. The surrounding Wall1 stucco background fills the cropped
# margin and gets clipped — invisible but harmless.
VISIBLE_FRACTION = 281.0 / 415.0       # ≈ 0.6771
TOTAL_MARGIN = 1.0 - VISIBLE_FRACTION  # ≈ 0.3229

# Anchor of the Tile inside the mask:
#   "center" — crop halved top/bottom and left/right
#   "bottom" — whole crop comes off the top, bottom edge of sprite flush
#              with bottom edge of mask (floor stays on the floor)
VERTICAL_ANCHOR = "bottom"
HORIZONTAL_ANCHOR = "center"


def build_rainbow_row(width: int) -> np.ndarray:
    """Return an (width, 3) float array in [0,1] — the rainbow gradient.

    The sweep is inset horizontally so the full hue range sits inside
    the visible mask window; pixels in the cropped margins get white
    (identity for the multiply blend) so the background passes through.
    """
    rgb = np.ones((width, 3), dtype=np.float32)
    if HORIZONTAL_ANCHOR == "center":
        x_start = int(round(TOTAL_MARGIN * 0.5 * width))
        x_end = int(round((1.0 - TOTAL_MARGIN * 0.5) * width))
    elif HORIZONTAL_ANCHOR == "left":
        x_start = 0
        x_end = int(round((1.0 - TOTAL_MARGIN) * width))
    elif HORIZONTAL_ANCHOR == "right":
        x_start = int(round(TOTAL_MARGIN * width))
        x_end = width
    else:
        raise ValueError(f"HORIZONTAL_ANCHOR={HORIZONTAL_ANCHOR!r}")
    visible_w = x_end - x_start
    hues = np.linspace(HUE_START, HUE_END, visible_w)
    visible = np.array([
        colorsys.hsv_to_rgb(h, RAINBOW_SATURATION, RAINBOW_VALUE)
        for h in hues
    ], dtype=np.float32)
    rgb[x_start:x_end] = visible
    return rgb  # (W, 3)


def build_band_mask(height: int, bands) -> np.ndarray:
    """Return a (H,) float mask in [0,1]: 1 inside a band, 0 outside.

    Image space has y=0 at the top; band Y is normalized bottom-up,
    with y_norm=0 meaning "floor" and y_norm=1 meaning "ceiling". We
    map the full [0,1] normalized range onto the *visible* vertical
    window of the preview mask so stripes land at the intuitive
    positions in the thumbnail.
    """
    mask = np.zeros(height, dtype=np.float32)
    if VERTICAL_ANCHOR == "center":
        visible_top_px = int(round(TOTAL_MARGIN * 0.5 * height))
        visible_bot_px = int(round((1.0 - TOTAL_MARGIN * 0.5) * height))
    elif VERTICAL_ANCHOR == "bottom":
        # Floor of sprite aligns with floor of mask — crop comes off top.
        visible_top_px = int(round(TOTAL_MARGIN * height))
        visible_bot_px = height
    elif VERTICAL_ANCHOR == "top":
        visible_top_px = 0
        visible_bot_px = int(round((1.0 - TOTAL_MARGIN) * height))
    else:
        raise ValueError(f"VERTICAL_ANCHOR={VERTICAL_ANCHOR!r}")
    visible_h = visible_bot_px - visible_top_px
    for start, end in bands:
        y_top_px = visible_bot_px - int(round(end * visible_h))
        y_bot_px = visible_bot_px - int(round(start * visible_h))
        y_top_px = max(0, min(height, y_top_px))
        y_bot_px = max(0, min(height, y_bot_px))
        mask[y_top_px:y_bot_px] = 1.0
    return mask


def bake(name: str, bands):
    base = Image.open(WALL1).convert("RGBA")
    W, H = base.size
    base_arr = np.asarray(base, dtype=np.float32) / 255.0  # (H, W, 4)
    rgb = base_arr[..., :3]
    alpha = base_arr[..., 3:4]

    # Broadcast rainbow row over all rows.
    rainbow = build_rainbow_row(W)                 # (W, 3)
    rainbow_full = np.broadcast_to(rainbow, (H, W, 3))  # (H, W, 3)

    # Vertical band mask: 1 inside bands, 0 outside.
    band_mask = build_band_mask(H, bands)[:, None, None]  # (H, 1, 1)

    # Multiply blend: result = base * tint. Where mask=0 tint=white
    # (identity). Where mask=1 tint=rainbow row.
    tint = np.where(band_mask > 0, rainbow_full, np.ones_like(rainbow_full))
    out_rgb = rgb * tint

    out = np.concatenate([out_rgb, alpha], axis=-1)
    out = (np.clip(out, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8)

    out_img = Image.fromarray(out, mode="RGBA")
    out_path = HERE / f"{name}.png"
    out_img.save(out_path)
    print(f"  wrote {out_path.name}  ({W}×{H})")


def main():
    print(f"Loading base from {WALL1}")
    for name, bands in WALLS.items():
        bake(name, bands)
    print("Done.")


if __name__ == "__main__":
    main()
