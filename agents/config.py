"""Paths and connection settings shared by the agents."""

import os
from pathlib import Path

from dotenv import load_dotenv

ROOT = Path(__file__).resolve().parents[1]
load_dotenv(ROOT / ".env", override=False)

CACHE_DIR = ROOT / ".cache" / "llm"
FHIR_REF = Path(__file__).parent / "fhir_ref"
ADT_OUTBOX = Path(os.environ.get("ADT_OUTBOX", ROOT / "mock-systems" / "adt-feed" / "outbox"))

REGISTRY_URL = os.environ.get("REGISTRY_URL", "http://localhost:8001")
ADT_HOST = os.environ.get("ADT_HOST", "localhost")
ADT_PORT = int(os.environ.get("ADT_PORT", "2575"))


def lab_db_settings():
    return {
        "host": os.environ.get("LAB_DB_HOST", "localhost"),
        "port": int(os.environ.get("LAB_DB_PORT", "5433")),
        "dbname": os.environ.get("LAB_DB_NAME", "labdb"),
        "user": os.environ.get("LAB_DB_USER") or "labuser",
        "password": os.environ.get("LAB_DB_PASSWORD") or "labpass",
    }


# Gemini model ids (free tier, checked on the pricing page in November 2026).
# A comma list means: try the next model when one is overloaded (HTTP 503),
# for example "gemini-3.8-flash,gemini-3.6-flash".
GEMINI_MODEL_STRONG = os.environ.get("GEMINI_MODEL_STRONG", "gemini-3.6-flash")
GEMINI_MODEL_FAST = os.environ.get("GEMINI_MODEL_FAST", "gemini-3.5-flash-lite")

SYSTEMS = {"a": "patient-registry-api", "b": "lab-db", "c": "adt-feed"}


def workspace():
    return Path(os.environ.get("AGENTS_WORKSPACE", ROOT / "workspace"))


def generated_dir():
    return Path(os.environ.get("AGENTS_GENERATED", ROOT / "generated"))


def system_dir(system):
    path = workspace() / system
    path.mkdir(parents=True, exist_ok=True)
    return path
