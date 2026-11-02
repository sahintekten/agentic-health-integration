# Architecture

![Architecture](architecture.png)

The Mermaid source of the picture:

```mermaid
flowchart LR
    subgraph sources["Mock hospital systems (synthetic data)"]
        A["A: patient registry<br/>REST JSON :8001"]
        B["B: lab database<br/>PostgreSQL :5433"]
        C["C: ADT feed<br/>HL7 v2 MLLP :2575 + files"]
        D["D: appointments export<br/>weekly CSV files"]
    end

    subgraph agents["Agent pipeline (Python)"]
        direction TB
        DIS["Discover<br/>inventory + profile"] --> G1{{"review"}}
        G1 --> DES["Design<br/>mapping + data rules<br/>+ API spec + ID plan"] --> G2{{"review"}}
        G2 --> GEN["Generate<br/>.NET 8 adapter + tests"]
        GEN --> VAL["Validate<br/>build, test, contract,<br/>PHI, reconciliation"]
        VAL -- "failing output" --> FIX["Repair<br/>(never weakens tests)"] --> VAL
        VAL --> G3{{"review"}}
    end

    subgraph adapters["Generated adapters (.NET 8, Firely SDK)"]
        AA["adapter A :9001"]
        AB["adapter B :9002"]
        AC["adapter C :9003"]
        AD["adapter D :9004"]
    end

    GW["Gateway :9000<br/>merged FHIR API<br/>patient index (SQLite)"]
    CLIENTS["FHIR clients"]
    DASH["Dashboard :8050<br/>pipeline, scores, links review"]

    LLM[("LLM provider layer<br/>gemini / claude_code / ...<br/>disk cache + run log")]
    EVAL[("eval/ground_truth<br/>+ score.py<br/>(agents never read it)")]

    A & B & C & D -. "interfaces only" .-> DIS
    G3 --> AA & AB & AC & AD
    A --> AA
    B --> AB
    C --> AC
    D --> AD
    AA & AB & AC & AD --> GW --> CLIENTS
    agents <--> LLM
    DES -. "mapping.yaml" .-> EVAL
    GW --> DASH
    VAL -. "reports + metrics" .-> DASH
```

## How to read it

- The **mock systems** are four messy hospital systems with synthetic data. The agents only use their interfaces: HTTP, SQL, MLLP and export files.
- The **agent pipeline** runs Discover, Design, Generate and Validate. A person approves the output at the review gates. Validate repairs the generated code in a loop, but it may never delete or weaken a test.
- Each **adapter** reads one source system and serves FHIR R4 JSON.
- The **gateway** is the single FHIR entry point. It links patients across systems by the shared hospital number. When it finds a likely match with a different id, it marks it as *probable* for review.
- The **LLM provider layer** sits behind every agent call, so the provider can change without changing the agents.
- **eval/** holds the answer key and the scorer. A test in CI makes sure the agents never open the answer key.

To make `architecture.png` again after you change the diagram (needs Node; the first run downloads mermaid-cli):

```bash
make diagram
```
