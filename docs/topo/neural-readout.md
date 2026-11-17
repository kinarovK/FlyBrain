# Neural readout discovery

Phase 5B observed the 912-root `reference` / `unsigned` session through the
public bounded readout API. All 15 configured input roots are excluded before
discovery, leaving 897 candidate roots. This prevents direct input identity
from becoming a trivial feature.

Each token trial resets the session, records a baseline, stimulates exactly one
semantic input, then records same-step logical snapshots at offsets 0, 1, 2, 5,
10, and 22 empty steps. A logical snapshot requests deterministic batches of at
most 256 roots and rejects any mixed simulation step.

Offset 0 had no non-input response. Offsets 1–22 produced deterministic
membrane-only downstream responses; no non-input spikes were observed. Offset
22 was selected because it had the largest observed membrane deltas (maximum
`0.6540677881 mV`) while retaining deterministic token-dependent variation.

`config/readout-population.json` contains 128 deterministically ranked,
non-input roots. Ranking prioritizes downstream spike delta when present, then
token-dependent membrane range, maximum membrane magnitude, and `root_id` as a
tie-breaker. The roots have no assigned biological or geographic meaning; they
are computational feature channels.

For each configured root, the fixed ordered feature triple is:

1. `membrane_delta_mV` (observation minus baseline)
2. `spike_count_delta` (observation cumulative count minus baseline)
3. `refractory_steps` (at observation)

The Phase 5B vector width is `128 × 3 = 384`. No normalization, classifier,
or coordinate decoder is included. In the observed reference session, all 15
token vectors were distinct with exact replay equality; this establishes only
a deterministic candidate signal, not biological interpretation or learning
success.
