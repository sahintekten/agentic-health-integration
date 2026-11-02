"""System C: hospital ADT feed (mock).

- MLLP listener on port 2575. It ACKs every HL7 v2 message it gets
  (AA, AE or AR) and keeps a copy in inbox/.
- At start it writes every ADT message to outbox/ as a .hl7 file.
  This is the file drop interface.

Use feed.py to push messages over MLLP (replay all, or send N for a demo).
"""

import asyncio
import logging
import os
import re
from pathlib import Path

from messages import load_messages, write_outbox
from mllp import END, START, build_ack, parse_msh, unframe

PORT = int(os.environ.get("MLLP_PORT", "2575"))
INBOX = Path(os.environ.get("ADT_INBOX", "inbox"))

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("adt-feed")


def check(message):
    msh = parse_msh(message)
    if msh is None:
        return "AR", "message does not start with MSH"
    if not msh[10]:
        return "AR", "MSH-10 control id is missing"
    if not msh[9].startswith("ADT^"):
        return "AE", f"unsupported message type {msh[9]}"
    if "\rPID|" not in message:
        return "AE", "PID segment is missing"
    return "AA", ""


def save(message):
    msh = parse_msh(message) or {}
    name = re.sub(r"[^A-Za-z0-9_-]", "_", msh.get(10, "")) or "no_control_id"
    INBOX.mkdir(parents=True, exist_ok=True)
    (INBOX / f"{name}.hl7").write_bytes(message.encode("utf-8"))


async def handle(reader, writer):
    peer = writer.get_extra_info("peername")
    buf = b""
    try:
        while chunk := await reader.read(4096):
            buf += chunk
            while END in buf:
                raw, buf = buf.split(END, 1)
                message = unframe(raw)
                code, text = check(message)
                save(message)
                writer.write(START + build_ack(message, code, text).encode("utf-8") + END)
                await writer.drain()
                msh = parse_msh(message) or {}
                log.info("from %s: %s %s -> %s %s", peer, msh.get(9, "?"), msh.get(10, "?"), code, text)
    except ConnectionError:
        pass
    finally:
        writer.close()


async def main():
    messages = load_messages()
    write_outbox(messages)
    log.info("wrote %d messages to outbox", len(messages))
    server = await asyncio.start_server(handle, "0.0.0.0", PORT)
    log.info("MLLP listener on port %d", PORT)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
