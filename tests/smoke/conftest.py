import os
import socket

import httpx
import psycopg
import pytest

START, END = b"\x0b", b"\x1c\r"


@pytest.fixture
def registry():
    with httpx.Client(base_url=os.environ.get("REGISTRY_URL", "http://localhost:8001"), timeout=5) as client:
        yield client


@pytest.fixture
def lab_db():
    conn = psycopg.connect(
        host=os.environ.get("LAB_DB_HOST", "localhost"),
        port=int(os.environ.get("LAB_DB_PORT", "5433")),
        dbname="labdb",
        user=os.environ.get("LAB_DB_USER") or "labuser",
        password=os.environ.get("LAB_DB_PASSWORD") or "labpass",
        connect_timeout=5,
    )
    yield conn
    conn.close()


@pytest.fixture
def mllp():
    host = os.environ.get("ADT_HOST", "localhost")
    port = int(os.environ.get("ADT_PORT", "2575"))

    def send(message):
        with socket.create_connection((host, port), timeout=5) as sock:
            sock.sendall(START + message.encode() + END)
            buf = b""
            while END not in buf:
                chunk = sock.recv(4096)
                if not chunk:
                    break
                buf += chunk
        return buf.strip(START + END).decode()

    return send
