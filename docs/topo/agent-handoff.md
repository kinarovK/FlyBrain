# Topo engineering handoff

> **FlyBrain note (2026-11-18):** Topo now lives in the FlyBrain repository as
> `src/Topo.*` (UI: `Topo.Module`), artifacts under `modules/topo/`. The brain
> is started by the FlyBrain app. "Do not modify FlyTrader" is superseded by
> [ADR 0002](../project/adr/0002-flybrain-monorepo.md); the frozen scientific
> contracts below still apply. Start with [FLYBRAIN_HANDOFF.md](../FLYBRAIN_HANDOFF.md).

## What this project is

Topo is a .NET 9 WPF research sandbox for sending integer geographic
coordinates through FlyTrader's local FlyWire Brain Service, observing a fixed
readout population, and decoding the response with a persisted external token
classifier. It is an experiment platform, not a model of biological navigation.

The repository lives at `D:\AI\Topo`. The separately owned Brain Service lives
at `D:\AI\FlyTrader` and is reached only over its public HTTP API at
`http://127.0.0.1:8765` (overridable with `TOPO_BRAIN_SERVICE_URL`). **Do not
modify FlyTrader or depend on its internal source/data from Topo.**

## Core pipeline

```text
GeoCoordinate (integer degrees)
  -> semantic tokens (N/S, digits, separator, E/W, digits)
  -> configured sparse input roots
  -> Brain Service reference / unsigned session
  -> 128-root bounded readout
  -> frozen external token classifier
  -> predicted semantic sequence
  -> GeoCoordinate grammar decoder
```

The WPF app has two modes:

- **Neural Decode Mode**: the user supplies a coordinate; Topo encodes and
  decodes it through the real frozen pipeline.
- **Autonomous Search Mode**: the user supplies a hidden target; an external
  deterministic policy proposes coordinates. Every proposal still goes through
  the real frozen pipeline, then Topo computes reward from target versus the
  *decoded* proposal. Map movement means evaluated decoded guesses, never
  biological locomotion.

## Solution layout

| Project | Responsibility |
|---|---|
| `src/Topo.Core` | Pure coordinate/token semantics, map rounding, and the deterministic reward formula. No dependencies. |
| `src/Topo.BrainClient` | Public Brain Service HTTP DTOs/client/options/errors. No WPF or policy logic. |
| `src/Topo.Training` | Encoder, stimulation/readout, frozen decode pipeline, classifier/preprocessing, owned-session service, Phase 9 policies, experiment persistence. No WPF dependency. |
| `src/Topo.Module` | WPF/WebView2/Leaflet UI, ViewModels, app-specific services, commands, map bridge. |
| `src/Topo.Experiments` | Non-WPF console runner for long Phase 9 real-service experiments; reuses production training/session/search components. |
| `tests/*` | Unit, ViewModel, HTTP client, guarded real-service smoke/integration tests. |

Key files:

- `src/Topo.Training/CoordinateDecoding.cs`: frozen encode/decode pipeline.
- `src/Topo.Training/OwnedBrainSessionDecodeService.cs`: owns one reference /
  unsigned session and implements `ICoordinateCandidateEvaluator`.
- `src/Topo.Training/AutonomousSearch.cs`: target-free policy contracts and the
  target-owning runner.
- `src/Topo.Training/AutonomousSearchExperiments.cs`: deterministic corpus,
  checkpoints/resume, metrics, and artifact serialization.
- `src/Topo.Module/ViewModels/MainViewModel.cs`: dashboard state and mode controls.
- `src/Topo.Module/Assets/Map/index.html`: Leaflet bridge and marker/trail UI.
- `config/brain-channels.json`, `config/readout-population.json`, and
  `data/models/token-readout-v1.json`: frozen runtime artifacts.

## Non-negotiable frozen contracts

Unless a new approved phase explicitly changes them, preserve all of these:

- Brain Service mode is `reference` with `unsigned` transmitters.
- Every independent coordinate/candidate reset begins with `ResetSessionAsync`.
- There is one application-owned session for the app/experiment lifetime;
  dispose it on shutdown, cancellation, and failure.
- Channel stimulus amplitude is **100**, stimulus duration is **1** step, and
  settle/observation offset is **22** steps.
- The readout population is the ordered 128 roots in
  `config/readout-population.json`; do not reorder or substitute roots.
- Raw features are membrane delta, spike-count delta, and refractory steps.
- `token-readout-v1.json`, its preprocessor, channel mapping, and readout
  configuration are mutually validated at startup. Do not silently retrain.
- The grammar decoder consumes classifier-predicted tokens only; the known
  input sequence supplies its boundary (there is no neural END token).
- Reward is exactly `exp(-angularErrorDegrees / 20)`, in `[0,1]`, and is an
  application-level score, not biological dopamine.

## Phase 9 search boundary

The target-leakage boundary is intentional and documented by
`docs/project/adr/0001-external-search-feedback-boundary.md`.

- `AutonomousSearchRunner` owns the target and performs target/decode reward
  comparison.
- `ICoordinateSearchPolicy` must never receive target coordinates, hemispheres,
  target tokens, or an equivalent target-bearing object.
- Policies receive only target-free `SearchFeedback` from completed neural
  evaluations.
- The approved hill climber is deterministic with seed `20260924`, radii
  `30, 15, 8, 4, 2, 1`, global exploration after 12 non-improving evaluations,
  maximum 250 evaluations, and 100 UI history items.
- The random baseline must retain equal target ordering, derived seed, and
  candidate budget.

Do not add reinforcement learning, learned policy parameters, Q-learning,
DQN/PPO/policy gradients, neural plasticity, mutable FlyWire weights, or
dopamine-modulated learning without a new approved task/spec/design.

## Current empirical result

The completed 50-target real-service experiment is at:

`data/experiments/autonomous-search-v1/phase9-50-target-seed-20260924/`

It includes `config.json`, `targets.json`, atomic per-target checkpoint traces
for both policies, and `summary.json`.

| Metric | Hill climbing | Random baseline |
|---|---:|---:|
| Exact successes | 11 / 50 (22%) | 0 / 50 (0%) |
| Mean final angular error | 18.483° | 101.402° |
| Median final angular error | 11.552° | 96.208° |
| Mean best angular error | 8.172° | 5.394° |
| Mean candidate evaluation | 218.57 ms | 221.31 ms |
| Total policy runtime | 40.83 min | 46.11 min |

The experiment supports better exact-success and final-position outcomes for
this deterministic hill climber under the recorded equal budget. It does **not**
show that the connectome learned a search policy; the policy is external and
the connectome is fixed.

## Build, test, and run

```powershell
cd D:\AI\Topo
dotnet restore Topo.sln
dotnet build Topo.sln --no-restore
dotnet test Topo.sln --no-build --no-restore
```

Start Brain Service separately:

```powershell
cd D:\AI\FlyTrader
.\.venv\Scripts\python.exe -m brain_service.server
```

Confirm readiness:

```powershell
Invoke-WebRequest http://127.0.0.1:8765/health/ready
```

Run the desktop app from Visual Studio or:

```powershell
dotnet run --project D:\AI\Topo\src\Topo.Module\Topo.Module.csproj
```

Run/resume the fixed Phase 9 benchmark (it can take roughly 1.5 hours against
the real local service):

```powershell
dotnet run --project D:\AI\Topo\src\Topo.Experiments\Topo.Experiments.csproj --no-build -- --run-name phase9-50-target-seed-20260924
```

The experiment validates persisted configuration and ordered target/seed
records before resuming. Completed target artifacts are not re-evaluated.

## How to extend safely

1. Read `docs/project/tasks/0009_TOPO-PHASE9/task.md`, `design.md`, `plan.md`,
   the ADR above, `docs/architecture.md`, and `docs/ui-sandbox.md` first.
2. Create the next task specification/design/plan and obtain approval before
   changing frozen scientific contracts or adding learning behavior.
3. Keep domain/training behavior in `Topo.Core`/`Topo.Training`; keep HTTP in
   `Topo.BrainClient`; keep only presentation/command logic in `Topo.Module`.
4. Add deterministic unit tests beside the changed layer. Keep real Brain
   Service tests guarded; do not put multi-hour benchmarks in VSTest.
5. Preserve offline WPF startup, async UI behavior, cancellation propagation,
   bounded histories/trails, and graceful map fallback.
6. Update task artifacts and this handoff whenever architecture, artifacts, or
   phase status materially changes.

## Known documentation/process state

`docs/project/status/CURRENT_TASK.md` still says the Phase 9 benchmark is
pending because it predates the successful console run. The persisted
`summary.json` is authoritative evidence that the benchmark completed. An
independent `sdd-review` is still recorded as pending in the Phase 9 review
material.
