from __future__ import annotations

import json
import sys
from dataclasses import replace
from pathlib import Path

import numpy as np
import pytest
import scipy.sparse as sp
from fastapi.testclient import TestClient

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

import scripts.brain_positions as positions_module
from brain_service.api import create_app
from brain_service.config import ServiceConfig, load_service_config
from brain_service.errors import ServiceError
from brain_service.layout import LayoutProvider
from brain_service.runtime import PreparedDomain
from scripts.brain_positions import PositionsError, build, load, positions_dir, sha256, validate
from scripts.flywire_data import ANNOTATIONS, DATASET_NAME, file_md5
from scripts.neural_dynamics import load_config

ROOTS = np.array([101, 102, 103, 104, 105, 106], dtype=np.int64)
HEADER = "supervoxel_id\troot_id\tpos_x\tpos_y\tpos_z\tsoma_x\tsoma_y\tsoma_z\tsuper_class\tcell_class\tcell_type\tside"
# Voxel coordinates spanning ~800 x 400 x 280 um at 4 x 4 x 40 nm (a plausible fly brain box).
ROWS = [
    (101, (25_000, 12_500, 1_000), None, ("central", "", "KCg-m", "left")),
    (102, (225_000, 112_500, 8_000), (225_004, 112_504, 8_001), ("optic", "ME", "T4c", "right")),
    (103, (100_000, 50_000, 2_500), (100_000, 50_000, 2_500), ("sensory", "olfactory", "ORN_DA1", "left")),
    (104, (125_000, 62_500, 5_000), (125_000, 62_500, 5_000), ("central", "ALPN", "DA1_lPN", "center")),
    (999, (1, 1, 1), None, ("central", "", "", "na")),  # annotation row whose root is not in the graph
]


def write_annotations(path: Path, rows=ROWS) -> None:
    lines = [HEADER]
    for root, pos, soma, classes in rows:
        soma_text = ["", "", ""] if soma is None else [str(value) for value in soma]
        lines.append("\t".join(["0", str(root), *(f"{value}.0" for value in pos), *soma_text, *classes]))
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def fixture_data(tmp_path: Path) -> Path:
    data = tmp_path / "flywire"
    graph = data / "derived" / "flytrader-sparse-v1"; graph.mkdir(parents=True)
    np.save(graph / "root_ids.npy", ROOTS)
    # Edges by canonical index: 101->102, 104->105, 105->101. Root 106 is isolated.
    np.save(graph / "csr_indptr.npy", np.array([0, 1, 1, 1, 2, 3, 3], dtype=np.uint64))
    np.save(graph / "csr_indices.npy", np.array([1, 4, 0], dtype=np.uint32))
    (graph / "metadata.json").write_text(json.dumps({"format": "flytrader-sparse-v1", "dataset": DATASET_NAME}), encoding="utf-8")
    write_annotations(data / ANNOTATIONS)
    return data


def test_export_round_trip_units_checksums_and_missing_annotations(tmp_path: Path):
    data = fixture_data(tmp_path)
    metadata = build(data)
    positions = load(data, expected_root_ids=ROOTS)

    assert np.array_equal(positions.root_ids, ROOTS)
    assert positions.xyz_um.dtype == np.float32 and positions.xyz_um.shape == (6, 3)
    np.testing.assert_array_equal(positions.xyz_um[2], np.array([400.0, 200.0, 100.0], dtype=np.float32))
    assert positions.has_position.tolist() == [True, True, True, True, False, False]
    # 105 has neighbours 101 (out) and 104 (in); 106 has none and falls back to the centroid.
    np.testing.assert_allclose(positions.xyz_um[4], (positions.xyz_um[0].astype(np.float64) + positions.xyz_um[3]) / 2, rtol=1e-6)
    np.testing.assert_allclose(positions.xyz_um[5], positions.xyz_um[:4].astype(np.float64).mean(axis=0), rtol=1e-6)
    listed = {entry["root_id"]: entry for entry in metadata["roots_without_annotation"]}
    assert listed[105]["placeholder_method"] == "mean_of_annotated_neighbours" and listed[105]["annotated_neighbour_count"] == 2
    assert listed[106]["placeholder_method"] == "whole_brain_centroid"
    assert metadata["annotation_roots_not_in_graph"] == [999]
    assert metadata["counts"] == {"canonical_roots": 6, "with_position": 4, "without_annotation": 2, "with_soma": 3, "annotation_rows_not_in_graph": 1}
    assert metadata["source_annotations"]["sha256"] == sha256(data / ANNOTATIONS)
    assert metadata["coordinate_unit"] == "um" and metadata["voxel_size_nm"] == [4.0, 4.0, 40.0]
    assert metadata["voxel_size_verification"]["documentation"]["url"].startswith("https://github.com/flyconnectome/flywire_annotations")
    for filename, record in metadata["files"].items():
        assert sha256(positions_dir(data) / filename) == record["sha256"]

    soma = np.load(positions_dir(data) / "soma_xyz_um.npy")
    assert np.isnan(soma[0]).all() and np.isnan(soma[4:]).all()
    np.testing.assert_allclose(soma[1], [900.016, 450.016, 320.04], rtol=1e-6)
    assert positions.class_value("cell_class", 0) is None and positions.class_value("cell_type", 0) == "KCg-m"
    assert positions.class_value("side", 3) == "center" and positions.class_value("super_class", 4) is None
    assert validate(data)["without_annotation"] == 2


def test_units_and_bounding_box_are_plausible(tmp_path: Path):
    data = fixture_data(tmp_path)
    box = build(data)["bounding_box_um"]
    assert box["extent"] == pytest.approx({"x": 800.0, "y": 400.0, "z": 280.0}, rel=1e-6)
    assert box["min"]["x"] == pytest.approx(100.0) and box["max"]["z"] == pytest.approx(320.0)


def test_wrong_voxel_size_is_rejected_by_the_bounding_box_check(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    data = fixture_data(tmp_path)
    monkeypatch.setattr(positions_module, "VOXEL_SIZE_NM", (4.0, 4.0, 4.0))
    with pytest.raises(PositionsError, match="implausible"):
        build(data)
    assert not positions_dir(data).exists()


def test_replace_is_explicit_and_rebuild_is_deterministic(tmp_path: Path):
    data = fixture_data(tmp_path)
    build(data)
    first = {path.name: sha256(path) for path in positions_dir(data).glob("*.npy")}
    with pytest.raises(PositionsError, match="--replace"):
        build(data)
    build(data, replace=True)
    assert first == {path.name: sha256(path) for path in positions_dir(data).glob("*.npy")}


def test_tampered_or_stale_positions_are_rejected(tmp_path: Path):
    data = fixture_data(tmp_path)
    build(data)
    with pytest.raises(PositionsError, match="do not match"):
        load(data, expected_root_ids=ROOTS[:-1])
    write_annotations(data / ANNOTATIONS, ROWS[:-1])
    with pytest.raises(PositionsError, match="Annotation table changed"):
        validate(data)
    path = positions_dir(data) / "xyz_um.npy"
    values = np.load(path); values[0, 0] += 1.0; np.save(path, values)
    with pytest.raises(PositionsError, match="checksum"):
        load(data)


def test_manifest_checksum_and_malformed_rows_are_rejected(tmp_path: Path):
    data = fixture_data(tmp_path)
    (data / "manifest.json").write_text(json.dumps({"files": {ANNOTATIONS.as_posix(): {"md5": "0" * 32}}}), encoding="utf-8")
    with pytest.raises(PositionsError, match="manifest"):
        build(data)
    (data / "manifest.json").write_text(json.dumps({"files": {ANNOTATIONS.as_posix(): {"md5": file_md5(data / ANNOTATIONS)}}}), encoding="utf-8")
    assert build(data)["source_annotations"]["manifest_md5_verified"] is True
    partial = list(ROWS); partial[0] = (101, (25_000, 12_500, 1_000), None, ("central", "", "", "left"))
    write_annotations(data / ANNOTATIONS, partial)
    text = (data / ANNOTATIONS).read_text(encoding="utf-8").replace("\t25000.0\t", "\t\t", 1)
    (data / ANNOTATIONS).write_text(text, encoding="utf-8")
    (data / "manifest.json").unlink()
    with pytest.raises(PositionsError, match="Partially missing"):
        build(data, replace=True)


class LayoutFixtureRuntime:
    """Fixture runtime: a three-root domain in non-sorted order plus real layout loading."""

    def __init__(self, config: ServiceConfig, data_dir: Path) -> None:
        self.config, self.status = config, "starting"
        self.phase4_config = load_config()
        self.graph_metadata = {"dataset": DATASET_NAME, "format": "flytrader-sparse-v1", "source_statistics": {"retained_edges": 1, "retained_synapses": 1}, "csr_pair_count": 1}
        roots = np.array([104, 101, 105], dtype=np.int64)
        matrix = sp.csr_matrix([[0.0, 0.0, 10.0], [0.0, 0.0, 0.0], [10.0, 0.0, 0.0]])
        self._domain = PreparedDomain("reference", roots, {104: 0, 101: 1, 105: 2}, matrix, np.array([0], dtype=np.uint32), self.graph_metadata, 2, 2, 2, self.phase4_config)
        self._layout = LayoutProvider(data_dir, lambda: ROOTS)

    def start(self) -> None: self.status = "ready"
    def close(self) -> None: self.status = "stopping"
    def ready_info(self) -> dict: return {"status": self.status, "dataset": DATASET_NAME, "graph_format": "flytrader-sparse-v1", "canonical_neuron_count": 139255, "startup_timings": {}}
    def require_ready(self) -> None:
        if self.status != "ready": raise ServiceError("GRAPH_NOT_READY", "not ready", 503)
    def domain(self, mode: str, transmitter_mode: str = "unsigned") -> PreparedDomain:
        self.require_ready(); return replace(self._domain, mode=mode)
    def layout(self):
        self.require_ready(); return self._layout.positions()


def service(data_dir: Path, max_layout_page: int = 2) -> TestClient:
    settings = ServiceConfig("127.0.0.1", 8765, 3, 1, 10, 2, 100, 100.0, 1.0, "INFO", max_layout_page=max_layout_page)
    return TestClient(create_app(config=settings, runtime=LayoutFixtureRuntime(settings, data_dir)))


def test_layout_endpoint_pages_in_root_order_with_positions_and_classes(tmp_path: Path):
    data = fixture_data(tmp_path); build(data)
    with service(data) as api:
        session_id = api.post("/api/v1/brain/sessions", json={}).json()["session_id"]
        first = api.get(f"/api/v1/brain/sessions/{session_id}/layout?offset=0&limit=2").json()
        second = api.get(f"/api/v1/brain/sessions/{session_id}/layout?offset=2&limit=2").json()
        roots = api.get(f"/api/v1/brain/sessions/{session_id}/roots?offset=0&limit=3").json()["root_ids"]
        assert first["coordinate_unit"] == "um" and first["total_root_count"] == 3
        assert first["returned_count"] == 2 and second["returned_count"] == 1 and second["limit"] == 2
        entries = first["entries"] + second["entries"]
        assert [entry["root_id"] for entry in entries] == roots == [101, 104, 105]
        positions = load(data)
        for entry in entries:
            index = int(np.searchsorted(ROOTS, entry["root_id"]))
            assert [np.float32(entry[axis]) for axis in "xyz"] == positions.xyz_um[index].tolist()
        assert entries[0] == {**entries[0], "has_position": True, "super_class": "central", "cell_class": None, "cell_type": "KCg-m", "side": "left"}
        assert entries[2]["has_position"] is False and entries[2]["cell_type"] is None
        beyond = api.get(f"/api/v1/brain/sessions/{session_id}/layout?offset=3").json()
        assert beyond["entries"] == [] and beyond["total_root_count"] == 3 and beyond["limit"] == 2


def test_layout_endpoint_validates_limits_and_sessions(tmp_path: Path):
    data = fixture_data(tmp_path); build(data)
    with service(data) as api:
        session_id = api.post("/api/v1/brain/sessions", json={}).json()["session_id"]
        for query in ("limit=3", "limit=0", "offset=-1", "limit=x"):
            response = api.get(f"/api/v1/brain/sessions/{session_id}/layout?{query}")
            assert response.status_code == 422 and response.json()["error"]["code"] == "INVALID_CONFIGURATION", query
        missing = api.get("/api/v1/brain/sessions/missing/layout")
        assert missing.status_code == 404 and missing.json()["error"]["code"] == "SESSION_NOT_FOUND"
        schema = api.get("/openapi.json").json()
        route = schema["paths"]["/api/v1/brain/sessions/{session_id}/layout"]["get"]
        assert route["responses"]["200"]["content"]["application/json"]["schema"]["$ref"].endswith("/SessionLayoutResponse")
        assert route["responses"]["503"]["content"]["application/json"]["schema"]["$ref"].endswith("/ErrorEnvelope")
        limit = next(parameter for parameter in route["parameters"] if parameter["name"] == "limit")["schema"]
        assert limit["maximum"] == 2 and limit["minimum"] == 1
        entry = schema["components"]["schemas"]["LayoutEntry"]["properties"]
        assert {"root_id", "x", "y", "z", "has_position", "super_class", "cell_class", "cell_type", "side"} == set(entry)


def test_missing_positions_return_layout_not_ready_without_affecting_readiness(tmp_path: Path):
    data = fixture_data(tmp_path)
    with service(data) as api:
        session_id = api.post("/api/v1/brain/sessions", json={}).json()["session_id"]
        response = api.get(f"/api/v1/brain/sessions/{session_id}/layout")
        assert response.status_code == 503 and response.json()["error"]["code"] == "LAYOUT_NOT_READY"
        assert "Traceback" not in response.text and str(tmp_path) not in response.text
        assert api.get("/health/ready").status_code == 200
        assert api.post(f"/api/v1/brain/sessions/{session_id}/step", json={"steps": 1}).status_code == 200
        build(data)  # failures are not cached: positions exported later are picked up
        assert api.get(f"/api/v1/brain/sessions/{session_id}/layout").status_code == 200


def test_step_and_reset_results_are_unchanged_by_layout_requests(tmp_path: Path):
    data = fixture_data(tmp_path); build(data)
    request = {"steps": 5, "stimulus": [{"root_id": 104, "amplitude": 100.0}], "max_returned_events": 100}
    with service(data) as api:
        plain = api.post("/api/v1/brain/sessions", json={}).json()["session_id"]
        viewed = api.post("/api/v1/brain/sessions", json={}).json()["session_id"]
        baseline = api.post(f"/api/v1/brain/sessions/{plain}/step", json=request).json()
        baseline_state = api.get(f"/api/v1/brain/sessions/{plain}/state").json()
        assert api.get(f"/api/v1/brain/sessions/{viewed}/layout").status_code == 200
        observed = api.post(f"/api/v1/brain/sessions/{viewed}/step", json=request).json()
        assert api.get(f"/api/v1/brain/sessions/{viewed}/layout").status_code == 200
        observed_state = api.get(f"/api/v1/brain/sessions/{viewed}/state").json()
        assert api.post(f"/api/v1/brain/sessions/{viewed}/reset").status_code == 200
        assert api.get(f"/api/v1/brain/sessions/{viewed}/layout").status_code == 200
        replay = api.post(f"/api/v1/brain/sessions/{viewed}/step", json=request).json()
        ignore = {"session_id", "engine_wall_seconds"}
        assert baseline["total_spikes"] > 0
        for result in (observed, replay):
            assert {k: v for k, v in result.items() if k not in ignore} == {k: v for k, v in baseline.items() if k not in ignore}
        assert {k: v for k, v in observed_state.items() if k != "session_id"} == {k: v for k, v in baseline_state.items() if k != "session_id"}


def test_default_service_config_bounds_layout_pages():
    assert load_service_config().max_layout_page == 4096
