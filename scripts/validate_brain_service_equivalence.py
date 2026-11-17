"""Real-data direct-engine versus HTTP-session equivalence validation."""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from brain_service.api import create_app
from scripts.neural_dynamics import advance_dynamic_state, initialize_dynamic_state


def main() -> None:
    with TestClient(create_app()) as client:
        client.get("/health/ready").raise_for_status()
        created = client.post("/api/v1/brain/sessions", json={"execution_mode": "reference"}); created.raise_for_status()
        session_id = created.json()["session_id"]
        session = client.app.state.sessions.get(session_id)
        domain = session.domain
        root = int(domain.root_ids[domain.input_local[0]])
        direct = initialize_dynamic_state(domain.root_ids.size, domain.phase4_config)
        external = np.zeros(domain.root_ids.size, dtype=np.float64); external[domain.root_to_local[root]] = 100.0
        direct_events: list[tuple[int, int]] = []; direct_counts = np.zeros(domain.root_ids.size, dtype=np.uint64)
        for _ in range(4):
            spikes, _ = advance_dynamic_state(direct, domain.matrix, external, domain.phase4_config)
            direct_counts += spikes
            for local in np.flatnonzero(spikes): direct_events.append((direct.step - 1, int(domain.root_ids[local])))
        response = client.post(f"/api/v1/brain/sessions/{session_id}/step", json={"steps": 4, "stimulus": [{"root_id": root, "amplitude": 100.0}], "max_returned_events": 100})
        response.raise_for_status(); payload = response.json(); api_session = client.app.state.sessions.get(session_id)
        api_events = [(event["step"], event["root_id"]) for event in payload["events"]]
        checks = {"step": api_session.state.step == direct.step, "membrane": np.array_equal(api_session.state.membrane, direct.membrane), "current": np.array_equal(api_session.state.current, direct.current), "refractory": np.array_equal(api_session.state.refractory, direct.refractory), "aggregate": np.array_equal(api_session.total_spike_counts, direct_counts), "events": api_events == direct_events}
        if not all(checks.values()):
            raise SystemExit(f"Direct/API equivalence failed: {checks}")
        client.delete(f"/api/v1/brain/sessions/{session_id}").raise_for_status()
        print({"dataset": domain.graph_metadata["dataset"], "reference_neurons": int(domain.root_ids.size), "driven_root": root, "checks": checks, "spikes": payload["total_spikes"]})


if __name__ == "__main__":
    main()
