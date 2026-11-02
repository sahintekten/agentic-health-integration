# Synthea output

This folder is filled by `data/scripts/generate_synthea.sh` (or `make data`).
Everything here except this file is ignored by git.

- `bin/` has the downloaded Synthea jar.
- `output/csv/` has the CSV export. The transform scripts read from here.
- `output/fhir/` has the FHIR R4 bundles. We only keep them for reference.

The data is fully synthetic. No real patient data is used anywhere in this repo.
