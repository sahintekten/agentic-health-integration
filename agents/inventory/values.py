"""Profile a list of values: types, null rate, shapes, formats and samples.

Everything here is deterministic. The LLM gets this output and must not
invent facts that are not in it.
"""

import re
from collections import Counter
from datetime import datetime

EPOCH_MIN, EPOCH_MAX = 946684800, 2208988800  # 2000-01-01 to 2040-01-01
SUSPICIOUS = re.compile(r"(?i)\b(zz\w*|test\w*|dummy\w*|xxx+)\b")


def shape(value):
    s = str(value)
    s = re.sub(r"[A-Z]", "A", s)
    s = re.sub(r"[a-z]", "a", s)
    s = re.sub(r"[0-9]", "9", s)
    return s[:32]


def _valid_date(s, fmt):
    try:
        datetime.strptime(s, fmt)
        return True
    except ValueError:
        return False


def _as_number(s):
    try:
        return float(s)
    except ValueError:
        return None


def detect_formats(values):
    strs = [str(v).strip() for v in values]
    if not strs:
        return []
    found = []
    if all(re.fullmatch(r"-?\d+", s) for s in strs):
        nums = [int(s) for s in strs]
        if all(EPOCH_MIN <= n <= EPOCH_MAX for n in nums):
            found.append("epoch_seconds")
        elif all(EPOCH_MIN * 1000 <= n <= EPOCH_MAX * 1000 for n in nums):
            found.append("epoch_milliseconds")
        if all(len(s) == 8 and _valid_date(s, "%Y%m%d") for s in strs):
            found.append("date_yyyymmdd")
        if all(len(s) == 14 and _valid_date(s, "%Y%m%d%H%M%S") for s in strs):
            found.append("datetime_yyyymmddhhmmss")
    slashed = [re.fullmatch(r"(\d{2})/(\d{2})/(\d{4})", s) for s in strs]
    if all(slashed):
        first = max(int(m[1]) for m in slashed)
        second = max(int(m[2]) for m in slashed)
        if first > 12 >= second:
            found.append("date_dd/mm/yyyy")
        elif second > 12 >= first:
            found.append("date_mm/dd/yyyy")
        else:
            found.append("date_nn/nn/yyyy_ambiguous")
    if all(re.fullmatch(r"\d{4}-\d{2}-\d{2}", s) for s in strs):
        found.append("date_iso")
    if all(re.fullmatch(r"\d{4}-\d{2}-\d{2}T[\d:.]+(Z|[+-]\d{2}:?\d{2})?", s) for s in strs):
        found.append("datetime_iso")
    is_text = not any(isinstance(v, (int, float)) for v in values)
    if is_text and not found and all(_as_number(s) is not None for s in strs):
        found.append("number_stored_as_text")
    lengths = {len(s) for s in strs}
    if len(lengths) > 1 and all(s.isdigit() for s in strs) and max(lengths) - min(lengths) == 1:
        found.append("digits_with_varying_length")
    return found


def profile(values, max_samples=5):
    total = len(values)
    present = [v for v in values if v is not None and str(v).strip() != ""]
    counts = Counter(str(v) for v in present)
    shapes = Counter(shape(v) for v in present)

    samples, seen = [], set()
    for s, _ in shapes.most_common():
        v = next(str(x) for x in present if shape(x) == s)
        if v not in seen:
            samples.append(v)
            seen.add(v)
        if len(samples) >= max_samples:
            break
    for v, _ in counts.most_common():
        if len(samples) >= max_samples:
            break
        if v not in seen:
            samples.append(v)
            seen.add(v)

    result = {
        "count": total,
        "null_rate": round(1 - len(present) / total, 3) if total else 0.0,
        "distinct": len(counts),
        "types": sorted({type(v).__name__ for v in present}),
        "shapes": [s for s, _ in shapes.most_common(4)],
        "samples": samples,
        "formats": detect_formats(present),
    }
    numbers = [n for n in (_as_number(str(v).strip()) for v in present) if n is not None]
    if numbers and len(numbers) == len(present):
        result["min"], result["max"] = min(numbers), max(numbers)
    if 0 < len(counts) <= 12 and len(present) > len(counts):
        result["value_counts"] = dict(counts.most_common(12))
    padded = sum(1 for v in present if isinstance(v, str) and (v != v.strip() or "  " in v))
    if padded:
        result["values_with_extra_spaces"] = padded
    odd = sorted({str(v) for v in present if isinstance(v, str) and SUSPICIOUS.search(v)})
    if odd:
        result["suspicious_values"] = odd[:5]
    return result
