#!/usr/bin/env bash
# Generate synthetic patients with Synthea.
# Output goes to data/synthea/output (csv/ and fhir/). Set FORCE=1 to regenerate.
set -euo pipefail

SYNTHEA_VERSION="${SYNTHEA_VERSION:-v4.0.0}"
SEED="${SEED:-20260101}"
POPULATION="${POPULATION:-100}"
# fixed dates so the output is the same on every run
REFERENCE_DATE="${REFERENCE_DATE:-20261031}"
END_DATE="${END_DATE:-20261031}"
YEARS_OF_HISTORY="${YEARS_OF_HISTORY:-5}"

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
BIN_DIR="$ROOT/data/synthea/bin"
OUT_DIR="$ROOT/data/synthea/output"
JAR="$BIN_DIR/synthea-with-dependencies-$SYNTHEA_VERSION.jar"
URL="https://github.com/synthetichealth/synthea/releases/download/$SYNTHEA_VERSION/synthea-with-dependencies.jar"

if ! java -version >/dev/null 2>&1; then
  echo "Java was not found. Synthea needs Java 17 or newer."
  echo "Install it with:  brew install --cask temurin@21"
  echo "Then run this script again."
  exit 1
fi

if [ -f "$OUT_DIR/csv/patients.csv" ] && [ "${FORCE:-0}" != "1" ]; then
  echo "Synthea output already exists in $OUT_DIR (set FORCE=1 to regenerate)."
  exit 0
fi

mkdir -p "$BIN_DIR"
if [ ! -f "$JAR" ]; then
  echo "Downloading Synthea $SYNTHEA_VERSION ..."
  curl -fL --progress-bar -o "$JAR.part" "$URL"
  mv "$JAR.part" "$JAR"
fi

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

echo "Running Synthea: seed=$SEED population=$POPULATION reference=$REFERENCE_DATE"
cd "$BIN_DIR"
java -jar "$JAR" \
  -s "$SEED" -cs "$SEED" -p "$POPULATION" -r "$REFERENCE_DATE" -e "$END_DATE" \
  --exporter.baseDirectory="$OUT_DIR/" \
  --exporter.years_of_history="$YEARS_OF_HISTORY" \
  --exporter.csv.export=true \
  --exporter.fhir.export=true \
  --exporter.hospital.fhir.export=false \
  --exporter.practitioner.fhir.export=false \
  --generate.append_numbers_to_person_names=false \
  --generate.thread_pool_size=1 \
  Massachusetts

echo "Done. CSV files are in $OUT_DIR/csv"
