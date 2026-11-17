from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

import numpy as np
import pyarrow as pa
import pyarrow.feather as feather
import pytest

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "scripts"
sys.path.insert(0, str(SCRIPTS))
# Child scripts print non-ASCII text; pin both ends to UTF-8 so the result does not depend on the Windows code page.
_UTF8_ENV = {**os.environ, "PYTHONIOENCODING": "utf-8"}

from flywire_data import (  # noqa: E402
    ANNOTATIONS,
    CONNECTIONS,
    ROOT_IDS,
    ConnectomeError,
    DATASET_NAME,
    ARTIFACTS,
    file_md5,
    validate_real_connectome,
)


def write_fixture(data_dir: Path) -> Path:
    data_dir.mkdir(parents=True)
    roots = np.array([101, 102, 103], dtype=np.int64)
    np.save(data_dir / ROOT_IDS, roots)
    annotation = data_dir / ANNOTATIONS
    annotation.parent.mkdir()
    annotation.write_text(
        "root_id\tcell_type\tsuper_class\tnt_type\n"
        "101\tsensory example\tsensory\tach\n"
        "102\tmushroom body example\tintrinsic\tgaba\n"
        "103\tdescending dopamine example\tdescending\tda\n",
        encoding="utf-8",
    )
    table = pa.table(
        {
            "pre_pt_root_id": pa.array([101, 102], type=pa.int64()),
            "post_pt_root_id": pa.array([102, 103], type=pa.int64()),
            "neuropil": ["AL_R", "MB_CA_R"],
            "syn_count": pa.array([4, 7], type=pa.int64()),
            "gaba_avg": [0.1, 0.2],
            "ach_avg": [0.7, 0.1],
            "glut_avg": [0.1, 0.2],
            "oct_avg": [0.0, 0.0],
            "ser_avg": [0.0, 0.0],
            "da_avg": [0.1, 0.5],
        }
    )
    feather.write_feather(table, data_dir / CONNECTIONS)
    manifest = {
        "dataset": DATASET_NAME,
        "materialization": 783,
        "profile": "compact",
        "files": {
            ROOT_IDS: {"md5": file_md5(data_dir / ROOT_IDS)},
            CONNECTIONS: {"md5": file_md5(data_dir / CONNECTIONS)},
            str(ANNOTATIONS): {"md5": file_md5(annotation)},
        },
    }
    (data_dir / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    return data_dir


def test_real_validator_reports_fixture_statistics(tmp_path: Path) -> None:
    report = validate_real_connectome(write_fixture(tmp_path / "fixture"), check_checksums=False)

    assert report.dataset == DATASET_NAME
    assert report.neuron_count == 3
    assert report.connection_count == 2
    assert report.synapse_count == 11
    assert report.pre_endpoints_in_root_ids == 2
    assert report.post_endpoints_in_root_ids == 2
    assert report.neurotransmitter_columns == ["gaba_avg", "ach_avg", "glut_avg", "oct_avg", "ser_avg", "da_avg"]
    assert report.categories["nt_type"] == [("ach", 1), ("gaba", 1), ("da", 1)]
    assert report.population_candidates["sensory"] == ["sensory example"]
    assert report.connections[0]["syn_count"] == 4


def test_validator_rejects_missing_manifest(tmp_path: Path) -> None:
    with pytest.raises(ConnectomeError, match="Missing"):
        validate_real_connectome(tmp_path)


def test_validator_rejects_bad_checksum(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    data_dir = write_fixture(tmp_path / "fixture")
    manifest_path = data_dir / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["annotation_commit"] = "a" * 40
    manifest["files"][ROOT_IDS].update({"url": ARTIFACTS[ROOT_IDS]["url"], "md5": "0" * 32})
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    monkeypatch.setitem(ARTIFACTS[ROOT_IDS], "md5", "0" * 32)
    with pytest.raises(ConnectomeError, match="Checksum failure"):
        validate_real_connectome(data_dir)


def test_validator_rejects_missing_required_manifest_entry(tmp_path: Path) -> None:
    data_dir = write_fixture(tmp_path / "fixture")
    manifest_path = data_dir / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["annotation_commit"] = "a" * 40
    del manifest["files"][ROOT_IDS]
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.raises(ConnectomeError, match="omits required artifact metadata"):
        validate_real_connectome(data_dir)


def test_validator_reports_endpoints_after_first_ten_thousand_rows(tmp_path: Path) -> None:
    data_dir = write_fixture(tmp_path / "fixture")
    rows = 10_001
    post = np.full(rows, 102, dtype=np.int64)
    post[-1] = 999
    table = pa.table(
        {
            "pre_pt_root_id": pa.array(np.full(rows, 101, dtype=np.int64)),
            "post_pt_root_id": pa.array(post),
            "neuropil": ["AL_R"] * rows,
            "syn_count": pa.array(np.ones(rows, dtype=np.int64)),
        }
    )
    feather.write_feather(table, data_dir / CONNECTIONS)
    report = validate_real_connectome(data_dir, check_checksums=False)
    assert report.connection_count == rows
    assert report.pre_endpoints_in_root_ids == rows
    assert report.post_endpoints_in_root_ids == rows - 1


def test_mock_brain_requires_explicit_flag_and_is_not_flywire(tmp_path: Path) -> None:
    completed = subprocess.run(
        [sys.executable, str(SCRIPTS / "validate_connectome.py"), "--data-dir", str(tmp_path), "--mock-brain"],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        env=_UTF8_ENV,
    )
    assert completed.returncode == 0
    assert "MOCK — NOT FLYWIRE" in completed.stdout


def test_download_refuses_without_principles_acknowledgement(tmp_path: Path) -> None:
    completed = subprocess.run(
        [sys.executable, str(SCRIPTS / "download_connectome.py"), "--data-dir", str(tmp_path), "--dry-run"],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        env=_UTF8_ENV,
    )
    assert completed.returncode == 2
    assert "FlyWire principles" in completed.stderr
