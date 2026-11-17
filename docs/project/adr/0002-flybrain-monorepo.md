# ADR 0002 — One FlyBrain repository and app

Status: accepted (2026-11-18, task 0010).

## Context

The FlyTrader repository held the validated FlyWire brain foundation and a
local HTTP brain service, with empty placeholders for a .NET trading client.
Topo was a separate .NET WPF app that consumed that service over HTTP and had
to be started after a manual PowerShell step.

## Decision

- Merge both into one repository (`kinarovK/FlyBrain`), using the FlyTrader
  folder as the base so the local FlyWire data and `.venv` stay in place.
- Keep the Python service as the single numerical engine; .NET code stays an
  HTTP client of the public `/api/v1` contract.
- The desktop app owns the service process lifetime (start button, job object,
  readiness polling) and hosts modules behind `IBrainModule`.
- Each module owns its own brain session; only the visible module holds one.
- The service prepares only the reference domain eagerly by default.

## Consequences

- One `FlyBrain.sln`, one test run, one CI workflow.
- The previous cross-repository boundary rules are superseded; the scientific
  invariants are unchanged.
- Users can switch modules without restarting anything; new modules are a
  project plus one catalog line.
