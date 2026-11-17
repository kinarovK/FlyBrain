# Phase 9 design — Autonomous reward-guided coordinate search

## Design scope

This design adds an external, deterministic search loop around the accepted
Phase 8 neural-coordinate codec. It preserves the frozen model, preprocessor,
readout population, channel mapping, 100/1/22 timing, `RewardCalculator`, and
the app-owned reference/unsigned Brain Service session. It introduces no
reinforcement learning, learned parameters, mutable connectome state, or
FlyTrader changes.

The governing target-leakage decision is recorded in
[ADR 0001](../../adr/0001-external-search-feedback-boundary.md).

## Runtime architecture

```text
WPF MainWindow / Leaflet map
             |
      MainViewModel
             |
  AutonomousSearchController (Topo.App)
             |
  AutonomousSearchRunner (Topo.Training)
             |                    |
  ICoordinateSearchPolicy    ICoordinateCandidateEvaluator
       |                            |
  HillClimbingPolicy          InteractiveDecodeService
       |                            |
  SearchFeedback only       existing frozen CoordinateNeuralDecodePipeline
                                    |
                         one reference/unsigned Brain Service session
```

`Topo.Training` owns pure settings, policy, state, runner, corpus, metrics,
and experiment serialization models. It has no WPF dependency. `Topo.App`
adapts the existing `InteractiveDecodeService` to the runner evaluator
contract, owns commands/ViewModel state, and translates map/UI updates.
`Topo.BrainClient` and FlyTrader are unchanged.

The existing `InteractiveDecodeService.DecodeAsync` continues to serialize
access to its one session and invokes `CoordinateNeuralDecodePipeline`, which
resets that session before every candidate. The Phase 9 evaluator adapter must
call that method exactly once per proposal; it must not implement a shortcut
or direct geographic reward path.

## Contracts and data model

The following are proposed public `Topo.Training` contracts. They intentionally
exclude target data from policy-facing types.

```csharp
public sealed record SearchSettings(
    int Seed,
    int MaximumIterations = 250,
    IReadOnlyList<int>? MutationRadiiDegrees = null,
    int NonImprovingAttemptsBeforeExplore = 12,
    int HistoryLimit = 100);

public sealed record SearchFeedback(
    int Iteration,
    GeoCoordinate ProposedCoordinate,
    GeoCoordinate DecodedCoordinate,
    double AngularErrorDegrees,
    double Reward,
    bool Accepted,
    GeoCoordinate BestDecodedCoordinate,
    double BestReward,
    double BestAngularErrorDegrees,
    int RadiusDegrees,
    int ConsecutiveNonImprovements);

public interface ICoordinateSearchPolicy
{
    GeoCoordinate ProposeInitial();
    GeoCoordinate ProposeNext(SearchFeedback previous);
}

public interface ICoordinateCandidateEvaluator
{
    Task<CandidateEvaluation> EvaluateAsync(
        GeoCoordinate proposal,
        IProgress<CoordinateDecodeProgress>? neuralProgress,
        CancellationToken cancellationToken);
}

public sealed record CandidateEvaluation(
    GeoCoordinate ProposedCoordinate,
    CoordinateNeuralDecode NeuralDecode,
    TimeSpan Elapsed);

public sealed record SearchIteration(
    int Iteration,
    GeoCoordinate ProposedCoordinate,
    GeoCoordinate? DecodedCoordinate,
    double? AngularErrorDegrees,
    double? Reward,
    bool Accepted,
    GeoCoordinate? BestDecodedCoordinate,
    double? BestReward,
    double? BestAngularErrorDegrees,
    int RadiusDegrees,
    bool IsExploration);
```

`SearchSettings` normalizes an omitted radii list to `[30, 15, 8, 4, 2, 1]`
and rejects non-positive or empty schedules, non-positive iteration/history
limits, and invalid exploration thresholds. The default experiment profile is
explicit: seed `20260924`, maximum 250 evaluations, history limit 100, and
12 non-improving attempts before global exploration. The UI and persisted
experiment configuration show these values; no reproducibility-affecting
setting is hidden.

`SearchState` is runner-owned and contains the bounded UI history plus the
untruncated bounded-by-maximum-iterations experiment trace, current accepted
anchor, best feedback, iteration count, termination reason, and elapsed
timings. It is not passed wholesale to the policy, avoiding accidental target
inclusion in a future field.

## Target-leakage boundary and evaluation sequence

`AutonomousSearchRunner.RunAsync(target, policy, evaluator, settings, ...)`
holds the target privately. It performs the only comparison involving the
target:

```text
proposal from policy
  -> evaluator performs frozen neural encode/decode and session reset
  -> runner validates decoded grammar result
  -> runner: RewardCalculator.Calculate(target, decoded)
  -> runner records SearchIteration
  -> runner sends target-free SearchFeedback to policy
```

The policy never receives `target`, `target.ToTokens()`, target hemisphere, or
a value computed directly from target other than scalar angular error/reward.
The first proposal comes from a seeded global uniform valid-coordinate draw;
it cannot be initialized near the target. Candidate reward is never calculated
from the raw proposal. If classifier output cannot be decoded, the iteration is
recorded as a decode failure and the run terminates as an evaluator failure;
the runner does not synthesize reward for an undecoded coordinate.

The same runner is used with a `RandomCoordinateSearchPolicy`, which always
draws the next valid global coordinate and ignores feedback. Both policies
receive the same per-target derived seed and exactly the same maximum candidate
budget. Per-target seeds derive from the configured base seed and corpus index
using a stable arithmetic derivation, not `GetHashCode`.

## Policy v1

`StochasticHillClimbingSearchPolicy` owns a private `Random` initialized from
`SearchSettings.Seed`, an accepted anchor, radius index, and non-improvement
count. Its algorithm is deterministic for a fixed seed and feedback sequence:

1. Draw a uniformly valid initial latitude `[-90, 90]` and longitude
   `[-180, 180]` without receiving target data.
2. Treat the first completed evaluation as the initial accepted anchor and
   best result.
3. Propose local candidates by independently drawing non-zero bounded integer
   latitude/longitude offsets using the current schedule radius, clamping to
   `GeoCoordinate` bounds, and retrying a bounded deterministic number of
   draws if the result equals the anchor.
4. Accept a local candidate only if its decoded reward is strictly greater
   than the current anchor reward. On acceptance, move the anchor and advance
   one radius level toward the minimum radius. A rejection never overwrites
   anchor or best state and increments the non-improvement count.
5. After 12 consecutive non-improving evaluations, propose one seeded global
   exploration candidate, reset the non-improvement count and radius to 30°,
   and continue. It becomes the new anchor only if its reward improves it.

The best result uses reward as primary order and angular error as deterministic
secondary order. With the frozen Phase 8 codec, equality is normally direct;
the tiebreak protects reproducibility for future imperfect predictions.

Search terminates immediately when decoded coordinate equals target or angular
error is zero, otherwise at 250 evaluations, on cancellation, or on decoder/
service failure. No candidate evaluation continues after a terminal result.

## Session, error, and cancellation behavior

The WPF app continues to construct exactly one `InteractiveDecodeService`.
The search evaluator adapter delegates every proposal to its `DecodeAsync`.
That existing path holds its gate and calls the frozen pipeline, whose first
operation is `ResetSessionAsync`; no candidate state persists into another
candidate. The one session is deleted by the existing app shutdown disposal.

Search uses one cancellation token source for the full run. Stop cancels the
current neural HTTP work through the evaluator and runner, preserves completed
search history, returns the UI to idle, and does not create another session.
Reset is enabled only while idle; it clears in-memory search display/trail/log
state but leaves the frozen artifact and connected session intact. Service,
model-compatibility, unsafe neural status, invalid decoded sequence, and map
errors become concise UI errors with detailed diagnostics in the bounded log.

## WPF and map design

`MainViewModel` gains an `ApplicationMode` enum with `NeuralDecode` and
`AutonomousSearch`. Switching modes is explicit and disabled while either
operation is running. The existing Decode command and its input behavior stay
unchanged in Neural Decode Mode. In Autonomous Search Mode, the same parser
and map-click conversion set the target only; candidates are not user inputs.

New ViewModel state includes current proposed/decoded coordinate, best decoded
coordinate, iteration, current/best reward, current/best angular error,
accepted/rejected state, radius, seed/settings summary, bounded search
history, and `StartSearch`, `StopSearch`, and `ResetSearch` commands. Search
progress is reported after each full candidate evaluation, so marker movement,
reward, and trail correspond to actual neural results. Existing token progress
may continue to show the current candidate's real token execution; it is not a
synthetic search animation.

The Leaflet bridge expands with target-safe methods such as `setCurrent`,
`setBest`, `setTrail`, and `clearSearch`. The MainWindow serializes coordinate
arrays as JSON numeric pairs before calling the local WebView page. The map
uses a target crosshair, current decoded fly, a visually distinct best marker,
and a single polyline of the last 100 decoded evaluated positions. It updates
the existing marker rather than creating one marker per iteration. Existing
WebView2/Leaflet/tile fallback behavior remains; manual input and search work
without a map. The wording describes visual decoded guesses, not neural
locomotion.

## Experiments and reporting

`AutonomousSearchEvaluationCorpus.Create(count: 50, seed)` starts with the six
required coordinates and adds deterministic unique valid coordinates. The
headless experiment runner evaluates reward-guided and random policies against
the exact same ordered corpus and per-target derived seeds. It persists:

- `config.json`: frozen-artifact identity, settings, base seed, policy kind,
  corpus identity, and timestamp;
- `targets.json`: ordered target list and derived seeds;
- one bounded trace per target/policy, with proposed/decoded result, reward,
  error, acceptance, radius, exploration, timing, and termination;
- `summary.json`: success rate, mean/median iterations to exact success,
  mean/median/maximum final angular error, mean candidate evaluation time,
  mean complete search time, and failure best-error metrics.

Results go under `data/experiments/autonomous-search-v1/` with a unique
run-name based on settings/UTC timestamp. No raw neural vectors or event
payloads are persisted. The final report compares metrics without inferring
that the connectome learned geography.

## Test strategy

`Topo.Training.Tests` will cover settings validation, valid candidates,
same-seed reproducibility, no target-bearing policy contract, accepted versus
rejected state, radius progression, exploration/restart, success, maximum
iterations, decoder failure, and cancellation through fake evaluators.

`Topo.App.Tests` will use an evaluator/service fake to verify explicit mode
switching, target-only search input, start/progress/stop/reset, retained decode
mode behavior, errors, and bounded trail/history updates. Pure map bridge
formatting is tested without WebView2; the real map is covered by WPF smoke.

The real guarded integration suite uses the existing Brain Service with one
owned session and records reproducible traces for `N35 W54`, `N47 E22`, and
`S23 E117`. It verifies a reset-backed frozen decode for every observed
candidate, session cleanup, and no model file writes. Headless runs produce
the 50-target policy/random comparison before UI conclusions are drawn.

## Risks and alternatives

The fixed decoder may be near-perfect, so the decoded coordinate can equal the
proposal; Phase 9 then measures the external policy's ability to exploit a
scalar geographic reward, not neural learning. A global/local stochastic hill
climber may have low exact-success rate over 250 evaluations on a 181×361
space; comparison with the equal-budget random baseline is required before
claiming guidance. Full experiments may take materially longer than UI runs,
so they are headless, cancellable, and persist bounded data.

Alternative learned policies, coordinate-gradient search using target values,
or connectome plasticity would change the scientific question and are rejected
for this phase. An all-random policy is retained only as a baseline, not the
primary autonomous behavior.

## Requirement-to-design mapping

| Acceptance criterion | Design response |
| --- | --- |
| 1 | `ApplicationMode`, explicit mode switch, and target-only search input retain decode mode. |
| 2 | Seeded policy/settings plus target-free `ICoordinateSearchPolicy` contracts and contract tests. |
| 3 | Runner -> evaluator -> existing `InteractiveDecodeService.DecodeAsync`; runner alone calls `RewardCalculator` with target/decoded. |
| 4 | Strict-reward hill climbing, radius advancement, rejection preservation, and 12-attempt seeded exploration. |
| 5 | Exact/zero-error, maximum-iteration, cancellation, and evaluator-failure terminal rules. |
| 6 | `setCurrent`, `setBest`, and bounded `setTrail` Leaflet bridge over decoded evaluations. |
| 7 | Search ViewModel state and Start/Stop/Reset commands; decode commands remain mode-specific. |
| 8 | 50-target deterministic corpus, bounded artifact files, timings, and aggregate metric definitions. |
| 9 | `RandomCoordinateSearchPolicy` through the same runner, corpus, per-target seed derivation, and budget. |
| 10 | Training/App test matrix plus three-target guarded real-service traces and map smoke. |
| 11 | ADR, UI wording, experiment report wording, and frozen-architecture constraints. |

