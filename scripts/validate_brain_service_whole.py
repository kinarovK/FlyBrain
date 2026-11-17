"""Separately invoked heavy validation that the API can control the exact whole domain."""
from __future__ import annotations

import sys
from pathlib import Path

from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from brain_service.api import create_app
from scripts.whole_brain_scaling import _peak_rss_bytes


def main() -> None:
    baseline_rss = _peak_rss_bytes()
    with TestClient(create_app()) as client:
        client.get("/health/ready").raise_for_status()
        created = client.post("/api/v1/brain/sessions", json={"execution_mode": "whole"})
        created.raise_for_status(); payload = created.json()
        if payload["neuron_count"] != 139255:
            raise SystemExit("Whole service domain is not the canonical 139255-root graph.")
        session_id = payload["session_id"]
        control = client.post(f"/api/v1/brain/sessions/{session_id}/step", json={"steps": 1})
        control.raise_for_status()
        if control.json()["total_spikes"] != 0:
            raise SystemExit("Whole-domain zero-drive service control was not silent.")
        client.delete(f"/api/v1/brain/sessions/{session_id}").raise_for_status()
        peak_rss = _peak_rss_bytes()
        print({"whole_neurons": payload["neuron_count"], "zero_drive_spikes": control.json()["total_spikes"], "step_wall_seconds": control.json()["engine_wall_seconds"], "peak_rss_bytes": peak_rss, "session_memory_delta_bytes": None if baseline_rss is None or peak_rss is None else peak_rss - baseline_rss, "phase5_whole_peak_rss_bytes": 1032822784})


if __name__ == "__main__":
    main()
