# FlyWire FAFB v783 data

This directory is intentionally mostly ignored by Git. It contains a local,
reproducible acquisition of **FlyWire FAFB materialization 783**, the adult
female *Drosophila melanogaster* connectome. It must not contain the raw FAFB
electron-microscopy volume.

## Default compact profile

| File | Source | Expected size | Integrity |
| --- | --- | ---: | --- |
| `proofread_root_ids_783.npy` | [Zenodo 10676866](https://zenodo.org/records/10676866) | 1.1 MB | MD5 `e0e6c19732fd8c7a4e39a2d170105421` |
| `proofread_connections_783.feather` | [Zenodo 10676866](https://zenodo.org/records/10676866) | 852.0 MB | MD5 `f48f972d262323a102aed49af1396b8a` |
| `annotations/Supplemental_file1_neuron_annotations.tsv` | [flyconnectome/flywire_annotations](https://github.com/flyconnectome/flywire_annotations) | changes by revision | pinned Git commit in `manifest.json` |

The root-ID array contains all proofread neuron/root IDs. The connectivity
table has one row per proofread pre-neuron/post-neuron/neuropil combination;
`syn_count` is the represented synapse count, and the `*_avg` columns are mean
neurotransmitter prediction probabilities. This is an aggregated proofread
subset, **not** the full individual-synapse table.

The annotation TSV documents, among other fields, neuron flow, superclass,
cell class, nerve, lineage, side, neurotransmitter, and VFB identifiers.
It is the annotation basis used by `fafbseg-py`.

## Optional full profile

`optional/flywire_synapses_783.feather` is the individual-synapse table from
the same Zenodo record: approximately 9.5 GB, MD5
`f8f1b97c9d4b0ea9b4c8b287f6b99091`. The table describes about 130 million
thresholded synapses and has its own reconstruction limitations; it is not
downloaded by default.

## Acquisition and preprocessing

Read and abide by the [FlyWire data principles](https://edit.flywire.ai/principles.html),
then run:

```powershell
python scripts\download_connectome.py --profile compact --agree-flywire-principles
python scripts\preprocess_connectome.py
python scripts\validate_connectome.py
```

Downloads stream to a resumable `.part` file and are MD5-verified before their final
name is used. The generated `manifest.json` is local provenance: it records
source URLs, exact declared version, retrieval time, checksums, and the
annotation commit. It is intentionally not committed with the source data.

## Licence and citation

Follow the data principles and the source record's rights/attribution terms.
For connectivity, cite the FlyWire Consortium/Dorkenwald et al. Zenodo dataset
([DOI 10.5281/zenodo.10676866](https://doi.org/10.5281/zenodo.10676866)). For
annotations, follow the repository's current citation instructions; current
revisions cite Schlegel et al. (2024), Dorkenwald et al. (2024), Matsliah et
al. (2024), and, for newer annotation releases, Berg et al. (2025).

No claim about neural dynamics, learning, consciousness, dopamine, or behavior
follows from this structural data alone.

## Phase 4 local experiment artifacts

`experiments/neural-v1/` is ignored local output from a simplified LIF model
over a selected v783 subgraph. Each complete experiment records the exact
source-manifest hash, annotation commit/query, selected root IDs and hops,
resolved model configuration, compact spikes, summaries, CSV diagnostics, and
PNG figures. These artifacts do not add any biological measurement: LIF state,
synaptic transformation, transmitter sign approximation, and external stimulus
are model assumptions documented in the experiment configuration.
