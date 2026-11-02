"""Push ADT messages over MLLP and drop a copy of each in outbox/.

  python feed.py replay          send every message, oldest first
  python feed.py send 5          send the next 5 messages (for a live demo)

The target is --target or ADT_TARGET (host:port). By default it is this
container's own listener, so you can see the send and ACK loop before any
adapter exists. Later, point it at the adapter's MLLP listener.
"""

import argparse
import os
import time
from pathlib import Path

from messages import load_messages, write_outbox
from mllp import ack_code, send

CURSOR = Path(os.environ.get("ADT_CURSOR", "/tmp/adt_feed_cursor"))


def push(messages, target, delay):
    host, _, port = target.rpartition(":")
    counts = {}
    for m in messages:
        write_outbox([m])
        code = ack_code(send(host, int(port), m["hl7"]))
        counts[code] = counts.get(code, 0) + 1
        print(f"{m['control_id']}  ADT^{m['event']}  {m['ts']}  -> {code}", flush=True)
        if delay:
            time.sleep(delay)
    print(f"sent {len(messages)} messages to {target}: " + ", ".join(f"{k}={v}" for k, v in sorted(counts.items())))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=["replay", "send"])
    parser.add_argument("count", nargs="?", type=int, default=5, help="number of messages for 'send'")
    parser.add_argument("--target", default=os.environ.get("ADT_TARGET", "127.0.0.1:2575"))
    parser.add_argument("--delay", type=float, default=None, help="seconds between messages")
    args = parser.parse_args()

    messages = load_messages()
    if args.command == "replay":
        push(messages, args.target, args.delay or 0)
        CURSOR.write_text("0")
        return

    start = int(CURSOR.read_text()) if CURSOR.exists() else 0
    batch = [messages[(start + i) % len(messages)] for i in range(args.count)]
    push(batch, args.target, 1.0 if args.delay is None else args.delay)
    CURSOR.write_text(str((start + args.count) % len(messages)))


if __name__ == "__main__":
    main()
