PYTHON ?= python3
VENV := .venv
N ?= 5
SYSTEM ?= a
AGENT_FLAGS = $(if $(YES),--yes,)

-include .env
export

.PHONY: help venv hooks data synthea transform up down seed smoke test replay-adt send-adt lint \
	discover design agents score generate validate pipeline metrics up-adapters \
	demo-add-system demo-reset dashboard gateway-test diagram

help:
	@echo "make data         run Synthea (if needed) and build the seed files"
	@echo "make up           start the three mock systems"
	@echo "make down         stop them and remove the database volume"
	@echo "make seed         rebuild seed files from Synthea output and reload the systems"
	@echo "make smoke        run the smoke tests"
	@echo "make replay-adt   send every ADT message over MLLP"
	@echo "make send-adt N=5 send the next N ADT messages (for a demo)"
	@echo "make test         run all tests (smoke tests and the eval guard)"
	@echo "make discover SYSTEM=a   run the discover agent (add YES=1 to skip the review prompt)"
	@echo "make design SYSTEM=a     run the design agent"
	@echo "make agents SYSTEM=all   run discover then design (use make pipeline for all steps)"
	@echo "make score SYSTEM=a      score the mapping against the ground truth"
	@echo "make generate SYSTEM=a   generate the .NET adapter"
	@echo "make validate SYSTEM=a   build, test and check the adapter, with self-healing"
	@echo "make pipeline SYSTEM=a   discover, design, generate and validate"
	@echo "make metrics             print the metrics table"
	@echo "make up-adapters  start the generated adapters (9001-9003) and the gateway (9000)"
	@echo "make dashboard    start the dashboard on http://localhost:8050"
	@echo "make demo-add-system SYSTEM=d   live demo: add a new system with review prompts"
	@echo "make demo-reset SYSTEM=d        remove the demo system again"
	@echo "make lint         run all pre-commit checks"
	@echo "make hooks        install the git hooks"

$(VENV)/.installed: requirements-dev.txt tests/requirements.txt agents/requirements.txt
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
	docker compose --profile adapters --profile demo down -v

seed: transform
	docker compose up -d --build --force-recreate --renew-anon-volumes --wait

smoke: venv
	$(VENV)/bin/pytest -q tests/smoke

test: venv
	$(VENV)/bin/pytest -q tests

discover: venv
	$(VENV)/bin/python -m agents run --system $(SYSTEM) --step discover $(AGENT_FLAGS)

design: venv
	$(VENV)/bin/python -m agents run --system $(SYSTEM) --step design $(AGENT_FLAGS)

agents: venv
	$(VENV)/bin/python -m agents run --system $(SYSTEM) --step discover $(AGENT_FLAGS)
	$(VENV)/bin/python -m agents run --system $(SYSTEM) --step design $(AGENT_FLAGS)

generate: venv
	$(VENV)/bin/python -m agents run --system $(SYSTEM) --step generate $(AGENT_FLAGS)

validate: venv
	$(VENV)/bin/python -m agents run --system $(SYSTEM) --step validate $(AGENT_FLAGS)

pipeline: venv
	$(VENV)/bin/python -m agents run --system $(SYSTEM) --step pipeline $(AGENT_FLAGS)

metrics: venv
	$(VENV)/bin/python -m agents metrics

up-adapters:
	docker compose --profile adapters up -d --build --wait

demo-add-system: venv
	LLM_CACHE=0 $(VENV)/bin/python -m agents demo --system $(SYSTEM)

demo-reset: venv
	$(VENV)/bin/python -m agents demo-reset --system $(SYSTEM)

dashboard: venv
	$(VENV)/bin/uvicorn dashboard.app:app --port 8050

gateway-test:
	dotnet test gateway/tests/Gateway.Tests.csproj

diagram:
	python3 -c "t=open('docs/architecture.md').read(); s=t.index('\x60\x60\x60mermaid')+10; open('docs/.architecture.mmd','w').write(t[s:t.index('\x60\x60\x60',s)].strip()+'\\n')"
	npx -y @mermaid-js/mermaid-cli@11.4.2 -i docs/.architecture.mmd -o docs/architecture.png -b white -w 1800 -s 2
	rm docs/.architecture.mmd

score: venv
	$(VENV)/bin/python eval/score.py --system $(SYSTEM)

replay-adt:
	docker compose exec -T adt-feed python feed.py replay

send-adt:
	docker compose exec -T adt-feed python feed.py send $(N)

lint: venv
	$(VENV)/bin/pre-commit run --all-files
