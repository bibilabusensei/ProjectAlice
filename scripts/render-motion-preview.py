"""Render a LOCAL production-core JSON export. No model uploads or web calls.

The exporter uses the actual VRM hierarchy and the exact Core choreography.
This diagnostic shows articulated bones and abstract hands, not textured mesh,
cloth, morphs, SceneKit skinning or iPhone frame timing. It cannot prove final
appearance. Preview JSON contains private model proportions; do not publish it.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

COLORS = {"left": "#ffbe65", "right": "#62b9ff", "body": "#9ea7bb", "head": "#c4cbd8"}
BACKGROUND = "#101723"


def add(a, b):
    return [x + y for x, y in zip(a, b)]


def mul(a, k):
    return [x * k for x in a]


def sub(a, b):
    return [x - y for x, y in zip(a, b)]


def cross(a, b):
    return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]


def length(a):
    return math.sqrt(sum(x * x for x in a))


def unit(a):
    return mul(a, 1 / max(1e-8, length(a)))


def font(size):
    for name in ["C:/Windows/Fonts/segoeui.ttf", "C:/Windows/Fonts/arial.ttf"]:
        if Path(name).exists():
            return ImageFont.truetype(name, size)
    return ImageFont.load_default()


def render_frame(frame, label, rest, width=900, height=640, trail=None):
    image = Image.new("RGB", (width, height), BACKGROUND)
    draw = ImageDraw.Draw(image)
    feet = [rest[name][1] for name in ["leftFoot", "rightFoot", "leftToes", "rightToes"] if name in rest]
    ground = min(feet)
    body_span = max(1e-8, rest["head"][1] - ground)
    head_radius = body_span * 0.059
    scale = (height - 118) / (body_span + 1.95 * head_radius)
    floor_y = height - 58
    draw.text((20, 14), label, font=font(22), fill="white")
    draw.text((20, 45), "Production Core | real VRM bone proportions | skeleton preview, not iOS mesh render", font=font(13), fill="#aab6c7")
    draw.text((20, height - 32), f"t = {frame['time']:.2f} s    Orange = character left    Blue = character right    Pink arrows = palm normal", font=font(13), fill="#bcc8d9")
    points = frame["points"]

    for view, center_x in [("front", width * 0.29), ("side", width * 0.76)]:
        def project(p):
            axis = p[0] if view == "front" else p[2]
            return center_x + axis * scale, floor_y - (p[1] - ground) * scale

        def line(a, b, color, thick):
            draw.line([project(a), project(b)], fill=color, width=thick)

        def disc(p, radius, color):
            x, y = project(p)
            draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=color)

        draw.text((center_x - 65, 74), "FRONT (+Z)" if view == "front" else "SIDE (+X)", fill="#7f8da3", font=font(14))
        draw.line([(center_x - 180, floor_y), (center_x + 180, floor_y)], fill="#304154", width=2)
        # Torso uses the actual hip/shoulder breadth rather than a tall generic body.
        left_s, right_s = points["leftUpperArm"], points["rightUpperArm"]
        left_h, right_h = points["leftUpperLeg"], points["rightUpperLeg"]
        torso = [left_s, right_s, right_h, left_h]
        draw.polygon([project(p) for p in torso], fill="#273445", outline="#62748d")
        for a, b in [("hips", "spine"), ("spine", "chest"), ("chest", "upperChest"), ("upperChest", "neck"), ("neck", "head")]:
            if a in points and b in points:
                line(points[a], points[b], COLORS["body"], 9)
        for side in ["right", "left"]:
            color = COLORS[side]
            for a, b in [("UpperLeg", "LowerLeg"), ("LowerLeg", "Foot"), ("Foot", "Toes")]:
                if side + a in points and side + b in points:
                    line(points[side + a], points[side + b], "#5d6c83", 15 if b != "Toes" else 10)
            shoulder, elbow, wrist = [points[side + name] for name in ["UpperArm", "LowerArm", "Hand"]]
            line(shoulder, elbow, color, 12)
            line(elbow, wrist, color, 10)
            disc(shoulder, 6, "#e5e9ef")
            disc(elbow, 6, "#e5e9ef")
            disc(wrist, 4, "#e5e9ef")
            palm = frame["palms"][side]
            long_axis, normal = palm["longitudinal"], palm["normal"]
            across = unit(cross(normal, long_axis))
            # Abstract finger geometry is only a readability aid. Actual pose
            # direction comes from production hand rotation and calibrated axes.
            hand_scale = length(sub(rest[side + "Hand"], rest[side + "LowerArm"])) * 0.29
            center = add(wrist, mul(long_axis, hand_scale * 0.34))
            corners = [add(center, add(mul(long_axis, along * hand_scale * 0.32), mul(across, wide * hand_scale * 0.34)))
                       for along, wide in [(-1, -1), (-1, 1), (1, 1), (1, -1)]]
            draw.polygon([project(p) for p in corners], fill=color, outline="#f0f3f9")
            for i in range(4):
                base = add(center, add(mul(long_axis, hand_scale * 0.30), mul(across, hand_scale * (i - 1.5) * 0.19)))
                middle = add(base, mul(long_axis, hand_scale * (0.48 if i in [1, 2] else 0.40)))
                tip = add(middle, add(mul(long_axis, hand_scale * 0.28), mul(normal, -hand_scale * palm["curl"])))
                line(base, middle, color, 3)
                line(middle, tip, color, 3)
            thumb = add(center, mul(across, hand_scale * 0.38))
            line(thumb, add(thumb, add(mul(across, hand_scale * 0.30), mul(long_axis, hand_scale * 0.28))), color, 3)
            n_end = add(center, mul(normal, hand_scale * 0.88))
            line(center, n_end, "#ff87b7", 3)
            disc(n_end, 2, "#ff87b7")
            if trail:
                track = [project(f["points"][side + "Hand"]) for f in trail]
                if len(track) > 1:
                    draw.line(track, fill="#614932" if side == "left" else "#274c68", width=2)
        # Head silhouette/forward arrow show nod/bow/yaw without a fake face.
        head_center = add(points["head"], mul(frame["headUp"], head_radius * 0.76))
        x, y = project(head_center)
        r = head_radius * scale
        draw.ellipse((x - r, y - r * 1.1, x + r, y + r * 1.1), fill="#657185", outline=COLORS["head"], width=3)
        line(head_center, add(head_center, mul(frame["headUp"], head_radius * 1.13)), "#c8d1e1", 3)
        line(head_center, add(head_center, mul(frame["headForward"], head_radius * 1.48)), "#f2a779", 4)
        disc(add(head_center, mul(frame["headForward"], head_radius * 1.48)), 3, "#f2a779")
    return image


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    options = parser.parse_args()
    data = json.loads(options.input.read_text(encoding="utf-8"))
    options.output.mkdir(parents=True, exist_ok=True)
    summary_tiles = []
    for clip in data["clips"]:
        label, frames = clip["action"], clip["frames"]
        # 15 fps full-view GIFs retain timing; exported data remains 30 Hz.
        rendered = [render_frame(frame, label, data["rest"], 720, 540) for frame in frames[::2]]
        palette = rendered[0].quantize(colors=128, method=Image.Quantize.MEDIANCUT)
        indexed = [frame.quantize(palette=palette, dither=Image.Dither.NONE) for frame in rendered]
        indexed[0].save(options.output / (label + ".gif"), save_all=True, append_images=indexed[1:],
                        duration=67, loop=0, optimize=False, disposal=2)
        indices = [min(len(frames) - 1, round(t * 30)) for t in [0, 0.55, 1.05, 1.6, 2.6, min(3.85, clip["duration"])]]
        sheet = Image.new("RGB", (1440, 800), BACKGROUND)
        for i, index in enumerate(indices):
            image = render_frame(frames[index], label, data["rest"], 720, 540).resize((480, 400))
            sheet.paste(image, ((i % 3) * 480, (i // 3) * 400))
        sheet.save(options.output / (label + "-contact.png"))
        summary_tiles.append(render_frame(frames[min(len(frames) - 1, 45)], label, data["rest"], 720, 540).resize((480, 360)))
        print(f"Rendered {label}: {len(frames)} exact production frames, local hierarchy transition included")
    summary = Image.new("RGB", (1920, math.ceil(len(summary_tiles) / 4) * 360), BACKGROUND)
    for i, tile in enumerate(summary_tiles):
        summary.paste(tile, ((i % 4) * 480, (i // 4) * 360))
    summary.save(options.output / "all-actions-contact.png")
    # First sweep: production palm direction, zoomed front view for both sides.
    clips_by_name = {clip["action"]: clip for clip in data["clips"]}
    sweep = Image.new("RGB", (1600, 630), BACKGROUND)
    sweep_draw = ImageDraw.Draw(sweep)
    sweep_draw.text((18, 12), "First wrist sweep: both hands mirror OUTWARDS. No independent preview choreography.", font=font(20), fill="white")
    for row, side in enumerate(["left", "right"]):
        frames = clips_by_name["wave_" + side]["frames"]
        for col, seconds in enumerate([1.2, 1.4, 1.6, 1.8, 2.0]):
            frame = frames[round(seconds * 30)]
            source = render_frame(frame, "wave_" + side, data["rest"], 720, 540)
            zoom = source.crop((34, 76, 380, 306)).resize((320, 213))
            x, y = col * 320, 50 + row * 284
            sweep.paste(zoom, (x, y))
            direction = frame["palms"][side]["longitudinal"]
            angle = math.degrees(math.atan2(direction[0] * (1 if side == "left" else -1), direction[1]))
            sweep_draw.text((x + 12, y + 216), f"{side}   t={seconds:.1f}s   outward={angle:+.1f} deg", font=font(16), fill=COLORS[side])
    sweep.save(options.output / "first-sweep-contact.png")
    html = "<!doctype html><meta charset='utf-8'><title>Local motion preview</title><style>body{background:#101723;color:#c4cbd8;font:16px sans-serif;margin:24px}section{display:inline-block;width:48%;vertical-align:top}img{width:100%;max-width:720px}h1{font-size:24px}</style><h1>Production Core / real VRM skeleton preview</h1><p>Not an iOS mesh render. No textures, clothing physics or expressions are simulated. All choreography and the production local-pose handoff are evaluated locally from production Core. Private model metadata stays local.</p>"
    for clip in data["clips"]:
        html += f"<section><h2>{clip['action']}</h2><img src='{clip['action']}.gif'></section>"
    (options.output / "index.html").write_text(html, encoding="utf-8")


if __name__ == "__main__":
    main()
