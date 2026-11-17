# FlyTrader Project Handoff

This is the onboarding guide for agents and developers extending FlyTrader.
Read it before modifying code, data, neural behaviour, or API contracts.

## What this project is

FlyTrader is an educational software experiment built around a **simplified
leaky integrate-and-fire (LIF) model** over structural connectivity from the
adult-female *Drosophila melanogaster* FlyWire FAFB v783 connectome.

It is not a living fly brain, a consciousness simulation, or a source of
measured electrophysiology, sensory signals, reward/dopamine state, or
behaviour. The FlyWire source contributes structural facts—root IDs,
connectivity, synapse counts, annotations, and transmitter-prediction
probabilities. Membrane dynamics, current, weights, and safety thresholds are
explicit modelling assumptions.

The project was initially named around a future paper-trading idea, but that
work is **not implemented** and is not part of the accepted foundation.

## Current status and boundaries

Phases 1–6 are accepted and closed. The active task is idle.

| Phase | Accepted result |
|---|---|
| 1–2 | Repository bootstrap and checksum/provenance-aware FlyWire acquisition. |
| 3 | Deterministic sparse canonical graph representation. |
| 4 | Validated bounded real-connectome LIF experiment. |
| 5 | Deterministic scaling through the exact 139,255-neuron canonical graph. |
| 6 | Local, versioned Python Brain Service API around the accepted engine. |

**Do not start Phase 7 automatically.** Any next phase needs separate scope,
specification, design approval, implementation, independent review, and human
acceptance. Consult [project status](../../docs/project/status/STATUS.md) and
the prior task records in `../../docs/project/tasks/` before proposing work.

The following are deliberately out of scope unless a later approved task says
otherwise:

- market/crypto data, market-to-neural encoding, BUY/HOLD/SELL output,
  reward/dopamine, paper trading, wallets, exchange connectivity;
- C#/.NET client code or generated API clients;
- UI, persistence, WebSockets, external network calls from the neural engine,
  public deployment, or unauthenticated public service exposure;
- a second implementation of graph loading, LIF equations, weight transforms,
  transmitter policy, propagation, or numerical safety.

## Provenance and graph invariants

The real-data foundation is the local, checksum-verified compact FlyWire FAFB
v783 profile. It must remain the source for production runs.

| Invariant | Accepted value/policy |
|---|---|
| Dataset | FlyWire FAFB v783, adult-female *Drosophila* structural connectome |
| Canonical roots | 139,255 |
| Retained source rows | 16,847,997 |
| Represented synapses | 54,492,922 |
| Pair-aggregated CSR entries | 15,091,983 |
| Sparse graph format | `flytrader-sparse-v1` |
| Graph policy | `induced_canonical_proofread_nodes` |

The local source and experiments are intentionally ignored by Git under
`data/flywire/`. Do not commit raw source data, generated artifacts, manifests
with machine-local paths, or experiment output unless an approved task changes
that policy. Read [data/flywire/README.md](../data/flywire/README.md) before
acquisition or reprocessing, honor FlyWire data principles, and retain the
manifest/checksum provenance chain.

The compact connection table is an aggregated proofread subset, not the full
individual-synapse table. Do not silently replace it with a fixture, a random
network, a sampled graph, or invented neurons.

## Architecture

```text
real FlyWire v783 source + manifest
            │
            ▼
Phase 3 sparse graph (`scripts/sparse_graph.py`)
            │
            ├── Phase 4 LIF engine (`scripts/neural_dynamics.py`)
            │       └── shared DynamicState / advance_dynamic_state seam
            │
            └── Phase 5 whole-brain selection/scaling (`scripts/whole_brain_scaling.py`)
                    │
                    ▼
            Phase 6 Brain Service (`brain_service/`)
              runtime → immutable cached domains → isolated sessions → FastAPI
```

The key rule is that the service is an adapter. It calls the shared numerical
engine; it must not reproduce equations or sparse propagation in HTTP code.
Phase 6’s real-data test proves direct-engine/API equality through an actual
spike and refractory period.

### Important modules

| Path | Owns |
|---|---|
| `scripts/flywire_data.py` | Strict source acquisition/manifest/checksum helpers. |
| `scripts/sparse_graph.py` | Phase 3 canonical mapping, lossless edge arrays, pair-aggregated CSR, validation. |
| `scripts/neural_dynamics.py` | Phase 4 config validation, deterministic selection, LIF primitive, shared dynamic-state advance, safety evaluation. |
| `scripts/whole_brain_scaling.py` | Phase 5 deterministic tier selection, preflight, bounded events, scale-study artifacts. |
| `brain_service/runtime.py` | One-process graph validation and cached immutable reference/whole domains. |
| `brain_service/sessions.py` | Opaque mutable sessions, reset/step, aggregate accounting, locks, limits. |
| `brain_service/schemas.py` | Public Pydantic request/response/error models; treat as API compatibility surface. |
| `brain_service/api.py` | FastAPI routes and stable error mapping only. |
| `brain/configs/*.json` | Versioned model, whole-scaling, and service configuration. |
| `tests/python/` | Fast fixture regression coverage. |
| `docs/brain-service-integration.md` | Consumer-facing Brain Service contract. |

## Numerical and behavioural invariants

Preserve these unless an explicitly approved scientific/numerical change says
otherwise:

- Phase 4 LIF update ordering, sparse propagation delay, reset/refractory
  behaviour, and safety semantics;
- row-level weight transformation before pair aggregation;
- unsigned transmitter mode as the default; the conservative transmitter mode
  only assigns confident GABA/ACh signs and falls back otherwise;
- exact root-ID membership—never silently remap an unknown ID;
- resource preflight before unsafe whole-domain allocation;
- bounded **recording**, not bounded dynamics: event caps never stop
  simulation or invalidate exact aggregate counters;
- deterministic reset/replay for the same configuration and inputs;
- no-drive controls should remain silent unless a new, documented mechanism
  changes that behaviour.

Existing configurations label assumptions and justifications. Keep scientific
parameters in `neural-v1.default.json`; keep service/deployment limits in
`brain-service-v1.default.json`.

## Running locally

From `D:\AI\FlyTrader` on Windows PowerShell:

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -r brain\requirements.txt
```

Fast unit/regression suite:

```powershell
.\.venv\Scripts\python.exe -m pytest -q -p no:cacheprovider
```

If Windows sandbox/permissions prevent pytest temporary-directory cleanup,
run the same project-local command with the required workspace permission; do
not weaken or delete unrelated files to work around it.

Real-data checks require the local validated source and Phase 3 derivative:

```powershell
.\.venv\Scripts\python.exe scripts\smoke_brain_service.py
.\.venv\Scripts\python.exe scripts\validate_brain_service_equivalence.py
.\.venv\Scripts\python.exe scripts\validate_brain_service_whole.py
.\.venv\Scripts\python.exe scripts\benchmark_brain_service.py
```

The whole validation prepares the complete 139,255-neuron domain and is
resource-sensitive. Run it deliberately and avoid concurrent whole-domain
processes.

## Brain Service essentials

The service starts with:

```powershell
.\.venv\Scripts\python.exe -m brain_service.server
```

It defaults to `127.0.0.1:8765`. It has no authentication and is not safe to
expose publicly. `/health/live` is process liveness; `/health/ready` confirms
the real graph/domains are prepared. The public contract is versioned under
`/api/v1` and OpenAPI is at `/openapi.json`.

Two modes are supported: `reference` (accepted 912-neuron validation domain)
and `whole` (exact 139,255-neuron canonical domain). The runtime holds shared
immutable domains; sessions own only dynamic state. Defaults restrict active
sessions to 4, whole sessions to 1, and simultaneous whole operations to 1.

Read the standalone [integration handoff](brain-service-integration.md) for
all routes, payloads, reset/disposal, limits, errors, and a minimal consumer
example. Do not make an external client depend on internal Python modules or
unlisted fields; use the generated OpenAPI schema and documented error codes.

## Tests and evidence

The accepted Phase 6 closure recorded:

- 39 full regression tests passing;
- six focused service tests covering health/readiness/OpenAPI, lifecycle,
  reset/replay, sparse validation, direct-engine/API equivalence, vector drive
  through refractory state, safety mapping, same-session conflict, and
  whole-session limits;
- real API/direct-engine equality for step, membrane/current/refractory,
  aggregate spikes, and events after a real spike/refractory cycle;
- silent zero-drive reference and exact whole-domain controls;
- eager whole-cache observed process peak near 1.36 GB, versus about 1.03 GB
  for the accepted Phase 5 whole-run baseline.

Do not claim a new feature preserves this foundation merely because fixture
tests pass. Add the relevant real-data integration check and describe what was
and was not executed.

## Safe extension workflow

1. Read this guide, `STATUS.md`, current-task status, applicable ADRs, and the
   relevant prior task/design/review files.
2. Decide whether the proposal changes public API, model behaviour, graph/data
   semantics, resource profile, or only an internal implementation detail.
3. For material work, create a separately scoped task/specification with
   measurable acceptance criteria. Do not bypass the approval gates.
4. Preserve or add a direct-engine/API comparison whenever changing a shared
   engine seam or service adapter.
5. Start with a fast fixture regression, then run real-data validation that
   matches the risk. Run the whole-domain path only when its evidence is needed.
6. Keep public API changes versioned and typed. Update OpenAPI/schema tests,
   the integration handoff, examples, and stable error semantics together.
7. Run independent review before requesting human acceptance; do not start the
   next phase automatically after closure.

## Common pitfalls

- The root README and this guide describe accepted current state; older task
  files are historical evidence, not an authorization for future work.
- Avoid dense JSON stimulus/state vectors. Use sparse root-ID stimuli and
  bounded responses.
- Avoid per-request graph/matrix rebuilding or per-neuron Python loops.
- Do not report Phase 5 event truncation as a simulation failure; it only caps
  retained detailed events.
- Do not treat structural connectivity or model current as biological proof.
- Never hide missing/malformed real data by using a test fixture in production
  mode.

## Where to read next

- [Brain Service integration handoff](brain-service-integration.md) — external
  HTTP consumer contract.
- [Brain Service API notes](brain-service-api.md) — local service operation.
- [Data provenance](../data/flywire/README.md) — source and citation rules.
- [Project status](../../docs/project/status/STATUS.md) — phase state.
- `../../docs/project/tasks/0003_*` through `0005_*` — accepted task,
  design, plan, review, and acceptance evidence.
