#!/usr/bin/env python3
"""Draws Onething's icon (one lit task, the rest faint) and writes every platform's copy.

The mark's shapes live here once; each platform gets them in its own format and frame.
Needs: pip install cairosvg pillow.  Run from the repo root:  python3 build/icons.py
"""

import io
import json
import pathlib

import cairosvg
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parent.parent

# Brand gradient for the icon (a little deeper than the web app's accent, so it holds up on any wallpaper).
STOPS = [(0, "#4f46e5"), (0.5, "#7c3aed"), (1, "#db2777")]
DARK_STOPS = [(0, "#1e1b4b"), (1, "#0b0a1f")]

# The mark on a 1024 grid: the task to do now (bright) and two later ones (faint).
NOW_DOT = (322, 330, 110)
NOW_BAR = (482, 290, 332, 80)
LATER = [  # (dot cx, cy, r), (bar x, y, w, h), opacity
    ((322, 560, 62), (442, 534, 300, 52), 0.3),
    ((322, 740, 62), (442, 714, 240, 52), 0.18),
]


def gradient(id_, stops):
    s = "".join(f'<stop offset="{o}" stop-color="{c}"/>' for o, c in stops)
    return f'<linearGradient id="{id_}" x1="0" y1="0" x2="1" y2="1">{s}</linearGradient>'


def mark(now="#fff", later="#fff", glow=True):
    cx, cy, r = NOW_DOT
    x, y, w, h = NOW_BAR
    out = ""
    if glow:
        out += f'<circle cx="{cx}" cy="{cy}" r="260" fill="url(#glow)"/>'
    out += f'<circle cx="{cx}" cy="{cy}" r="{r}" fill="{now}"/>'
    out += f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{h / 2}" fill="{now}"/>'
    for (dx, dy, dr), (bx, by, bw, bh), o in LATER:
        out += f'<circle cx="{dx}" cy="{dy}" r="{dr}" fill="{later}" fill-opacity="{o}"/>'
        out += f'<rect x="{bx}" y="{by}" width="{bw}" height="{bh}" rx="{bh / 2}" fill="{later}" fill-opacity="{o}"/>'
    return out


DEFS = (
    "<defs>"
    + gradient("bg", STOPS)
    + gradient("dark", DARK_STOPS)
    + '<radialGradient id="glow" cx=".5" cy=".5" r=".5"><stop offset="0" stop-color="#fff" stop-opacity=".45"/>'
    '<stop offset=".5" stop-color="#fff" stop-opacity=".1"/><stop offset="1" stop-color="#fff" stop-opacity="0"/></radialGradient>'
    '<linearGradient id="sheen" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff" stop-opacity=".14"/>'
    '<stop offset=".55" stop-color="#fff" stop-opacity="0"/></linearGradient>'
    "</defs>"
)


def full_bleed(variant="light"):
    """A square icon with no corners: iOS, Android and the website round it themselves."""
    if variant == "dark":
        body = '<rect width="1024" height="1024" fill="url(#dark)"/>' + mark(now="url(#bg)", later="#a78bfa", glow=False)
    elif variant == "tinted":
        body = '<rect width="1024" height="1024" fill="#000"/>' + mark(now="#fff", later="#fff", glow=False)
    else:
        body = '<rect width="1024" height="1024" fill="url(#bg)"/><rect width="1024" height="1024" fill="url(#sheen)"/>' + mark()
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024">{DEFS}{body}</svg>'


def mac():
    """The Mac draws icons as given: a rounded square inside the standard margin, with a soft shadow."""
    inner = (
        '<rect width="1024" height="1024" fill="url(#bg)"/><rect width="1024" height="1024" fill="url(#sheen)"/>' + mark()
    )
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024">{DEFS}'
        '<filter id="shadow" x="-10%" y="-10%" width="120%" height="125%"><feGaussianBlur stdDeviation="14"/></filter>'
        '<clipPath id="squircle"><rect x="100" y="100" width="824" height="824" rx="185"/></clipPath>'
        '<rect x="100" y="112" width="824" height="824" rx="185" fill="#000" fill-opacity=".3" filter="url(#shadow)"/>'
        f'<g clip-path="url(#squircle)"><g transform="translate(100 100) scale({824 / 1024})">{inner}</g></g></svg>'
    )


def small_logo():
    """The 64-unit logo the web app, extension and website show next to the name."""
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">{DEFS}'
        '<clipPath id="r"><rect x="4" y="4" width="56" height="56" rx="16"/></clipPath>'
        f'<g clip-path="url(#r)"><g transform="translate(4 4) scale({56 / 1024})">'
        '<rect width="1024" height="1024" fill="url(#bg)"/>' + mark(glow=False) + "</g></g></svg>\n"
    )


def png(svg, size):
    return Image.open(io.BytesIO(cairosvg.svg2png(bytestring=svg.encode(), output_width=size, output_height=size)))


def save(img, path, opaque=False):
    path = ROOT / path
    path.parent.mkdir(parents=True, exist_ok=True)
    (img.convert("RGB") if opaque else img).save(path, optimize=True)


def apple():
    folder = "apple/App/Assets.xcassets/AppIcon.appiconset"
    save(png(full_bleed(), 1024), f"{folder}/ios.png", opaque=True)
    save(png(full_bleed("dark"), 1024), f"{folder}/ios-dark.png", opaque=True)
    save(png(full_bleed("tinted"), 1024), f"{folder}/ios-tinted.png", opaque=True)
    images = [
        {"filename": "ios.png", "idiom": "universal", "platform": "ios", "size": "1024x1024"},
        {"appearances": [{"appearance": "luminosity", "value": "dark"}], "filename": "ios-dark.png", "idiom": "universal", "platform": "ios", "size": "1024x1024"},
        {"appearances": [{"appearance": "luminosity", "value": "tinted"}], "filename": "ios-tinted.png", "idiom": "universal", "platform": "ios", "size": "1024x1024"},
    ]
    for pt in (16, 32, 128, 256, 512):
        for scale in (1, 2):
            name = f"mac-{pt}@{scale}x.png"
            save(png(mac(), pt * scale), f"{folder}/{name}")
            images.append({"filename": name, "idiom": "mac", "scale": f"{scale}x", "size": f"{pt}x{pt}"})
    info = {"author": "xcode", "version": 1}
    (ROOT / folder / "Contents.json").write_text(json.dumps({"images": images, "info": info}, indent=2) + "\n")
    (ROOT / "apple/App/Assets.xcassets/Contents.json").write_text(json.dumps({"info": info}, indent=2) + "\n")


def android():
    """The adaptive icon's foreground: the mark scaled into the 72dp the launcher shows (18..90 of 108)."""
    def vector(color_now, color_later, mono=False):
        k = 72 / 1024
        def p(v):
            return f"{18 + v * k:.2f}"
        def circle(cx, cy, r):
            return f"M{p(cx - r)},{p(cy)} a{r * k:.2f},{r * k:.2f} 0 1,0 {2 * r * k:.2f},0 a{r * k:.2f},{r * k:.2f} 0 1,0 {-2 * r * k:.2f},0z"
        def pill(x, y, w, h):
            rr = h / 2 * k
            return (f"M{p(x + h / 2)},{p(y)} h{(w - h) * k:.2f} a{rr:.2f},{rr:.2f} 0 0,1 0,{h * k:.2f} "
                    f"h{-(w - h) * k:.2f} a{rr:.2f},{rr:.2f} 0 0,1 0,{-h * k:.2f}z")
        paths = [(circle(*NOW_DOT) + " " + pill(*NOW_BAR), color_now, 1)]
        for dot, bar, o in LATER:
            paths.append((circle(*dot) + " " + pill(*bar), color_later, o if not mono else o + 0.25))
        body = "".join(
            f'\n    <path\n        android:pathData="{d}"\n        android:fillColor="{c}"\n        android:fillAlpha="{a:.2f}" />'
            for d, c, a in paths
        )
        return (
            '<?xml version="1.0" encoding="utf-8"?>\n'
            "<!-- Onething's mark: the task to do now, lit, and two later ones, faint. Drawn by build/icons.py;\n"
            "     inside the adaptive icon's safe zone. -->\n"
            '<vector xmlns:android="http://schemas.android.com/apk/res/android"\n'
            '    android:width="108dp"\n    android:height="108dp"\n'
            '    android:viewportWidth="108"\n    android:viewportHeight="108">'
            f"{body}\n</vector>\n"
        )
    res = ROOT / "android/app/src/main/res"
    (res / "drawable/ic_launcher_foreground.xml").write_text(vector("#FFFFFFFF", "#FFFFFFFF"))
    (res / "drawable/ic_launcher_monochrome.xml").write_text(vector("#FFFFFFFF", "#FFFFFFFF", mono=True))


def windows():
    img = png(mac(), 256)
    (ROOT / "src/TodoTracker.Windows/Assets").mkdir(parents=True, exist_ok=True)
    img.save(ROOT / "src/TodoTracker.Windows/Assets/app.ico", sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])


def web():
    logo = small_logo()
    for path in ("src/TodoTracker.Server/wwwroot/img/logo.svg", "extension/icons/logo.svg", "website/public/img/favicon.svg"):
        (ROOT / path).write_text(logo)
    save(png(logo, 128), "extension/icons/icon128.png")
    save(png(logo, 128), "integrations/claude-desktop/icon.png")
    save(png(full_bleed(), 256), "website/public/img/apple-touch-icon.png", opaque=True)
    (ROOT / "build/icon.svg").write_text(full_bleed() + "\n")


if __name__ == "__main__":
    apple()
    android()
    windows()
    web()
    print("Icons written.")
