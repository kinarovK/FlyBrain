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
