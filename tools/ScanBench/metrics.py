"""
Scores ScanBench results.

  python metrics.py synthetic <samples dir> <results dir> [--ocr] [--out summary.md]
  python metrics.py smartdoc  <frames root> <results.csv>  [--out summary.md]

Synthetic metrics per photo (ground truth from render_synthetic.py):
  iou         detected outline vs true paper (page + any folded flap)
  missing     share of the true paper the outline leaves out        (lower is better; "the entire document")
  foreign     share of the outline that is not this paper           (table, other sheets of a packet, fingers)
  corner      mean corner error, % of the page diagonal (to the true / unfolded corner)
  msssim      multi-scale SSIM between the scan and the perfect flat page   (higher is better)
  ld          local distortion: mean optical-flow displacement scan -> perfect page, px at ~1 MP (lower is better)
  cer         OCR character error rate vs OCR of the perfect page (--ocr; lower is better)
"""
import argparse
import glob
import json
import math
import os
import subprocess
import sys
from concurrent.futures import ProcessPoolExecutor

import cv2
import numpy as np


# ------------------------------------------------------------------ helpers

def outline_polygon(o, w, h):
    ring = o["Top"] + o["Right"] + list(reversed(o["Bottom"])) + list(reversed(o["Left"]))
    return np.array([[p[0] * w, p[1] * h] for p in ring], np.float32)


def poly_mask(poly, w, h):
    m = np.zeros((h, w), np.uint8)
    cv2.fillPoly(m, [np.round(poly).astype(np.int32)], 255)
    return m


def corner_error(det, truth):
    det = np.asarray(det, float)
    truth = np.asarray(truth, float)
    best = math.inf
    for flip in (False, True):
        d = det[::-1] if flip else det
        for k in range(4):
            best = min(best, np.mean(np.linalg.norm(np.roll(d, k, axis=0) - truth, axis=1)))
    return best


def normalize_light(gray):
    g = gray.astype(np.float32)
    bg = cv2.GaussianBlur(g, (0, 0), 25) + 1
    return np.clip(g / bg * 200, 0, 255).astype(np.uint8)


def ms_ssim(a, b):
    weights = [0.0448, 0.2856, 0.3001, 0.2363, 0.1333]
    a = a.astype(np.float64)
    b = b.astype(np.float64)
    C1, C2 = (0.01 * 255) ** 2, (0.03 * 255) ** 2
    values = []
    for level, w in enumerate(weights):
        mu_a = cv2.GaussianBlur(a, (11, 11), 1.5)
        mu_b = cv2.GaussianBlur(b, (11, 11), 1.5)
        saa = cv2.GaussianBlur(a * a, (11, 11), 1.5) - mu_a ** 2
        sbb = cv2.GaussianBlur(b * b, (11, 11), 1.5) - mu_b ** 2
        sab = cv2.GaussianBlur(a * b, (11, 11), 1.5) - mu_a * mu_b
        cs = (2 * sab + C2) / (saa + sbb + C2)
        if level == len(weights) - 1:
            ssim = ((2 * mu_a * mu_b + C1) / (mu_a ** 2 + mu_b ** 2 + C1)) * cs
            values.append(max(ssim.mean(), 1e-6) ** w)
        else:
            values.append(max(cs.mean(), 1e-6) ** w)
        a = cv2.resize(a, (a.shape[1] // 2, a.shape[0] // 2), interpolation=cv2.INTER_AREA)
        b = cv2.resize(b, (b.shape[1] // 2, b.shape[0] // 2), interpolation=cv2.INTER_AREA)
    return float(np.prod(values))


def local_distortion(ref, out):
    dis = cv2.DISOpticalFlow_create(cv2.DISOPTICAL_FLOW_PRESET_MEDIUM)
    flow = dis.calc(ref, out, None)
    return float(np.mean(np.linalg.norm(flow, axis=2)))


def align_to_gt(scan_gray, gt_gray):
    """Our scanner keeps the page as seen in the photo; turn it the way the original page is upright."""
    h, w = gt_gray.shape
    small_gt = cv2.resize(gt_gray, (96, int(96 * h / w)))
    best, best_img = -2, None
    for k in range(4):
        r = np.rot90(scan_gray, k)
        cand = cv2.resize(r, (w, h), interpolation=cv2.INTER_AREA)
        s = cv2.resize(cand, small_gt.shape[::-1])
        score = np.corrcoef(s.ravel().astype(float), small_gt.ravel().astype(float))[0, 1]
        if score > best:
            best, best_img = score, cand
    return best_img


def ocr(path_or_img, cache=None):
    if isinstance(path_or_img, str) and cache and os.path.exists(cache):
        return open(cache).read()
    if not isinstance(path_or_img, str):
        tmp = f"/tmp/claude-0/ocr-{os.getpid()}.png"
        cv2.imwrite(tmp, path_or_img)
        path = tmp
    else:
        path = path_or_img
    text = subprocess.run(["tesseract", path, "-", "--psm", "3"], capture_output=True, text=True).stdout
    if cache:
        with open(cache, "w") as fh:
            fh.write(text)
    return text


def cer(ref, hyp):
    ref = " ".join(ref.split())
    hyp = " ".join(hyp.split())
    if not ref:
        return float("nan")
    prev = list(range(len(hyp) + 1))
    for i, rc in enumerate(ref, 1):
        cur = [i] + [0] * len(hyp)
        for j, hc in enumerate(hyp, 1):
            cur[j] = min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (rc != hc))
        prev = cur
    return prev[-1] / len(ref)


# ------------------------------------------------------------------ synthetic

def score_sample(job):
    sample_dir, result_dir, do_ocr = job
    name = os.path.basename(sample_dir)
    meta = json.load(open(os.path.join(sample_dir, "meta.json")))
    out = json.load(open(os.path.join(result_dir, "outline.json")))
    w, h = meta["image"]
    truth = cv2.imread(os.path.join(sample_dir, "mask.png"), cv2.IMREAD_GRAYSCALE)
    if meta["family"] in ("dogear", "mixed") and "dogear" in meta["params"]:
        cv2.fillPoly(truth, [np.round(np.array(meta["corners_virtual"])).astype(np.int32)], 255)
    det = poly_mask(outline_polygon(out, w, h), w, h)
    t, d = truth > 0, det > 0
    inter = np.logical_and(t, d).sum()
    union = np.logical_or(t, d).sum()
    row = {
        "name": name, "family": meta["family"], "confident": out["Confident"],
        "iou": inter / max(union, 1),
        "missing": 1 - inter / max(t.sum(), 1),
        "foreign": 1 - inter / max(d.sum(), 1),
        "ms": out["ms"]["detect"] + out["ms"]["render"],
    }
    corners = np.array([[c[0] * w, c[1] * h] for c in out["Corners"]])
    diag = math.hypot(*(np.max(meta["corners_virtual"], axis=0) - np.min(meta["corners_virtual"], axis=0)))
    row["corner"] = corner_error(corners, meta["corners_virtual"]) / diag * 100

    gt = cv2.imread(os.path.join(sample_dir, "gt.png"), cv2.IMREAD_GRAYSCALE)
    scan = cv2.imread(os.path.join(result_dir, "scan.jpg"), cv2.IMREAD_GRAYSCALE)
    # ~1 MP comparison size, like the DocUNet protocol (598,400 px)
    s = math.sqrt(598400 / (gt.shape[0] * gt.shape[1]))
    gt_s = cv2.resize(gt, (int(gt.shape[1] * s), int(gt.shape[0] * s)), interpolation=cv2.INTER_AREA)
    aligned = align_to_gt(scan, gt_s)
    a, b = normalize_light(gt_s), normalize_light(aligned)
    row["msssim"] = ms_ssim(a, b)
    row["ld"] = local_distortion(a, b)
    if do_ocr:
        gt_text = ocr(os.path.join(sample_dir, "gt.png"), cache=os.path.join(sample_dir, "gt.ocr.txt"))
        up = cv2.resize(align_to_gt(scan, gt), (gt.shape[1] * 2, gt.shape[0] * 2), interpolation=cv2.INTER_CUBIC)
        row["cer"] = min(cer(gt_text, ocr(up)), 1.0)
    return row


def summarize(rows, keys, group="family"):
    groups = {}
    for r in rows:
        groups.setdefault(r[group], []).append(r)
    groups["ALL"] = rows
    lines = ["| " + group + " | n | " + " | ".join(keys) + " |", "|" + "---|" * (len(keys) + 2)]
    for g in sorted(groups, key=lambda x: (x == "ALL", x)):
        rs = groups[g]
        cells = []
        for k in keys:
            vals = [r[k] for r in rs if k in r and r[k] == r[k]]
            if not vals:
                cells.append("-")
            elif k == "confident":
                cells.append(f"{100 * np.mean(vals):.0f}%")
            elif k in ("missing", "foreign"):
                cells.append(f"{100 * np.mean(vals):.2f}% (worst {100 * np.max(vals):.1f}%)")
            elif k in ("corner",):
                cells.append(f"{np.mean(vals):.2f}% (worst {np.max(vals):.1f}%)")
            elif k == "ms":
                cells.append(f"{np.mean(vals):.0f}")
            else:
                cells.append(f"{np.mean(vals):.3f}")
        lines.append(f"| {g} | {len(rs)} | " + " | ".join(cells) + " |")
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("mode")
    ap.add_argument("input")
    ap.add_argument("results")
    ap.add_argument("--ocr", action="store_true")
    ap.add_argument("--out", default=None)
    ap.add_argument("--csv", default=None)
    args = ap.parse_args()

    if args.mode == "synthetic":
        jobs = []
        for d in sorted(glob.glob(os.path.join(args.input, "*"))):
            r = os.path.join(args.results, os.path.basename(d))
            if os.path.exists(os.path.join(r, "outline.json")):
                jobs.append((d, r, args.ocr))
        with ProcessPoolExecutor(4) as pool:
            rows = list(pool.map(score_sample, jobs))
        keys = ["iou", "missing", "foreign", "corner", "msssim", "ld"] + (["cer"] if args.ocr else []) + ["confident", "ms"]
        table = summarize(rows, keys)
    else:
        import csv, gzip
        meta = {}
        with gzip.open(os.path.join(args.input, "metadata.csv.gz"), "rt") as fh:
            for r in csv.DictReader(fh):
                meta[r["image_path"]] = r
        rows = []
        for r in csv.DictReader(open(args.results)):
            m = meta.get(r["image_path"])
            if not m:
                continue
            w, h = int(r["width"]), int(r["height"])
            det = np.array([[float(r[f"x{i}"]) * w, float(r[f"y{i}"]) * h] for i in range(4)], np.float32)
            gt = np.array([[float(m["tl_x"]), float(m["tl_y"])], [float(m["tr_x"]), float(m["tr_y"])],
                           [float(m["br_x"]), float(m["br_y"])], [float(m["bl_x"]), float(m["bl_y"])]], np.float32)
            a, b = poly_mask(det, w, h) > 0, poly_mask(gt, w, h) > 0
            iou = np.logical_and(a, b).sum() / max(np.logical_or(a, b).sum(), 1)
            diag = math.hypot(*(gt.max(0) - gt.min(0)))
            rows.append({"background": m["bg_name"], "iou": iou, "corner": corner_error(det, gt) / diag * 100,
                         "confident": r["confident"] == "True", "ms": float(r["ms"]), "good": float(iou > 0.9)})
        table = summarize(rows, ["iou", "good", "corner", "confident", "ms"], group="background")

    print(table)
    if args.out:
        with open(args.out, "w") as fh:
            fh.write(table + "\n")
    if args.csv:
        import csv
        with open(args.csv, "w", newline="") as fh:
            wtr = csv.DictWriter(fh, fieldnames=sorted({k for r in rows for k in r}))
            wtr.writeheader()
            wtr.writerows(rows)


if __name__ == "__main__":
    main()
