# Golden files

Frozen-behaviour fingerprints checked by real-brain tests (`FLYBRAIN_REAL=1`):

| File | Test | Pins |
|---|---|---|
| `topo-n35w54-v1.json` | `Topo.Training.Tests.FrozenBaselineFingerprintTests` | Topo decode of `N35 W54`: predicted tokens, confidences, simulation steps |
| `trader-states-v1.json` | `Trader.Tests.FrozenTraderFingerprintTests` | Trader input roots, pool sizes and the BUY/SELL/HOLD answer with pool scores for all 30 market states |

Record them once with `scripts\gate.ps1 -Sprint G0 -RecordGolden` and commit
them. Re-record only for an intended, explained change of frozen behaviour.
