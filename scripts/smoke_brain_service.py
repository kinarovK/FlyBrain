"""Run a local real-data Phase 6 readiness/session/reset smoke check."""
from __future__ import annotations

import sys
from pathlib import Path

from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from brain_service.api import create_app
from scripts.neural_dynamics import default_data_dir, load_config, select_subgraph


def main() -> None:
    with TestClient(create_app()) as client:
        ready = client.get("/health/ready"); ready.raise_for_status()
        session = client.post("/api/v1/brain/sessions", json={"execution_mode": "reference"}); session.raise_for_status()
        session_id = session.json()["session_id"]
        info = client.get("/api/v1/brain/info").json()
        reference = select_subgraph(default_data_dir(), load_config())
        input_root = int(reference.root_ids[reference.hop == 0][0])
        root = int(client.get(f"/api/v1/brain/sessions/{session_id}").json()["neuron_count"])
        # A bounded no-stimulus step is the real control; no root is required.
        first = client.post(f"/api/v1/brain/sessions/{session_id}/step", json={"steps": 1}); first.raise_for_status()
        client.post(f"/api/v1/brain/sessions/{session_id}/reset").raise_for_status()
        replay = client.post(f"/api/v1/brain/sessions/{session_id}/step", json={"steps": 1}); replay.raise_for_status()
        if first.json()["total_spikes"] != replay.json()["total_spikes"]:
            raise SystemExit("Deterministic reset/replay check failed.")
        driven = client.post(f"/api/v1/brain/sessions/{session_id}/step", json={"steps": 5, "stimulus": [{"root_id": input_root, "amplitude": 2.0}]}); driven.raise_for_status()
        client.delete(f"/api/v1/brain/sessions/{session_id}").raise_for_status()
        print({"ready": ready.json()["canonical_neuron_count"], "reference_neurons": root, "dataset": info["source_biology"]["dataset"], "validated_input_root": input_root, "silent_control_spikes": first.json()["total_spikes"], "bounded_driven_spikes": driven.json()["total_spikes"]})


if __name__ == "__main__":
    main()
