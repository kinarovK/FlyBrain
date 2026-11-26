# FlyBrain

> "Why use artificial intelligence when you can use actual intelligence?"
> — a joke, not a scientific claim.

FlyBrain is an educational desktop app that runs a **simplified leaky
integrate-and-fire model over the real adult-female *Drosophila* FlyWire FAFB
v783 connectome** and lets that fixed "digital fly brain" drive interchangeable
modules:

| Module | What the fly does |
|---|---|
| **Topo** | Integer map coordinates are encoded as tokens, sent through the connectome and decoded back by a frozen external classifier; an external hill-climber searches for hidden targets on a map. |
| **Trader** | Hourly BTC/USDT market states (2024 offline replay) are sent to the brain as tokens; two fixed readout pools decide BUY / SELL / HOLD for a simulated 10,000 USDT paper account, compared with buy-and-hold, momentum and random baselines. |

It is **not** a living fly brain, a consciousness simulation or a prediction
model. The connectome is structural data; membrane dynamics, stimuli, token
mappings and readouts are explicit, application-defined modelling choices.
Nothing is learned inside the connectome. There is no real-money trading, no
exchange connection and no wallet.

## Quick start (Windows)

Requirements: .NET 9 SDK, Python 3.12+, WebView2 runtime (ships
with Windows 11), about 3 GB free disk for the FlyWire data.

```powershell
git clone https://github.com/kinarovK/FlyBrain.git
cd FlyBrain
dotnet run --project src\FlyBrain.App
```

1. If the yellow **Setup needed** bar appears, open **SETUP...** and click
   **1. Create Python environment**, then **2. Download brain data** (read and
   accept the [FlyWire data principles](https://edit.flywire.ai/principles.html)
   first; the download and preprocessing take a while).
2. For Trader, put the Binance BTCUSDT 1h kline zips for 2024 into
   `data/market/binance/` — see [data/market/README.md](data/market/README.md).
3. Click **START BRAIN**. The app starts the Python brain service itself,
   waits until it is ready and stops it when you close the window.
4. Pick **Topo** or **Trader** on the left. Only the visible module holds a
   brain session.

Options: tick **Whole brain** to prepare the 139,255-neuron domain at start
(slower, ~1.4 GB RAM; the default is the fast 912-neuron reference domain).
`FLYBRAIN_AUTOSTART=1` or `--start-brain` starts the brain on launch.
`FLYBRAIN_ROOT`, `FLYBRAIN_PYTHON` and `FLYBRAIN_BRAIN_SERVICE_URL` override
the repository, interpreter and service address.

## Repository layout

```text
src/
  FlyBrain.App                  WPF shell: brain button, setup, module list
  FlyBrain.BrainHost            starts/stops the Python service, first-run setup
  FlyBrain.BrainClient          typed HTTP client for the brain service API
  FlyBrain.Common               repository locator
  FlyBrain.Modules.Abstractions IBrainModule contract + small MVVM helpers
  Topo.Core / Topo.Training / Topo.Module / Topo.Experiments
  Trader.Core / Trader.Market / Trader.Brain / Trader.Module
brain_service/                  Python FastAPI adapter around the neural engine
scripts/                        FlyWire acquisition, preprocessing, LIF engine
brain/configs/                  versioned model and service configuration
modules/topo, modules/trader    frozen module artifacts and configuration
data/                           local data (git-ignored except READMEs)
tests/python, tests/dotnet      fast regression tests
docs/                           handoff, API contract, ADRs, task records
```

## Build and test

```powershell
dotnet build FlyBrain.sln
dotnet test FlyBrain.sln
.\.venv\Scripts\python.exe -m pytest tests\python -q -p no:cacheprovider
```

Real-brain integration tests are guarded by environment variables and are not
part of the default run. See [docs/FLYBRAIN_HANDOFF.md](docs/FLYBRAIN_HANDOFF.md)
for architecture, invariants and how to add a module.

## Data provenance and citations

The brain uses the FlyWire FAFB materialization 783 proofread root IDs and
aggregated proofread connectivity ([Zenodo 10676866](https://doi.org/10.5281/zenodo.10676866))
plus canonical v783 annotations from
[flyconnectome/flywire_annotations](https://github.com/flyconnectome/flywire_annotations).
Respect the [FlyWire data principles](https://edit.flywire.ai/principles.html).

- Dorkenwald et al. (2024), FlyWire whole-brain connectome data, Zenodo 10676866.
- Schlegel et al. (2024), *Whole-brain annotation and multi-connectome cell
  typing of Drosophila*, [Nature](https://doi.org/10.1038/s41586-024-07686-5).
- Shiu et al. (2024), *A Drosophila computational brain model reveals
  sensorimotor processing*, [Nature](https://doi.org/10.1038/s41586-024-07763-9).

Market data: Binance public data (data.binance.vision), used locally only.
