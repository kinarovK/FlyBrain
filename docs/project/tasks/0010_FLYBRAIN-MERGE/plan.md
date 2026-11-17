# 0010 FlyBrain merge — plan

Goal: merge the Topo map sandbox (formerly `D:\AI\Topo`) and the FlyTrader brain
foundation (`D:\AI\FlyTrader`) into one repository and one desktop application,
**FlyBrain**, where the user starts the digital fly brain with a single button
and switches between brain-driven modules (Topo, Trader, later more).

Decisions (approved by the user on 2026-11-17):

- The FlyTrader folder is the base; the repository is `kinarovK/FlyBrain` (public).
- FlyWire source data stays local and git-ignored (`data/flywire/`).
- Trader demo data: offline historical replay of Binance BTCUSDT 1h klines
  (2024), downloaded by the user into `data/market/binance/` (git-ignored).
- The brain starts with the fast `reference` domain only; the full 139,255-neuron
  `whole` domain is prepared on demand.
- Workflow: one branch per phase from `staging`, merged into `staging`; a single
  PR `staging -> main` at the end.

## Phases

| Phase | Scope | Done when |
|---|---|---|
| F0 | Git baseline: first commit on `main`, `.gitattributes`, extended `.gitignore`, `staging` branch, this plan. | `main` and `staging` exist. |
| F1 | Monorepo: Topo projects imported under `src/` and `tests/dotnet/`; `Topo.BrainClient` becomes the shared `FlyBrain.BrainClient`; `FlyBrain.Common.RepositoryLocator` replaces the fragile `..\..\..\..\..` lookup; Topo artifacts live in `modules/topo/`; one `FlyBrain.sln`. Empty `FlyTrader.*` placeholders removed. | Build and all existing tests pass on Windows. |
| F2 | One-button brain: `FlyBrain.BrainHost` starts/stops `python -m brain_service.server` from the repository `.venv` as a child process bound to a Windows Job Object, polls `/health/ready`, captures logs, attaches to an already running service. Brain service gets an opt-in `FLYBRAIN_EAGER_WHOLE` switch (default off). | The app starts the brain itself; closing the app stops it. |
| F3 | Modular shell: `FlyBrain.Modules.Abstractions` (`IBrainModule`), `FlyBrain.App` WPF shell with brain toolbar and module switcher; Topo UI becomes `Topo.Module` with unchanged behaviour. | Topo works inside the shell; switching modules releases sessions. |
| F4 | Trader module: `Trader.Core` (candles, features, paper portfolio, strategies, backtest), `Trader.Market` (Binance kline zip/CSV replay), `Trader.Brain` (market tokens -> sparse stimulus -> readout -> BUY/HOLD/SELL), `Trader.Module` (UI with price/equity charts and baselines). | Deterministic backtest of brain vs buy-and-hold vs random on the 2024 replay. |
| F5 | First-run checks and environment setup from the app, merged handoff documentation, low-cost CI (PR only, path filters, fast tests only). | Fresh clone + data -> app guides the user to a running brain. |

## Non-negotiables carried over

- Brain service invariants from `docs/PROJECT_HANDOFF.md` (FlyTrader phases 1–6).
- Topo frozen contracts from `docs/topo/agent-handoff.md` (reference/unsigned,
  amplitude 100, duration 1, settle 22, 128-root readout, frozen classifier).
- No real-money trading, no exchange orders, no wallets, no live network calls
  from the brain. The connectome is fixed; any decision layer is external.

## Status (2026-11-18)

F0–F5 implemented on `staging` (feature branches merged with `--no-ff`) and
verified on Windows 11 / .NET 9 in branch `fix/verify-0010`. The CI workflow
runs on pull requests.

Verified:

- `dotnet build FlyBrain.sln`: 0 warnings, 0 errors (full rebuild).
- `dotnet test FlyBrain.sln`: 180 passed, 0 failed (guarded real-brain tests
  return early without their `TOPO_*` / `FLYBRAIN_*` variables).
- `pytest tests\python`: 45 passed.
- Brain service: ready in ~3.5 s with the reference domain only (peak ~650 MB);
  with `FLYBRAIN_EAGER_WHOLE=1` startup is ~5.2 s and peak ~1.3 GB, so the
  whole domain is built only when requested.
- GUI (driven via UI Automation): START BRAIN launches the service and shows
  BRAIN ON (green); Topo decodes `N35 W54` (reward 1.000); switching to Trader
  deletes the Topo session (`DELETE /api/v1/brain/sessions/...` 204) before
  Trader creates its own; Trader loads 2024 data (8,784 candles, checksums
  12/12, 8,039 tradable hours); RUN BACKTEST (ALL) fills the table; closing the
  window leaves no `brain_service` Python process and frees port 8765.

Trader 2024 backtest (reference/unsigned, default `brain-trader.json`,
10,000 USDT, 0.1 % fee; deterministic across repeated runs). Inputs: 10 roots
from rank 15; readout 451 BUY / 451 SELL neurons; 30 distinct market states
(brain: 9 BUY, 1 SELL, 20 HOLD states).

| Strategy | Final (USDT) | Return | Max DD | Trades | B/S/H signals |
|---|---:|---:|---:|---:|---|
| Fly brain | 19,431.70 | +94.32 % | 32.53 % | 55 | 2599/32/5408 |
| Buy & hold | 22,266.45 | +122.66 % | 32.31 % | 1 | 1/0/8038 |
| Momentum (trend token) | 9,201.43 | −7.99 % | 38.03 % | 253 | 2749/2191/3099 |
| Random (seed 20260924) | 9,639.34 | −3.61 % | 31.30 % | 396 | 371/412/7256 |

The BUY/SELL pools are an arbitrary mapping; these numbers are not evidence of
market skill. Reproduce with `FLYBRAIN_RUN_TRADER_SMOKE=1` and a running brain:
`dotnet test tests\dotnet\Trader.Tests --filter TraderBrainSmokeTests`.

Fixed during verification: Trader view crashed the app on first display
(read-only `Progress` bound TwoWay by `ProgressBar.Value`); two test bugs
(BrainHost timeout test probe sequence, code-page-dependent subprocess
decoding in a connectome bootstrap test).

Still open / manual: REPLAY WITH THE FLY, the whole-brain checkbox from the
GUI, first-run setup buttons (create venv / download data) on a fresh clone,
and the Topo Phase 9 benchmark and whole-brain validation scripts (not run).
In each evaluation the brain produces exactly 3 spikes (the input pulses), so
the readout difference is subthreshold membrane response only.
