# FlyTrader Brain Service API v1

## Boundary and startup

The local Python service exposes a simplified LIF simulation over the real
FlyWire FAFB v783 structural connectome. It is not a biologically complete
living fly brain and its external-current inputs are experimental model values,
not sensory measurements. It loads/validates the accepted Phase 3 graph and
neural configuration, binds to `127.0.0.1` by default, and has no
authentication; do not expose it publicly.

Start with `python -m brain_service.server`. Use `GET /health/live` for
process liveness, `GET /health/ready` for real-graph readiness, and
`GET /api/v1/brain/info` for source/model facts. OpenAPI is at `/openapi.json`.

## Sessions and step contract

Create with `POST /api/v1/brain/sessions`:

```json
{"execution_mode":"reference","transmitter_mode":"unsigned"}
```

The response supplies an opaque `session_id`. `reference` is the accepted
Phase 4 912-neuron domain; `whole` is exactly the 139,255-root Phase 3 domain.
The graph/CSR domain is immutable and process-shared; session state is private
and is lost on service restart.

Step with `POST /api/v1/brain/sessions/{session_id}/step`:

```json
{"steps":10,"stimulus":[{"root_id":720575940605686886,"amplitude":2.0}]}
```

Root IDs must be in the active domain and duplicate IDs are rejected. Results
are bounded aggregates plus an optional bounded event prefix. `events_truncated`
never means the actual neural dynamics or exact aggregate accounting stopped.
Use `/state` for a bounded summary and `/neurons/{root_id}` only for explicit
single-neuron diagnostics. `POST /reset` resets dynamic state/counters/clock
while retaining the immutable graph and resolved configuration.

For a fixed ordered set of up to 256 neuron diagnostics at one simulation step,
use `POST /api/v1/brain/sessions/{session_id}/readout` with
`{"root_ids":[...]}`. The response preserves request order, is read-only, and
returns only requested model-state diagnostics; it does not expose graph data
or measured biological activity.

Enumerate valid roots for a session with `GET
/api/v1/brain/sessions/{session_id}/roots?offset=0&limit=64`. Pages are
ascending numeric root IDs, stable across reset, with a default limit of 64
and maximum of 256. Root enumeration is read-only and exposes no graph or
neural-state details.

## Limits, errors, and lifecycle

Defaults are max four sessions, one whole-brain session, 1,000 steps/request,
256 stimulus entries, and 10,000 returned events. A same-session concurrent
mutation returns `409 SESSION_BUSY`; resource limits return `429
RESOURCE_LIMIT_EXCEEDED`. Other stable error codes include `GRAPH_NOT_READY`,
`SESSION_NOT_FOUND`, `UNKNOWN_ROOT_ID`, `INVALID_CONFIGURATION`,
`INVALID_STEP_COUNT`, `NUMERICAL_FAILURE`, `RUNAWAY_ACTIVITY`, and
`INTERNAL_ERROR`. Errors use `{"error":{"code", "message", "details"}}`
and do not expose paths or tracebacks.

Delete with `DELETE /api/v1/brain/sessions/{session_id}`. Shutdown rejects new
work, releases sessions, and does not persist state. A future .NET client may
consume this versioned OpenAPI contract, but no .NET client is part of Phase 6.

## Verification commands

```powershell
python scripts\smoke_brain_service.py
python scripts\validate_brain_service_whole.py
python scripts\benchmark_brain_service.py
```
