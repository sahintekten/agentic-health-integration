"""Score an agent mapping against the ground truth.

  python eval/score.py --system a|b|c|all

Compares workspace/<system>/mapping.yaml with eval/ground_truth/<name>.yaml
and writes eval/results/<system>.json. This is the only code that may read
the ground truth folder.

How matching works:
- Sources are compared case-insensitively. Database sources become table.column.
  HL7 sources become seg-n or seg-n.c, and a field also matches its components.
- A FHIR path matches when the resource is the same and one path is a prefix of
  the other at an element boundary, ignoring [..] parts (Patient.identifier
  matches Patient.identifier[x].value). "A and B" in the ground truth means either.
- Ground truth "ignore" needs a predicted ignore/use/lookup. Ground truth "use"
  and "lookup" accept any prediction for that source.
- Extra predicted "ignore" items for sources that are not in the ground truth
  are not counted.
- Data rules are scored on their own: the set of predicted rule_id values
  against the expected set. Rules with rule_id "other" are not counted.
"""

import argparse
import json
import re
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[1]
GROUND_TRUTH = ROOT / "eval" / "ground_truth"
RESULTS = ROOT / "eval" / "results"
WORKSPACE = ROOT / "workspace"
NAMES = {"a": "patient_registry_api", "b": "lab_db", "c": "adt_feed", "d": "appointments_export"}

HL7_SOURCE = re.compile(r"^([a-z][a-z0-9]{2})[-.](\d+)(?:[.-](\d+))?$")


def norm_source(source, table=None):
    s = str(source).strip().lower()
    m = HL7_SOURCE.match(s)
    if m:
        s = f"{m[1]}-{m[2]}" + (f".{m[3]}" if m[3] else "")
    if table and "." not in s:
        s = f"{str(table).strip().lower()}.{s}"
    return s


def norm_path(path):
    p = re.sub(r"\[[^\]]*\]", "", str(path)).strip().lower()
    return re.sub(r"\s+", "", p)


def target_of(item):
    if "path" in item and item.get("path"):
        paths = [norm_path(p) for p in re.split(r"\s+and\s+", str(item["path"]))]
        return ("map", tuple(paths))
    for kind in ("ignore", "use", "lookup"):
        if kind in item:
            return (kind, ())
    return ("none", ())


def items(doc):
    mappings = doc.get("mappings") or []
    if isinstance(mappings, dict):
        for table, rows in mappings.items():
            for item in rows or []:
                if isinstance(item, dict):
                    yield table, item
    else:
        for item in mappings:
            if isinstance(item, dict):
                yield None, item


def flatten(doc):
    """Return {source: [target, ...]}. A source like 'MSH-3, MSH-4' is split."""
    out = {}
    for table, item in items(doc):
        for src in str(item.get("source", "")).split(","):
            if src.strip():
                targets = out.setdefault(norm_source(src, table), [])
                if target_of(item) not in targets:
                    targets.append(target_of(item))
    return out


def paths_match(a, b):
    if not a or not b:
        return False
    sa, sb = a.split("."), b.split(".")
    short, long_ = (sa, sb) if len(sa) <= len(sb) else (sb, sa)
    return len(short) >= 2 and long_[: len(short)] == short


def target_match(gt, pred):
    gkind, gpaths = gt
    pkind, ppaths = pred
    if gkind in ("use", "lookup"):
        return pkind != "none"
    if gkind == "ignore":
        return pkind in ("ignore", "use", "lookup")
    if pkind != "map":
        return False
    return any(paths_match(g, p) for g in gpaths for p in ppaths)


def related(source, other):
    return source == other or source.startswith(other + ".") or other.startswith(source + ".")


def describe(target):
    kind, paths = target
    return " | ".join(paths) if kind == "map" else kind


def _f1(hit, n_pred, n_gt):
    precision = hit / n_pred if n_pred else 0.0
    recall = hit / n_gt if n_gt else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    return round(precision, 3), round(recall, 3), round(f1, 3)


def score_rules(gt_doc, pred_doc):
    expected = {str(r.get("rule_id")) for r in gt_doc.get("data_rules") or []}
    predicted = {str(r.get("rule_id")) for r in pred_doc.get("data_rules") or [] if isinstance(r, dict)}
    predicted.discard("other")
    hit = expected & predicted
    precision, recall, f1 = _f1(len(hit), len(predicted), len(expected))
    return {
        "precision": precision,
        "recall": recall,
        "f1": f1,
        "found": sorted(hit),
        "missed": sorted(expected - predicted),
        "extra": sorted(predicted - expected),
    }


def score(system):
    gt_doc = yaml.safe_load((GROUND_TRUTH / f"{NAMES[system]}.yaml").read_text())
    gt = flatten(gt_doc)
    mapping_path = WORKSPACE / system / "mapping.yaml"
    if not mapping_path.exists():
        raise SystemExit(f"{mapping_path} not found. Run the design step first.")
    pred_doc = yaml.safe_load(mapping_path.read_text()) or {}
    pred = flatten(pred_doc)

    rows, gt_pairs, gt_hit = [], 0, 0
    used_pred = set()
    for source, targets in gt.items():
        candidates = [(s, t) for s, ts in pred.items() if related(source, s) for t in ts]
        for target in targets:
            gt_pairs += 1
            hits = [(s, t) for s, t in candidates if target_match(target, t)]
            used_pred.update((s, t) for s, t in hits)
            if hits:
                gt_hit += 1
                status = "correct"
            elif candidates:
                status = "wrong"
            else:
                status = "missed"
            rows.append(
                {
                    "source": source,
                    "expected": describe(target),
                    "got": "; ".join(sorted({describe(t) for _, t in candidates})) or "-",
                    "status": status,
                }
            )

    pred_pairs = 0
    extra = []
    for source, targets in pred.items():
        in_gt = any(related(source, g) for g in gt)
        for t in targets:
            if not in_gt and t[0] != "map":
                continue
            pred_pairs += 1
            if not in_gt:
                extra.append({"source": source, "got": describe(t)})
    pred_hit = len(used_pred)

    precision = pred_hit / pred_pairs if pred_pairs else 0.0
    recall = gt_hit / gt_pairs if gt_pairs else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    result = {
        "system": system,
        "ground_truth": NAMES[system],
        "precision": round(precision, 3),
        "recall": round(recall, 3),
        "f1": round(f1, 3),
        "counts": {
            "ground_truth_pairs": gt_pairs,
            "matched_ground_truth_pairs": gt_hit,
            "predicted_pairs": pred_pairs,
            "matched_predicted_pairs": pred_hit,
            "correct": sum(r["status"] == "correct" for r in rows),
            "wrong": sum(r["status"] == "wrong" for r in rows),
            "missed": sum(r["status"] == "missed" for r in rows),
            "extra": len(extra),
        },
        "fields": rows,
        "extra": extra,
        "rules": score_rules(gt_doc, pred_doc),
    }
    RESULTS.mkdir(parents=True, exist_ok=True)
    (RESULTS / f"{system}.json").write_text(json.dumps(result, indent=1))
    return result


def print_result(r):
    print(f"\nSystem {r['system']} ({r['ground_truth']})")
    print(f"{'source':<22} {'status':<8} {'expected':<42} got")
    for row in r["fields"]:
        print(f"{row['source'][:22]:<22} {row['status']:<8} {row['expected'][:42]:<42} {row['got'][:60]}")
    for row in r["extra"]:
        print(f"{row['source'][:22]:<22} {'extra':<8} {'-':<42} {row['got'][:60]}")
    c = r["counts"]
    print(
        f"precision {r['precision']:.2f}  recall {r['recall']:.2f}  F1 {r['f1']:.2f}  "
        f"(correct {c['correct']}, wrong {c['wrong']}, missed {c['missed']}, extra {c['extra']})"
    )
    rules = r["rules"]
    print(
        f"rules: precision {rules['precision']:.2f}  recall {rules['recall']:.2f}  F1 {rules['f1']:.2f}  "
        f"found {rules['found']}  missed {rules['missed']}  extra {rules['extra']}"
    )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--system", default="all", choices=["a", "b", "c", "d", "all"])
    args = parser.parse_args()
    systems = [s for s in NAMES if (WORKSPACE / s / "mapping.yaml").exists()] if args.system == "all" else [args.system]
    results = [score(s) for s in systems]
    for r in results:
        print_result(r)
    if len(results) > 1:
        print(
            "\nsummary: "
            + "  ".join(f"{r['system']}: F1 {r['f1']:.2f} rules F1 {r['rules']['f1']:.2f}" for r in results)
        )


if __name__ == "__main__":
    main()
