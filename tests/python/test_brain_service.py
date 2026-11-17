from __future__ import annotations

import sys
from dataclasses import replace
from pathlib import Path

import numpy as np
import scipy.sparse as sp
from fastapi.testclient import TestClient
from pydantic import ValidationError

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

from brain_service.api import create_app
from brain_service.config import ServiceConfig
from brain_service.runtime import PreparedDomain
from brain_service.schemas import BatchReadoutRequest
from scripts.neural_dynamics import initialize_dynamic_state, load_config, advance_dynamic_state


class FixtureRuntime:
    def __init__(self, config: ServiceConfig) -> None:
        self.config, self.status = config, "starting"
        self.phase4_config = load_config()
        self.graph_metadata = {"dataset": "FlyWire FAFB v783", "format": "flytrader-sparse-v1", "source_statistics": {"retained_edges": 1, "retained_synapses": 1}, "csr_pair_count": 1}
        roots = np.array([1002, 1001], dtype=np.int64)
        self._domain = PreparedDomain("reference", roots, {1002: 0, 1001: 1}, sp.csr_matrix([[0.0, 10.0], [0.0, 0.0]]), np.array([0], dtype=np.uint32), self.graph_metadata, 1, 1, 1, self.phase4_config)

    def start(self) -> None: self.status = "ready"
    def close(self) -> None: self.status = "stopping"
    def ready_info(self) -> dict: return {"status": self.status, "dataset": "FlyWire FAFB v783", "graph_format": "flytrader-sparse-v1", "canonical_neuron_count": 139255, "startup_timings": {"startup_seconds": 0.0}}
    def require_ready(self) -> None:
        if self.status != "ready": raise RuntimeError("not ready")
    def domain(self, mode: str, transmitter_mode: str = "unsigned") -> PreparedDomain:
        self.require_ready()
        return replace(self._domain, mode=mode)


def config() -> ServiceConfig:
    return ServiceConfig("127.0.0.1", 8765, 2, 1, 10, 2, 2, 100.0, 1.0, "INFO")


def client() -> TestClient:
    settings = config()
    return TestClient(create_app(config=settings, runtime=FixtureRuntime(settings)))


def test_liveness_readiness_openapi_and_error_contract():
    with client() as api:
        assert api.get("/health/live").status_code == 200
        assert api.get("/health/ready").json()["canonical_neuron_count"] == 139255
        schema = api.get("/openapi.json").json()
        assert "/api/v1/brain/sessions" in schema["paths"]
        readout = schema["paths"]["/api/v1/brain/sessions/{session_id}/readout"]["post"]
        assert readout["requestBody"]["content"]["application/json"]["schema"]["$ref"].endswith("/BatchReadoutRequest")
        assert readout["responses"]["200"]["content"]["application/json"]["schema"]["$ref"].endswith("/BatchReadoutResponse")
        root_ids = schema["components"]["schemas"]["BatchReadoutRequest"]["properties"]["root_ids"]
        assert root_ids["minItems"] == 1 and root_ids["maxItems"] == 256
        for path, method in (("/api/v1/brain/sessions", "post"), ("/api/v1/brain/sessions/{session_id}", "delete")):
            response_schema = schema["paths"][path][method]["responses"]["422"]["content"]["application/json"]["schema"]
            assert response_schema["$ref"].endswith("/ErrorEnvelope")
        response = api.get("/api/v1/brain/sessions/missing")
        assert response.status_code == 404 and response.json()["error"]["code"] == "SESSION_NOT_FOUND"
        session_id = api.post("/api/v1/brain/sessions", json={}).json()["session_id"]
        invalid = api.post(f"/api/v1/brain/sessions/{session_id}/step", json={"steps": 0})
        assert invalid.status_code == 422 and invalid.json()["error"]["code"] == "INVALID_CONFIGURATION"


def test_create_step_reset_replay_diagnostic_and_limits():
    with client() as api:
        created = api.post("/api/v1/brain/sessions", json={"execution_mode": "reference"})
        assert created.status_code == 201; session_id = created.json()["session_id"]
        request = {"steps": 2, "stimulus": [{"root_id": 1001, "amplitude": 30.0}], "max_returned_events": 1}
        first = api.post(f"/api/v1/brain/sessions/{session_id}/step", json=request)
        assert first.status_code == 200 and first.json()["events_truncated"] in {True, False}
        assert api.get(f"/api/v1/brain/sessions/{session_id}/state").status_code == 200
        assert api.get(f"/api/v1/brain/sessions/{session_id}/neurons/1001").status_code == 200
        assert api.post(f"/api/v1/brain/sessions/{session_id}/reset").status_code == 200
        replay = api.post(f"/api/v1/brain/sessions/{session_id}/step", json=request)
        for key in ("total_spikes", "unique_spiking_neurons", "events", "ending_step"):
            assert replay.json()[key] == first.json()[key]
        assert api.post(f"/api/v1/brain/sessions/{session_id}/step", json={"steps": 11}).status_code == 422
        unknown = api.post(f"/api/v1/brain/sessions/{session_id}/step", json={"steps": 1, "stimulus": [{"root_id": 9999, "amplitude": 1.0}]})
        assert unknown.status_code == 404 and unknown.json()["error"]["code"] == "UNKNOWN_ROOT_ID"
        assert api.delete(f"/api/v1/brain/sessions/{session_id}").status_code == 204


def test_session_root_enumeration_is_paged_stable_and_read_only():
    with client() as api:
        created = api.post("/api/v1/brain/sessions", json={"execution_mode": "reference"})
        assert created.status_code == 201
        session_id = created.json()["session_id"]

        first = api.get(f"/api/v1/brain/sessions/{session_id}/roots?offset=0&limit=1")
        second = api.get(f"/api/v1/brain/sessions/{session_id}/roots?offset=1&limit=1")
        assert first.status_code == 200 and second.status_code == 200
        first_page, second_page = first.json(), second.json()
        assert first_page["total_root_count"] == created.json()["neuron_count"]
        assert first_page["returned_count"] == len(first_page["root_ids"]) == 1
        assert second_page["returned_count"] == len(second_page["root_ids"]) == 1
        assert first_page["root_ids"] == sorted(first_page["root_ids"])
        assert second_page["root_ids"] == sorted(second_page["root_ids"])
        assert set(first_page["root_ids"]).isdisjoint(second_page["root_ids"])
        assert api.get(f"/api/v1/brain/sessions/{session_id}/roots?offset=2&limit=1").json()["root_ids"] == []

        before = api.get(f"/api/v1/brain/sessions/{session_id}/state").json()
        for root_id in first_page["root_ids"] + second_page["root_ids"]:
            assert api.get(f"/api/v1/brain/sessions/{session_id}/neurons/{root_id}").status_code == 200
        assert api.post(f"/api/v1/brain/sessions/{session_id}/reset").status_code == 200
        after_reset = api.get(f"/api/v1/brain/sessions/{session_id}/roots?offset=0&limit=1").json()
        after = api.get(f"/api/v1/brain/sessions/{session_id}/state").json()
        assert after_reset["root_ids"] == first_page["root_ids"]
        assert before["simulation_step"] == after["simulation_step"] == 0
        assert before["total_accumulated_spikes"] == after["total_accumulated_spikes"] == 0


def test_session_root_enumeration_validates_queries_and_missing_sessions():
    with client() as api:
        session_id = api.post("/api/v1/brain/sessions", json={}).json()["session_id"]
        for query in ("offset=-1&limit=1", "offset=0&limit=0", "offset=0&limit=257"):
            response = api.get(f"/api/v1/brain/sessions/{session_id}/roots?{query}")
            assert response.status_code == 422 and response.json()["error"]["code"] == "INVALID_CONFIGURATION"
        missing = api.get("/api/v1/brain/sessions/missing/roots?offset=0&limit=1")
        assert missing.status_code == 404 and missing.json()["error"]["code"] == "SESSION_NOT_FOUND"


def test_batch_readout_preserves_request_order_is_read_only_and_matches_single_diagnostic_after_stimulation():
    with client() as api:
        session_id = api.post("/api/v1/brain/sessions", json={}).json()["session_id"]
        roots = [1001, 1002]
        before = api.get(f"/api/v1/brain/sessions/{session_id}/state").json()
        first = api.post(f"/api/v1/brain/sessions/{session_id}/readout", json={"root_ids": roots})
        second = api.post(f"/api/v1/brain/sessions/{session_id}/readout", json={"root_ids": roots})

        assert first.status_code == second.status_code == 200
        first_body = first.json()
        assert first_body == second.json()
        assert first_body["session_id"] == session_id
        assert first_body["simulation_step"] == before["simulation_step"] == 0
        assert first_body["returned_count"] == len(roots)
        assert [neuron["root_id"] for neuron in first_body["neurons"]] == roots
        assert all(set(neuron) == {"root_id", "membrane_mV", "refractory_steps", "cumulative_spike_count", "first_spike_step", "note"} for neuron in first_body["neurons"])
        assert api.get(f"/api/v1/brain/sessions/{session_id}/state").json() == before

        stepped = api.post(f"/api/v1/brain/sessions/{session_id}/step", json={"steps": 1, "stimulus": [{"root_id": 1001, "amplitude": 100.0}]}).json()
        after = api.post(f"/api/v1/brain/sessions/{session_id}/readout", json={"root_ids": roots})
        assert after.status_code == 200
        after_body = after.json()
        single = api.get(f"/api/v1/brain/sessions/{session_id}/neurons/1001").json()
        assert after_body["simulation_step"] == stepped["ending_step"]
        assert after_body["neurons"][0] == {key: single[key] for key in after_body["neurons"][0]}
        assert api.post(f"/api/v1/brain/sessions/{session_id}/readout", json={"root_ids": roots}).json() == after_body


def test_batch_readout_validates_unknown_duplicate_empty_bounds_and_missing_sessions():
    with client() as api:
        session_id = api.post("/api/v1/brain/sessions", json={}).json()["session_id"]
        for payload in ({}, {"root_ids": []}, {"root_ids": [1001, 1001]}):
            response = api.post(f"/api/v1/brain/sessions/{session_id}/readout", json=payload)
            assert response.status_code == 422 and response.json()["error"]["code"] == "INVALID_CONFIGURATION"

        unknown = api.post(f"/api/v1/brain/sessions/{session_id}/readout", json={"root_ids": [1001, 9999]})
        assert unknown.status_code == 404 and unknown.json()["error"] == {"code": "UNKNOWN_ROOT_ID", "message": "Readout root is not in this session domain.", "details": {"root_id": 9999}}
        missing = api.post("/api/v1/brain/sessions/missing/readout", json={"root_ids": [1001]})
        assert missing.status_code == 404 and missing.json()["error"]["code"] == "SESSION_NOT_FOUND"

    assert len(BatchReadoutRequest(root_ids=list(range(256))).root_ids) == 256
    try:
        BatchReadoutRequest(root_ids=list(range(257)))
    except ValidationError:
        pass
    else:
        raise AssertionError("257 readout roots must be rejected.")


def test_shared_direct_engine_step_equivalence():
    cfg = load_config(); matrix = sp.csr_matrix([[0.0, 10.0], [0.0, 0.0]])
    direct = initialize_dynamic_state(2, cfg)
    external = np.array([0.0, 30.0])
    expected_spikes = 0
    for _ in range(2):
        spikes, _ = advance_dynamic_state(direct, matrix, external, cfg)
        expected_spikes += int(spikes.sum())
    with client() as api:
        session_id = api.post("/api/v1/brain/sessions", json={}).json()["session_id"]
        response = api.post(f"/api/v1/brain/sessions/{session_id}/step", json={"steps": 2, "stimulus": [{"root_id": 1001, "amplitude": 30.0}]})
        assert response.status_code == 200
        assert response.json()["total_spikes"] == expected_spikes
        assert api.get(f"/api/v1/brain/sessions/{session_id}/neurons/1001").json()["membrane_mV"] == float(direct.membrane[1])


def test_vector_drive_survives_spike_and_refractory_cycle():
    with client() as api:
        session_id = api.post("/api/v1/brain/sessions", json={}).json()["session_id"]
        response = api.post(f"/api/v1/brain/sessions/{session_id}/step", json={"steps": 4, "stimulus": [{"root_id": 1001, "amplitude": 100.0}]})
        assert response.status_code == 200
        assert response.json()["total_spikes"] >= 1


def test_runaway_safety_maps_to_stable_error_code():
    with client() as api:
        runtime = api.app.state.runtime
        runtime._domain.phase4_config["safety"]["max_consecutive_synchronous_steps"] = 1
        session_id = api.post("/api/v1/brain/sessions", json={}).json()["session_id"]
        response = api.post(f"/api/v1/brain/sessions/{session_id}/step", json={"steps": 1, "stimulus": [{"root_id": 1001, "amplitude": 100.0}]})
        assert response.status_code == 409 and response.json()["error"]["code"] == "RUNAWAY_ACTIVITY"


def test_whole_session_limit_and_same_session_busy_contract():
    with client() as api:
        first = api.post("/api/v1/brain/sessions", json={"execution_mode": "whole"})
        assert first.status_code == 201
        assert api.post(f"/api/v1/brain/sessions/{first.json()['session_id']}/readout", json={"root_ids": [1002, 1001]}).status_code == 200
        assert api.post("/api/v1/brain/sessions", json={"execution_mode": "whole"}).status_code == 429
        manager = api.app.state.sessions; session = manager.get(first.json()["session_id"])
        assert session.lock.acquire(blocking=False)
        try:
            response = api.post(f"/api/v1/brain/sessions/{session.session_id}/step", json={"steps": 1})
            assert response.status_code == 409 and response.json()["error"]["code"] == "SESSION_BUSY"
            readout = api.post(f"/api/v1/brain/sessions/{session.session_id}/readout", json={"root_ids": [1001]})
            assert readout.status_code == 409 and readout.json()["error"]["code"] == "SESSION_BUSY"
        finally:
            session.lock.release()
