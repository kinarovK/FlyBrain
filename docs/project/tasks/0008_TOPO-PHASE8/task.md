# TOPO-PHASE8 — Interactive neural-coordinate sandbox

## Status

Accepted. Implementation and verification complete.
Process tier: Full. Execution mode: interactive desktop implementation and real
local-service smoke verification.

## Expected result

Topo's existing WPF bootstrap becomes an MVVM dashboard in **Neural Decode
Mode**. A user may enter or select an integer-degree target, run the frozen
Phase 6 classifier through the real Brain Service pipeline, inspect ordered
token progress and bounded membrane telemetry, and compare target versus the
decoded neural prediction on a map. An application-level reward is calculated
from angular error.

## In scope

1. Frozen model/config loading and compatibility checks at app startup.
2. One app-owned reference/unsigned Brain Service session, reset per decode
   and deleted at orderly shutdown.
3. Async, cancellable decode command with real per-token progress.
4. WPF MVVM dashboard, status/log stream, result and telemetry panels.
5. Map target/decoded marker presentation, click-to-rounded-coordinate
   selection, and a map-unavailable fallback.
6. Deterministic reward `exp(-angularErrorDegrees / 20)` in non-WPF code.
7. Unit tests, documentation, and real local-service UI smoke validation.

## Out of scope

- autonomous search, fly navigation, path planning, or coordinate guessing;
- reinforcement learning, reward-driven policy learning, dopamine-modulated
  plasticity, or mutable connectome weights;
- FlyTrader source, schema, or service modifications.

## Constraints

- The decoded marker represents neural prediction, never autonomous movement.
- Integer-degree `GeoCoordinate` parsing/normalization remains authoritative.
- The classifier, normalization metadata, retained features, and readout
  population remain frozen; no retraining is allowed.
- Accepted timing stays amplitude 100, stimulus 1, settle 22, observation 22.
- Brain Service may be offline; Topo must still launch normally.
- Leaflet/OpenStreetMap attribution must remain visible if tiles are used.

## Acceptance criteria

1. Manual coordinate input and map click selection produce valid normalized
   Topo coordinates without automatically decoding.
2. Decode uses the existing frozen pipeline and reports real token progress,
   result, confidence score, telemetry, angular error, and reward.
3. UI stays responsive, supports cancellation, and restores usable state.
4. Session lifecycle is bounded to one app-owned session and cleanup occurs on
   shutdown/failure.
5. Offline, service, invalid input, and model-compatibility failures are clear
   user-visible states.
6. Target and decoded markers are visually distinct and the decoded marker is
   explicitly non-autonomous.
7. Reward is deterministic, bounded, testable, and not described as measured
   biological dopamine.
8. Automated tests and real smoke checks cover the requested behaviors.
