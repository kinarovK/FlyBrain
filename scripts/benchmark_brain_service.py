"""Measure local Phase 6 adapter overhead separately from neural engine time."""
from __future__ import annotations

import sys
import time
from pathlib import Path

from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from brain_service.api import create_app
from scripts.whole_brain_scaling import _peak_rss_bytes


def main() -> None:
    process_before = _peak_rss_bytes()
    with TestClient(create_app()) as client:
        ready = client.get("/health/ready"); ready.raise_for_status()
        create_started = time.perf_counter(); created = client.post("/api/v1/brain/sessions", json={"execution_mode": "reference"}); created.raise_for_status()
        create_seconds = time.perf_counter() - create_started; session_id = created.json()["session_id"]
        step_started = time.perf_counter(); stepped = client.post(f"/api/v1/brain/sessions/{session_id}/step", json={"steps": 10}); stepped.raise_for_status()
        request_seconds = time.perf_counter() - step_started; payload = stepped.json()
        reset_started = time.perf_counter(); client.post(f"/api/v1/brain/sessions/{session_id}/reset").raise_for_status(); reset_seconds = time.perf_counter() - reset_started
        client.delete(f"/api/v1/brain/sessions/{session_id}").raise_for_status()
        print({"startup_timings": ready.json()["startup_timings"], "session_create_seconds": create_seconds, "step_request_seconds": request_seconds, "engine_seconds": payload["engine_wall_seconds"], "api_overhead_seconds": max(0.0, request_seconds - payload["engine_wall_seconds"]), "reset_seconds": reset_seconds, "peak_rss_bytes": _peak_rss_bytes(), "peak_rss_delta_from_before_bytes": None if process_before is None or _peak_rss_bytes() is None else _peak_rss_bytes() - process_before, "phase5_whole_peak_rss_bytes": 1032822784})


if __name__ == "__main__":
    main()
