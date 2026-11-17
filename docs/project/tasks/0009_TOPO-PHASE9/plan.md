# Phase 9 implementation plan

## Slice 1 — Search-domain contracts and deterministic policies

**Files**

- `src/Topo.Training/AutonomousSearch.cs` (new)
- `tests/Topo.Training.Tests/AutonomousSearchPolicyTests.cs` (new)

**Work**

- Add `SearchSettings`, target-free policy feedback/iteration/result models,
  termination states, and `ICoordinateSearchPolicy`.
- Implement the seeded `StochasticHillClimbingSearchPolicy` and same-contract
  `RandomCoordinateSearchPolicy`.
- Normalize the exact schedule `[30, 15, 8, 4, 2, 1]`, maximum 250
  iterations, 12 non-improving threshold, and 100-item history limit.
- Keep all coordinate production through `GeoCoordinate`; no target-bearing
  policy field, property, or parameter is permitted.

**Completion condition**

- Same settings/feedback produces identical candidate sequences; proposals are
  valid; accepted/rejected behavior, radius progression, and exploration are
  observable as pure policy state.

**Verification**

- Unit tests for settings validation, valid proposals, deterministic sequence,
  no-target contract inspection, acceptance/rejection preservation, radius
  progression, and deterministic global exploration.
- `dotnet test tests/Topo.Training.Tests/Topo.Training.Tests.csproj`.

## Slice 2 — Target-owning runner and fake-evaluator coverage

**Files**

- `src/Topo.Training/AutonomousSearch.cs`
- `tests/Topo.Training.Tests/AutonomousSearchRunnerTests.cs` (new)

**Work**

- Add `ICoordinateCandidateEvaluator` and `AutonomousSearchRunner`.
- Keep target comparison and Phase 8 `RewardCalculator.Calculate` solely in
  the runner after a decoded evaluation result is received.
- Report ordered full-candidate progress, retain a 100-item UI history and
  bounded-by-250 full experiment trace, and support success, limit,
  cancellation, invalid decode, and evaluator failure terminals.

**Completion condition**

- The runner calls its evaluator once per proposal, never scores raw
  proposals, supplies only target-free feedback to the policy, and makes no
  subsequent proposal after a terminal result.

**Verification**

- Fake-evaluator tests for neural-evaluation ordering, reward feedback,
  immediate exact success, maximum-iteration stop, decode failure, and clean
  cancellation.
- Re-run the Training test project.

## Slice 3 — Frozen app evaluator and one-session integration

**Files**

- `src/Topo.App/Services/InteractiveDecodeService.cs`
- `src/Topo.App/Services/AutonomousSearchController.cs` (new, if a small
  adapter/controller improves composition)
- `tests/Topo.App.Tests/InteractiveDecodeServiceIntegrationTests.cs`
- `tests/Topo.App.Tests/AutonomousSearchIntegrationTests.cs` (new)

**Work**

- Adapt `InteractiveDecodeService.DecodeAsync` to
  `ICoordinateCandidateEvaluator` without altering model loading, channel
  mapping, readout configuration, timing, or its session gate.
- Ensure each evaluator call makes exactly one existing frozen decode call;
  the pipeline reset therefore occurs before every candidate.
- Preserve `ConnectAsync`, reconnect, disposal, and one reference/unsigned
  session semantics.

**Completion condition**

- Search and decode share exactly one app-owned connected session, and no new
  session is created per candidate or leaked on cancellation/error.

**Verification**

- Fake-service session/count tests.
- Guarded real Brain Service test records short deterministic traces for
  `N35 W54`, `N47 E22`, and `S23 E117`, verifies one evaluation per candidate,
  reset-backed decode behavior, and disposal cleanup.

## Slice 4 — Deterministic corpus, baseline, metrics, and experiment output

**Files**

- `src/Topo.Training/AutonomousSearchExperiments.cs` (new)
- `tests/Topo.Training.Tests/AutonomousSearchExperimentTests.cs` (new)
- `data/experiments/autonomous-search-v1/` (generated verification artifacts)

**Work**

- Generate the deterministic 50-target corpus beginning with all six required
  coordinates and derive stable per-target seeds.
- Run hill climbing and random policy through the same runner, evaluator,
  corpus ordering, and 250-candidate budget.
- Serialize config, target/seed list, bounded traces, per-target timing,
  outcomes, and aggregate policy/baseline metrics without raw neural payloads.

**Completion condition**

- Experiment files are reproducible from recorded configuration and report
  success rate; mean/median iterations; mean/median/maximum final error; best
  failure error; average candidate evaluation; and average search time for
  both policies.

**Verification**

- Unit tests for corpus determinism/required members, equal budgets, stable
  metric aggregation, and serialized artifact schema using fakes.
- Run the real 50-target experiment only after its guarded integration setup
  is ready; record actual timing/metrics rather than forecasting them.

## Slice 5 — ViewModel modes, search lifecycle, and tests

**Files**

- `src/Topo.App/ViewModels/MainViewModel.cs`
- `src/Topo.App/ViewModels/AutonomousSearchViewModels.cs` (new if display
  models are separated)
- `tests/Topo.App.Tests/MainViewModelTests.cs`
- `tests/Topo.App.Tests/AutonomousSearchViewModelTests.cs` (new)

**Work**

- Add explicit `NeuralDecode`/`AutonomousSearch` mode state while retaining
  current decode commands and behavior.
- Add target-only Start Search, Stop Search, Reset Search, seed/settings
  display, real per-candidate progress, current/best telemetry, and bounded
  history/trail state.
- Reuse existing async command/cancellation/error patterns and disable mode
  switches or conflicting actions during active work.

**Completion condition**

- A cancelled/failed/completed search returns the dashboard to a usable state;
  Reset clears search display only; Decode Mode remains functionally unchanged.

**Verification**

- Fake-runner ViewModel tests for mode switch, target input/map selection,
  start/progress/stop/reset, exact completion, max-iteration completion,
  error, bounded history, and retained Decode Mode regression.

## Slice 6 — WPF dashboard and Leaflet map bridge

**Files**

- `src/Topo.App/MainWindow.xaml`
- `src/Topo.App/MainWindow.xaml.cs`
- `src/Topo.App/Assets/Map/index.html`
- `tests/Topo.App.Tests/MapBridgeTests.cs` (new if script/coordinate formatting
  is extracted to a testable helper)

**Work**

- Add explicit mode controls, search telemetry, Start/Stop/Reset controls,
  and bounded history display to the existing scientific dashboard.
- Extend the local map API with current decoded marker, distinct best marker,
  one bounded trail polyline, and search clear/update calls.
- Continue to use JSON numeric-coordinate serialization, retain OSM
  attribution, and retain WPF/Leaflet/tile graceful fallback.

**Completion condition**

- Map updates occur only after actual completed candidate evaluations; they
  show target/current/best/trail without creating unbounded map layers or
  describing movement as biological locomotion.

**Verification**

- Unit tests for bounded trail/script payload construction.
- WPF smoke: mode selection, target selection, start, live marker/reward/trail
  updates, stop, reset, and Decode Mode regression. If native UI automation is
  unavailable, document that limitation and retain strongest service/ViewModel
  integration evidence.

## Slice 7 — Documentation, complete verification, and handoff

**Files**

- `docs/ui-sandbox.md`
- `docs/architecture.md`
- `docs/project/tasks/0009_TOPO-PHASE9/plan.md`
- generated `data/experiments/autonomous-search-v1/` artifacts

**Work**

- Document external-policy scientific limits, no-target-leakage boundary,
  exact settings, session/reset lifecycle, evaluation/baseline interpretation,
  and non-autonomous-locomotion map wording.
- Execute restore/build/full test, guarded real-service traces, corpus/baseline
  experiment, and WPF smoke; record only actual results in the execution log
  and final report.

**Completion condition**

- All acceptance criteria have traceable test or smoke evidence, no FlyTrader
  file has changed, and the completion report provides metrics rather than
  predictions.

**Verification**

- `dotnet restore Topo.sln`
- `dotnet build Topo.sln --no-restore`
- `dotnet test Topo.sln --no-build --no-restore`
- Guarded real Brain Service integration and experiment commands, followed by
  an independent `sdd-review` in a fresh context after implementation.

## Execution log

- 2026-09-22: approved Phase 9 design and ADR received; implementation plan
  created. No production implementation has begun.
- 2026-09-22: implemented deterministic target-free policies, target-owning
  runner, frozen one-session evaluator adapter, corpus/baseline experiment
  runner, WPF search mode, and Leaflet current/best/trail bridge. Automated
  verification and guarded real-service traces passed. The opt-in full
  50-target real experiment was attempted twice; this host orphaned/killed its
  long-running test host before it flushed artifacts, so no synthetic metrics
  are recorded. It must be rerun in a stable long-running test environment.
- 2026-09-23: added `Topo.Experiments`, a non-WPF console host using the
  shared `OwnedBrainSessionDecodeService` and the existing
  `AutonomousSearchExperimentRunner`. Experiment artifacts now atomically
  checkpoint each completed policy/target and resume only after validating the
  persisted configuration, target ordering, and derived seed. Restore/build
  passed with zero warnings/errors; 131 tests passed. The real 50-target run
  is executing under `data/experiments/autonomous-search-v1/phase9-50-target-seed-20260924`;
  report metrics only after its `summary.json` is persisted.
