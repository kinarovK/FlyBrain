# Prompt: verify and fix the FlyBrain merge (task 0010)

Run this in Claude Code (or another coding agent) from `D:\AI\FlyTrader` on Windows.

---

You are working in the FlyBrain repository at `D:\AI\FlyTrader` (Windows,
.NET 9 SDK, Python venv in `.venv`). Phases F0–F5 of task 0010 were written
without a compiler. Your job is to make the solution build, make every test
pass, and smoke-test the app. Do not add features.

Read first: `docs/FLYBRAIN_HANDOFF.md`, `docs/project/tasks/0010_FLYBRAIN-MERGE/plan.md`,
`docs/project/adr/0002-flybrain-monorepo.md`.

## Rules

- Work on a new branch `fix/verify-0010` created from `staging`. Commit in small
  logical steps. Commit messages and code comments in English.
- Fix the code, not the tests, unless a test is clearly wrong; explain each such
  case in the commit message.
- Do not change the scientific/numerical behaviour of `brain_service/` or
  `scripts/`, the Topo frozen contracts (`modules/topo/*`, amplitude 100,
  1 stimulus step, 22 settle steps, 128-root readout, frozen classifier), or
  `modules/trader/config/brain-trader.json`, unless a bug forces it — then
  say so explicitly.
- Never commit anything under `data/` except the READMEs, and never commit
  `.venv`, `bin/`, `obj/`.
- Do not run the multi-hour Topo Phase 9 benchmark or the whole-brain
  validation scripts.

## Steps

1. `git checkout staging && git pull` (if a remote exists) then
   `git checkout -b fix/verify-0010`.
2. `dotnet build FlyBrain.sln` — fix all errors and new warnings in our code.
   Known risk areas: WPF projects do not get `System.IO` as an implicit using;
   test projects need `using Xunit;` (global `<Using Include="Xunit" />`);
   C# 12 collection expressions in overloaded/generic calls.
3. `dotnet test FlyBrain.sln` — make all tests pass. Real-brain tests are
   guarded by `TOPO_*` environment variables and should return early.
4. `.\.venv\Scripts\python.exe -m pytest tests\python -q -p no:cacheprovider` —
   make all tests pass (includes the new `test_service_config.py`).
5. Brain smoke test without the GUI:
   - `.\.venv\Scripts\python.exe -m brain_service.server` in the background,
     wait for `http://127.0.0.1:8765/health/ready` to return 200, then stop it.
   - Confirm startup did NOT prepare the whole domain (reference only) unless
     `FLYBRAIN_EAGER_WHOLE=1` is set.
6. Trader smoke test against the real brain (write a small throwaway console
   snippet or a guarded xUnit test enabled by `FLYBRAIN_RUN_TRADER_SMOKE=1`;
   keep it guarded if you commit it):
   - load `data/market/binance` with `BinanceKlineLoader`, extract snapshots,
   - connect `TraderBrainSession` with the default configuration,
   - report the 10 selected input roots, pool sizes, and run the full 2024
     backtest for the brain and the three baselines,
   - print final equity, return %, max drawdown, trade count and the number of
     distinct market states (expected 30). If all 30 brain decisions are HOLD,
     report the raw pool scores; do not tune the dead zone silently.
7. GUI smoke test: `dotnet run --project src\FlyBrain.App`. Check that the
   window opens, START BRAIN starts the service and turns green, Topo decodes
   `N35 W54`, switching to Trader releases the Topo session, Trader loads 2024
   data and RUN BACKTEST (ALL) fills the table, and closing the window stops the
   Python process (no `python.exe` left running brain_service). If you cannot
   drive the GUI, say which checks remain manual.
8. Update `docs/project/tasks/0010_FLYBRAIN-MERGE/plan.md` "Status" with what
   was verified, the Trader backtest numbers, and anything still open.
9. Merge `fix/verify-0010` into `staging` with `--no-ff`, push `main`,
   `staging` and the branch, and open (or update) the PR `staging -> main`
   with a summary of fixes and verification results.

Finish with a short report: what was broken, what you changed, test counts,
the Trader numbers, and remaining manual checks.
