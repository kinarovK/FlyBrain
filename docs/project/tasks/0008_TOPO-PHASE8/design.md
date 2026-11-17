# Phase 8 design — Interactive neural-coordinate sandbox

## Architecture

```text
MainWindow / MapView (WPF + WebView2/Leaflet)
              |
        MainViewModel
              |
   InteractiveDecodeService (Topo.App)
              |
  AppBrainSessionOwner + progress adapter
              |
Topo.Training frozen decode pipeline -> Topo.BrainClient HTTP client
```

`Topo.Core` receives deterministic reward and map-coordinate rounding helpers.
`Topo.Training` receives a progress-aware frozen decode operation; it remains
free of WPF types. `Topo.App` owns startup paths, the persisted model/config,
the single interactive session, UI state, commands, and logging.

## Key contracts

- `RewardCalculator.Calculate(target, prediction, scaleDegrees = 20)` returns
  angular distance and `exp(-distance / scale)` constrained to `[0, 1]`.
- `MapCoordinateConverter.ToTopoCoordinate(latitude, longitude)` rounds to the
  nearest valid integer degree, clamps only external decimal click values to
  valid Earth bounds, then constructs `GeoCoordinate`.
- `DecodeProgress` provides ordered token-started/observed/predicted events,
  simulation step, membrane response summary, and safe aggregate step data.
- `InteractiveDecodeService` loads/validates the frozen model once, checks
  readiness, creates at most one reference/unsigned session, serializes
  decode operations, resets before each coordinate, and deletes on dispose.

## UI state and map

The dashboard has a dark scientific-console layout: header/status, Leaflet
map, target/result/reward side panel, token-progress row, rolling membrane
bar chart, telemetry, input/action bar, and bounded log stream. `WebView2`
hosts local Leaflet HTML that uses OpenStreetMap tiles and visible attribution.
The page accepts .NET marker updates and sends map clicks to .NET; if the
control or tiles fail, a clear map-unavailable state leaves manual decoding
fully usable. Target is a crosshair; decoded prediction uses a custom SVG fly
marker animated only as a visual decoded-marker transition.

## Error and lifecycle behavior

Startup is offline-safe and never launches Python. Reconnect reads readiness,
then creates one session only after model/config compatibility succeeds.
Decode converts known service failures and cancellation to concise UI state;
technical detail enters the log. The current operation gets a cancellation
token, controls reflect the running state, and a completed/cancelled/error run
returns the ViewModel to idle. App shutdown asynchronously deletes the owned
session when possible.

## Requirement mapping

| Acceptance criterion | Design response |
| --- | --- |
| 1 | Existing parser plus map rounding helper; selection does not decode. |
| 2 | Progress-aware frozen pipeline, result/telemetry/reward display models. |
| 3 | Async commands, cancellation token source, UI-thread property updates. |
| 4 | Single session owner and disposal cleanup. |
| 5 | Explicit service/model/input/map status and compact log. |
| 6 | Distinct crosshair/fly SVG markers; label says decoded neural prediction. |
| 7 | Core reward calculator and unit tests. |
| 8 | Core/App unit tests plus opt-in real WPF smoke instructions. |

## Risks and decisions

- WebView2 requires a NuGet dependency and Windows runtime. If unavailable,
  the dashboard shows map unavailable without disabling decoding.
- OSM tiles require network access. Attribution is baked into the Leaflet page.
- The Phase 7 decode pipeline currently reports only final results; it will be
  extended with a callback, preserving the same frozen model/timing behavior.
- The UI receives no automatic END signal. It shows progress for the known
  source sequence boundary and documents that limitation.

No autonomous action, learning, policy, plasticity, or FlyTrader modification
is part of this design.
