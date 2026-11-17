# TOPO-PHASE9 — Autonomous reward-guided coordinate search

## Status

Approved. Design approved; implementation plan prepared. Process tier: Full.
Execution mode: deterministic headless experiments plus interactive desktop
validation against the independently managed local Brain Service.

## Expected result

Topo retains **Neural Decode Mode** and adds an explicit **Autonomous Search
Mode**. In search mode, the user chooses a hidden target coordinate; an
external deterministic policy generates candidate coordinates, each candidate
is evaluated by the unchanged neural encode/decode pipeline, and the Phase 8
reward calculated from target versus decoded candidate feeds back to the
policy. The desktop map presents the target, current decoded candidate, best
decoded candidate, and a bounded evaluated-decoded trail.

The result validates a correct `guess -> frozen neural evaluation -> reward ->
next guess` loop. It does not claim the fixed FlyWire connectome learned
geography or implements reinforcement learning.

## In scope

1. A non-WPF, deterministic, derivative-free coordinate-search policy with a
   seeded stochastic hill-climbing strategy, bounded integer mutation radii,
   and controlled deterministic global exploration/restart behavior.
2. Search state and bounded history containing iteration, proposed coordinate,
   decoded coordinate, angular error, reward, accept/reject outcome, and best
   result.
3. A search runner which evaluates **every** candidate through the existing
   frozen neural encode/decode pipeline, resets the existing app-owned Brain
   Service session before each candidate, calculates the existing Phase 8
   reward from target versus decoded candidate, and stops on exact success,
   configured maximum iterations, error, or cancellation.
4. An explicit target-leakage boundary: the policy API receives candidate,
   decoded result, reward/error, prior policy state, seed, and configuration;
   it never receives target coordinates, hemispheres, or target tokens.
5. WPF mode switching, autonomous-search controls, progress/telemetry, live
   reward and best-result presentation, cancellation, reset, and bounded log
   history while retaining the existing Neural Decode Mode.
6. Leaflet bridge support for a decoded-current marker, best marker, and
   bounded search-trail polyline. Marker movement represents evaluated decoded
   guesses, not biological locomotion.
7. Deterministic headless experiment output under
   `data/experiments/autonomous-search-v1/`, including configuration, seed,
   targets, bounded iteration traces, timings, success/failure, and aggregate
   metrics.
8. A deterministic evaluation corpus of at least 50 targets including
   `N35 W54`, `N47 E22`, `S23 E117`, `N0 E0`, `N90 E180`, and `S33 E111`, plus
   a same-budget pure random-search baseline.
9. Unit, runner, ViewModel, headless experiment, and real Brain Service
   integration coverage.

## Out of scope

- reinforcement learning or a learned search policy;
- Q-learning, DQN, PPO, policy gradients, genetic/evolutionary policies, or
  any other optimization which learns policy parameters from runs;
- neural plasticity, mutable FlyWire/connectome weights, or
  dopamine-modulated learning;
- Brain Service/FlyTrader API, source, schema, or configuration changes;
- autonomous path planning, locomotion, or claims of autonomous animal
  navigation.

## Constraints

- The Phase 8 frozen classifier, preprocessor, 128-root readout population,
  channel mapping, and neural timing remain unchanged.
- The policy's initial candidate must be seed-derived and valid, and must not
  use target information to choose a near answer.
- Reward remains exactly `exp(-angularErrorDegrees / 20)` and is calculated
  only from target versus the decoded candidate, never the raw proposal.
- A candidate's neural state must not influence the next candidate: each
  evaluation begins with an independent session reset while preserving the
  within-coordinate token sequence.
- The application owns no more than one reference/unsigned Brain Service
  session for its lifetime and must not leak sessions on search cancellation or
  error.
- All search settings that influence reproducibility—at minimum seed, maximum
  iterations, mutation-radii schedule, exploration/restart rule, and history
  limit—must be explicit in configuration and persisted with experiments.
- The UI must remain responsive and cancellation must retain completed history
  without modifying the persisted token model.
- Search and random-baseline runs must use equal candidate-evaluation budgets.

## Acceptance criteria

1. The user can explicitly switch between retained Neural Decode Mode and new
   Autonomous Search Mode; search mode accepts a target via manual input or
   map click and never asks for candidate guesses.
2. Given identical seed, search settings, frozen model/configuration, and
   decoder behavior, the policy produces the same candidate sequence. Its
   candidates are always valid `GeoCoordinate` values and target data is not
   present in its public input contract.
3. The runner evaluates each proposed candidate through the real frozen
   neural pipeline, calculates reward/error from target versus decoded result,
   records the result, and only then requests the next policy proposal.
4. The policy accepts/rejects candidates according to recorded reward/error,
   preserves the best result through rejections, follows its configured radius
   schedule, and deterministically performs configured exploration/restarts.
5. Search stops immediately on exact decoded-target success, otherwise at its
   configured maximum iteration count, and cancellation cleanly stops active
   work without corrupting state, models, or sessions.
6. The map shows distinct target, current decoded, and best decoded markers;
   it updates a bounded evaluated-decoded trail and live reward/error values
   after each actual neural evaluation.
7. The UI provides Start Search, Stop, and Reset controls plus iteration,
   proposed/decoded coordinate, current/best reward, current/best angular
   error, acceptance, radius, and bounded history/log presentation. Existing
   decode-mode behavior continues to work.
8. Headless experiments persist reproducible configuration, seed, traces,
   timings, per-target outcomes, and aggregate metrics for at least 50
   targets. Report success rate, mean/median iterations, and mean/median/max
   final angular error.
9. A random-search baseline is evaluated with the same target corpus, seed
   policy, and maximum candidate budget, and its metrics are reported next to
   reward-guided search without claiming superiority from anecdotes.
10. Tests cover deterministic policy behavior, target-leakage boundary,
    accepted/rejected state, radius/exploration behavior, runner neural
    evaluation/success/limit/cancellation, ViewModel mode/search controls, and
    map coordinate/trail bridge behavior. Real integration traces cover at
    least `N35 W54`, `N47 E22`, and `S23 E117`.
11. Documentation distinguishes the external Topo policy/reward loop from the
    unchanged fixed FlyWire connectome and explicitly excludes reinforcement
    learning and neural plasticity.
