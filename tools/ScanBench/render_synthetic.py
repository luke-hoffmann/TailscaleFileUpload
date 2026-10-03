"""
Synthetic "phone photo of real paper" generator with exact ground truth, for benchmarking the scanner.

A document image is mapped onto a 3D paper mesh that is deformed the way real paper is: dog-eared corners
(flap folded over, back side showing), hinged creases (half and tri-folds), curls, crumples, plus a finger
over an edge. The mesh is photographed with a phone-like pinhole camera (f ~ 0.75 x long edge, like an
iPhone main camera), Lambert lighting, a soft drop shadow, vignetting, blur, sensor noise and JPEG.

For every photo we write:
  photo.jpg   the 12 MP "phone photo"
  gt.png      what a perfect scan looks like (flat page; areas that are physically hidden or missing
              because of a dog-ear are paper-white, the folded-over flap shows its blank back)
  mask.png    which photo pixels are paper (page or flap) - the ground-truth outline
  meta.json   family, parameters, true page corners (actual and "virtual" i.e. as if unfolded)

Usage: python render_synthetic.py --docs <dir of page images> --out <dir> --count 300 [--seed 1]
"""
import argparse
import glob
import json
import math
import os

import cv2
import numpy as np

PX_PER_MM = 4.0           # ground-truth page resolution
IMG_LONG, IMG_SHORT = 4032, 3024


# ---------------------------------------------------------------- page textures

def load_page(path, w_mm, h_mm):
    img = cv2.imread(path, cv2.IMREAD_COLOR)
    tw, th = int(round(w_mm * PX_PER_MM)), int(round(h_mm * PX_PER_MM))
    return cv2.resize(img, (tw, th), interpolation=cv2.INTER_AREA)


def make_receipt(rng, w_mm, h_mm):
    tw, th = int(w_mm * PX_PER_MM), int(h_mm * PX_PER_MM)
    tex = np.full((th, tw, 3), (238, 242, 243), np.uint8)
    y = 40
    words = ["TOTAL", "SUBTOTAL", "TAX", "VISA", "CASH", "QTY", "ITEM", "MILK", "BREAD", "COFFEE", "APPLES",
             "CHANGE", "THANK", "YOU", "STORE", "#0421", "12.99", "3.49", "0.89", "24.10", "2026-10-02"]
    cv2.putText(tex, "CORNER MARKET", (int(tw * 0.12), y + 20), cv2.FONT_HERSHEY_DUPLEX, 0.9, (40, 40, 40), 2, cv2.LINE_AA)
    y += 70
    while y < th - 40:
        line = " ".join(rng.choice(words, size=rng.integers(2, 4)))
        price = f"{rng.uniform(0.5, 60):6.2f}"
        cv2.putText(tex, line, (14, y), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (50, 50, 50), 1, cv2.LINE_AA)
        cv2.putText(tex, price, (tw - 92, y), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (50, 50, 50), 1, cv2.LINE_AA)
        y += int(rng.integers(26, 34))
    return tex


# ---------------------------------------------------------------- geometry helpers

def rodrigues_rotate(points, axis_point, axis_dir, angle):
    k = axis_dir / np.linalg.norm(axis_dir)
    p = points - axis_point
    cos, sin = math.cos(angle), math.sin(angle)
    rotated = p * cos + np.cross(k, p) * sin + np.outer(p @ k, k) * (1 - cos)
    return rotated + axis_point


def rot_matrix(tilt, yaw, roll):
    cx, sx = math.cos(tilt), math.sin(tilt)
    cy, sy = math.cos(yaw), math.sin(yaw)
    cz, sz = math.cos(roll), math.sin(roll)
    rx = np.array([[1, 0, 0], [0, cx, -sx], [0, sx, cx]])
    ry = np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]])
    rz = np.array([[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]])
    return rz @ ry @ rx


# ---------------------------------------------------------------- deformations (page mm coordinates -> 3D)
# Page plane: x = u - W/2, y = v - H/2, z = 0; negative z points toward the camera (paper lifting off a table).

def deform(family, rng, W, H, U, V):
    x = U - W / 2
    y = V - H / 2
    P = np.stack([x, y, np.zeros_like(x)], axis=-1).reshape(-1, 3)
    flap = np.zeros(P.shape[0], bool)
    params = {}
    flat_uv = np.stack([U.ravel(), V.ravel()], axis=-1)
    if family == "tab":
        params["touch"] = bool(rng.random() < 0.5)

    def side(a_uv, b_uv, uv):
        d = np.array(b_uv) - np.array(a_uv)
        return (uv[:, 0] - a_uv[0]) * d[1] - (uv[:, 1] - a_uv[1]) * d[0]

    if family in ("crease", "mixed"):
        kind = rng.choice(["half", "trifold"])
        horizontal = rng.random() < 0.7
        positions = [0.5] if kind == "half" else [1 / 3, 2 / 3]
        angles = []
        for i, t in enumerate(positions):
            jitter = rng.uniform(-0.03, 0.03)
            if horizontal:
                a_uv, b_uv = (0, (t + jitter) * H), (W, (t - jitter) * H)
            else:
                a_uv, b_uv = ((t + jitter) * W, 0), ((t - jitter) * W, H)
            angle = math.radians(rng.uniform(10, 38)) * (1 if (kind == "half" or i == 0 or rng.random() < 0.5) else -1)
            angles.append(angle)
            s = side(a_uv, b_uv, flat_uv)
            mask = s > 0 if (horizontal) else s < 0
            # Re-derive the fold axis from the current 3D positions of the fold line ends (earlier folds moved them).
            a3, b3 = _current_line(P, flat_uv, a_uv, b_uv)
            P[mask] = rodrigues_rotate(P[mask], a3, b3 - a3, angle)
        params["crease"] = {"kind": kind, "horizontal": bool(horizontal), "angles_deg": [round(math.degrees(a), 1) for a in angles]}

    if family in ("curl", "mixed") or (family == "receipt" and rng.random() < 0.8):
        mode = rng.choice(["edge", "arc"])
        axis = rng.choice(["u", "v"]) if family != "receipt" else "v"
        R = rng.uniform(40, 160)
        coord = P[:, 0] if axis == "u" else P[:, 1]
        extent = W if axis == "u" else H
        if mode == "edge":
            s0 = rng.uniform(0.15, 0.32) * extent * rng.choice([-1, 1])
            sign = np.sign(s0)
            t = (coord - s0) * sign
            m = t > 0
            phi = np.minimum(t[m] / R, math.radians(95))
            new_c = s0 + sign * R * np.sin(phi) + sign * np.maximum(t[m] - R * math.radians(95), 0) * math.cos(math.radians(95))
            dz = -R * (1 - np.cos(phi))
        else:
            m = np.ones_like(coord, bool)
            phi = coord / R * 0.6
            new_c = (R / 0.6) * np.sin(phi)
            dz = -(R / 0.6) * (1 - np.cos(phi)) * rng.choice([-1, 1])
        if axis == "u":
            P[m, 0] = new_c
        else:
            P[m, 1] = new_c
        P[m, 2] += dz
        params["curl"] = {"mode": str(mode), "axis": str(axis), "radius_mm": round(R, 1)}

    if family == "crumple":
        n = 6
        zf = np.zeros_like(U)
        for _ in range(rng.integers(6, 14)):          # soft bumps
            cu, cv = rng.uniform(0, W), rng.uniform(0, H)
            s = rng.uniform(20, 70)
            zf += rng.uniform(-6, 6) * np.exp(-((U - cu) ** 2 + (V - cv) ** 2) / (2 * s * s))
        for _ in range(rng.integers(2, 6)):            # sharp ridges
            ang = rng.uniform(0, math.pi)
            cu, cv = rng.uniform(0.2, 0.8) * W, rng.uniform(0.2, 0.8) * H
            d = (U - cu) * math.sin(ang) - (V - cv) * math.cos(ang)
            zf += rng.uniform(-4, 4) * np.maximum(0, 1 - np.abs(d) / rng.uniform(6, 18))
        P[:, 2] += -np.abs(zf.ravel()) * 0.7 + zf.ravel() * 0.3
        params["crumple"] = {"ridges": True}

    if family in ("dogear", "mixed"):
        corner = int(rng.integers(0, 4))
        cu, cv = [(0, 0), (W, 0), (W, H), (0, H)][corner]
        a = rng.uniform(0.10, 0.30) * min(W, H)
        b = a * rng.uniform(0.7, 1.4)
        du = 1 if cu == 0 else -1
        dv = 1 if cv == 0 else -1
        a_uv = (cu + du * a, cv)
        b_uv = (cu, cv + dv * b)
        s = side(a_uv, b_uv, flat_uv)
        s_corner = side(a_uv, b_uv, np.array([[cu, cv]]))[0]
        mask = np.sign(s) == np.sign(s_corner)
        a3, b3 = _current_line(P, flat_uv, a_uv, b_uv)
        angle = math.radians(rng.uniform(150, 178)) * (1 if corner in (0, 2) else -1)
        # Fold toward the camera (on top of the page): pick the rotation sign that ends up at negative z.
        test = rodrigues_rotate(P[mask][:1], a3, b3 - a3, angle)
        if test[0, 2] > a3[2]:
            angle = -angle
        P[mask] = rodrigues_rotate(P[mask], a3, b3 - a3, angle)
        P[mask, 2] -= 0.4  # lies on top
        flap = mask
        params["dogear"] = {"corner": corner, "a_mm": round(a, 1), "b_mm": round(b, 1), "fold_uv": [a_uv, b_uv]}

    return P, flap, params


def _current_line(P, flat_uv, a_uv, b_uv):
    """3D positions of two flat points (nearest mesh vertices, interpolated well enough on a dense grid)."""
    def nearest(target):
        d = np.sum((flat_uv - np.array(target)) ** 2, axis=1)
        return P[int(np.argmin(d))].copy()
    return nearest(a_uv), nearest(b_uv)


# ---------------------------------------------------------------- rendering

def render(rng, page_tex, W, H, family, portrait):
    img_w, img_h = (IMG_SHORT, IMG_LONG) if portrait else (IMG_LONG, IMG_SHORT)
    f = 0.75 * max(img_w, img_h)
    cx, cy = img_w / 2, img_h / 2

    nu = int(W / 2) + 1
    nv = int(H / 2) + 1
    us = np.linspace(0, W, nu)
    vs = np.linspace(0, H, nv)
    U, V = np.meshgrid(us, vs)
    P, flap, params = deform(family, rng, W, H, U, V)

    # Camera pose: tilt the page away, a little yaw/roll, then push it back until it fits with a margin.
    tilt = math.radians(rng.uniform(0, 32))
    yaw = math.radians(rng.uniform(-16, 16))
    roll = math.radians(rng.uniform(-18, 18) + (90 if (not portrait and H > W and rng.random() < 0.5) else 0))
    Rm = rot_matrix(tilt, yaw, roll)
    Pc = P @ Rm.T
    fill = rng.uniform(0.62, 0.92)
    for _ in range(40):
        D = f * max(W, H) / (fill * max(img_w, img_h))
        Z = Pc[:, 2] + D
        xs = cx + f * Pc[:, 0] / Z
        ys = cy + f * Pc[:, 1] / Z
        margin = 0.035 if family != "frame_edge" else -0.02
        if family == "tab" and params.get("touch"):
            margin = -0.02
        if xs.min() > margin * img_w and xs.max() < (1 - margin) * img_w and ys.min() > margin * img_h and ys.max() < (1 - margin) * img_h:
            break
        fill *= 0.95
    offset = (rng.uniform(-0.04, 0.04) * img_w, rng.uniform(-0.04, 0.04) * img_h)
    xs = xs + offset[0]
    ys = ys + offset[1]
    if family == "frame_edge" or (family == "tab" and params.get("touch")):
        # push the page so one corner just touches / crosses the frame, like the user's photo
        side_x = rng.choice([-1, 1])
        shift = (xs.min() if side_x < 0 else img_w - xs.max()) + rng.uniform(0, 0.015) * img_w
        xs = xs + side_x * shift
        offset = (offset[0] + side_x * shift, offset[1])
    xs = np.clip(xs, 0, img_w - 1)
    ys = np.clip(ys, 0, img_h - 1)

    # Normals for shading (camera space).
    grid = Pc.reshape(nv, nu, 3)
    du_ = np.gradient(grid, axis=1)
    dv_ = np.gradient(grid, axis=0)
    normals = np.cross(du_, dv_)
    normals /= np.linalg.norm(normals, axis=-1, keepdims=True) + 1e-9
    normals = normals.reshape(-1, 3)
    normals[normals[:, 2] > 0] *= -1                    # face the camera
    light = np.array([rng.uniform(-0.6, 0.6), rng.uniform(-0.8, -0.2), -1.0])
    light /= np.linalg.norm(light)
    shade = 0.62 + 0.38 * np.clip(normals @ light, 0, 1)

    # Triangles (two per grid cell). Flap triangles are drawn last, the rest far-to-near.
    idx = np.arange(nu * nv).reshape(nv, nu)
    t1 = np.stack([idx[:-1, :-1], idx[:-1, 1:], idx[1:, :-1]], -1).reshape(-1, 3)
    t2 = np.stack([idx[:-1, 1:], idx[1:, 1:], idx[1:, :-1]], -1).reshape(-1, 3)
    tris = np.concatenate([t1, t2])
    tri_flap = flap[tris].all(axis=1)
    tri_mixed = flap[tris].any(axis=1) & ~tri_flap
    tris = tris[~tri_mixed]
    tri_flap = tri_flap[~tri_mixed]
    depth = Z[tris].mean(axis=1)
    order = np.lexsort((-depth, tri_flap))
    tris = tris[order]
    tri_flap = tri_flap[order]

    ids = np.full((img_h, img_w), -1, np.int32)
    pts = np.stack([xs, ys], -1)
    pts_fixed = np.round(pts * 16).astype(np.int32)
    for t, tri in enumerate(tris):
        cv2.fillConvexPoly(ids, pts_fixed[tri], t, lineType=cv2.LINE_8, shift=4)

    # Barycentric interpolation of (u, v, shade) per pixel.
    yy, xx = np.nonzero(ids >= 0)
    tid = ids[yy, xx]
    tv = tris[tid]
    p0, p1, p2 = pts[tv[:, 0]], pts[tv[:, 1]], pts[tv[:, 2]]
    d1, d2 = p1 - p0, p2 - p0
    det = d1[:, 0] * d2[:, 1] - d1[:, 1] * d2[:, 0]
    det[np.abs(det) < 1e-9] = 1e-9
    rx, ry = xx - p0[:, 0], yy - p0[:, 1]
    l1 = (rx * d2[:, 1] - ry * d2[:, 0]) / det
    l2 = (d1[:, 0] * ry - d1[:, 1] * rx) / det
    l0 = 1 - l1 - l2
    Uf, Vf = U.ravel(), V.ravel()
    u = l0 * Uf[tv[:, 0]] + l1 * Uf[tv[:, 1]] + l2 * Uf[tv[:, 2]]
    v = l0 * Vf[tv[:, 0]] + l1 * Vf[tv[:, 1]] + l2 * Vf[tv[:, 2]]
    s = l0 * shade[tv[:, 0]] + l1 * shade[tv[:, 1]] + l2 * shade[tv[:, 2]]
    is_flap = tri_flap[tid]

    # Background, then any other sheets lying under / next to the page (a packet, a folded-back tab, an envelope).
    photo = make_background(rng, img_w, img_h, "wood" if family == "tab" else None).astype(np.float32)
    def project(points_mm):
        pc = np.asarray(points_mm, float) @ Rm.T
        z = pc[:, 2] + D
        return np.stack([cx + f * pc[:, 0] / z + offset[0], cy + f * pc[:, 1] / z + offset[1]], -1).astype(np.float32)
    extras = []
    if family == "tab":
        # The user's photo: a packet's folded-back sheet lying against one corner of the page (under it, cream,
        # running off towards the frame), plus a dark magazine nearby.
        corner = int(rng.integers(0, 4))
        sx, sy = [(-1, -1), (1, -1), (1, 1), (-1, 1)][corner]
        sw, sh = rng.uniform(0.7, 1.0) * W, rng.uniform(0.6, 1.0) * H
        dx = sx * (W / 2 + sw / 2 - rng.uniform(0.2, 0.45) * sw)
        dy = sy * (H / 2 + sh / 2 - rng.uniform(0.2, 0.45) * sh)
        ang = math.radians(rng.uniform(-25, 25))
        ca, sa = math.cos(ang), math.sin(ang)
        quad = [(-sw / 2, -sh / 2), (sw / 2, -sh / 2), (sw / 2, sh / 2), (-sw / 2, sh / 2)]
        q = project([(dx + ca * x - sa * y, dy + sa * x + ca * y, 0.4) for x, y in quad])
        tone = np.array([rng.uniform(195, 225), rng.uniform(220, 238), rng.uniform(232, 248)], np.float32)
        m = np.zeros((img_h, img_w), np.float32)
        cv2.fillConvexPoly(m, np.round(q).astype(np.int32), 1.0, cv2.LINE_AA)
        shade_tab = cv2.GaussianBlur(m, (0, 0), 40)
        photo = photo * (1 - m[..., None]) + (tone * (0.85 + 0.15 * shade_tab[..., None])) * m[..., None]
        extras.append(q.tolist())
        # magazine: a dark glossy rectangle somewhere away from the page
        mw, mh = rng.uniform(0.6, 0.9) * W, rng.uniform(0.7, 1.0) * H
        mx = -sx * (W / 2 + mw / 2 + rng.uniform(5, 40))
        my = rng.uniform(-0.3, 0.3) * H
        ang = math.radians(rng.uniform(-30, 30))
        ca, sa = math.cos(ang), math.sin(ang)
        quad = [(-mw / 2, -mh / 2), (mw / 2, -mh / 2), (mw / 2, mh / 2), (-mw / 2, mh / 2)]
        q = project([(mx + ca * x - sa * y, my + sa * x + ca * y, 0.3) for x, y in quad])
        cv2.fillConvexPoly(photo, np.round(q).astype(np.int32), tuple(float(v) for v in rng.uniform(25, 70, 3)), cv2.LINE_AA)
    if family in ("neighbor", "packet"):
        count = 1 if family == "neighbor" else int(rng.integers(2, 4))
        for k in range(count):
            if family == "packet":
                dx, dy = rng.uniform(-14, 14), rng.uniform(-14, 14)
                ang = math.radians(rng.uniform(-6, 6))
                sw, sh = W, H
            else:
                # another sheet poking out from under one corner / edge
                corner = int(rng.integers(0, 4))
                sx, sy = [(-1, -1), (1, -1), (1, 1), (-1, 1)][corner]
                # like the user's photo: a bright sheet overlapping one corner of the page and sticking out
                sw, sh = rng.uniform(0.5, 1.0) * W, rng.uniform(0.4, 0.9) * H
                dx = sx * (W / 2 + sw / 2 - rng.uniform(0.15, 0.45) * sw)
                dy = sy * (H / 2 + sh / 2 - rng.uniform(0.15, 0.45) * sh)
                ang = math.radians(rng.uniform(-30, 30))
            ca, sa = math.cos(ang), math.sin(ang)
            quad = [(-sw / 2, -sh / 2), (sw / 2, -sh / 2), (sw / 2, sh / 2), (-sw / 2, sh / 2)]
            pts3 = [(dx + ca * x - sa * y, dy + sa * x + ca * y, 0.6 + k * 0.2) for x, y in quad]
            q = project(pts3)
            tex = np.full((400, 300, 3), rng.uniform(228, 246), np.float32)
            if rng.random() < 0.6:
                for yline in range(30, 380, 18):
                    cv2.line(tex, (25, yline), (int(rng.uniform(150, 280)), yline), (90, 90, 90), 2)
            Hm = cv2.getPerspectiveTransform(np.float32([[0, 0], [299, 0], [299, 399], [0, 399]]), q)
            warped = cv2.warpPerspective(tex, Hm, (img_w, img_h), flags=cv2.INTER_LINEAR, borderValue=(0, 0, 0))
            m = cv2.warpPerspective(np.ones((400, 300), np.float32), Hm, (img_w, img_h), flags=cv2.INTER_LINEAR)
            photo = photo * (1 - m[..., None]) + warped * m[..., None] * rng.uniform(0.85, 1.0)
            extras.append(q.tolist())
    paper_mask = np.zeros((img_h, img_w), np.uint8)
    paper_mask[yy, xx] = 255
    # Soft drop shadow of the paper on the table.
    shadow = cv2.GaussianBlur(paper_mask, (0, 0), 18).astype(np.float32) / 255
    shadow = np.roll(shadow, (int(rng.uniform(6, 22)), int(rng.uniform(-14, 14))), axis=(0, 1))
    photo *= (1 - 0.35 * shadow)[..., None]

    # Paper colour: front = texture, flap = blank back of the paper with faint show-through.
    th, tw = page_tex.shape[:2]
    map_x = np.full((img_h, img_w), -1, np.float32)
    map_y = np.full((img_h, img_w), -1, np.float32)
    map_x[yy, xx] = u * PX_PER_MM
    map_y[yy, xx] = v * PX_PER_MM
    front = cv2.remap(page_tex, map_x, map_y, cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)[yy, xx].astype(np.float32)
    back_paper = np.array([228, 233, 236], np.float32)
    show_through = 0.06 * (255 - front)
    colour = np.where(is_flap[:, None], back_paper - show_through, front)
    photo[yy, xx] = colour * s[:, None] * 1.02

    # Ground truth: flat page; dog-ear vacated triangle and the area under the flap become blank paper.
    gt = page_tex.copy()
    if "dogear" in params:
        a_uv, b_uv = params["dogear"]["fold_uv"]
        corner = [(0, 0), (W, 0), (W, H), (0, H)][params["dogear"]["corner"]]
        tri = np.array([corner, a_uv, b_uv], np.float32) * PX_PER_MM
        cv2.fillConvexPoly(gt, np.round(tri).astype(np.int32), (236, 240, 243), cv2.LINE_AA)
        # The flap covers the mirror image of that triangle across the fold line.
        a, b = np.array(a_uv), np.array(b_uv)
        dvec = (b - a) / np.linalg.norm(b - a)
        c = np.array(corner, float)
        proj = a + np.dot(c - a, dvec) * dvec
        mirrored = 2 * proj - c
        tri2 = np.array([mirrored, a_uv, b_uv], np.float32) * PX_PER_MM
        cv2.fillConvexPoly(gt, np.round(tri2).astype(np.int32), (236, 240, 243), cv2.LINE_AA)

    finger = None
    if family == "finger" or (family == "mixed" and rng.random() < 0.3):
        finger = draw_finger(rng, photo, paper_mask, xs, ys, nu, nv)

    # Camera: global light falloff, vignette, slight blur, noise, JPEG.
    yy2, xx2 = np.mgrid[0:img_h, 0:img_w].astype(np.float32)
    g = 1.0 + 0.12 * ((xx2 / img_w - 0.5) * rng.uniform(-1, 1) + (yy2 / img_h - 0.5) * rng.uniform(-1, 1))
    vignette = 1 - 0.18 * (((xx2 - cx) / cx) ** 2 + ((yy2 - cy) / cy) ** 2) / 2
    photo *= (g * vignette)[..., None]
    if rng.random() < 0.35 or family == "tab":
        photo *= np.array([0.78, 0.92, 1.08], np.float32)          # warm indoor light (BGR)
    if rng.random() < 0.25:
        gx, gy = rng.uniform(0.2, 0.8) * img_w, rng.uniform(0.2, 0.8) * img_h
        glare = np.exp(-(((xx2 - gx) ** 2 + (yy2 - gy) ** 2) / (2 * (0.12 * img_w) ** 2)))
        photo += (glare * rng.uniform(25, 60))[..., None]
    photo = cv2.GaussianBlur(photo, (0, 0), rng.uniform(0.6, 1.2))
    photo += rng.normal(0, rng.uniform(1.5, 4), photo.shape).astype(np.float32)
    photo = np.clip(photo, 0, 255).astype(np.uint8)

    # Corners: actual (where the paper corners are) and virtual (unfolded corner of a dog-ear).
    corner_idx = [idx[0, 0], idx[0, -1], idx[-1, -1], idx[-1, 0]]
    actual = [[float(xs[i]), float(ys[i])] for i in corner_idx]
    virtual = actual
    if "dogear" in params:
        # Where the folded corner would be if it were flattened back out (same pose, undeformed plane).
        c = params["dogear"]["corner"]
        unfolded = np.array([[-W / 2, -H / 2, 0], [W / 2, -H / 2, 0], [W / 2, H / 2, 0], [-W / 2, H / 2, 0]])[c]
        pc = unfolded @ Rm.T
        z = pc[2] + D
        virtual = [list(p) for p in actual]
        virtual[c] = [float(cx + f * pc[0] / z + offset[0]), float(cy + f * pc[1] / z + offset[1])]

    meta = {
        "family": family, "page_mm": [W, H], "image": [img_w, img_h], "focal_px": f,
        "pose_deg": [round(math.degrees(tilt), 1), round(math.degrees(yaw), 1), round(math.degrees(roll), 1)],
        "params": params, "corners_actual": actual, "corners_virtual": virtual, "finger": finger, "extra_sheets": extras,
    }
    return photo, gt, paper_mask, meta


def draw_finger(rng, photo, paper_mask, xs, ys, nu, nv):
    """A thumb pressing on one edge of the page."""
    h, w = paper_mask.shape
    edge = rng.integers(0, 4)
    t = rng.uniform(0.25, 0.75)
    grid_x, grid_y = xs.reshape(nv, nu), ys.reshape(nv, nu)
    if edge == 0:
        px, py = grid_x[0, int(t * (nu - 1))], grid_y[0, int(t * (nu - 1))]
    elif edge == 1:
        px, py = grid_x[int(t * (nv - 1)), -1], grid_y[int(t * (nv - 1)), -1]
    elif edge == 2:
        px, py = grid_x[-1, int(t * (nu - 1))], grid_y[-1, int(t * (nu - 1))]
    else:
        px, py = grid_x[int(t * (nv - 1)), 0], grid_y[int(t * (nv - 1)), 0]
    centre = np.array([w / 2, h / 2])
    out = np.array([px, py]) - centre
    out /= np.linalg.norm(out) + 1e-9
    length, width = rng.uniform(0.09, 0.14) * w, rng.uniform(0.045, 0.065) * w
    tip = np.array([px, py]) - out * rng.uniform(0.02, 0.05) * w
    base = tip + out * length * 1.6
    mask = np.zeros((h, w), np.uint8)
    cv2.line(mask, tuple(np.int32(tip)), tuple(np.int32(base)), 255, int(width), cv2.LINE_AA)
    cv2.circle(mask, tuple(np.int32(tip)), int(width / 2), 255, -1, cv2.LINE_AA)
    m = cv2.GaussianBlur(mask, (0, 0), 3).astype(np.float32)[..., None] / 255
    skin = np.array([rng.uniform(90, 140), rng.uniform(120, 165), rng.uniform(170, 220)], np.float32)
    photo[:] = photo * (1 - m) + skin * m
    return {"edge": int(edge), "tip": [float(tip[0]), float(tip[1])]}


def make_background(rng, w, h, kind=None):
    kind = kind or rng.choice(["wood", "dark_wood", "granite", "cloth", "desk", "light_desk", "dark"])
    base = {
        "wood": (70, 110, 160), "dark_wood": (35, 55, 80), "granite": (110, 112, 115), "cloth": (140, 90, 50),
        "desk": (180, 185, 188), "light_desk": (205, 210, 212), "dark": (30, 30, 34),
    }[kind]
    small = rng.normal(0, 14 if kind not in ("light_desk", "desk") else 6, (h // 32 + 2, w // 32 + 2, 1)).astype(np.float32)
    noise = cv2.resize(small, (w, h), interpolation=cv2.INTER_CUBIC)[..., None]
    bg = np.ones((h, w, 3), np.float32) * np.array(base, np.float32) + noise
    if "wood" in kind:
        yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
        grain = 10 * np.sin(yy / 9 + 4 * np.sin(xx / 260 + rng.uniform(0, 6)))
        bg += grain[..., None]
    if kind == "cloth":
        yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
        bg += (6 * np.sin(xx / 2.5) * np.sin(yy / 2.5))[..., None]
    return np.clip(bg, 0, 255)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--docs", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--count", type=int, default=300)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--start", type=int, default=0)
    ap.add_argument("--families", default="flat,dogear,crease,curl,crumple,finger,receipt,neighbor,packet,frame_edge,mixed")
    args = ap.parse_args()

    docs = sorted(glob.glob(os.path.join(args.docs, "*.png")) + glob.glob(os.path.join(args.docs, "*.jpg")))
    families = args.families.split(",")
    os.makedirs(args.out, exist_ok=True)
    for i in range(args.start, args.start + args.count):
        rng = np.random.default_rng(args.seed * 100003 + i)
        family = families[i % len(families)]
        if family == "receipt":
            W, H = 80.0, float(rng.uniform(150, 260))
            tex = make_receipt(rng, W, H)
            doc = "receipt"
        else:
            W, H = 210.0, 297.0
            doc = docs[int(rng.integers(0, len(docs)))] if docs else None
            tex = load_page(doc, W, H) if doc else make_receipt(rng, W, H)
        portrait = rng.random() < 0.75
        photo, gt, mask, meta = render(rng, tex, W, H, family, portrait)
        meta["doc"] = os.path.basename(doc) if doc else None
        d = os.path.join(args.out, f"{i:04d}-{family}")
        os.makedirs(d, exist_ok=True)
        cv2.imwrite(os.path.join(d, "photo.jpg"), photo, [cv2.IMWRITE_JPEG_QUALITY, 90])
        cv2.imwrite(os.path.join(d, "gt.png"), gt)
        cv2.imwrite(os.path.join(d, "mask.png"), mask)
        with open(os.path.join(d, "meta.json"), "w") as fh:
            json.dump(meta, fh, indent=1)
        print(d, flush=True)


if __name__ == "__main__":
    main()
