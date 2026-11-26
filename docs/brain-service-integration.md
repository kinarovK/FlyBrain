# Brain Service API v1 — Integration Handoff

This document is the standalone contract for consumers of the local Brain
Service. Treat the generated [OpenAPI document](http://127.0.0.1:8765/openapi.json)
as the authoritative machine-readable schema.

## Start and connect

Install the service's declared Python dependencies, then start it locally:

```powershell
python -m brain_service.server
```

Default base URL: `http://127.0.0.1:8765`.

The service binds only to localhost by default and has no authentication. Do
not expose it on a public network without adding an appropriate security layer.

## Health and readiness

| Request | Meaning |
|---|---|
| `GET /health/live` | HTTP process is running; does not run a simulation. |
| `GET /health/ready` | Returns `200` when the required runtime is loaded and ready, otherwise `503`. |
| `GET /api/v1/brain/info` | Versioned capability, source-summary, and model-configuration information. |

Example readiness response:

```json
{
  "status": "ready",
  "dataset": "...",
  "graph_format": "...",
  "canonical_neuron_count": 139255,
  "startup_timings": {"startup_seconds": 6.7},
  "service_version": "0.1.0"
}
```

Do not create a session until readiness is `200`.

## Session lifecycle

Create a session with `POST /api/v1/brain/sessions`:

```json
{
  "execution_mode": "reference",
  "transmitter_mode": "unsigned"
}
```

`execution_mode` is either `reference` or `whole`; `transmitter_mode` is
either `unsigned` or `transmitter`. The result is `201` and includes an opaque
`session_id`, selected mode, neuron count, and resolved configuration identity.
Never derive meaning from a session ID or use a filesystem path as one.

Use these lifecycle routes:

| Request | Result |
|---|---|
| `GET /api/v1/brain/sessions/{session_id}` | Session status and resolved configuration. |
| `POST /api/v1/brain/sessions/{session_id}/reset` | Resets dynamic state, counters, events, and clock; retains session configuration. |
| `GET /api/v1/brain/sessions/{session_id}/state` | Bounded current-state summary. |
| `GET /api/v1/brain/sessions/{session_id}/roots?offset=0&limit=64` | Bounded page of valid session-domain root IDs. |
| `GET /api/v1/brain/sessions/{session_id}/layout?offset=0&limit=1024` | Bounded page of neuron positions (µm) and classes, same order as `/roots`. |
| `POST /api/v1/brain/sessions/{session_id}/readout` | Bounded, read-only batch diagnostic snapshot for requested roots. |
| `GET /api/v1/brain/sessions/{session_id}/neurons/{root_id}` | Explicit single-neuron diagnostic. |
| `DELETE /api/v1/brain/sessions/{session_id}` | Releases the session; returns `204`. |

Session state is not persistent. Service restart or orderly shutdown disposes
all sessions.

## Session-domain root enumeration

`GET /api/v1/brain/sessions/{session_id}/roots` enumerates only the existing
session domain. It is read-only and does not advance simulation state. Results
are ordered by ascending numeric root ID, which is stable across reset and
equivalent sessions with the same resolved domain.

Use zero-based `offset` (default `0`) and `limit` (default `64`, minimum `1`,
maximum `256`). A page beyond the end returns an empty `root_ids` list with the
same `total_root_count`; it is not an error. This endpoint returns IDs only,
not neuron diagnostics, graph details, or annotations.

## Session-domain layout (neuron positions)

`GET /api/v1/brain/sessions/{session_id}/layout` returns, for the same roots and
in the same order as `/roots` (ascending numeric root ID), the neuron position
and annotation classes. It is read-only and never touches simulation state.

Query: zero-based `offset` (default `0`) and `limit` (default `1024`, minimum
`1`, maximum `max_layout_page` from the service configuration, default
`4096`). A page beyond the end returns an empty `entries` list.

```json
{
  "session_id": "...", "offset": 0, "limit": 1024, "returned_count": 912,
  "total_root_count": 912, "coordinate_unit": "um",
  "entries": [
    {"root_id": 720575940603231916, "x": 448.152, "y": 225.884, "z": 48.64,
     "has_position": true, "super_class": "central", "cell_class": "ALPN",
     "cell_type": "DA1_lPN", "side": "left"}
  ]
}
```

- Coordinates are the FlyWire annotation anchor point (`pos_x/y/z`, on the
  neuron backbone, not the soma) converted from 4 × 4 × 40 nm voxels to µm, in
  FAFB space (the axes of the FlyWire volume; z is the section axis).
- `has_position: false` marks a canonical root without an annotation row (14
  in v783). Its `x/y/z` is a placeholder — the mean of its annotated graph
  neighbours — and its classes are `null`. Never present it as a real position.
- Classes are `null` where the annotation is empty.
- Positions come from `data/flywire/derived/positions-v1/`, built by
  `scripts/export_positions.py`. The service loads and validates them once, on
  the first layout request. If they are missing or invalid, layout requests
  return `503 LAYOUT_NOT_READY`; brain readiness and all other routes are
  unaffected.

## Batch neural readout

`POST /api/v1/brain/sessions/{session_id}/readout` accepts a required
`root_ids` list of 1–256 unique, Int64-compatible roots from that active session
domain. It returns `simulation_step`, `simulated_ms`, `returned_count`, and
per-root model-state diagnostics (`membrane_mV`, `refractory_steps`,
`cumulative_spike_count`, and `first_spike_step`). `neurons` preserves the
request order exactly. A request containing any unknown root fails as a whole
with `404 UNKNOWN_ROOT_ID`; duplicates and invalid bounds return the standard
`422 INVALID_CONFIGURATION` envelope.

The readout obtains one read-only snapshot while holding the session lock, so
all returned diagnostics are from the stated simulation step. It does not
advance or otherwise mutate neural state. These are model-state diagnostics,
not measured biological activity.

## Stepping and sparse stimulus

Advance a session with `POST /api/v1/brain/sessions/{session_id}/step`:

```json
{
  "steps": 10,
  "stimulus": [
    {"root_id": 720575940605686886, "amplitude": 2.0}
  ],
  "max_returned_events": 100
}
```

`steps` is a positive integer. Each stimulus entry is a root ID and a finite
amplitude. Root IDs must be in the selected session domain and may appear only
once. Amplitude is an external model-current value; it is not a measured
biological or sensory signal.

The response includes start/end step, simulated time, exact spike aggregates,
active fraction, peak simultaneous activity, safety status, engine time, and
a bounded event list. If `events_truncated` is `true`, only returned event
details were capped: simulation and exact aggregate counters still ran to
completion.

Reset then replay the same step sequence to obtain deterministic results for
the same service/configuration/runtime.

## Limits

Default limits are:

| Limit | Default |
|---|---:|
| Active sessions | 4 |
| Whole-mode sessions | 1 |
| Simultaneous whole-mode operation | 1 |
| Steps per request | 1,000 |
| Stimulus entries per request | 256 |
| Returned events per request | 10,000 |

Same-session mutations are never concurrent. A competing mutation returns a
conflict rather than racing. Consumers should keep step requests bounded and
delete sessions when finished.

## Error contract

Every service error uses:

```json
{
  "error": {
    "code": "UNKNOWN_ROOT_ID",
    "message": "Stimulus root is not in this session domain.",
    "details": {"root_id": 123}
  }
}
```

Important stable codes include:

| Code | Typical HTTP status |
|---|---:|
| `GRAPH_NOT_READY`, `LAYOUT_NOT_READY` | 503 |
| `SESSION_NOT_FOUND`, `UNKNOWN_ROOT_ID` | 404 |
| `SESSION_BUSY`, `RUNAWAY_ACTIVITY`, `NUMERICAL_FAILURE` | 409 |
| `INVALID_CONFIGURATION`, `INVALID_STEP_COUNT` | 422 |
| `RESOURCE_LIMIT_EXCEEDED` | 429 |
| `INTERNAL_ERROR` | 500 |

Validation errors also use this envelope. Client code should branch on `code`,
not on prose in `message`.

## Minimal integration example

```powershell
$base = 'http://127.0.0.1:8765'
Invoke-RestMethod "$base/health/ready"
$session = Invoke-RestMethod "$base/api/v1/brain/sessions" -Method Post -ContentType 'application/json' -Body '{"execution_mode":"reference","transmitter_mode":"unsigned"}'
$body = '{"steps":1,"stimulus":[]}'
Invoke-RestMethod "$base/api/v1/brain/sessions/$($session.session_id)/step" -Method Post -ContentType 'application/json' -Body $body
Invoke-RestMethod "$base/api/v1/brain/sessions/$($session.session_id)/reset" -Method Post
Invoke-RestMethod "$base/api/v1/brain/sessions/$($session.session_id)" -Method Delete
```

Use the OpenAPI document to generate or handwrite clients. Do not rely on
implementation modules, local storage paths, internal process memory, or
undocumented fields.
