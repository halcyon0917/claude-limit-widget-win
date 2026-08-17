"""Convert the 4 mascot SVGs into compact frame data (sprites.json) for the widget.

Each animation becomes: {"frames": [[ [x,y,w,h,"#rrggbb"], ... ], ...], "w": vw, "h": vh, "frameMs": n}
Coordinates are floats in the SVG's own viewBox space; the C# renderer scales them.
"""
import json
import math
import os
import re
import sys
import xml.etree.ElementTree as ET

SVG_DIR = os.path.expanduser(r"~\OneDrive\Desktop")
OUT = os.path.join(os.path.dirname(__file__), "..", "ClaudeLimitWidget", "sprites.json")


def strip_ns(tag):
    return tag.split('}')[-1]


def parse_transform(s):
    """Return a 3x3 affine matrix for an SVG transform attribute string."""
    m = [1, 0, 0, 1, 0, 0]  # a b c d e f  (x' = a*x + c*y + e ; y' = b*x + d*y + f)

    def mul(m1, m2):
        a1, b1, c1, d1, e1, f1 = m1
        a2, b2, c2, d2, e2, f2 = m2
        return [
            a1 * a2 + c1 * b2,
            b1 * a2 + d1 * b2,
            a1 * c2 + c1 * d2,
            b1 * c2 + d1 * d2,
            a1 * e2 + c1 * f2 + e1,
            b1 * e2 + d1 * f2 + f1,
        ]

    if not s:
        return m
    for func, args in re.findall(r"(\w+)\(([^)]*)\)", s):
        vals = [float(v) for v in re.split(r"[\s,]+", args.strip()) if v]
        if func == "translate":
            tx = vals[0]
            ty = vals[1] if len(vals) > 1 else 0
            m = mul(m, [1, 0, 0, 1, tx, ty])
        elif func == "matrix":
            m = mul(m, vals)
        elif func == "scale":
            sx = vals[0]
            sy = vals[1] if len(vals) > 1 else sx
            m = mul(m, [sx, 0, 0, sy, 0, 0])
        elif func == "rotate":
            ang = math.radians(vals[0])
            cos, sin = math.cos(ang), math.sin(ang)
            rot = [cos, sin, -sin, cos, 0, 0]
            if len(vals) == 3:
                cx, cy = vals[1], vals[2]
                m = mul(m, [1, 0, 0, 1, cx, cy])
                m = mul(m, rot)
                m = mul(m, [1, 0, 0, 1, -cx, -cy])
            else:
                m = mul(m, rot)
    return m


def apply(m, x, y):
    a, b, c, d, e, f = m
    return a * x + c * y + e, b * x + d * y + f


def is_hidden(el):
    style = el.get("style", "")
    if "display: none" in style or "display:none" in style:
        return True
    # style display wins over the attribute; inline style overrides display attr
    if "display: inline" in style or "display:inline" in style:
        return False
    return el.get("display") == "none"


def collect_rects(el, m, out, skip=None):
    """Recursively collect rects under el with accumulated transform m."""
    if skip and el in skip:
        return
    tag = strip_ns(el.tag)
    m2 = m
    t = el.get("transform")
    if t:
        # compose: parent * local
        a1, b1, c1, d1, e1, f1 = m
        a2, b2, c2, d2, e2, f2 = parse_transform(t)
        m2 = [
            a1 * a2 + c1 * b2,
            b1 * a2 + d1 * b2,
            a1 * c2 + c1 * d2,
            b1 * c2 + d1 * d2,
            a1 * e2 + c1 * f2 + e1,
            b1 * e2 + c1 * f2 + f1,
        ]
        # NOTE: f-term must use b1*e2 + d1*f2 + f1 — fix below
        m2[5] = b1 * e2 + d1 * f2 + f1
    if tag == "rect":
        fill = el.get("fill", "")
        if fill and fill != "none":
            x = float(el.get("x", 0))
            y = float(el.get("y", 0))
            w = float(el.get("width", 0))
            h = float(el.get("height", 0))
            pts = [apply(m2, px, py) for px, py in
                   [(x, y), (x + w, y), (x, y + h), (x + w, y + h)]]
            xs = [p[0] for p in pts]
            ys = [p[1] for p in pts]
            out.append([round(min(xs), 2), round(min(ys), 2),
                        round(max(xs) - min(xs), 2), round(max(ys) - min(ys), 2), fill])
        return
    for child in el:
        collect_rects(child, m2, out, skip)


IDENT = [1, 0, 0, 1, 0, 0]


def top_groups(root):
    return [c for c in root if strip_ns(c.tag) == "g"]


def convert_cheering(path):
    root = ET.parse(path).getroot()
    groups = top_groups(root)
    body_frames = [g for g in groups if (g.get("id") or "").startswith("l0")]
    confetti = [g for g in groups if not (g.get("id") or "").startswith("l0")]
    n = len(body_frames)
    frames = []
    for i in range(n):
        rects = []
        collect_rects(body_frames[i], IDENT, rects)
        for cont in confetti:
            subs = [c for c in cont if strip_ns(c.tag) == "g"]
            if i < len(subs):
                m = parse_transform(cont.get("transform"))
                # The site floats confetti far above the mascot (overflow:visible);
                # at taskbar size that would shrink the body, so pull bursts closer.
                m[5] += 40
                collect_rects(subs[i], m, rects)
        frames.append(rects)
    return {"frames": frames, "w": 129, "h": 113, "frameMs": 110}


def convert_workout(path):
    root = ET.parse(path).getroot()
    frames = []
    for g in top_groups(root):
        rects = []
        collect_rects(g, IDENT, rects)
        if rects:
            frames.append(rects)
    return {"frames": frames, "w": 158, "h": 128, "frameMs": 130}


def convert_waving(path):
    root = ET.parse(path).getroot()
    # static base: everything except the flag sub-frame groups (the g's that
    # contain black/white pixel rects inside the right-hand group)
    flag_frames = []
    skip = set()

    def find_flags(el):
        for child in el:
            if strip_ns(child.tag) == "g" and child.get("transform", "").startswith("translate") \
                    and child.get("id") is None and child.get("data-svg-origin") is None:
                # candidate flag frame: contains only rects with black/white fills
                fills = {r.get("fill") for r in child.iter() if strip_ns(r.tag) == "rect"}
                if fills and fills <= {"black", "white"}:
                    flag_frames.append(child)
                    skip.add(child)
                    continue
            find_flags(child)

    find_flags(root)

    # accumulate transforms down to each flag frame properly: rebuild by walking
    # from root with collect_rects and a "skip" set for base; for flags, we need
    # their ancestor chain transforms.
    def ancestors_transform(target):
        chain = []

        def walk(el, path):
            if el is target:
                chain.extend(path + [el])
                return True
            for c in el:
                if walk(c, path + [el]):
                    return True
            return False

        walk(root, [])
        m = IDENT
        for el in chain:
            t = el.get("transform")
            if t:
                a1, b1, c1, d1, e1, f1 = m
                a2, b2, c2, d2, e2, f2 = parse_transform(t)
                m = [a1 * a2 + c1 * b2, b1 * a2 + d1 * b2,
                     a1 * c2 + c1 * d2, b1 * c2 + d1 * d2,
                     a1 * e2 + c1 * f2 + e1, b1 * e2 + d1 * f2 + f1]
        return m

    base = []
    collect_rects(root, IDENT, base, skip=skip)

    frames = []
    for ff in flag_frames:
        # deep-copy: normalization mutates rects in place, so frames must not share them
        rects = [list(r) for r in base]
        # parent transform chain up to (excluding) ff, then ff itself is handled
        # inside collect_rects via its own transform attr — so pass parent chain m.
        m = ancestors_transform(ff)
        # ancestors_transform included ff's own transform; collect_rects would
        # apply it again — so strip ff's own contribution by passing children.
        for child in ff:
            collect_rects(child, m, rects)
        # include direct rects of ff (rare)
        for child in [c for c in ff if strip_ns(c.tag) == "rect"]:
            pass  # already handled by collect_rects above
        frames.append(rects)
    return {"frames": frames, "w": 140, "h": 146, "frameMs": 120}


def convert_walking(path):
    """The walking SVG is GSAP-driven (no frame groups), so build procedural frames
    from its base geometry, on a fixed 107×92 canvas (6px hop headroom, ground at 92).
    Locomotion itself is positional — the C# animator moves/mirrors the sprite — so
    the walk cycle has no baked sway. Also emits the "idle" set: stand, blink,
    glance left/right, hop. Movements are large in sprite units; at taskbar scale
    (~0.4) subtle offsets vanish to <1px."""
    body_color = "#DD775B"
    GROUND = 92
    BODY_Y = 6  # normal body top; the hop frame uses 0

    def frame(body_dy, legs, eye_dx=0, blink=False, feet_off_ground=0):
        """legs: per leg (x-offset, lift). eye_dx shifts pupils; blink closes them."""
        rects = []
        for (lx, (dx, lift)) in zip([11, 32, 64, 85], legs):
            h = 26 - lift
            bottom = GROUND - feet_off_ground
            if lift > 0:
                # stepping leg: thigh + forward foot for a bent-knee look
                rects.append([lx, bottom - lift - h, 11, h - 6, body_color])
                rects.append([lx + dx, bottom - lift - 6, 11, 6, body_color])
            else:
                rects.append([lx + dx, bottom - h, 11, h, body_color])
        y = BODY_Y + body_dy
        rects.append([11, y, 85, 65, body_color])
        rects.append([85, y + 21, 22, 23, body_color])   # right hand
        rects.append([0, y + 21, 22, 23, body_color])    # left hand
        if blink:
            rects.append([79 + eye_dx, y + 31, 11, 3, "black"])
            rects.append([25 + eye_dx, y + 31, 11, 3, "black"])
        else:
            rects.append([79 + eye_dx, y + 23, 11, 11, "black"])
            rects.append([25 + eye_dx, y + 23, 11, 11, "black"])
        return rects

    up = lambda dx: (dx, 8)   # lifted, foot swung forward
    dn = (0, 0)               # planted

    walking = {
        "frames": [
            frame(2, [up(5), dn, up(5), dn]),
            frame(0, [dn, dn, dn, dn]),
            frame(2, [dn, up(5), dn, up(5)]),
            frame(0, [dn, dn, dn, dn]),
        ],
        "w": 107, "h": GROUND, "frameMs": 150,
    }

    # Indices matter — MascotAnimator refers to them by number:
    # 0 stand · 1 blink · 2 glance left · 3 glance right · 4 hop
    idle = {
        "frames": [
            frame(0, [dn, dn, dn, dn]),
            frame(0, [dn, dn, dn, dn], blink=True),
            frame(0, [dn, dn, dn, dn], eye_dx=-5),
            frame(0, [dn, dn, dn, dn], eye_dx=5),
            frame(-6, [dn, dn, dn, dn], feet_off_ground=6),
        ],
        "w": 107, "h": GROUND, "frameMs": 150,
    }
    return walking, idle


def main():
    # Fall back to the previously converted data when a source SVG is gone —
    # the SVGs lived on the user's Desktop and may not stick around.
    existing = {}
    out_path = os.path.abspath(OUT)
    if os.path.exists(out_path):
        with open(out_path) as f:
            existing = json.load(f)

    def convert(name, fn, svg, needs_svg=True):
        path = os.path.join(SVG_DIR, svg)
        if needs_svg and not os.path.exists(path):
            if name in existing:
                print(f"{name}: source SVG missing, keeping existing frames")
                return existing[name], False  # already normalized
            raise FileNotFoundError(f"{path} missing and no existing data for {name}")
        return fn(path), True

    results = {
        "waving": convert("waving", convert_waving, "waving-flag-mascot.svg"),
        "workout": convert("workout", convert_workout, "workout-mascot.svg"),
        "cheering": convert("cheering", convert_cheering, "cheering-mascot.svg"),
    }
    sprites = {k: v[0] for k, v in results.items()}
    # SVG-derived anims get bbox-normalized below; walking/idle are hand-designed
    # on a fixed shared canvas and must NOT be normalized (their scales must match).
    fresh = {k for k, v in results.items() if v[1]}
    sprites["walking"], sprites["idle"] = convert_walking(None)
    # Normalize each animation so all frames fit a (0,0)-anchored bounding box —
    # the confetti/flag pixels extend outside the nominal viewBox (overflow:visible on the site).
    # Only freshly converted animations — reused ones are already normalized (and their
    # frames may share rect objects, which double-shifting would corrupt).
    for name in fresh:
        s = sprites[name]
        all_rects = [r for f in s["frames"] for r in f]
        minx = min(r[0] for r in all_rects)
        miny = min(r[1] for r in all_rects)
        maxx = max(r[0] + r[2] for r in all_rects)
        maxy = max(r[1] + r[3] for r in all_rects)
        for f in s["frames"]:
            for r in f:
                r[0] = round(r[0] - minx, 2)
                r[1] = round(r[1] - miny, 2)
        s["w"] = round(maxx - minx, 2)
        s["h"] = round(maxy - miny, 2)

    for name, s in sprites.items():
        counts = [len(f) for f in s["frames"]]
        print(f"{name}: {len(s['frames'])} frames, rects per frame: {counts}")
        if not s["frames"] or min(counts, default=0) == 0:
            print(f"  WARNING: {name} has empty frames", file=sys.stderr)
    with open(os.path.abspath(OUT), "w") as f:
        json.dump(sprites, f, separators=(",", ":"))
    print("wrote", os.path.abspath(OUT), os.path.getsize(os.path.abspath(OUT)), "bytes")


if __name__ == "__main__":
    main()
