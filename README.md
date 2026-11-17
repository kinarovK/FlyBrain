# FlyTrader

> “I gave a fruit fly $10,000 and access to the crypto market.”

FlyTrader is an educational experiment that will eventually run a simplified
neural dynamical model over the real adult-female *Drosophila melanogaster*
FlyWire connectome, while paper-trading BTC/USDT. It has **no real-money
trading, exchange-order submission, or wallet/private-key handling**.

> “Why use artificial intelligence when you can use actual intelligence?”
> 
> It is a joke, not a scientific claim. A connectome is a structural wiring
> diagram; this project will not upload a fly brain, simulate consciousness, or
> measure happiness/dopamine. Future membrane dynamics, market mappings,
> decisions, and reward state are explicit model assumptions.

## Current milestone

Phases 1–6 are accepted: real FlyWire data acquisition, Phase 3 sparse-graph
derivation, bounded Phase 4 neural dynamics, Phase 5 whole-brain scaling, and
the Phase 6 local Brain Service API. Trading, market inputs, decision logic,
reward/dopamine modelling, .NET clients, UI, persistence, exchange access, and
Phase 7 remain intentionally deferred.

## Phase 3 sparse graph

`python scripts\preprocess_sparse_graph.py` builds a provenance-linked
`data/flywire/derived/flytrader-sparse-v1/` graph. Its default policy is an
induced graph over the 139,255 canonical proofread root IDs. It stores lossless
row-level edges and transmitter averages, plus a separately labelled,
pair-aggregated CSR view for future **mathematical** sparse operations. This is
not neural dynamics.

```powershell
python scripts\preprocess_sparse_graph.py
python scripts\validate_sparse_graph.py
python scripts\benchmark_sparse_graph.py --repeats 10
```

## Phase 4 neural-dynamics experiment

Phase 4 adds a deliberately bounded, deterministic LIF experiment over real
FlyWire v783 connectivity. It is a model, not a biological simulation: FlyWire
supplies annotated root IDs, structural connectivity, synapse counts, and
transmitter prediction probabilities; membrane potentials, weights, stimulus,
and transmitter-sign treatment are explicit experimental assumptions.

The default selects 16 annotated `ORN_DA1` afferent sensory roots and expands
three bounded downstream hops (912 neurons in the validated local dataset).
It defaults to unsigned structural connectivity. `transmitter` mode is a
conservative comparison: only confident GABA/ACh predictions affect signs;
glutamate, monoamines, ties, missing values, and low-confidence rows use the
recorded unsigned fallback because receptor context is not present in FlyWire.

```powershell
python scripts\run_neural_experiment.py --experiment-id phase4-orn-da1-unsigned-v1 `
  --config brain\configs\neural-v1.default.json --synapse-sign-mode unsigned
python scripts\run_neural_experiment.py --experiment-id phase4-orn-da1-no-stimulus-v1 `
  --config brain\configs\neural-v1.default.json --no-stimulus-control
python scripts\validate_neural_experiment.py data\flywire\experiments\neural-v1\phase4-orn-da1-unsigned-v1
python scripts\benchmark_neural_experiment.py --config brain\configs\neural-v1.default.json
```

Each command requires the validated local v783 source, annotations, and Phase
3 derivative; it never silently falls back to a fixture. Results are ignored
local artifacts under `data/flywire/experiments/neural-v1/` with resolved
configuration, provenance, compact spikes, CSV summaries, and diagnostic PNGs.
Safety limits report non-finite state, saturation, and configured runaway
activity instead of interpreting such output biologically.

## Phase 6 local brain service

Phase 6 exposes the accepted numerical engine as a local, versioned Python
service. It is a simplified neural simulation over the real FlyWire structural
connectome—not a biologically complete living fly brain. The service binds to
`127.0.0.1` by default; exposing it publicly without authentication is unsafe.

```powershell
python -m brain_service.server
python scripts\smoke_brain_service.py
```

The `/health/live`, `/health/ready`, and `/api/v1/brain/info` endpoints are
read-only. Sessions are opaque IDs; `/api/v1/brain/sessions` creates a bounded
reference or whole-canonical session, `/step` accepts sparse experimental
external-current root IDs, `/reset` provides deterministic replay, and state
responses are intentionally bounded. The generated OpenAPI document is at
`/openapi.json`. There is no .NET client, market/trading capability, durable
session state, or external network call in this phase.

See [the API contract](docs/brain-service-api.md) for lifecycle, sparse
stimulus, error, limit, security-boundary, and future-.NET integration details.
For consumers outside this repository, use the standalone
[Brain Service integration handoff](docs/brain-service-integration.md).

## Data provenance

The default `compact` profile contains the real FlyWire FAFB materialization
783 proofread root-ID list and aggregated proofread connectivity table from the
[FlyWire Consortium Zenodo record](https://doi.org/10.5281/zenodo.10676866),
plus canonical v783 annotations from
[flyconnectome/flywire_annotations](https://github.com/flyconnectome/flywire_annotations).
The connectivity table is a **proofread subset** summarized by
pre-neuron/post-neuron/neuropil, not the complete 9.5 GB individual-synapse
table. See [data/flywire/README.md](data/flywire/README.md).

Respect the [FlyWire data principles](https://edit.flywire.ai/principles.html)
and cite the published FlyWire data and annotation work when using results.

## Windows PowerShell setup

```powershell
cd FlyTrader
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -r brain\requirements.txt

# Read and agree to the FlyWire principles first, then acquire compact data.
python scripts\download_connectome.py --profile compact --agree-flywire-principles
python scripts\preprocess_connectome.py
python scripts\validate_connectome.py
python -m pytest tests\python -q
```

Use `--dry-run` to inspect downloads without network traffic. Add
`--profile full` only when the optional 9.5 GB individual-synapse table is
needed.

## Planned architecture

```
src/       reserved .NET 8+ application projects
brain/     reserved Python brain-service package and configuration
scripts/   FlyWire acquisition, preprocessing, and validation
data/      ignored source data and tracked provenance instructions
tests/     isolated fixture-based tests
```

## Citations

- Dorkenwald et al. (2024), FlyWire whole-brain connectome data,
  [Zenodo 10676866](https://doi.org/10.5281/zenodo.10676866).
- Schlegel et al. (2024), *Whole-brain annotation and multi-connectome cell
  typing of Drosophila*, [Nature](https://doi.org/10.1038/s41586-024-07686-5).
- Shiu et al. (2024), *A Drosophila computational brain model reveals
  sensorimotor processing*, [Nature](https://doi.org/10.1038/s41586-024-07763-9).

The annotation repository may require additional citations depending on its
pinned revision; the downloader records that revision in `manifest.json`.
