# FlyBrain engineering handoff

Start here. This merges the former FlyTrader brain handoff
([PROJECT_HANDOFF.md](PROJECT_HANDOFF.md)) and the Topo handoff
([topo/agent-handoff.md](topo/agent-handoff.md)); both remain valid for their
layer. Task record: [0010_FLYBRAIN-MERGE](project/tasks/0010_FLYBRAIN-MERGE/plan.md),
decision: [ADR 0002](project/adr/0002-flybrain-monorepo.md).

## Runtime picture

```text
FlyBrain.App (WPF shell)
 ├─ BrainHost ── starts ──> python -m brain_service.server  (127.0.0.1:8765, child process in a kill-on-close job)
 │                 └─ polls /health/ready, attaches if a service is already running
 ├─ BrainSetup ── creates .venv, pip install, FlyWire download + sparse graph build
 └─ active IBrainModule (one at a time)
      ├─ Topo.Module   ── own reference/unsigned session ─┐
      └─ Trader.Module ── own reference/unsigned session ─┴─ HTTP /api/v1 ──> brain_service
```

- The Python service is the only owner of graph loading and LIF equations.
  .NET code talks to it only through `FlyBrain.BrainClient` (public HTTP API).
- At startup the service prepares the `reference` domain (912 neurons). The
  `whole` domain (139,255 neurons, ~1.4 GB) is prepared at startup only with
  `eager_whole_domain` / `FLYBRAIN_EAGER_WHOLE=1` (the shell checkbox), and
  otherwise on the first whole-session request.
- The shell tells the active module when the brain becomes ready or goes away
  (`OnBrainAvailabilityChangedAsync`) and deactivates the old module before
  showing another one. Modules must create their sessions on "ready" and delete
  them on "not ready" or deactivation. Service limits: 4 sessions, 1 whole.

## Adding a module

1. Create `src/<Name>.Module` (net9.0-windows, WPF) referencing
   `FlyBrain.Modules.Abstractions` and `FlyBrain.BrainClient`; keep domain
   logic in a plain net9.0 project with its own tests.
2. Implement `IBrainModule`: `View` (a UserControl), `ActivateAsync`,
   `DeactivateAsync` (cancel work, release sessions),
   `OnBrainAvailabilityChangedAsync`, `DisposeAsync`.
3. Put frozen artifacts/configuration under `modules/<name>/` and resolve them
   with `RepositoryLocator.ModuleDirectory(root, "<name>")`.
4. Reference the project from `FlyBrain.App` and add one line to
   `ModuleCatalog.Create`.
5. Choose input/readout roots deterministically and document it; never
   reuse another module's session.

## Trader design (task 0010, F4)

- **Tokens** (`Trader.Core.MarketFeatureExtractor`), past data only, per hour:
  last 1 h log return in units of 24 h hourly volatility (5 bins), 24 h trend
  (3 bins), volatility above/below its 30-day median (2 bins). Warm-up is 745 h.
- **Brain** (`Trader.Brain.TraderBrainSession`): one reference/unsigned session.
  Input roots: ascending root IDs from rank 15 (Topo uses ranks 0–14), each
  accepted only if one 100-amplitude pulse makes it spike. Readout: all other
  roots, even ranks = BUY pool, odd ranks = SELL pool. Every evaluation resets
  the session, stimulates the three tokens (1 step + 22 settle steps each, the
  Topo-calibrated values) and compares mean pool responses (membrane delta +
  spike delta) with a 10 % dead zone. Results are cached per token sequence —
  the 2024 replay has 30 distinct market states.
- **Trading** (`Trader.Core`): 10,000 USDT, all-in/all-out, 0.1 % fee,
  decision at the close of hour *t* executed at the open of hour *t + 1*.
- Configuration: `modules/trader/config/brain-trader.json` (versioned).
- The BUY/SELL pools are an arbitrary mapping. Any result is a property of
  this fixed mapping on this fixed graph, not evidence of market skill.

## Build, test, run

```powershell
dotnet build FlyBrain.sln
dotnet test FlyBrain.sln
.\.venv\Scripts\python.exe -m pytest tests\python -q -p no:cacheprovider
dotnet run --project src\FlyBrain.App          # GUI
dotnet run --project src\Topo.Experiments      # long Topo Phase 9 benchmark (brain must be running)
```

Guarded real-brain tests: see `docs/topo/agent-handoff.md` (`TOPO_*` variables)
and the FlyTrader real-data scripts in `docs/PROJECT_HANDOFF.md`.

## Environment variables

| Variable | Effect |
|---|---|
| `FLYBRAIN_ROOT` | Repository root (default: found by walking up to `brain_service/server.py`). `TOPO_REPOSITORY_ROOT` still works. |
| `FLYBRAIN_PYTHON` | Python interpreter for the brain (default `.venv\Scripts\python.exe`). |
| `FLYBRAIN_BRAIN_SERVICE_URL` | Brain service address (default `http://127.0.0.1:8765`). `TOPO_BRAIN_SERVICE_URL` still works. |
| `FLYBRAIN_EAGER_WHOLE` | `1` prepares the whole domain at service start. |
| `FLYBRAIN_AUTOSTART` | `1` starts the brain when the app opens (same as `--start-brain`). |

## Superseded rules

The earlier handoffs said "no C#/.NET client, UI or trading in FlyTrader" and
"Topo must not modify FlyTrader". Both were boundaries between two separate
repositories and are superseded by ADR 0002. Every scientific and numerical
invariant in those documents still applies.
