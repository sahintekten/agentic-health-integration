"""MLLP framing and small HL7 v2 helpers. Standard library only."""

import socket
from datetime import datetime, timezone

START = b"\x0b"
END = b"\x1c\r"


def frame(message):
    return START + message.encode("utf-8") + END


def unframe(raw):
    return raw[raw.find(START) + 1 :].decode("utf-8", errors="replace")


def parse_msh(message):
    """Return {n: value} for MSH-2 to MSH-12, or None if there is no MSH."""
    first = message.split("\r", 1)[0]
    if not first.startswith("MSH") or len(first) < 4:
        return None
    parts = first.split(first[3])
    # MSH-1 is the field separator itself, so MSH-n sits at parts[n - 1]
    return {n: parts[n - 1] if n - 1 < len(parts) else "" for n in range(2, 13)}


def build_ack(message, code, text=""):
    msh = parse_msh(message) or {}
    trigger = msh.get(9, "").split("^")
    msg_type = f"ACK^{trigger[1]}^ACK" if len(trigger) > 1 else "ACK"
    control_id = msh.get(10, "")
    now = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    header = [
        "MSH",
        "^~\\&",
        msh.get(5) or "ADT_FEED",
        msh.get(6) or "HOSP",
        msh.get(3, ""),
        msh.get(4, ""),
        now,
        "",
        msg_type,
        f"ACK{control_id}",
        "P",
        msh.get(12) or "2.5",
    ]
    msa = ["MSA", code, control_id] + ([text] if text else [])
    return "\r".join(["|".join(header), "|".join(msa)])


def ack_code(ack):
    for seg in ack.split("\r"):
        if seg.startswith("MSA|"):
            return seg.split("|")[1]
    return "??"


def read_frame(sock):
    buf = b""
    while END not in buf:
        chunk = sock.recv(4096)
        if not chunk:
            raise ConnectionError("connection closed before the end of the MLLP frame")
        buf += chunk
    return unframe(buf.split(END, 1)[0])


def send(host, port, message, timeout=5.0):
    with socket.create_connection((host, port), timeout=timeout) as sock:
        sock.sendall(frame(message))
        return read_frame(sock)
