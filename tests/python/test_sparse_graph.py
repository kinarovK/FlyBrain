from __future__ import annotations
import json, sys
from pathlib import Path
import numpy as np
import pyarrow as pa
import pyarrow.feather as feather
import pytest

ROOT = Path(__file__).resolve().parents[2]; sys.path.insert(0, str(ROOT / "scripts"))
from flywire_data import ANNOTATIONS, ARTIFACTS, CONNECTIONS, DATASET_NAME, ROOT_IDS, file_md5
from sparse_graph import SparseGraphError, build, graph_dir, sha256, validate

def refresh_hash(data: Path, filename: str):
    meta_path=graph_dir(data)/"metadata.json"; meta=json.loads(meta_path.read_text()); path=graph_dir(data)/filename; meta["files"][filename]["sha256"]=sha256(path); meta_path.write_text(json.dumps(meta))

def fixture_source(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Path:
    data = tmp_path / "flywire"; (data / "annotations").mkdir(parents=True)
    roots = np.arange(720575940000000000, 720575940000139255, dtype=np.int64); np.save(data / ROOT_IDS, roots)
    ann = data / ANNOTATIONS; ann.write_text("root_id\n" + "\n".join(map(str, roots[:3])) + "\n", encoding="utf-8")
    table = pa.table({"pre_pt_root_id": [int(roots[0]), int(roots[1]), 99], "post_pt_root_id": [int(roots[1]), int(roots[0]), int(roots[0])], "neuropil": ["B", "A", "A"], "syn_count": [2, 3, 5], "gaba_avg": [0.1,0.2,0.3], "ach_avg": [0.2,0.3,0.4], "glut_avg": [0.3,0.4,0.5], "oct_avg": [0.,0.,0.], "ser_avg": [0.,0.,0.], "da_avg": [0.,0.,0.]})
    feather.write_feather(table, data / CONNECTIONS)
    monkeypatch.setitem(ARTIFACTS[ROOT_IDS], "md5", file_md5(data / ROOT_IDS)); monkeypatch.setitem(ARTIFACTS[CONNECTIONS], "md5", file_md5(data / CONNECTIONS))
    files = {ROOT_IDS: {"url": ARTIFACTS[ROOT_IDS]["url"], "md5": file_md5(data / ROOT_IDS)}, CONNECTIONS: {"url": ARTIFACTS[CONNECTIONS]["url"], "md5": file_md5(data / CONNECTIONS)}, str(ANNOTATIONS): {"url": "https://raw.githubusercontent.com/flyconnectome/flywire_annotations/" + "a"*40 + "/supplemental_files/Supplemental_file1_neuron_annotations.tsv", "md5": file_md5(ann)}}
    (data / "manifest.json").write_text(json.dumps({"dataset": DATASET_NAME, "materialization":783,"profile":"compact","annotation_commit":"a"*40,"files":files}),encoding="utf-8"); return data

def test_induced_graph_mapping_weights_and_round_trip(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    data=fixture_source(tmp_path,monkeypatch); meta=build(data); result=validate(data,samples=2)
    ids=np.load(graph_dir(data)/"root_ids.npy"); weights=np.load(graph_dir(data)/"edge_syn_count.npy")
    assert ids.size==139255 and np.array_equal(ids, np.sort(ids)) and weights.tolist()==[2,3]
    assert meta["source_statistics"]["affected_edges"]==1 and result["retained_synapses"]==5

def test_malformed_derived_weight_is_rejected(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    data=fixture_source(tmp_path,monkeypatch); build(data); path=graph_dir(data)/"edge_syn_count.npy"; weights=np.load(path); weights[0]=0; np.save(path,weights)
    with pytest.raises(SparseGraphError): validate(data)

def test_rebuild_is_deterministic(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    data=fixture_source(tmp_path,monkeypatch); build(data); first={p.name:sha256(p) for p in graph_dir(data).glob("*.npy")}; build(data,replace=True); assert first=={p.name:sha256(p) for p in graph_dir(data).glob("*.npy")}

@pytest.mark.parametrize("filename,dtype", [("edge_pre_index.npy",np.int64),("edge_transmitter_avg.npy",np.float64),("csr_indices.npy",np.int64)])
def test_wrong_derived_dtype_is_rejected(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, filename: str, dtype):
    data=fixture_source(tmp_path,monkeypatch); build(data); path=graph_dir(data)/filename; np.save(path,np.load(path).astype(dtype)); refresh_hash(data,filename)
    with pytest.raises(SparseGraphError,match="Invalid dtype"): validate(data)

def test_invalid_index_and_csr_mismatch_are_rejected(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    data=fixture_source(tmp_path,monkeypatch); build(data); path=graph_dir(data)/"edge_pre_index.npy"; values=np.load(path); values[0]=139255; np.save(path,values); refresh_hash(data,"edge_pre_index.npy")
    with pytest.raises(SparseGraphError,match="Invalid edge index"): validate(data)
