"""Inventory for flat-file exports (CSV): encoding, delimiter, columns and repeated keys across files."""

import csv
import io
from collections import Counter, defaultdict

from agents.inventory.values import profile


def detect_encoding(raw):
    try:
        raw.decode("utf-8")
        return "utf-8", False
    except UnicodeDecodeError:
        return "windows-1252", True


def read_export(folder):
    """Return (rows, facts). Rows are dicts keyed by header name, plus _file."""
    files = sorted(folder.glob("*.csv"))
    if not files:
        raise SystemExit(f"No .csv files in {folder}.")
    raw0 = files[0].read_bytes()
    encoding, utf8_failed = detect_encoding(b"".join(f.read_bytes() for f in files))
    first_line = raw0.decode(encoding).splitlines()[0]
    delimiter = csv.Sniffer().sniff(first_line, delimiters=";,|\t").delimiter
    header = next(csv.reader([first_line], delimiter=delimiter))
    trailing_empty = header and header[-1] == ""
    columns = [h for h in header if h != ""]
    rows, other_lines, line_endings = [], Counter(), Counter()
    for f in files:
        text = f.read_bytes().decode(encoding)
        line_endings["CRLF" if "\r\n" in text else "LF"] += 1
        reader = csv.reader(io.StringIO(text), delimiter=delimiter)
        next(reader)
        for parts in reader:
            if len(parts) < len(columns):
                if any(p.strip() for p in parts):
                    other_lines[" ".join(parts)[:40]] += 1
                continue
            row = dict(zip(columns, parts[: len(columns)]))
            row["_file"] = f.name
            rows.append(row)
    facts = {
        "files": [f.name for f in files],
        "encoding": encoding,
        "utf8_decode_failed": utf8_failed,
        "delimiter": delimiter,
        "line_endings": dict(line_endings),
        "header": header,
        "empty_trailing_column": bool(trailing_empty),
        "non_data_lines": {"count": sum(other_lines.values()), "examples": list(other_lines)[:3]},
    }
    return rows, facts


def build(folder):
    rows, facts = read_export(folder)
    columns = [c for c in rows[0] if c != "_file"]
    fields = {c: {"location": "csv column", **profile([r[c] for r in rows])} for c in columns}

    repeated = {}
    for c in columns:
        by_value = defaultdict(set)
        for r in rows:
            by_value[r[c]].add(r["_file"])
        in_many = sum(1 for files in by_value.values() if len(files) > 1)
        if fields[c]["distinct"] > len(rows) * 0.3 and in_many:
            changing = Counter()
            groups = defaultdict(list)
            for r in rows:
                groups[r[c]].append(r)
            for group in groups.values():
                if len(group) > 1:
                    for other in columns:
                        if len({g[other] for g in group}) > 1:
                            changing[other] += 1
            repeated[c] = {
                "values_in_more_than_one_file": in_many,
                "columns_that_change_between_files": dict(changing.most_common(5)),
            }
    return {
        "system": "d",
        "kind": "csv-files",
        "source": str(folder),
        "row_count": len(rows),
        **facts,
        "repeated_keys": repeated,
        "fields": fields,
    }
