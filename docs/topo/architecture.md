# Topo architecture

Topo is a standalone desktop application. The FlyTrader Brain Service remains
owned by FlyTrader and will be consumed only through its documented local HTTP
API; Topo never references FlyTrader source code or data as a dependency.

Topo owns application state and future external training state. Brain Service
session state remains external and transient.

```text
Topo.Module
   |
   +--------------> Topo.BrainClient
   |
   +--------------> Topo.Training
   |
   +--------------> Topo.Core

Topo.BrainClient -- HTTP --> Brain Service
                             D:\AI\FlyTrader
```

`Topo.Core`, `Topo.BrainClient`, and `Topo.Training` do not depend on the WPF
application. `Topo.Core` owns geographic coordinate semantics and has no
project dependencies. `Topo.BrainClient` owns Brain Service HTTP integration;
neither project depends on the other.

`Topo.BrainClient` uses the public Brain Service HTTP contract only. Its base
address defaults to `http://127.0.0.1:8765` and can be overridden with the
`TOPO_BRAIN_SERVICE_URL` environment variable. Higher layers own a session and
must serialize its `step`, `reset`, and `delete` mutations; the client exposes
`SESSION_BUSY` as a structured error and does not retry it automatically.

```text
GeoCoordinate
     ↓
GeoCoordinateToken[]
     ↓
Topo.Training encoder
     ↓
brain-channels.json
     ↓
Topo.BrainClient
     ↓
Brain Service
```

Phase 5 adds a bounded observation path. `Topo.Training` enumerates the
reference-domain roots, excludes every configured direct input root, and uses
ordered batch readouts to compose a same-step logical snapshot. The selected
readout population and ordered delta-feature layout are stored in
`config/readout-population.json`; feature extraction remains outside the WPF
application and contains no learning or prediction logic.

Phase 6 consumes that fixed configuration in an external, deterministic token
readout classifier. Contextual datasets reset once per complete coordinate and
are split only on complete-coordinate boundaries. Training-only preprocessing
removes constant fields and z-scores retained fields before a small linear
softmax model; the persisted model includes all normalization and readout
metadata needed for reproducible inference. This remains an experimental
semantic-token readout, not a coordinate decoder or a claim that the
connectome learned anything.

Phase 7 adds a pure semantic-token grammar decoder and a serial end-to-end
evaluation path. It loads the Phase 6 model unchanged, validates its feature
and readout identity before a session is created, and decodes only classifier
predictions. Sequence termination remains supplied by the experiment runner;
there is no neural END token.

Phase 9 adds a deterministic external autonomous-search runner in
`Topo.Training`. The runner alone owns the hidden target and compares it with
the decoded candidate through the existing application reward. Its policy API
does not receive target coordinates, hemispheres, or target tokens; it receives
only proposed/decoded candidates and derived feedback. Every proposal is
evaluated by the unchanged frozen decode pipeline and therefore begins with a
Brain Service session reset. The policy and an equal-budget random baseline are
external to the fixed FlyWire connectome and do not alter any neural weights.
