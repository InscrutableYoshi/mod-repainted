#!/usr/bin/env python3
"""Bake decal ART layers (RGBA foreground + matte) for Repainted 2.0.

The game does not ship the standalone decal art — only albedos with the
art pre-composited (and DXT1-compressed) over a base. Since we KNOW the
background under every pixel (the paired base albedo, or the flat
background color), we can UNMIX the composite by color-line matting:

    px = a·F + (1−a)·B      (B known per pixel)
    a  = clamp( dot(px−B, D−B) / |D−B|² )   D = estimated decal color
    F  = B + (px−B)/a       (a > 0)

This recovers proper soft alpha through DXT block noise and antialiased
edges — thin stencil lettering keeps its counters (no more 'O's filling
in), dotted lines survive, and recomposing F over a NEW base color leaves
no old-color halos. Output: RGBA PNGs embedded in the DLL; runtime
composites them over the synthesized base and never samples the decal
albedo at all.

Noise rejection: hysteresis on alpha (weak pixels survive only in
components containing a strong pixel) + a small minimum component size.
Thresholds are on ALPHA, not raw diff, so grout/grain noise (small
projection onto the base→decal color line) drops out while tiny pure-art
elements (text, dots, dashes) are strongly-alpha'd and survive.

Re-run whenever archive diffs show a decal albedo changed; eyeball the
printed stats and the PNGs. Requires: UnityPy pillow scipy numpy.
"""

import os
import numpy as np
from PIL import Image
from scipy import ndimage
import UnityPy

GAME_ASSETS = os.environ.get(
    "MSS_ASSETS",
    "../../game/Megastore Simulator_Data/sharedassets1.assets")
OUT = os.path.dirname(os.path.abspath(__file__))

WALLS = {
    # out_name: (decal albedo, base albedo or None=flat bg,
    #            a_weak, a_strong, min_area)
    "art_Wall28.png":       ("Wall28", "Wall27", 0.10, 0.55, 16),
    "art_tile23WDecal.png": ("tile23WDecal", "Tile23Albedo", 0.12, 0.55, 16),
    "art_summer_palm.png":  ("Adsız tasarım - 2026-06-06T195402.433", None, 0.10, 0.50, 16),
}

env = UnityPy.load(GAME_ASSETS)
tex = {}
wanted = {n for v in WALLS.values() for n in (v[0], v[1]) if n}
for obj in env.objects:
    if obj.type.name == "Texture2D":
        d = obj.read()
        if d.m_Name in wanted:
            tex[d.m_Name] = d.image.convert("RGB")

def arr(im, size=None):
    if size:
        im = im.resize(size, Image.LANCZOS)
    return np.asarray(im, dtype=np.float64)

for out_name, (decal_name, base_name, a_weak, a_strong, min_area) in WALLS.items():
    decal = arr(tex[decal_name])
    h, w = decal.shape[:2]
    if base_name:
        B = arr(tex[base_name], (w, h))
    else:
        m0 = decal[::8, ::8].reshape(-1, 3).mean(axis=0)
        near = np.abs(decal - m0).sum(axis=2) < 45
        B = np.broadcast_to(decal[near].mean(axis=0), decal.shape)

    # Estimate the decal color D from clearly-decal pixels.
    rawdiff = np.abs(decal - B).sum(axis=2)
    strong_px = decal[rawdiff > np.percentile(rawdiff[rawdiff > 30], 60)] \
        if (rawdiff > 30).any() else decal[rawdiff > 30]
    D = np.median(strong_px, axis=0)

    # Color-line matte.
    line = D[None, None, :] - B
    denom = np.maximum((line * line).sum(axis=2), 1e-6)
    a = np.clip(((decal - B) * line).sum(axis=2) / denom, 0.0, 1.0)

    # Hysteresis + speck filter on alpha.
    weak = a > a_weak
    strong = a > a_strong
    lbl, _ = ndimage.label(weak)
    keep = np.unique(lbl[strong])
    keep = keep[keep != 0]
    sizes = ndimage.sum(np.ones_like(lbl), lbl, keep)
    keep = keep[sizes >= min_area]
    region = np.isin(lbl, keep)
    a = np.where(region, a, 0.0)

    # Unmix the foreground.
    safe_a = np.maximum(a, 1e-3)[..., None]
    F = np.clip(B + (decal - B) / safe_a, 0, 255)
    F = np.where(a[..., None] > 0, F, 0)

    rgba = np.dstack([F.astype(np.uint8), np.round(a * 255).astype(np.uint8)])
    Image.fromarray(rgba, mode="RGBA").save(os.path.join(OUT, out_name), optimize=True)
    print(f"{out_name:24} D={D.round(0)} comps={len(keep):4d} "
          f"coverage={(a > 0).mean()*100:5.2f}% "
          f"size={os.path.getsize(os.path.join(OUT, out_name))//1024}KB")

# ──────────────────────────────────────────────────────────────────────
# Preserve layers: grout/mortar mattes for the BASE walls, composited
# back at original color after face colorization (Tint flavor) so
# recoloring tiles/bricks leaves the grout alone — same unmixing idea
# as the decal art, one level down. Baked at ≤2048 (runtime caps
# generated textures there; vanilla ships the decal brick at 2048 too).

PRESERVE = {
    # out_name: (albedo, mode, bake_res, field_pct, top_pct)
    # luma mode: grout/mortar = the population brighter than field_pct.
    "preserve_Wall27.png":        ("Wall27", "colorline", 2048, None, None),
    "preserve_Tile23Albedo.png":  ("Tile23Albedo", "luma", 2048, 70, 99),
    # Fish Market Wall: white grout over varied blue-grey tiles; tighter
    # band since the tile faces carry bright flecks themselves.
    "preserve_FishMarket.png":    ("Adsız tasarım - 2026-04-01T200132.265",
                                   "luma", 2048, 88, 99),
}

for out_name, (name, mode, res, field_pct, top_pct) in PRESERVE.items():
    if name not in tex:
        env2 = env  # already loaded; fetch lazily
        for obj in env2.objects:
            if obj.type.name == "Texture2D":
                d = obj.read()
                if d.m_Name == name:
                    tex[name] = d.image.convert("RGB")
                    break
    a3 = arr(tex[name])  # matte at native res for precision
    h, w = a3.shape[:2]
    mean = a3[::8, ::8].reshape(-1, 3).mean(axis=0)

    if mode == "luma":
        # grout/mortar = the lighter-luminance population above the field
        luma = a3 @ [0.299, 0.587, 0.114]
        field = np.percentile(luma[::4, ::4], field_pct)
        top = np.percentile(luma[::4, ::4], top_pct)
        alpha = np.clip((luma - field) / max(1.0, top - field), 0, 1)
        strong = alpha > 0.6
        weak = alpha > 0.15
        F = a3
    else:
        # grout = the far-from-field color population (color-line matte)
        diff = np.abs(a3 - mean).sum(axis=2)
        D = np.median(a3[diff > np.percentile(diff[diff > 30], 60)], axis=0)
        line = D - mean
        alpha = np.clip(((a3 - mean) @ line) / max(line @ line, 1e-6), 0, 1)
        strong = alpha > 0.5
        weak = alpha > 0.1
        safe = np.maximum(alpha, 1e-3)[..., None]
        F = np.clip(mean + (a3 - mean) / safe, 0, 255)

    lbl, _ = ndimage.label(weak)
    keep = np.unique(lbl[strong]); keep = keep[keep != 0]
    sizes = ndimage.sum(np.ones_like(lbl), lbl, keep)
    keep = keep[sizes >= 40]
    alpha = np.where(np.isin(lbl, keep), alpha, 0)
    F = np.where(alpha[..., None] > 0, F, 0)

    # Premultiplied-alpha downscale to bake_res (straight-alpha resize halos).
    if w != res or h != res:
        pm = F * alpha[..., None]
        pm_img = Image.fromarray(np.clip(pm, 0, 255).astype(np.uint8)).resize((res, res), Image.LANCZOS)
        a_img = Image.fromarray(np.round(alpha * 255).astype(np.uint8), "L").resize((res, res), Image.LANCZOS)
        a_r = np.asarray(a_img, dtype=np.float64) / 255.0
        pm_r = np.asarray(pm_img, dtype=np.float64)
        F = np.where(a_r[..., None] > 1e-3, np.clip(pm_r / np.maximum(a_r[..., None], 1e-3), 0, 255), 0)
        alpha = a_r

    rgba = np.dstack([F.astype(np.uint8), np.round(alpha * 255).astype(np.uint8)])
    Image.fromarray(rgba, mode="RGBA").save(os.path.join(OUT, out_name), optimize=True)
    print(f"{out_name:28} coverage={(alpha > 0).mean()*100:5.2f}% "
          f"size={os.path.getsize(os.path.join(OUT, out_name))//1024}KB")
