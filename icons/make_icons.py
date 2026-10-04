"""Builds 512x512 CustomItem icons for the Livestock Airdrops signals from the game's supply signal icon,
plus one Workshop folder per animal (icon.png + manifest.txt) ready for SteamCMD upload."""
import colorsys
import json
import os

from PIL import Image, ImageDraw, ImageFilter, ImageFont

# The game's own supply signal icon (Rust client: Bundles/items/supply.signal.png) and a bold label font.
BASE_ICON = os.environ.get("RUST_SUPPLY_SIGNAL_ICON", r"E:\SteamLibrary\steamapps\common\Rust\Bundles\items\supply.signal.png")
LABEL_FONT = os.environ.get("ICON_LABEL_FONT", r"C:\Windows\Fonts\seguibl.ttf")
AUTHOR_STEAM_ID = 76561198830318125
OUT_DIR = os.path.dirname(os.path.abspath(__file__))
TWEMOJI_DIR = os.path.join(OUT_DIR, "twemoji")  # CC-BY 4.0, see twemoji/LICENSE.txt

# folder name, Twemoji code point, band/label colour, badge background (chosen to contrast with the animal), label
ANIMALS = [
    ("prize_cow", "1f404", (40, 40, 40), (150, 205, 120), "COW"),
    ("enraged_bull", "1f402", (200, 30, 30), (255, 214, 140), "BULL"),
    ("domestic_sheep", "1f411", (235, 225, 200), (110, 165, 85), "SHEEP"),
    ("proud_ram", "1f40f", (120, 90, 60), (225, 195, 145), "RAM"),
    ("baby_lamb", "1f411", (245, 160, 200), (245, 160, 200), "LAMB"),
    ("baby_calf", "1f42e", (110, 175, 235), (110, 175, 235), "CALF"),
]

BADGE_CENTER = (402, 104)
BADGE_RADIUS = 96


def recolour_band(img, colour):
    """Repaints the purple band in the animal colour, keeping its shading."""
    target_h, target_l, target_s = colorsys.rgb_to_hls(*(c / 255 for c in colour))
    px = img.load()
    for y in range(img.height):
        for x in range(img.width):
            r, g, b, a = px[x, y]
            if a == 0:
                continue
            h, l, s = colorsys.rgb_to_hls(r / 255, g / 255, b / 255)
            # The band is the only strongly saturated magenta/purple in the icon.
            if 0.70 < h < 0.95 and s > 0.18 and l > 0.08:
                new_l = max(0.0, min(1.0, target_l * (l / 0.45)))
                nr, ng, nb = colorsys.hls_to_rgb(target_h, new_l, target_s if target_s > 0.05 else 0.0)
                px[x, y] = (int(nr * 255), int(ng * 255), int(nb * 255), a)
    return img


def luminance(colour):
    r, g, b = colour
    return 0.299 * r + 0.587 * g + 0.114 * b


def draw_badge(img, emoji, colour):
    cx, cy = BADGE_CENTER
    r = BADGE_RADIUS

    # Soft shadow so the badge lifts off the icon.
    shadow = Image.new("RGBA", img.size, (0, 0, 0, 0))
    ImageDraw.Draw(shadow).ellipse((cx - r + 6, cy - r + 10, cx + r + 6, cy + r + 10), fill=(0, 0, 0, 140))
    img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(8)))

    d = ImageDraw.Draw(img)
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(255, 255, 255, 255))
    inner = r - 9
    d.ellipse((cx - inner, cy - inner, cx + inner, cy + inner), fill=colour + (255,))

    art = Image.open(os.path.join(TWEMOJI_DIR, emoji + ".png")).convert("RGBA")
    size = int(inner * 1.45)
    art = art.resize((size, size), Image.LANCZOS)
    img.alpha_composite(art, (cx - size // 2, cy - size // 2))


def draw_label(img, text, colour):
    d = ImageDraw.Draw(img)
    font = ImageFont.truetype(LABEL_FONT, 62)
    left, top, right, bottom = d.textbbox((0, 0), text, font=font)
    w, h = right - left, bottom - top
    pad_x, pad_y = 22, 12
    x0 = 256 - w // 2 - pad_x
    y0 = 494 - h - 2 * pad_y
    box = (x0, y0, x0 + w + 2 * pad_x, y0 + h + 2 * pad_y)

    shadow = Image.new("RGBA", img.size, (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle((box[0] + 4, box[1] + 6, box[2] + 4, box[3] + 6), 18, fill=(0, 0, 0, 150))
    img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(5)))

    d.rounded_rectangle(box, 18, fill=colour + (255,), outline=(255, 255, 255, 255), width=5)
    text_colour = (25, 25, 25, 255) if luminance(colour) > 150 else (255, 255, 255, 255)
    d.text(((box[0] + box[2]) // 2, (box[1] + box[3]) // 2), text, font=font, fill=text_colour, anchor="mm")


# Same shape as the icon-only "CustomItem" skins Rust downloads from the Workshop: no textures, just icon.png.
def write_manifest(folder):
    manifest = {
        "Version": 3,
        "ItemType": "CustomItem",
        "AuthorId": AUTHOR_STEAM_ID,
        "PublishDate": "2026-10-04T00:00:00.0000000Z",
        "Groups": [{
            "Textures": {},
            "Floats": {"_Cutoff": 0.0, "_BumpScale": 1.0, "_Glossiness": 0.0, "_OcclusionStrength": 1.0},
            "Colors": {
                "_Color": {"r": 1.0, "g": 1.0, "b": 1.0},
                "_SpecColor": {"r": 0.0, "g": 0.0, "b": 0.0},
                "_EmissionColor": {"r": 0.0, "g": 0.0, "b": 0.0},
            },
        }],
    }
    with open(os.path.join(folder, "manifest.txt"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)


def make_icon(name, emoji, colour, badge_colour, label):
    img = Image.open(BASE_ICON).convert("RGBA").resize((512, 512), Image.LANCZOS)
    img = recolour_band(img, colour)
    draw_badge(img, emoji, badge_colour)
    draw_label(img, label, colour)
    folder = os.path.join(OUT_DIR, "workshop", name)
    os.makedirs(folder, exist_ok=True)
    img.save(os.path.join(folder, "icon.png"))
    write_manifest(folder)
    return img


def make_preview(icons):
    """One sheet with each icon large plus a hotbar-sized copy, on a Rust-like dark background."""
    cell, small, gap = 256, 80, 24
    width = len(icons) * (cell + gap) + gap
    sheet = Image.new("RGBA", (width, cell + small + 3 * gap), (38, 40, 36, 255))
    for i, img in enumerate(icons):
        x = gap + i * (cell + gap)
        sheet.alpha_composite(img.resize((cell, cell), Image.LANCZOS), (x, gap))
        slot = Image.new("RGBA", (small + 8, small + 8), (70, 72, 66, 255))
        slot.alpha_composite(img.resize((small, small), Image.LANCZOS), (4, 4))
        sheet.alpha_composite(slot, (x + (cell - small) // 2 - 4, cell + 2 * gap - 4))
    sheet.save(os.path.join(OUT_DIR, "preview.png"))


if __name__ == "__main__":
    made = [make_icon(*animal) for animal in ANIMALS]
    make_preview(made)
    print("ok")
