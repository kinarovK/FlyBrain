# Neural input channels

The Phase 4 channels were selected from `GET /api/v1/brain/sessions/{session_id}/roots?offset=0&limit=64`
on a `reference` / `unsigned` session. The first 15 ascending root IDs were
validated individually through the public neuron-diagnostic endpoint, then
assigned in `GeoCoordinateToken` order: North, South, East, West, Digit0–9,
Separator.

Each channel currently has width 1. The assignment is entirely
application-defined: each ID is a Topo input-channel entry point, not a neuron
with a biological geographic or numeric meaning.

Phase 4C calibrated the existing mapping against a real reference session. The
previous amplitude `2.0` was silent for every channel. The selected amplitude
is `100.0`: it is the weakest tested value that produces one safe,
non-truncated spike for every mapped input root. The stimulus duration remains
one step. Phase 4D found that a just-spiked input neuron has 22 refractory
steps, so `settle_steps` is `22`: the smallest tested global empty-step gap
that permits a repeated token to spike again in a continuous session.

`InputStimulationCalibrationRunner` runs independent, deterministically ordered
reset–stimulate–settle samples and records aggregate telemetry; it does not
derive neural features or perform learning.
