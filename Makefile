PYTHON ?= python3
VENV := .venv
N ?= 5

-include .env
export

.PHONY: help venv hooks data synthea transform up down seed smoke replay-adt send-adt lint

help:
	@echo "make data         run Synthea (if needed) and build the seed files"
	@echo "make up           start the three mock systems"
	@echo "make down         stop them and remove the database volume"
	@echo "make seed         rebuild seed files from Synthea output and reload the systems"
	@echo "make smoke        run the smoke tests"
	@echo "make replay-adt   send every ADT message over MLLP"
	@echo "make send-adt N=5 send the next N ADT messages (for a demo)"
	@echo "make lint         run all pre-commit checks"
	@echo "make hooks        install the git hooks"

$(VENV)/.installed: requirements-dev.txt tests/requirements.txt
	$(PYTHON) -m venv $(VENV)
	$(VENV)/bin/pip install -q --upgrade pip
	$(VENV)/bin/pip install -q -r requirements-dev.txt
	touch $@

venv: $(VENV)/.installed

# If you use a global core.hooksPath that calls .git/hooks, the second
# command installs the hooks there anyway.
hooks: venv
	$(VENV)/bin/pre-commit install || GIT_CONFIG_GLOBAL=/dev/null $(VENV)/bin/pre-commit install

data: synthea transform

synthea:
	bash data/scripts/generate_synthea.sh

transform:
	$(PYTHON) data/scripts/transform_all.py

up:
	docker compose up -d --build --wait

down:
	docker compose down -v

seed: transform
	docker compose up -d --build --force-recreate --renew-anon-volumes --wait

smoke: venv
	$(VENV)/bin/pytest -q tests/smoke

replay-adt:
	docker compose exec -T adt-feed python feed.py replay

send-adt:
	docker compose exec -T adt-feed python feed.py send $(N)

lint: venv
	$(VENV)/bin/pre-commit run --all-files
