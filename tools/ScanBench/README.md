# ScanBench

Measures the document scanner (`Taildrop.Core/Scanning`) on photos with known answers, and trains the page
ranker the detector uses. The C# runner calls the shipping code; Python is only used to make data and score it.

Requirements: .NET 8 SDK; Python 3 with `numpy scipy opencv-python-headless scikit-learn` (and `tesseract` for
the optional OCR metric).

## Data

**Synthetic pages with exact ground truth.** `render_synthetic.py` lays real document pages on a 3D paper mesh
and photographs them with a 12 MP phone camera model: lighting, shading, shadows, blur, noise and JPEG. Families:

| family | what it tests |
|---|---|
| `flat` | control |
| `dogear` | a corner folded over |
| `crease` | half and tri-fold creases |
| `curl` | curled pages and receipts |
| `crumple` | crumpled paper |
| `finger` | a thumb over an edge |
| `receipt` | narrow receipts |
| `frame_edge` | page touching the frame |
| `neighbor` | another sheet touching a corner |
| `packet` | stacked sheets |
| `tab` | a packet's folded-back sheet against a corner, on warm wood |
| `mixed` | several of the above together |

Each sample folder holds:
- `photo.jpg`
- `gt.png`: the perfect flat page
- `mask.png`: where the paper is
- `meta.json`: corners and parameters

The page images come from SmartDoc 2015's document originals.

    python render_synthetic.py --docs <folder of page images> --out synth --count 352 --seed 1

**SmartDoc 2015, challenge 1** (real video frames with corner ground truth, 5 backgrounds):
<https://github.com/jchazalon/smartdoc15-ch1-dataset> (the `frames` release).

**Unit-test scenes**: `SCENE_EXPORT=<dir> dotnet test Taildrop.Core.Tests --filter DetectorSurvey` writes the
test-suite scenes in the same format (`SCENE_SEEDS`, `SCENE_SEED0` for more variants).

## Running

    dotnet build tools/ScanBench -c Release
    ScanBench synthetic <samples> <results>      # scan.jpg, outline.json, overlay.jpg per sample
    ScanBench smartdoc  <frames root> <out.csv>  # detected corners per frame
    ScanBench photos    <folder> <results>       # your own photos, for visual review
    python metrics.py synthetic <samples> <results> [--ocr] [--out summary.md] [--csv rows.csv]
    python metrics.py smartdoc  <frames root> <out.csv>

**Metrics:**
- `iou`, `missing` and `foreign`: the outline against the true paper. Missing means paper was cut off; foreign
  means table or another sheet came in.
- `corner`: error, as a percentage of the page diagonal.
- `msssim` and `ld`: the scan against the perfect page. `ld` is local distortion, the mean optical-flow
  displacement.
- `cer`: OCR error (optional).
- runtime.

Environment switches for experiments: `SCANBENCH_RANKER=off`, `SCANBENCH_CLEAN=off`.
`ScanBench debug <sample>` prints the scores of the true outline and the top hypotheses; `ScanBench lines
<photo> <out.jpg>` draws the line pool.

## The page ranker

The detector proposes many page outlines: quadrilaterals from straight lines and from paper-like regions.
Choosing the whole sheet over these impostors needs several cues at once:
- a panel of a folded sheet
- a block of print on the page
- the sheet underneath a packet
- a tab against a corner

`PageRanker` uses a small gradient-boosted tree model over 34 features per candidate (edge evidence, inside and
outside appearance, its relation to the other candidates). The model predicts the candidate's overlap with the
real page. To retrain it:

    ScanBench dump <synthetic dir>    synth.jsonl       # features from the shipping code
    ScanBench dump <smartdoc frames>  smartdoc.jsonl
    python train_ranker.py --names <PageRanker.FeatureNames, comma separated> \
        --synth synth.jsonl:<synthetic dir> --smartdoc smartdoc.jsonl:<frames root> \
        --test test.jsonl:<held-out synthetic dir> --out ../../Taildrop.Core/Scanning/page_ranker.json

SmartDoc is split by document. Half the documents train the ranker; the other half are only ever used for the
reported numbers. Rerun the training whenever the candidate generation or the features change: the model checks
the feature names at load.
