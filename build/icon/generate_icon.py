#!/usr/bin/env python3
"""Generates the CaYaScreenBridge application icon.

The mark follows the CaYaDev identity: a near black panel with a deep red glow, an angular red
"near" screen and an angular white "far" screen, and a cursor crossing the gap between them. The
angular cuts echo the clipped corners of the CaYaDev wordmark.

Run:  python3 build/icon/generate_icon.py
Writes: src/CaYaScreenBridge.Windows/Assets/app.ico and the PNG previews next to it.
"""

from __future__ import annotations

import os
from PIL import Image, ImageDraw, ImageFilter

# --- brand palette ---------------------------------------------------------------------------
BG_DEEP = (8, 10, 18, 255)
BG_EDGE = (14, 17, 27, 255)
GLOW = (170, 18, 32)
RED = (229, 32, 43, 255)
RED_DARK = (154, 16, 26, 255)
WHITE = (255, 255, 255, 255)
WHITE_DIM = (214, 219, 230, 255)

SIZE = 1024  # master resolution, downsampled for every icon entry
ICO_SIZES = [256, 128, 64, 48, 40, 32, 24, 20, 16]


def s(value: float) -> int:
    """Scales a coordinate expressed against a 256 unit design grid."""
    return round(value * SIZE / 256.0)


def rounded_panel() -> Image.Image:
    """Dark rounded panel with a red radial glow in the lower left, as on the CaYaDev site."""
    panel = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    draw = ImageDraw.Draw(panel)
    draw.rounded_rectangle(
        [0, 0, SIZE - 1, SIZE - 1],
        radius=s(58),
        fill=BG_EDGE,
    )

    glow = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    gdraw = ImageDraw.Draw(glow)
    cx, cy, r = s(74), s(196), s(150)
    for step in range(28, 0, -1):
        alpha = int(150 * (step / 28.0) ** 2.4)
        radius = int(r * step / 28.0)
        gdraw.ellipse([cx - radius, cy - radius, cx + radius, cy + radius], fill=GLOW + (alpha,))
    glow = glow.filter(ImageFilter.GaussianBlur(s(14)))

    panel = Image.alpha_composite(panel, glow)

    # Vignette towards the top right keeps the mark grounded at small sizes.
    shade = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    sdraw = ImageDraw.Draw(shade)
    sdraw.polygon(
        [(SIZE, 0), (SIZE, s(120)), (s(120), 0)],
        fill=BG_DEEP[:3] + (110,),
    )
    shade = shade.filter(ImageFilter.GaussianBlur(s(40)))
    panel = Image.alpha_composite(panel, shade)

    # Re-apply the rounded mask so the glow never bleeds past the corners.
    mask = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, SIZE - 1, SIZE - 1], radius=s(58), fill=255)
    panel.putalpha(mask)
    return panel


def angular_screen(points, fill, outline=None, width=0):
    layer = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    draw.polygon([(s(x), s(y)) for x, y in points], fill=fill, outline=outline, width=width)
    return layer


def build() -> Image.Image:
    image = rounded_panel()

    # Near screen: red, denser, on the left. Angular bottom left cut, echoing the wordmark.
    image = Image.alpha_composite(
        image,
        angular_screen([(30, 92), (108, 92), (108, 164), (44, 164), (30, 150)], RED),
    )
    image = Image.alpha_composite(
        image,
        angular_screen([(57, 164), (81, 164), (87, 184), (51, 184)], RED_DARK),
    )

    # Far screen: larger and white, on the right. Angular top right cut.
    image = Image.alpha_composite(
        image,
        angular_screen([(150, 68), (212, 68), (226, 82), (226, 156), (150, 156)], WHITE),
    )
    image = Image.alpha_composite(
        image,
        angular_screen([(176, 156), (200, 156), (206, 178), (170, 178)], WHITE_DIM),
    )

    # The bridge itself: an accent bar spanning the gap between the two panels.
    bridge = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    ImageDraw.Draw(bridge).polygon(
        [(s(104), s(140)), (s(154), s(140)), (s(154), s(152)), (s(104), s(152))],
        fill=RED,
    )
    image = Image.alpha_composite(image, bridge)

    # The cursor crossing that bridge, from the red panel onto the white one.
    cursor_points = [
        (100, 84), (100, 138), (113, 125), (122, 144), (132, 139), (123, 121), (140, 120),
    ]

    shadow = angular_screen(
        [(x + 4, y + 5) for x, y in cursor_points],
        (0, 0, 0, 170),
    ).filter(ImageFilter.GaussianBlur(s(3)))
    image = Image.alpha_composite(image, shadow)

    image = Image.alpha_composite(image, angular_screen(cursor_points, WHITE))

    return image


def main() -> None:
    root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
    assets = os.path.join(root, "src", "CaYaScreenBridge.Windows", "Assets")
    os.makedirs(assets, exist_ok=True)

    master = build()
    master.save(os.path.join(assets, "app-256.png"))
    master.resize((512, 512), Image.LANCZOS).save(os.path.join(assets, "app-512.png"))

    frames = [master.resize((size, size), Image.LANCZOS) for size in ICO_SIZES]
    frames[0].save(
        os.path.join(assets, "app.ico"),
        format="ICO",
        sizes=[(size, size) for size in ICO_SIZES],
        append_images=frames[1:],
    )

    # A monochrome variant for the tray, so the icon stays legible on a light taskbar.
    tray = build()
    tray.resize((256, 256), Image.LANCZOS).save(os.path.join(assets, "tray-256.png"))

    print(f"Wrote {assets}/app.ico ({', '.join(str(x) for x in ICO_SIZES)})")


if __name__ == "__main__":
    main()
