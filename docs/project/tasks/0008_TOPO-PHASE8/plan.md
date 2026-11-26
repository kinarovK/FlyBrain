# Phase 8 implementation plan

## Slice 1 — Domain helpers

- Files: `Topo.Core/RewardCalculator.cs`, `Topo.Core/MapCoordinateConverter.cs`,
  Core tests.
- Deliver: deterministic angular reward and decimal-map rounding.
- Verify: reward bounds/monotonicity and coordinate boundary tests.

## Slice 2 — Progress-aware frozen execution

- Files: `Topo.Training/CoordinateDecoding.cs`, Training tests.
- Deliver: ordered callback events from the existing 100/1/22 pipeline with
  no model mutation or extra steps.
- Verify: existing decode tests plus progress ordering/cancellation tests.

## Slice 3 — App services and MVVM

- Files: Topo.App services/view-models and app tests.
- Deliver: frozen artifact/config initialization, one owned session, readiness,
  decode/cancel/reconnect commands, telemetry/result/log presentation.
- Verify: fake-service ViewModel scenarios.

## Slice 4 — Dashboard and map

- Files: WPF XAML, local Leaflet page, map bridge, SVG marker asset.
- Deliver: responsive console dashboard, attribution, click selection,
  target/decoded marker updates, graceful fallback.
- Verify: build and manual real-service smoke.

## Slice 5 — Documentation and verification

- Files: `docs/ui-sandbox.md`, task execution log.
- Deliver: lifecycle, wording, model dependency, map fallback documentation.
- Verify: restore/build/test and three real decode examples.

## Execution log

- 2026-09-22: approved design received; implementation started.
- 2026-09-22: completed Core reward/map helpers, progress-aware frozen decode,
  app-owned session service, MVVM dashboard, WebView2/Leaflet bridge, unit
  tests, and real service smoke coverage for N35 W54, N47 E22, S23 E117.
- 2026-09-22: final restore/build/test verification completed with 116 passing
  tests, zero failures, warnings, or errors. The WPF process launches and
  responds; the host's computer-use provider exposes no native application
  surface, so visual click-through automation was not available.
