"""
Trains the page ranker (Taildrop.Core/Scanning/page_ranker.json): a small gradient-boosted tree ensemble that
predicts, for each page-outline candidate the detector proposes, how well it overlaps the real page (IoU).
The detector then takes the candidate with the highest prediction.

  ScanBench dump <synthetic dir>   synth.jsonl       candidate features (C#, the shipping code)
  ScanBench dump <smartdoc frames> smartdoc.jsonl
  python train_ranker.py --synth synth.jsonl:<synthetic dir> [--synth ...] --smartdoc smartdoc.jsonl:<frames root>
                         [--test synth_test.jsonl:<dir>] --out page_ranker.json

SmartDoc frames are split by document: half the documents train, the other half are only ever evaluated.
"""
import argparse
import gzip
import csv
import json
import os
from concurrent.futures import ProcessPoolExecutor

import cv2
import numpy as np
from sklearn.ensemble import GradientBoostingRegressor

LABEL_SCALE = 0.25
CALIBRATION = []


def load(path):
    with open(path) as fh:
        return [json.loads(line) for line in fh if line.strip()]


def quad_mask(c, w, h):
    m = np.zeros((h, w), np.uint8)
    pts = np.round(np.array(c, np.float64) * [w, h]).astype(np.int32)
    cv2.fillPoly(m, [pts], 1)
    return m


def iou(a, b):
    u = np.logical_or(a, b).sum()
    return float(np.logical_and(a, b).sum() / u) if u else 0.0


def label_synth(job):
    rec, root = job
    d = os.path.join(root, os.path.dirname(rec["path"]))
    truth = cv2.imread(os.path.join(d, "mask.png"), cv2.IMREAD_GRAYSCALE)
    w, h = max(1, int(rec["w"] * LABEL_SCALE)), max(1, int(rec["h"] * LABEL_SCALE))
    t = cv2.resize(truth, (w, h), interpolation=cv2.INTER_AREA) > 127
    meta = json.load(open(os.path.join(d, "meta.json")))
    return [iou(quad_mask(c["c"], w, h), t) for c in rec["cands"]], meta["family"]


def label_smartdoc(job):
    rec, gt = job
    w, h = max(1, int(rec["w"] * LABEL_SCALE)), max(1, int(rec["h"] * LABEL_SCALE))
    t = quad_mask(gt, w, h) > 0
    return [iou(quad_mask(c["c"], w, h), t) for c in rec["cands"]], None


def smartdoc_truth(root):
    out = {}
    with gzip.open(os.path.join(root, "metadata.csv.gz"), "rt") as fh:
        for r in csv.DictReader(fh):
            out[r["image_path"]] = r
    return out


def build(specs, kind):
    recs, jobs = [], []
    for spec in specs:
        path, root = spec.split(":", 1)
        data = load(path)
        if kind == "smartdoc":
            meta = smartdoc_truth(root)
            for rec in data:
                m = meta.get(rec["path"])
                if not m:
                    continue
                img = cv2.imread(os.path.join(root, rec["path"]), cv2.IMREAD_REDUCED_COLOR_4)
                fw, fh = img.shape[1] * 4, img.shape[0] * 4
                gt = [[float(m[f"{k}_x"]) / fw, float(m[f"{k}_y"]) / fh] for k in ("tl", "tr", "br", "bl")]
                rec["group"] = m["model_name"]
                rec["bg"] = m["bg_name"]
                recs.append(rec)
                jobs.append((rec, gt))
        else:
            for rec in data:
                recs.append(rec)
                jobs.append((rec, root))
    fn = label_smartdoc if kind == "smartdoc" else label_synth
    cache = specs[0].split(":", 1)[0] + ".labels.json" if len(specs) == 1 else None
    if cache and os.path.exists(cache) and os.path.getmtime(cache) > os.path.getmtime(specs[0].split(":", 1)[0]):
        labels = [tuple(x) for x in json.load(open(cache))]
    else:
        with ProcessPoolExecutor(2) as pool:
            labels = list(pool.map(fn, jobs, chunksize=8))
        if cache:
            json.dump(labels, open(cache, "w"))
    for rec, (lab, fam) in zip(recs, labels):
        rec["labels"] = lab
        rec["family"] = fam or rec.get("bg")
    return recs


def matrix(recs):
    X, y = [], []
    for rec in recs:
        for c, lab in zip(rec["cands"], rec["labels"]):
            X.append(c["f"])
            y.append(lab)
    return np.array(X, np.float32), np.array(y, np.float32)


def evaluate(model, recs, name):
    by = {}
    for rec in recs:
        X = np.array([c["f"] for c in rec["cands"]], np.float32)
        pred = model.predict(X) if model is not None else None
        pick = int(np.argmax(pred)) if pred is not None else rec["chosen"]
        if pred is not None:
            CALIBRATION.append((float(pred[pick]), rec["labels"][pick]))
        lab = rec["labels"]
        row = by.setdefault(rec["family"], {"model": [], "heuristic": [], "top": [], "oracle": []})
        row["model"].append(lab[pick])
        row["heuristic"].append(lab[max(rec["chosen"], 0)])
        row["top"].append(lab[0])
        row["oracle"].append(max(lab))
    print(f"\n{name}: mean candidate-quad IoU (before edge tracing)")
    print(f"{'group':<14}{'n':>5}{'top':>8}{'growth':>8}{'model':>8}{'oracle':>8}")
    allr = {k: [] for k in ("model", "heuristic", "top", "oracle")}
    for fam in sorted(by):
        r = by[fam]
        for k in allr:
            allr[k] += r[k]
        print(f"{fam:<14}{len(r['model']):>5}{np.mean(r['top']):8.3f}{np.mean(r['heuristic']):8.3f}{np.mean(r['model']):8.3f}{np.mean(r['oracle']):8.3f}")
    print(f"{'ALL':<14}{len(allr['model']):>5}{np.mean(allr['top']):8.3f}{np.mean(allr['heuristic']):8.3f}{np.mean(allr['model']):8.3f}{np.mean(allr['oracle']):8.3f}")
    return np.mean(allr["model"])


def export(model, names, path):
    trees = []
    for est in model.estimators_[:, 0]:
        t = est.tree_
        trees.append({
            "feature": [int(f) if f >= 0 else 0 for f in t.feature],
            "threshold": [float(x) for x in t.threshold],
            "left": [int(x) for x in t.children_left],
            "right": [int(x) for x in t.children_right],
            "value": [round(float(v), 6) for v in t.value[:, 0, 0]],
        })
    bias = float(model.init_.constant_.ravel()[0])
    with open(path, "w") as fh:
        json.dump({"features": names, "bias": bias, "rate": model.learning_rate, "trees": trees}, fh, separators=(",", ":"))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--synth", action="append", default=[])
    ap.add_argument("--smartdoc", action="append", default=[])
    ap.add_argument("--test", action="append", default=[])
    ap.add_argument("--names", required=True, help="feature names, comma separated (PageRanker.FeatureNames)")
    ap.add_argument("--out")
    ap.add_argument("--trees", type=int, default=250)
    ap.add_argument("--depth", type=int, default=4)
    ap.add_argument("--subsample", type=float, default=0.6)
    ap.add_argument("--power", type=float, default=1.0)
    args = ap.parse_args()

    synth = [r for spec in args.synth for r in build([spec], "synth")]
    smart = [r for spec in args.smartdoc for r in build([spec], "smartdoc")]
    test = [r for spec in args.test for r in build([spec], "synth")]
    groups = sorted({r["group"] for r in smart})
    held = set(groups[1::2])
    smart_train = [r for r in smart if r["group"] not in held]
    smart_test = [r for r in smart if r["group"] in held]
    train = synth + smart_train
    X, y = matrix(train)
    y = y ** args.power   # stretch the top: telling 0.95 from 0.99 matters, 0.2 from 0.4 does not
    print(f"train: {len(train)} photos, {len(y)} candidates; smartdoc held-out documents: {sorted(held)}")

    model = GradientBoostingRegressor(n_estimators=args.trees, max_depth=args.depth, learning_rate=0.05,
                                      subsample=args.subsample, min_samples_leaf=20, random_state=0)
    model.fit(X, y)
    names = args.names.split(",")
    imp = sorted(zip(model.feature_importances_, names), reverse=True)
    print("importance: " + ", ".join(f"{n} {v:.3f}" for v, n in imp[:14]))
    if test:
        evaluate(model, test, "synthetic test set (separate seed)")
    if smart_test:
        evaluate(model, smart_test, "SmartDoc, held-out documents")
    if CALIBRATION:
        cal = np.array(CALIBRATION)
        print("\nconfidence threshold on the predicted score (held-out photos): share with IoU >= 0.9 above it, share of photos above it")
        for t in (0.5, 0.6, 0.7, 0.75, 0.8, 0.85, 0.9):
            above = cal[cal[:, 0] >= t]
            if len(above):
                print(f"  {t:.2f}: {np.mean(above[:, 1] >= 0.9):.3f} of {len(above) / len(cal):.3f}")
        low = cal[cal[:, 0] < 0.3]
        print(f"  below 0.30: {len(low)} photos, mean IoU {low[:, 1].mean() if len(low) else float('nan'):.3f}")
    if args.out:
        export(model, names, args.out)
        print("wrote", args.out)


if __name__ == "__main__":
    main()
