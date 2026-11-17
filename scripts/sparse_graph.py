"""Deterministic, provenance-checked sparse FlyWire v783 graph tooling.

This module performs data transformation only; it contains no neural dynamics.
"""
from __future__ import annotations

import hashlib
import json
import os
import shutil
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import numpy as np
import scipy.sparse as sp

try:  # Supports direct scripts and package imports by the service runtime.
    from .flywire_data import (
        ANNOTATIONS, CONNECTIONS, DATASET_NAME, ROOT_IDS, TRANSMITTER_COLUMNS,
        ConnectomeError, default_data_dir, file_md5, load_manifest,
        verify_manifest_checksums,
    )
except ImportError:  # pragma: no cover - direct script execution compatibility
    from flywire_data import (
        ANNOTATIONS, CONNECTIONS, DATASET_NAME, ROOT_IDS, TRANSMITTER_COLUMNS,
        ConnectomeError, default_data_dir, file_md5, load_manifest,
        verify_manifest_checksums,
    )

FORMAT = "flytrader-sparse-v1"
POLICY = "induced_canonical_proofread_nodes"
SEED = 783
ARRAYS = ("root_ids", "edge_pre_index", "edge_post_index", "edge_syn_count", "edge_neuropil_code", "edge_transmitter_avg", "edge_source_row", "csr_indptr", "csr_indices", "csr_syn_count")
DTYPES = {"root_ids": np.dtype("int64"), "edge_pre_index": np.dtype("uint32"), "edge_post_index": np.dtype("uint32"), "edge_syn_count": np.dtype("uint32"), "edge_neuropil_code": np.dtype("uint16"), "edge_transmitter_avg": np.dtype("float32"), "csr_indptr": np.dtype("uint64"), "csr_indices": np.dtype("uint32"), "csr_syn_count": np.dtype("uint64")}


class SparseGraphError(RuntimeError):
    pass


def _arrow():
    try:
        import pyarrow.ipc as ipc
        return ipc
    except ImportError as exc:
        raise SparseGraphError("PyArrow is required; install brain/requirements.txt.") from exc


def _membership(values: np.ndarray, roots: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    positions = np.searchsorted(roots, values)
    valid = positions < roots.size
    return valid & (np.take(roots, positions, mode="clip") == values), positions


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def graph_dir(data_dir: Path) -> Path:
    return data_dir / "derived" / FORMAT


def _source(data_dir: Path):
    manifest = load_manifest(data_dir)
    profile = str(manifest.get("profile", "compact"))
    try:
        verify_manifest_checksums(data_dir, manifest, profile)
    except ConnectomeError as exc:
        raise SparseGraphError(str(exc)) from exc
    roots = np.load(data_dir / ROOT_IDS, allow_pickle=False)
    if roots.ndim != 1 or roots.size != 139_255 or not np.issubdtype(roots.dtype, np.integer):
        raise SparseGraphError("Expected exactly 139,255 canonical integer root IDs.")
    roots = np.sort(roots.astype(np.int64, copy=False))
    if np.unique(roots).size != roots.size:
        raise SparseGraphError("Canonical root IDs are not unique.")
    return manifest, roots


def source_statistics(data_dir: Path) -> dict[str, Any]:
    manifest, roots = _source(data_dir)
    ipc = _arrow()
    reader = ipc.open_file(data_dir / CONNECTIONS)
    required = {"pre_pt_root_id", "post_pt_root_id", "syn_count", "neuropil", *TRANSMITTER_COLUMNS}
    if missing := required.difference(reader.schema.names):
        raise SparseGraphError(f"Connectivity schema lacks {sorted(missing)}")
    totals = {"total_edge_rows": 0, "total_synapses": 0, "pre_external_endpoints": 0, "post_external_endpoints": 0, "affected_edges": 0, "affected_synapses": 0, "retained_edges": 0, "retained_synapses": 0}
    sources: set[int] = set(); targets: set[int] = set(); neuropils: set[str] = set()
    for batch_number in range(reader.num_record_batches):
        batch = reader.get_batch(batch_number); names = batch.schema.names
        pre = batch.column(names.index("pre_pt_root_id")).to_numpy(zero_copy_only=False)
        post = batch.column(names.index("post_pt_root_id")).to_numpy(zero_copy_only=False)
        weight = batch.column(names.index("syn_count")).to_numpy(zero_copy_only=False)
        if np.any(weight < 1): raise SparseGraphError(f"Non-positive syn_count in source batch {batch_number}")
        pre_ok, _ = _membership(pre, roots); post_ok, _ = _membership(post, roots); keep = pre_ok & post_ok
        totals["total_edge_rows"] += len(pre); totals["total_synapses"] += int(np.sum(weight, dtype=np.int64))
        totals["pre_external_endpoints"] += int(np.count_nonzero(~pre_ok)); totals["post_external_endpoints"] += int(np.count_nonzero(~post_ok))
        totals["affected_edges"] += int(np.count_nonzero(~keep)); totals["affected_synapses"] += int(np.sum(weight[~keep], dtype=np.int64))
        totals["retained_edges"] += int(np.count_nonzero(keep)); totals["retained_synapses"] += int(np.sum(weight[keep], dtype=np.int64))
        sources.update(map(int, np.unique(pre))); targets.update(map(int, np.unique(post)))
        neuropils.update(str(value) for value in batch.column(names.index("neuropil")).to_pylist())
    return {**totals, "unique_source_ids": len(sources), "unique_target_ids": len(targets), "neuropils": sorted(neuropils), "canonical_neurons": int(roots.size), "source_manifest_sha256": sha256(data_dir / "manifest.json"), "source_manifest": manifest}


def build(data_dir: Path, *, replace: bool = False) -> dict[str, Any]:
    started = time.perf_counter(); stats = source_statistics(data_dir); manifest, roots = _source(data_dir)
    target = graph_dir(data_dir); staging = target.with_name(target.name + ".staging")
    if target.exists() and not replace: raise SparseGraphError(f"Derived graph already exists: {target}; use --replace explicitly.")
    if staging.exists(): shutil.rmtree(staging)
    staging.mkdir(parents=True)
    count = stats["retained_edges"]; neuropils = stats.pop("neuropils"); neuropil_codes = {value: index for index, value in enumerate(neuropils)}
    if len(neuropils) > np.iinfo(np.uint16).max: raise SparseGraphError("Too many neuropils for uint16 codes.")
    np.save(staging / "root_ids.npy", roots)
    pre_out = np.lib.format.open_memmap(staging / "edge_pre_index.npy", mode="w+", dtype=np.uint32, shape=(count,))
    post_out = np.lib.format.open_memmap(staging / "edge_post_index.npy", mode="w+", dtype=np.uint32, shape=(count,))
    weight_out = np.lib.format.open_memmap(staging / "edge_syn_count.npy", mode="w+", dtype=np.uint32, shape=(count,))
    region_out = np.lib.format.open_memmap(staging / "edge_neuropil_code.npy", mode="w+", dtype=np.uint16, shape=(count,))
    transmitter_out = np.lib.format.open_memmap(staging / "edge_transmitter_avg.npy", mode="w+", dtype=np.float32, shape=(count, len(TRANSMITTER_COLUMNS)))
    ordinal_out = np.lib.format.open_memmap(staging / "edge_source_row.npy", mode="w+", dtype=np.uint32 if stats["total_edge_rows"] <= np.iinfo(np.uint32).max else np.uint64, shape=(count,))
    ipc = _arrow(); reader = ipc.open_file(data_dir / CONNECTIONS); cursor = 0; ordinal = 0
    for batch_number in range(reader.num_record_batches):
        batch = reader.get_batch(batch_number); names = batch.schema.names
        pre = batch.column(names.index("pre_pt_root_id")).to_numpy(zero_copy_only=False); post = batch.column(names.index("post_pt_root_id")).to_numpy(zero_copy_only=False)
        weight = batch.column(names.index("syn_count")).to_numpy(zero_copy_only=False); pre_ok, pre_idx = _membership(pre, roots); post_ok, post_idx = _membership(post, roots); keep = pre_ok & post_ok; length = int(np.count_nonzero(keep)); end = cursor + length
        pre_out[cursor:end] = pre_idx[keep]; post_out[cursor:end] = post_idx[keep]; weight_out[cursor:end] = weight[keep]
        region_out[cursor:end] = np.fromiter((neuropil_codes[str(v)] for v in batch.column(names.index("neuropil")).to_pylist() if True), dtype=np.uint16, count=len(pre))[keep]
        transmitter_out[cursor:end] = np.column_stack([batch.column(names.index(col)).to_numpy(zero_copy_only=False) for col in TRANSMITTER_COLUMNS])[keep].astype(np.float32)
        ordinal_out[cursor:end] = np.arange(ordinal, ordinal + len(pre), dtype=ordinal_out.dtype)[keep]
        cursor = end; ordinal += len(pre)
    del pre_out, post_out, weight_out, region_out, transmitter_out, ordinal_out
    if cursor != count: raise SparseGraphError(f"Retained-edge count drift: expected {count}, wrote {cursor}")
    pre = np.load(staging / "edge_pre_index.npy", mmap_mode="r"); post = np.load(staging / "edge_post_index.npy", mmap_mode="r"); weight = np.load(staging / "edge_syn_count.npy", mmap_mode="r")
    csr = sp.coo_matrix((weight.astype(np.uint64), (pre, post)), shape=(roots.size, roots.size), dtype=np.uint64).tocsr(); csr.sum_duplicates()
    np.save(staging / "csr_indptr.npy", csr.indptr.astype(np.uint64)); np.save(staging / "csr_indices.npy", csr.indices.astype(np.uint32)); np.save(staging / "csr_syn_count.npy", csr.data.astype(np.uint64))
    # Windows cannot atomically rename a directory containing still-mapped files.
    del pre, post, weight
    (staging / "neuropils.json").write_text(json.dumps(neuropils, indent=2) + "\n", encoding="utf-8")
    files = {path.name: {"bytes": path.stat().st_size, "sha256": sha256(path)} for path in staging.iterdir() if path.is_file()}
    metadata = {"format": FORMAT, "dataset": DATASET_NAME, "materialization": 783, "policy": POLICY, "seed": SEED, "source_manifest_sha256": sha256(data_dir / "manifest.json"), "source_files": manifest["files"], "transmitter_columns": list(TRANSMITTER_COLUMNS), "source_statistics": stats, "csr_pair_count": int(csr.nnz), "files": files, "preprocessing_seconds": time.perf_counter() - started}
    (staging / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    if target.exists(): shutil.rmtree(target)
    staging.rename(target)
    return metadata


def _load(directory: Path):
    metadata_path = directory / "metadata.json"
    if not metadata_path.is_file(): raise SparseGraphError(f"Missing derived metadata: {metadata_path}")
    metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    if metadata.get("format") != FORMAT or metadata.get("dataset") != DATASET_NAME or metadata.get("materialization") != 783 or metadata.get("policy") != POLICY: raise SparseGraphError("Derived graph identity/policy mismatch.")
    arrays = {name: np.load(directory / f"{name}.npy", mmap_mode="r") for name in ARRAYS}
    return metadata, arrays


def validate(data_dir: Path, *, samples: int = 64) -> dict[str, Any]:
    metadata, arrays = _load(graph_dir(data_dir)); manifest, roots = _source(data_dir)
    if metadata.get("source_manifest_sha256") != sha256(data_dir / "manifest.json"): raise SparseGraphError("Source manifest drift since preprocessing.")
    for filename, record in metadata.get("files", {}).items():
        path = graph_dir(data_dir) / filename
        if not path.is_file() or sha256(path) != record.get("sha256"): raise SparseGraphError(f"Derived artifact checksum failure: {filename}")
    ids = arrays["root_ids"]; n = roots.size
    for name, expected_dtype in DTYPES.items():
        if arrays[name].dtype != expected_dtype: raise SparseGraphError(f"Invalid dtype for {name}: {arrays[name].dtype}, expected {expected_dtype}.")
    if arrays["edge_source_row"].dtype not in {np.dtype("uint32"), np.dtype("uint64")}:
        raise SparseGraphError("edge_source_row must be uint32 or uint64.")
    if ids.shape != (n,) or not np.array_equal(ids, roots) or np.any(np.diff(ids) <= 0): raise SparseGraphError("Invalid canonical ID mapping.")
    e = arrays["edge_pre_index"].size
    if e != metadata["source_statistics"]["retained_edges"]: raise SparseGraphError("Retained edge count mismatch.")
    for name in ("edge_post_index", "edge_syn_count", "edge_neuropil_code", "edge_source_row"):
        if arrays[name].shape != (e,): raise SparseGraphError(f"Invalid shape for {name}.")
    if arrays["edge_transmitter_avg"].shape != (e, len(TRANSMITTER_COLUMNS)): raise SparseGraphError("Invalid transmitter array shape.")
    try: neuropils = json.loads((graph_dir(data_dir) / "neuropils.json").read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc: raise SparseGraphError(f"Malformed neuropil dictionary: {exc}") from exc
    if not isinstance(neuropils, list) or neuropils != sorted(set(neuropils)): raise SparseGraphError("Neuropil dictionary is not deterministic and unique.")
    if np.any(arrays["edge_pre_index"] >= n) or np.any(arrays["edge_post_index"] >= n) or np.any(arrays["edge_syn_count"] < 1) or np.any(arrays["edge_neuropil_code"] >= len(neuropils)): raise SparseGraphError("Invalid edge index, weight, or neuropil code.")
    if np.unique(arrays["edge_source_row"]).size != e: raise SparseGraphError("Derived graph contains duplicate source-row ordinals.")
    if int(np.sum(arrays["edge_syn_count"], dtype=np.int64)) != metadata["source_statistics"]["retained_synapses"]: raise SparseGraphError("Retained synapse total mismatch.")
    csr = sp.csr_matrix((arrays["csr_syn_count"], arrays["csr_indices"], arrays["csr_indptr"]), shape=(n, n)); expected = sp.coo_matrix((arrays["edge_syn_count"].astype(np.uint64), (arrays["edge_pre_index"], arrays["edge_post_index"])), shape=(n,n)).tocsr()
    if csr.nnz != expected.nnz or (csr != expected).nnz: raise SparseGraphError("CSR aggregate does not match row-level edges.")
    chosen = np.random.default_rng(int(metadata["seed"])).choice(e, size=min(samples, e), replace=False)
    wanted = {int(arrays["edge_source_row"][index]): int(index) for index in chosen}; found = 0; ordinal = 0; reader = _arrow().open_file(data_dir / CONNECTIONS)
    for batch_number in range(reader.num_record_batches):
        batch = reader.get_batch(batch_number); names = batch.schema.names; length = batch.num_rows
        for source_row, edge_index in tuple(wanted.items()):
            if ordinal <= source_row < ordinal + length:
                local = source_row - ordinal
                pre = int(batch.column(names.index("pre_pt_root_id"))[local].as_py()); post = int(batch.column(names.index("post_pt_root_id"))[local].as_py()); weight = int(batch.column(names.index("syn_count"))[local].as_py())
                if int(ids[arrays["edge_pre_index"][edge_index]]) != pre or int(ids[arrays["edge_post_index"][edge_index]]) != post or int(arrays["edge_syn_count"][edge_index]) != weight: raise SparseGraphError("Sampled source edge does not round-trip to derived graph.")
                derived_nt = arrays["edge_transmitter_avg"][edge_index]
                source_nt = np.array([batch.column(names.index(col))[local].as_py() for col in TRANSMITTER_COLUMNS], dtype=np.float32)
                if not np.allclose(derived_nt, source_nt, equal_nan=True): raise SparseGraphError("Sampled transmitter values do not round-trip.")
                if neuropils[int(arrays["edge_neuropil_code"][edge_index])] != batch.column(names.index("neuropil"))[local].as_py(): raise SparseGraphError("Sampled neuropil does not round-trip.")
                found += 1; del wanted[source_row]
        ordinal += length
        if not wanted: break
    if wanted: raise SparseGraphError("Sampled source-row ordinal is absent from connectivity source.")
    return {"neurons": n, "retained_edges": e, "retained_synapses": int(np.sum(arrays["edge_syn_count"], dtype=np.int64)), "csr_pairs": int(csr.nnz), "samples": found}


def benchmark(data_dir: Path, repeats: int = 5) -> dict[str, Any]:
    def rss_bytes():
        try:
            import ctypes
            class Counters(ctypes.Structure): _fields_=[("cb",ctypes.c_ulong),("PageFaultCount",ctypes.c_ulong),("PeakWorkingSetSize",ctypes.c_size_t),("WorkingSetSize",ctypes.c_size_t),("QuotaPeakPagedPoolUsage",ctypes.c_size_t),("QuotaPagedPoolUsage",ctypes.c_size_t),("QuotaPeakNonPagedPoolUsage",ctypes.c_size_t),("QuotaNonPagedPoolUsage",ctypes.c_size_t),("PagefileUsage",ctypes.c_size_t),("PeakPagefileUsage",ctypes.c_size_t)]
            counters=Counters(); counters.cb=ctypes.sizeof(counters); ok=ctypes.windll.psapi.GetProcessMemoryInfo(ctypes.windll.kernel32.GetCurrentProcess(),ctypes.byref(counters),counters.cb); return int(counters.WorkingSetSize) if ok and counters.WorkingSetSize else None
        except Exception: return None
    rss_before = rss_bytes(); started = time.perf_counter(); metadata, arrays = _load(graph_dir(data_dir)); load_seconds = time.perf_counter() - started; rss_after_load = rss_bytes(); n = arrays["root_ids"].size
    csr = sp.csr_matrix((arrays["csr_syn_count"], arrays["csr_indices"], arrays["csr_indptr"]), shape=(n, n)); activity = np.random.default_rng(SEED).random(n, dtype=np.float64); csr.T @ activity
    timings = []
    for _ in range(repeats):
        then = time.perf_counter(); result = csr.T @ activity; timings.append(time.perf_counter() - then)
    storage = sum(path.stat().st_size for path in graph_dir(data_dir).iterdir() if path.is_file())
    return {"derived_load_seconds": load_seconds, "preprocessing_seconds": metadata["preprocessing_seconds"], "storage_bytes": storage, "logical_array_bytes": int(sum(value.nbytes for value in arrays.values())), "rss_before_bytes": rss_before, "rss_after_mmap_load_bytes": rss_after_load, "rss_after_propagation_bytes": rss_bytes(), "neurons": n, "retained_edges": arrays["edge_pre_index"].size, "retained_synapses": int(np.sum(arrays["edge_syn_count"], dtype=np.int64)), "csr_pairs": int(csr.nnz), "propagation_seconds_min": min(timings), "propagation_seconds_mean": float(np.mean(timings)), "propagation_checksum": float(np.sum(result)), "note": "Sparse propagation is a mathematical operation, not neural activity."}
