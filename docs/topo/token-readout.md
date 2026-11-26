# Token readout classifier

Phase 6 trains a small external classifier from the accepted Phase 5 readout
configuration. It does not train the connectome or alter Brain Service state
beyond normal, serial session stepping.

## Fixed neural contract

`config/readout-population.json` remains authoritative: reference execution,
the ordered 128 non-input readout roots, a 22-step observation offset, and the
per-root feature triplet in this exact order:

1. `membrane_delta_mV`
2. `spike_count_delta`
3. `refractory_steps`

That produces a raw vector width of 384. Preprocessing is fitted from the
training split only. It removes exactly zero-variance dimensions, preserves
their original indices in the model artifact, then z-scores every retained
dimension with training-only mean and population standard deviation.

## Dataset protocol

The opt-in experiment first takes several isolated, reset-per-trial examples
of every semantic `GeoCoordinateToken`. It reports those results separately;
they are a clean signal check, not coordinate decoding.

Its primary dataset is a deterministic corpus of 200 complete coordinates
(configurable from 12 to 500). It includes both hemispheres, all decimal
digits, one-, two-, and three-digit magnitudes, repeated digits, and these
required examples: `N5 E7`, `N11 E11`, `N55 W11`, `S33 E111`, `N90 W180`, and
`S0 E0`. Every coordinate starts with one session reset. Each token is then
stimulated and settled using the accepted channel configuration, and observed
immediately after its configured 22 settle steps. There is no extra settling
or reset between tokens in a coordinate.

Complete coordinates, never individual token observations, are shuffled by a
fixed seed and split 70/15/15 into training/validation/test groups. This stops
the same coordinate context from leaking across partitions.

Reported macro recall averages only classes represented in the evaluated
partition; the accompanying class-support table makes any absent small-split
classes explicit.

## Model and artifact

The model is a transparent multiclass linear softmax classifier trained with
cross-entropy and mini-batch gradient descent. Its default seed is `20260922`,
learning rate is `0.08`, epochs are `180`, batch size is `64`, and L2 weight
regularization is `0.0001`. There are no ML-library dependencies.

The live experiment is deliberately opt-in:

```powershell
$env:TOPO_RUN_TOKEN_READOUT_EXPERIMENT = '1'
$env:TOPO_REPOSITORY_ROOT = 'D:\AI\Topo'
$env:TOPO_BRAIN_CHANNELS_PATH = 'D:\AI\Topo\config\brain-channels.json'
$env:TOPO_READOUT_POPULATION_PATH = 'D:\AI\Topo\config\readout-population.json'
dotnet test D:\AI\Topo\tests\Topo.Training.Tests --no-build --no-restore --filter "Category=Integration"
```

It writes `data/models/token-readout-v1.json` and a companion
`token-readout-v1-report.json`. The model records class ordering; raw and
retained dimensions; retained indices; normalization means and standard
deviations; weights and biases; feature ordering; readout roots and offset;
and all deterministic training hyperparameters. Loading validates the artifact
schema and feature contract before inference.

The trained output is only a semantic token classifier. It does not infer or
decode a full coordinate, use a reward signal, apply dopamine/plasticity, or
provide a UI.
