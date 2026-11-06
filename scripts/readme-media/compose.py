"""Places the rendered panel images on an invented desktop for the README.

Usage: python compose.py <render folder> <output folder>

The render folder is what ReadmeMedia.exe wrote: transparent captures of the panel and hero/frames.json with the
frames of the animation. Nothing here comes from a real screen: the wallpaper, the window behind the panel and the
pointer are drawn below.
"""

import json
import math
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

FPS = 30


def wallpaper(width, height, seed=0):
    """A soft, muted gradient. Blobs are drawn small, blurred, then enlarged, so the result has no hard edges."""
    small = (max(8, width // 8), max(8, height // 8))
    image = Image.new("RGB", small, (214, 219, 228))
    draw = ImageDraw.Draw(image)
    w, h = small
    blobs = [
        ((-.15, -.4, .55, .9), (178, 192, 212)),
        ((.45, -.3, 1.2, .7), (232, 216, 200)),
        ((.2, .45, .85, 1.5), (198, 192, 216)),
        ((-.2, .6, .35, 1.4), (206, 214, 204)),
    ]
    for (x0, y0, x1, y1), color in blobs:
        draw.ellipse((x0 * w, y0 * h, x1 * w, y1 * h), fill=color)
    image = image.filter(ImageFilter.GaussianBlur(min(w, h) * .28))
    return image.resize((width, height), Image.BICUBIC).convert("RGBA")


def window(size, scale):
    """A light application window with placeholder lines instead of text."""
    width, height = size
    s = scale
    layer = Image.new("RGBA", (width + int(80 * s), height + int(80 * s)), (0, 0, 0, 0))
    shadow = Image.new("RGBA", layer.size, (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle((40 * s, 46 * s, 40 * s + width, 46 * s + height), 10 * s, fill=(30, 36, 52, 70))
    layer.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(18 * s)))
    body = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    d = ImageDraw.Draw(body)
    d.rounded_rectangle((0, 0, width - 1, height - 1), 8 * s, fill=(250, 250, 251, 255), outline=(222, 224, 230, 255), width=max(1, round(s)))
    d.rectangle((0, 36 * s, width, 37 * s), fill=(232, 234, 238, 255))
    # Windows 11 title bar: app icon and title at the left, minimize, maximize and close at the right.
    d.rounded_rectangle((14 * s, 11 * s, 28 * s, 25 * s), 3 * s, fill=(96, 132, 196, 255))
    d.rounded_rectangle((38 * s, 15 * s, 128 * s, 21 * s), 3 * s, fill=(214, 218, 225, 255))
    glyph, line = (92, 96, 104, 255), max(1, round(s))
    cy = 18 * s
    for i in range(3):
        cx = width - (23 + (2 - i) * 46) * s
        if i == 0:
            d.line((cx - 5 * s, cy, cx + 5 * s, cy), fill=glyph, width=line)
        elif i == 1:
            d.rectangle((cx - 5 * s, cy - 5 * s, cx + 5 * s, cy + 5 * s), outline=glyph, width=line)
        else:
            d.line((cx - 5 * s, cy - 5 * s, cx + 5 * s, cy + 5 * s), fill=glyph, width=line)
            d.line((cx - 5 * s, cy + 5 * s, cx + 5 * s, cy - 5 * s), fill=glyph, width=line)
    d.rounded_rectangle((0, 37 * s, 190 * s, height - 1), 0, fill=(244, 245, 247, 255))
    for i in range(9):
        y = (58 + i * 22) * s
        d.rounded_rectangle((20 * s, y, (20 + [96, 120, 80, 132, 104, 88, 124, 70, 110][i]) * s, y + 8 * s), 4 * s, fill=(222, 225, 231, 255))
    widths = [260, 420, 360, 500, 300, 460, 390, 220, 480, 340, 410, 280]
    for i, w in enumerate(widths):
        y = (60 + i * 24) * s
        x = (220 + (i % 4 in (1, 2)) * 24) * s
        d.rounded_rectangle((x, y, x + w * s, y + 9 * s), 4.5 * s, fill=(226, 229, 235, 255) if i % 3 else (208, 216, 232, 255))
    layer.alpha_composite(body, (int(40 * s), int(40 * s)))
    return layer, (int(40 * s), int(40 * s))


def cursor(scale, pressed=False):
    """The standard arrow pointer, drawn at four times the size and reduced for smooth edges."""
    points = [(0, 0), (0, 17), (4.2, 13.2), (7, 19.6), (9.4, 18.6), (6.6, 12.3), (12.2, 12.3)]
    factor = 4 * scale * (.88 if pressed else 1)
    size = int(26 * factor)
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(image)
    outline = [(x * factor + 3 * factor, y * factor + 3 * factor) for x, y in points]
    shadow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).polygon([(x + 1.2 * factor, y + 1.8 * factor) for x, y in outline], fill=(0, 0, 0, 90))
    image.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(1.4 * factor)))
    d.polygon(outline, fill=(255, 255, 255, 255), outline=(0, 0, 0, 255), width=max(1, int(1.1 * factor)))
    image = image.resize((size // 4, size // 4), Image.LANCZOS)
    return image, (int(3 * scale), int(3 * scale))


def ease(t):
    t = min(1, max(0, t))
    return t * t * (3 - 2 * t)


def path_at(keys, t):
    """Pointer position at time t; keys are (time, x, y) and moves between them are eased."""
    if t <= keys[0][0]:
        return keys[0][1:]
    for (t0, x0, y0), (t1, x1, y1) in zip(keys, keys[1:]):
        if t <= t1:
            p = ease((t - t0) / (t1 - t0)) if t1 > t0 else 1
            return x0 + (x1 - x0) * p, y0 + (y1 - y0) * p
    return keys[-1][1:]


def rounded(image, radius):
    mask = Image.new("L", image.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, image.width - 1, image.height - 1), radius, fill=255)
    result = image.copy()
    result.putalpha(ImageChops.multiply(result.getchannel("A"), mask))
    return result


def hero(render, out):
    data = json.loads((render / "hero" / "frames.json").read_text(encoding="utf-8"))
    marks, frames = data["marks"], data["frames"]
    scale = marks["scale"]
    stage_w, stage_h = marks["stage"]["width"], marks["stage"]["height"]
    canvas_w, canvas_h = 1000, 300
    ox = (canvas_w - stage_w) / 2
    W, H = round(canvas_w * scale), round(canvas_h * scale)
    background = wallpaper(W, H)
    layer, _ = window((round(820 * scale), round(360 * scale)), scale)
    background.alpha_composite(layer, (round((canvas_w - 820) / 2 * scale) - round(40 * scale), round(96 * scale) - round(40 * scale)))
    arrow, hot = cursor(scale)
    arrow_pressed, hot_pressed = cursor(scale, pressed=True)

    expand, switch, collapse, end = marks["expand"], marks["switch"], marks["collapse"], marks["end"]
    monitor_switch = marks["monitorSwitch"]
    button, monitor_button = marks["musicButton"], marks["monitorButton"]
    music, monitor = marks["musicPanel"], marks["monitorPanel"]
    start = (660, 262)
    keys = [
        (0.0, *start),
        (expand - .15, stage_w / 2 + 4, 17),
        (expand + .15, stage_w / 2 + 4, 20),
        (expand + .9, 250, 150),
        (switch - .35, button["x"] + 2, button["y"] + 2),
        (switch + .35, button["x"] + 2, button["y"] + 2),
        (switch + .95, music["x"] + music["width"] * .56, 168),
        (monitor_switch - .9, music["x"] + music["width"] * .56, 168),
        (monitor_switch - .35, monitor_button["x"] + 2, monitor_button["y"] + 2),
        (monitor_switch + .35, monitor_button["x"] + 2, monitor_button["y"] + 2),
        (monitor_switch + .95, monitor["x"] + monitor["width"] * .3, 150),
        (collapse - .6, monitor["x"] + monitor["width"] * .3, 150),
        (collapse, monitor["x"] + monitor["width"] * .4, monitor["height"] + 34),
        (end, *start),
    ]
    times = [f["time"] for f in frames]
    images = {}

    def frame_at(t):
        i = min(range(len(times)), key=lambda k: abs(times[k] - t))
        if i not in images:
            images[i] = Image.open(render / "hero" / frames[i]["file"]).convert("RGBA")
        return images[i]

    # Shown where animation is not supported: the open usage page, without the pointer.
    still_frame = background.copy()
    still_frame.alpha_composite(frame_at((expand + switch) / 2), (round(ox * scale), 0))
    still_frame = rounded(still_frame, round(12 * scale))
    output = []
    for n in range(int(end * FPS)):
        t = n / FPS
        canvas = background.copy()
        canvas.alpha_composite(frame_at(t), (round(ox * scale), 0))
        x, y = path_at(keys, t)
        pressed = any(s - .08 <= t <= s + .06 for s in (switch, monitor_switch))
        sprite, spot = (arrow_pressed, hot_pressed) if pressed else (arrow, hot)
        canvas.alpha_composite(sprite, (round((ox + x) * scale) - spot[0], round(y * scale) - spot[1]))
        output.append(rounded(canvas, round(12 * scale)))
    duration = round(1000 / FPS)
    still_frame.save(out / "hero.png", save_all=True, append_images=output, default_image=True, duration=duration, loop=0, optimize=True)
    return len(output)


def still(render, out, name, margin=(48, 0, 48, 48), scale=2, radius=12):
    """A capture on the wallpaper; margin is left, top, right, bottom in DIPs around the visible panel."""
    image = Image.open(render / f"{name}.png").convert("RGBA")
    x0, y0, x1, y1 = image.getbbox()
    left, top, right, bottom = (round(m * scale) for m in margin)
    box = (x0 - left, 0, x1 + right, y1 + bottom)
    canvas = wallpaper(box[2] - box[0], box[3] - box[1])
    canvas.alpha_composite(image, (-box[0], -box[1]))
    rounded(canvas, round(radius * scale)).save(out / f"{name}.png", optimize=True)


def clock_grid(render, out, name="compact-clock", columns=2, scale=2, margin=(36, 0, 36, 30), radius=10, gap=10, listing="clocks.json"):
    """Clock samples in a grid. Each cell is framed exactly like compact-notch.png, so at the same README scale
    (width x 0.45) its notch shows at the same size; a small label sits in the bottom padding. The panel themes use the
    same grid with their own listing."""
    data = json.loads((render / listing).read_text(encoding="utf-8"))
    font = None
    for face in (r"C:\Windows\Fonts\msyh.ttc", r"C:\Windows\Fonts\segoeui.ttf"):
        try:
            font = ImageFont.truetype(face, round(10.5 * scale))
            break
        except OSError:
            pass
    font = font or ImageFont.load_default()
    cells = []
    for item in data:
        image = Image.open(render / item["file"]).convert("RGBA")
        x0, y0, x1, y1 = image.getbbox()
        left, top, right, bottom = (round(m * scale) for m in margin)
        box = (x0 - left, 0, x1 + right, y1 + bottom)
        cell = wallpaper(box[2] - box[0], box[3] - box[1])
        cell.alpha_composite(image, (-box[0], -box[1]))
        draw = ImageDraw.Draw(cell)
        draw.text((cell.width / 2, cell.height - bottom / 2), item["title"], font=font, fill=(64, 68, 82, 255), anchor="mm")
        cells.append(rounded(cell, round(radius * scale)))
    w, h = max(c.width for c in cells), max(c.height for c in cells)
    g = round(gap * scale)
    rows = (len(cells) + columns - 1) // columns
    sheet = Image.new("RGBA", (columns * w + (columns - 1) * g, rows * h + (rows - 1) * g), (0, 0, 0, 0))
    for i, cell in enumerate(cells):
        sheet.alpha_composite(cell, ((i % columns) * (w + g) + (w - cell.width) // 2, (i // columns) * (h + g)))
    sheet.save(out / f"{name}.png", optimize=True)


def window_shot(render, out, name, scale=1.5, margin=40, radius=8):
    """A captured window with Windows 11 corners and shadow, on the wallpaper."""
    image = rounded(Image.open(render / f"{name}.png").convert("RGBA"), round(radius * scale))
    m = round(margin * scale)
    canvas = wallpaper(image.width + 2 * m, image.height + 2 * m)
    shadow = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle((m, m + 8 * scale, m + image.width, m + image.height + 8 * scale), radius * scale, fill=(20, 24, 36, 110))
    canvas.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(16 * scale)))
    canvas.alpha_composite(image, (m, m))
    rounded(canvas, round(12 * scale)).save(out / f"{name}.png", optimize=True)


def main():
    render, out = Path(sys.argv[1]), Path(sys.argv[2])
    out.mkdir(parents=True, exist_ok=True)
    for name in ["usage", "details", "music", "monitor"]:
        if (render / f"{name}.png").exists():
            still(render, out, name)
    for name in ["compact-notch", "compact-notch-lyrics", "compact-capsule", "compact-line", "compact-alert", "compact-monitor"]:
        if (render / f"{name}.png").exists():
            still(render, out, name, margin=(36, 0, 36, 30), radius=10)
    if (render / "clocks.json").exists():
        clock_grid(render, out)
    if (render / "themes.json").exists():
        clock_grid(render, out, "themes", margin=(24, 0, 24, 30), listing="themes.json")
    if (render / "settings.png").exists():
        window_shot(render, out, "settings")
    icon = Image.open(Path(__file__).resolve().parents[2] / "src" / "12Island" / "Assets" / "12Island.ico")
    icon.size = max(icon.info["sizes"])
    icon.convert("RGBA").resize((128, 128), Image.LANCZOS).save(out.parents[1] / "icon.png", optimize=True)
    print("hero frames:", hero(render, out))


if __name__ == "__main__":
    main()
