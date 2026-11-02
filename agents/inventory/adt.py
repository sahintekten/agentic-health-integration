"""Inventory for System C (HL7 v2 ADT): message types, segments and fields from the outbox files."""

import socket
from collections import Counter, defaultdict

import hl7

from agents.inventory.values import profile


def _mllp_open(host, port):
    try:
        socket.create_connection((host, port), timeout=3).close()
        return True
    except OSError:
        return False


def build(outbox, host, port):
    files = sorted(outbox.glob("*.hl7"))
    if not files:
        raise SystemExit(f"No .hl7 files in {outbox}. Start the systems with 'make up' first.")

    event_counts = Counter()
    segment_counts = Counter()
    seg_occurrences = Counter()
    values = defaultdict(list)
    events_with_value = defaultdict(set)
    composite = set()
    line_endings = Counter()

    for path in files:
        raw = path.read_bytes().decode("utf-8")
        line_endings["CR" if "\r" in raw and "\n" not in raw else "LF/CRLF"] += 1
        msg = hl7.parse(raw.replace("\r\n", "\r").replace("\n", "\r"))
        msh = msg.segment("MSH")
        event = str(msh[9])
        event_counts[event] += 1
        trigger = event.split("^")[1] if "^" in event else event
        for seg in msg:
            name = str(seg[0])
            segment_counts[name] += 1
            seg_occurrences[name] += 1
            start = 3 if name == "MSH" else 1
            for i in range(start, len(seg)):
                field = seg[i]
                text = str(field)
                key = f"{name}-{i}"
                values[key].append(text)
                if text:
                    events_with_value[key].add(trigger)
                if "^" in text:
                    composite.add(key)
                    for c, comp in enumerate(field[0], start=1):
                        ckey = f"{key}.{c}"
                        values[ckey].append(str(comp))
                        if str(comp):
                            events_with_value[ckey].add(trigger)

    fields = {}
    for key, vals in values.items():
        seg_name, rest = key.split("-", 1)
        base = f"{seg_name}-{rest.split('.')[0]}"
        if key == base and key in composite:
            continue
        if "." in rest and base not in composite:
            continue
        total = seg_occurrences[seg_name]
        padded = vals + [""] * (total - len(vals))
        p = profile(padded)
        if p["null_rate"] == 1.0:
            continue
        p["events"] = sorted(events_with_value[key])
        if "." in rest:
            p["component_of"] = base
        fields[key] = p

    return {
        "system": "c",
        "kind": "hl7v2",
        "source": str(outbox),
        "message_files": len(files),
        "line_endings": dict(line_endings),
        "mllp_listener_open": _mllp_open(host, port),
        "message_types": dict(event_counts),
        "segments": dict(segment_counts),
        "fields": dict(sorted(fields.items(), key=lambda kv: _sort_key(kv[0]))),
    }


def _sort_key(key):
    order = ["MSH", "EVN", "PID", "PV1"]
    seg, rest = key.split("-", 1)
    parts = [int(x) for x in rest.split(".")]
    return (order.index(seg) if seg in order else len(order), seg, parts)
