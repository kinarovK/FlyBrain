# 0011 — 3D brain viewer and mushroom-body learning: implementation plan

Status: **planned** (2026-11-23). Nothing here is implemented yet.

Two tracks, done in this order:

- **Track V — 3D brain viewer** (sprints V0–V4): see where signals go in the
  connectome while Topo and Trader run.
- **Track L — real learning** (sprints L0–L7): an opt-in plastic mode in which
  dopamine-like reward changes Kenyon-cell → MBON synapses of the mushroom body,
  so the same input can produce a different answer after training.

Track V comes first because it is lower risk, and because the viewer is the
tool we use to inspect learning in Track L.

Workflow (same as task 0010): one branch per sprint from `staging`
(`feature/0011-V0-...`), merged into `staging` with `--no-ff` only after the
sprint gate passes; one PR `staging -> main` per track. Code, comments and
commit messages in English.

---

## 0. Facts this plan relies on (checked 2026-11-23)

| Fact | Source |
|---|---|
| Every annotated root has `pos_x/y/z` and `soma_x/y/z` (139,248 annotated rows; canonical graph has 139,255 roots). Values look like FlyWire voxel coordinates (x ≈ 1–2·10⁵, z ≈ 10³ → 4 × 4 × 40 nm voxels). Units must be confirmed in V0. | `data/flywire/annotations/Supplemental_file1_neuron_annotations.tsv` |
| The 912-neuron `reference` domain is the ORN_DA1 3-hop neighbourhood: mostly ALPN (238), ALLN (195), LHLN (122), olfactory ORNs; only 2 MBON, 2 DAN, **no Kenyon cells**. | `data/flywire/experiments/neural-v1/phase4-orn-da1-unsigned-v1/selected_neurons.csv` |
| Mushroom body populations in the annotations: ~5,177 Kenyon cells (KCg, KCab, KCa'b'), 96 MBONs, ~331 DANs (PAM 307, PPL1 16, PPL2 8), APL. | same TSV, `cell_class`/`cell_type` |
| LIF parameters: dt 0.1 ms, τm 20 ms, τsyn 5 ms, threshold −45 mV, reset −52 mV, refractory 2.2 ms (Shiu et al. 2024). | `brain/configs/neural-v1.default.json` |
| With the current Topo/Trader stimulus (amplitude 100, 1 step, 22 settle steps) only the stimulated input neurons spike; downstream responses are sub-threshold membrane changes. | task 0010 verification, `plan.md` status |

Consequences: (1) the viewer must show membrane changes, not only spikes, and
needs a separate "visible propagation" stimulus preset; (2) learning cannot run
in the `reference` domain — it needs a new mushroom-body domain.

---

## 1. The gate: automatic tests after every sprint

Every sprint ends with the same scripted gate. A sprint is not merged into
`staging` until the gate is green, and the gate report is committed.

`scripts/gate.ps1` (created in sprint **G0**, before V0):

1. `dotnet build FlyBrain.sln -warnaserror` (new projects; existing warnings
   allow-listed).
2. `dotnet test FlyBrain.sln` — all fast tests.
3. `.\.venv\Scripts\python.exe -m pytest tests\python -q -p no:cacheprovider`.
4. If `-Real` is passed (default when the FlyWire data exists): start the brain
   service from `.venv`, wait for `/health/ready`, run the guarded real-brain
   tests of this and all previous sprints (`FLYBRAIN_REAL=1`), stop the service,
   assert no `brain_service` process and a free port 8765.
5. Writes `docs/project/tasks/0011_VIEWER-AND-LEARNING/gates/<sprint>.md` with
   date, commit, test counts, real-brain results and timings.

CI (`.github/workflows/ci.yml`) keeps running steps 1–3 on pull requests.
Real-brain steps stay local (they need the multi-GB FlyWire data).

Each sprint below lists **Gate additions**: the tests that sprint must add.
Rules for every sprint:

- No existing test may be deleted or weakened to make the gate pass.
- Frozen-mode regression: `tests/python` direct-engine/API equality and the Topo
  token decode tests must stay green in every sprint. Learning and viewer code
  must not change frozen results by a single bit.
- Real-brain tests are guarded by environment variables and are deterministic
  (fixed seeds, reset before each case).

### Sprint G0 — gate tooling (½ day)

- `scripts/gate.ps1` as above; `scripts/gate.sh` for the fast part (CI/Linux).
- `FLYBRAIN_REAL=1` convention; move the existing guarded tests
  (`TOPO_*`, `FLYBRAIN_RUN_TRADER_SMOKE`) under it while keeping old variables
  working.
- Gate additions: a test that `gate.ps1 -DryRun` lists every step; a real-brain
  "baseline fingerprint" test that records the Topo `N35 W54` decode and the
  Trader 30-state decision table, so later sprints can prove frozen behaviour
  is unchanged.

---

## Track V — 3D brain viewer

Goal: a 3D view inside the app where neurons are points at their real
positions, coloured by role, lighting up as membrane potential and spikes
change, for the session of the active module, live or replayed step by step.

### Sprint V0 — positions and layout data

- `scripts/export_positions.py`: from the annotations TSV build
  `data/flywire/derived/positions-v1/` (`root_ids.npy`, `xyz_um.npy` float32
  in µm, `soma_xyz_um.npy`, `cell_class.json`, `metadata.json` with source
  checksum and unit conversion). Confirm voxel size against FlyWire docs and
  record it; never guess silently.
- Roots without annotation (≈7): flagged `has_position=false`, placed at the
  centroid of their neighbours, never invented as real positions.
- Brain service: `GET /api/v1/brain/sessions/{id}/layout?offset&limit` →
  root id, x/y/z µm, super/cell class, cell type, side, role flags. Bounded
  paging (≤ 4,096 per page). Versioned in OpenAPI.
- `FlyBrain.BrainClient`: `GetSessionLayoutAsync`.
- Optional: neuropil surface meshes (FlyWire/FAFB neuropil meshes) downloaded by
  a separate script with provenance; the viewer works without them.

Gate additions: positions file round-trip and checksum test; every root of
`reference` has a layout entry; units sanity test (whole-brain bounding box in
the expected µm range); API schema/OpenAPI test; client deserialization test;
real-brain: layout of a `reference` session equals the exported file.

### Sprint V1 — activity frames

- Brain service: optional step recording. `POST .../step` accepts
  `record: {"every_steps": k, "roots": "session" | [ids], "fields": ["membrane","spikes"]}`
  and returns compact frames (membrane as float16 deltas, spikes as index lists,
  base64) for at most N frames per request. Bounded by new limits in
  `brain-service-v1.default.json` (`max_record_frames`, `max_record_roots`).
- No WebSockets: the client pulls frames per step request (keeps the current
  security model). Revisit in an ADR only if V3 needs it.
- `FlyBrain.Visualization` (new net9.0 project): `ActivityFrame`,
  `ActivityRecording` (ring buffer, ≤ 2,000 frames), decoding, colour mapping
  (membrane → colour ramp, spike → flash), level-of-detail selection
  (top-k most changed neurons for the whole domain).

Gate additions: Python tests — recording off gives byte-identical step results
(frozen-mode guarantee), frames reproduce direct-engine membrane values within
float16 tolerance, limits enforced with stable error codes; .NET tests —
decoding, ring buffer, colour mapping, LOD selection; real-brain: record a Topo
`N` token and check frame count and that the stimulated root spikes in frame 0–1.

### Sprint V2 — 3D viewer control

- `FlyBrain.Visualization.Wpf`: a WebView2 control hosting a local, offline
  page (`Assets/BrainViewer/index.html`) with **three.js** bundled in the repo
  (MIT licence file included, version pinned, SHA-256 recorded).
- Points (instanced) at real positions; colour by role: input (Topo/Trader
  input roots), readout (Topo 128 roots, Trader BUY/SELL pools), other;
  optional neuropil wireframe; orbit/zoom/pan; click a neuron → tooltip
  (root id, cell type, membrane, spike count).
- C# ↔ JS bridge: `setLayout`, `setRoles`, `pushFrames`, `seek(frame)`; all
  messages JSON-schema'd; the JS side never calls the brain service.
- Falls back to a 2D projection list if WebView2 is missing (as Topo's map does).

Gate additions: C# tests for message builders and role assignment; a JS test
run with Node only if Node is present (otherwise skipped and noted in the gate
report) for frame application and colour ramps; a guarded WebView2 smoke test
(`FLYBRAIN_UI=1`) that loads the page, sends 1,000 points and asserts the page
reports "ready" and a frame count; three.js checksum test.

### Sprint V3 — viewer in the app

- Shell: a **BRAIN VIEW** toggle opens a dock panel showing the active module's
  session. Modules expose `IBrainActivitySource` (optional interface): input
  roots, readout roots, and a recorder hook used during evaluations.
- Topo: record each token (stimulus + 22 settle steps); timeline with token
  labels `N 3 5 | W 5 4`; replay slider.
- Trader: record one market hour (3 tokens); pool colours BUY/SELL; replay
  shows why BUY or SELL won (pool means overlay).
- Recording is opt-in per run (it slows evaluation); cached Trader states show
  the stored recording of their first evaluation.

Gate additions: ViewModel tests with fake modules/recordings (play, pause,
seek, module switch clears the view, no recording when the panel is closed);
real-brain: Topo decode with recording on gives the **same decoded result** as
with recording off; Trader 30-state table unchanged with recording on.

### Sprint V4 — visible propagation and whole brain

- "Exploration" stimulus presets (stronger/longer pulses, multi-root bursts)
  available only in the viewer's sandbox panel, clearly separate from the
  frozen Topo/Trader contracts.
- Whole-domain viewing: LOD (only the top-k changed neurons drawn, e.g.
  20,000), neuropil context, frame budget.
- Export: PNG snapshot and a short frame sequence (JSON) for teaching material.

Gate additions: performance tests (frame build for 139,255 roots under a set
budget on the dev machine, recorded in the gate report); preset validation
(limits respected, unsafe status surfaced, never applied to Topo/Trader runs);
real-brain: an exploration preset produces downstream spikes beyond hop 0 in
`reference` (records which hops light up).

Track V done when: a user can open BRAIN VIEW, run Topo or Trader, and replay
where the signal went, on the 912-neuron domain and (with LOD) the whole brain.

---

## Track L — mushroom-body learning

Goal: an opt-in **plastic mode** in which a dopamine-like reward signal changes
Kenyon-cell → MBON synapse weights, inspired by the fly's associative learning
circuit. The frozen modes stay exactly as they are.

Scientific framing (to be written into the L0 spec and the UI): this is a
simplified three-factor learning rule on real structural connectivity, not a
model of real fly memory. Every parameter is a labelled modelling assumption.

### Sprint L0 — specification and ADR (no code)

- `docs/project/tasks/0011_VIEWER-AND-LEARNING/learning-spec.md`:
  - Domain: new `mushroom_body` execution mode = selected ALPN inputs + all
    Kenyon cells + MBONs + DANs + APL (+ MBINs), with the induced graph among
    them; expected ≈ 6,000 neurons. Exact selection rule by annotation
    `cell_class` / `cell_type`, deterministic order.
  - Plastic synapses: only KC → MBON edges (pair-aggregated), weights copied
    per session; everything else stays fixed.
  - Rule (first version): eligibility trace `e_ij` per KC→MBON pair driven by
    presynaptic KC spikes (decay τe, labelled assumption); on reward `R`
    (per compartment or global, decided in the spec):
    `Δw_ij = −η · R_c · e_ij` for dopamine-induced depression of the KC→MBON
    synapse (punishment/reward sign per DAN cluster, PAM vs PPL1), weights
    clipped to `[0, w_max]`.
  - Readout: MBON groups → actions (documented mapping, e.g. by compartment /
    published valence; still an application choice).
  - Controls that every experiment must report: shuffled reward, zero reward,
    frozen (no plasticity), random-weight perturbation.
- ADR 0003: plasticity is allowed only in the new mode; frozen modes and their
  equality tests are invariants; learned weights are local state
  (`data/learning/`, git-ignored) with provenance.

Gate additions: none in code; the gate run proves nothing changed. Human
approval of the spec is required before L1.

### Sprint L1 — mushroom-body domain

- `scripts/whole_brain_scaling.py`-style deterministic tier selection for
  `mushroom_body`; runtime prepares it lazily (like `whole`); `/brain/info`
  lists it; resource preflight.
- Input channels: PN (ALPN) roots grouped into "odour channels" (by glomerulus
  cell type), so modules stimulate groups, not single roots.
- Transmitter mode: `transmitter` required for APL (GABA) feedback; document
  why unsigned would remove KC sparsening.

Gate additions: selection determinism and counts (golden file), every
KC→MBON pair of the canonical graph present, no-drive control silent,
direct-engine/API equality in the new domain, frozen fingerprint unchanged;
real-brain: domain prepares within the memory budget recorded in the gate.

### Sprint L2 — calibration: sparse KC coding

- Calibrate PN channel amplitude/duration so a channel activates a sparse KC
  subset (target 2–10 % of KCs, labelled target from literature) and different
  channels activate different KC subsets.
- Calibration runner + persisted calibration artifact
  `modules/learning/config/mb-calibration-v1.json`.

Gate additions: real-brain calibration test (KC active fraction within band,
pairwise KC-pattern overlap between channels below threshold, deterministic
replay); fixture tests for the runner; safety (no runaway) asserted.

### Sprint L3 — plasticity engine

- Engine: session-owned plastic weight vector for KC→MBON pairs; eligibility
  traces updated inside the shared dynamic-state advance (one implementation,
  no copy in HTTP code); weights applied in propagation.
- API: `POST .../sessions/{id}/reward {"value": r, "compartments": ...}`,
  `GET/PUT .../sessions/{id}/plastic-weights` (export/import with checksum and
  domain fingerprint), `POST .../sessions/{id}/reset-weights`.
  `reset` keeps learned weights unless `reset_weights=true`.
- Plasticity can be switched off per session (`plasticity: false` gives the
  frozen behaviour of the same domain).

Gate additions: zero reward → weights bit-identical; plasticity off → state
identical to frozen engine; weights stay in bounds; deterministic replay of a
fixed stimulus/reward sequence (golden weights checksum); export/import round
trip; frozen fingerprint unchanged; direct-engine/API equality including
plasticity.

### Sprint L4 — .NET learning client

- `FlyBrain.BrainClient`: reward, weight export/import, reset-weights.
- `FlyBrain.Learning` (net9.0): `LearningSession` (owns a plastic session,
  trial loop: reset state → stimulate → read MBON groups → act → reward),
  learned-state store `data/learning/<module>/<run-id>/` (weights + metadata:
  domain fingerprint, seed, rule parameters, trial count), learning curves.
- Viewer integration: MBON group activity and weight-change heat map per
  compartment (uses Track V).

Gate additions: fake-client tests for the trial loop, persistence, resume,
fingerprint mismatch rejection; real-brain: 20-trial smoke run completes,
weights change only on rewarded trials.

### Sprint L5 — Conditioning module (proof that learning works)

- New module **Conditioning**: the classic fly experiment in software. Two
  "odours" A and B (PN channels); reward paired with A only; test trials
  without reward measure which MBON group responds more.
- UI: trial counter, learning curve, A vs B response, controls (shuffled
  reward, no plasticity), brain view of MBON compartments.
- Acceptance metric (fixed in L0 spec): discrimination index after training
  significantly above the shuffled-reward and frozen controls over ≥ 10 seeds.

Gate additions: real-brain experiment test (reduced trials) asserting the
metric direction and that controls stay near zero; ViewModel tests; results
written to `modules/learning/experiments/conditioning-v1/summary.json`.

### Sprint L6 — Trader with learning

- Trader "Learning fly" strategy: market tokens → PN channels; MBON groups →
  BUY/SELL/HOLD; reward = sign/size of next-hour PnL of the chosen action.
- Walk-forward protocol: train Jan–Jun 2024, freeze weights, test Jul–Dec;
  report against buy & hold, momentum, random, frozen fly, shuffled-reward fly.
- No tuning on the test half; parameters fixed in the spec before running.

Gate additions: fake-client tests for reward assignment (no look-ahead: reward
for hour t uses only hour t+1 after the decision); determinism of the full
walk-forward on synthetic data; real-brain: reduced walk-forward (one month
train / one month test) completes and writes a report.

### Sprint L7 — Topo with learning (optional)

- A "hot/cold" variant: the brain receives reward when its decoded guess gets
  closer to the hidden target; measure whether learned weights reduce the
  number of evaluations needed versus the external hill climber alone.
- Only if L5 passed its acceptance metric.

Gate additions: same pattern as L6 (fake-client, determinism, reduced
real-brain run, controls).

Track L done when: the Conditioning module shows learning that beats its
controls, and Trader/Topo learning results are reported honestly against
their baselines and controls.

---

## 2. Order and rough size

| Order | Sprint | Size (focused days) | Depends on |
|---|---|---|---|
| 1 | G0 gate tooling | 0.5 | — |
| 2 | V0 positions/layout | 1–2 | G0 |
| 3 | V1 activity frames | 2 | V0 |
| 4 | V2 3D viewer control | 2–3 | V1 |
| 5 | V3 viewer in app | 2 | V2 |
| 6 | V4 propagation + whole brain | 2 | V3 |
| 7 | L0 spec + ADR | 1 (+ approval) | — (can run in parallel with V) |
| 8 | L1 mushroom-body domain | 2–3 | L0 |
| 9 | L2 KC calibration | 2 | L1 |
| 10 | L3 plasticity engine | 3–4 | L2 |
| 11 | L4 .NET learning client | 2 | L3, V3 |
| 12 | L5 Conditioning module | 2–3 | L4 |
| 13 | L6 Trader learning | 2–3 | L5 |
| 14 | L7 Topo learning (optional) | 2–3 | L5 |

## 3. Risks

- **Units/positions**: wrong voxel scaling would distort the 3D view → V0 test
  against known neuropil extents.
- **Frame volume**: whole-domain frames are large → LOD and bounded recording.
- **KC sparseness**: unsigned mode can make KCs fire densely → transmitter mode
  and APL inhibition required; calibration may fail and need spec changes.
- **No learning effect**: the rule may not discriminate on this graph → L5 is
  the go/no-go point before L6/L7; a negative result is reported, not hidden.
- **Scope creep**: frozen Topo/Trader contracts stay untouched; anything new is
  a separate mode, preset or module.

## 4. Starting the next session

Tell the agent: "Read `docs/FLYBRAIN_HANDOFF.md` and
`docs/project/tasks/0011_VIEWER-AND-LEARNING/plan.md`, then implement sprint
G0 (or the next open sprint), run `scripts/gate.ps1`, commit the gate report
and merge into `staging`." Update the status line at the top of this file
after each sprint.
