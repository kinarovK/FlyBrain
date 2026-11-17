"""Phase 5 CPU scaling experiments over validated FlyWire v783 connectivity.

This module preserves Phase 4 LIF semantics. It adds deterministic tiering,
memory preflight, bounded event recording, and whole-graph reporting only.
"""
from __future__ import annotations

import csv
import ctypes
import json
import math
import os
import platform
import shutil
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import numpy as np
import scipy
import scipy.sparse as sp

try:  # Supports direct scripts and package imports by the service runtime.
    from .neural_dynamics import (
        FORMAT as PHASE4_FORMAT,
        NeuralDynamicsError,
        _annotation_rows,
        _load_graph,
        _rank_frontier,
        _row_weights,
        _transmitter_signs,
        default_config_path,
        default_data_dir,
        load_config,
        repository_root,
        select_subgraph,
        validate_config,
    )
except ImportError:  # pragma: no cover - direct script execution compatibility
    from neural_dynamics import (
    FORMAT as PHASE4_FORMAT,
    NeuralDynamicsError,
    _annotation_rows,
    _load_graph,
    _rank_frontier,
    _row_weights,
    _transmitter_signs,
    default_config_path,
    default_data_dir,
    load_config,
    repository_root,
    select_subgraph,
        validate_config,
    )

FORMAT = "flytrader-whole-brain-v1"
WHOLE_NODES = 139_255


@dataclass(frozen=True)
class WholeTier:
    name: str
    root_ids: np.ndarray
    full_indices: np.ndarray
    hop: np.ndarray
    input_local: np.ndarray
    matrix: sp.csr_matrix
    row_edge_count: int
    represented_synapses: int
    csr_pair_count: int
    p99_syn_count: float
    sign_counts: dict[str, int]
    graph_metadata: dict[str, Any]
    initialization_timings: dict[str, float] = field(default_factory=dict)


@dataclass(frozen=True)
class ResourceEstimate:
    required_bytes: int
    available_bytes: int | None
    budget_bytes: int | None
    matrix_bytes: int
    temporary_bytes: int
    event_bytes: int
    allowed: bool


@dataclass(frozen=True)
class WholeResult:
    status: str
    reason: str | None
    spike_steps: np.ndarray
    spike_neurons: np.ndarray
    events_truncated: bool
    spike_count_by_neuron: np.ndarray
    first_step_by_neuron: np.ndarray
    population_spikes: np.ndarray
    peak_simultaneous_fraction: float
    trace_indices: np.ndarray
    traces: np.ndarray
    final_membrane: np.ndarray
    total_steps: int
    timings: dict[str, float]


def default_whole_config_path() -> Path:
    return repository_root() / "brain" / "configs" / "whole-brain-v1.default.json"


def _read_json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise NeuralDynamicsError(f"Malformed whole-brain JSON: {path}") from exc
    if not isinstance(value, dict):
        raise NeuralDynamicsError(f"Expected JSON object: {path}")
    return value


def _merge(base: dict[str, Any], override: dict[str, Any], prefix: str = "") -> dict[str, Any]:
    result = dict(base)
    for key, value in override.items():
        if key not in base:
            raise NeuralDynamicsError(f"Unknown whole-brain configuration key: {prefix}{key}")
        if isinstance(base[key], dict):
            if not isinstance(value, dict):
                raise NeuralDynamicsError(f"Expected object for {prefix}{key}")
            result[key] = _merge(base[key], value, f"{prefix}{key}.")
        else:
            result[key] = value
    return result


def validate_whole_config(config: dict[str, Any]) -> None:
    if set(config) != {"format", "version", "tiers", "memory", "events", "analysis"} or config.get("format") != FORMAT or config.get("version") != 1:
        raise NeuralDynamicsError("Unsupported whole-brain configuration format/version.")
    tiers = config["tiers"]
    expected = {"reference": 912, "tier_5k": 5000, "tier_20k": 20000, "tier_50k": 50000, "whole": WHOLE_NODES}
    if tiers != expected:
        raise NeuralDynamicsError("Whole-brain tier targets must match the approved deterministic ladder.")
    fraction = config["memory"].get("available_memory_fraction")
    if not isinstance(fraction, (int, float)) or not 0 < float(fraction) <= 1:
        raise NeuralDynamicsError("memory.available_memory_fraction must be within (0, 1].")
    cap = config["events"].get("max_recorded_events")
    if not isinstance(cap, int) or cap < 1:
        raise NeuralDynamicsError("events.max_recorded_events must be a positive integer.")
    analysis = config["analysis"]
    if not isinstance(analysis.get("max_distance_hops"), int) or analysis["max_distance_hops"] < 0 or not isinstance(analysis.get("top_active_limit"), int) or analysis["top_active_limit"] < 1:
        raise NeuralDynamicsError("Invalid whole-brain analysis integer limit.")
    for key in ("broad_activation_fraction", "pathological_activation_fraction"):
        value = analysis.get(key)
        if not isinstance(value, (int, float)) or not 0 <= float(value) <= 1:
            raise NeuralDynamicsError(f"analysis.{key} must be within [0, 1].")
    if float(analysis["broad_activation_fraction"]) > float(analysis["pathological_activation_fraction"]):
        raise NeuralDynamicsError("Broad activation threshold cannot exceed pathological threshold.")
    for section in ("memory", "events", "analysis"):
        if not isinstance(config[section].get("justification"), str):
            raise NeuralDynamicsError(f"{section}.justification is required.")


def load_whole_config(path: Path | None = None, *, override: dict[str, Any] | None = None) -> dict[str, Any]:
    config = _read_json(default_whole_config_path())
    if path is not None and path.resolve() != default_whole_config_path().resolve():
        config = _merge(config, _read_json(path))
    if override:
        config = _merge(config, override)
    validate_whole_config(config)
    return config


def _input_indices(data_dir: Path, arrays: dict[str, np.ndarray], phase4_config: dict[str, Any]) -> tuple[np.ndarray, np.ndarray]:
    rows = _annotation_rows(data_dir, phase4_config["selection"]["query"])
    limit = phase4_config["selection"]["input_limit"]
    if len(rows) < limit:
        raise NeuralDynamicsError("ORN_DA1 query returned fewer inputs than the Phase 4 reference.")
    roots = np.array([int(row["root_id"]) for row in rows[:limit]], dtype=np.int64)
    indices = np.searchsorted(arrays["root_ids"], roots).astype(np.uint32)
    if np.any(indices >= arrays["root_ids"].size) or not np.array_equal(arrays["root_ids"][indices], roots):
        raise NeuralDynamicsError("A reference ORN_DA1 root is absent from Phase 3 graph mapping.")
    return roots, indices


def _distance_labels(frontier: np.ndarray, arrays: dict[str, np.ndarray], max_hops: int) -> np.ndarray:
    labels = np.full(arrays["root_ids"].size, -1, dtype=np.int16)
    labels[frontier] = 0
    current = frontier
    for distance in range(1, max_hops + 1):
        parts = [arrays["csr_indices"][arrays["csr_indptr"][int(node)]:arrays["csr_indptr"][int(node) + 1]] for node in current]
        if not parts:
            break
        candidates = np.unique(np.concatenate(parts))
        current = candidates[labels[candidates] < 0].astype(np.uint32, copy=False)
        if current.size == 0:
            break
        labels[current] = distance
    return labels


def _build_weighted_matrix(arrays: dict[str, np.ndarray], full_indices: np.ndarray, phase4_config: dict[str, Any], *, whole: bool) -> tuple[sp.csr_matrix, int, int, float, dict[str, int]]:
    pre, post, syn = arrays["edge_pre_index"], arrays["edge_post_index"], arrays["edge_syn_count"]
    if whole:
        local_pre, local_post, local_syn = pre, post, syn
        row_count = int(pre.size)
    else:
        local_map = np.full(arrays["root_ids"].size, -1, dtype=np.int32)
        local_map[full_indices] = np.arange(full_indices.size, dtype=np.int32)
        mask = (local_map[pre] >= 0) & (local_map[post] >= 0)
        local_pre, local_post, local_syn = local_map[pre[mask]], local_map[post[mask]], syn[mask]
        row_count = int(mask.sum())
    weights, p99 = _row_weights(np.asarray(local_syn), phase4_config)
    if phase4_config["transmitter"]["mode"] == "unsigned":
        signs, counts = np.ones(row_count, dtype=np.float64), {"unsigned": row_count, "gaba_negative": 0, "ach_positive": 0, "fallback": 0}
    else:
        values = arrays["edge_transmitter_avg"] if whole else arrays["edge_transmitter_avg"][mask]
        signs, counts = _transmitter_signs(np.asarray(values), phase4_config)
    matrix = sp.coo_matrix((weights * signs, (local_pre, local_post)), shape=(full_indices.size, full_indices.size), dtype=np.float64).tocsr()
    matrix.sum_duplicates()
    return matrix, row_count, int(np.sum(local_syn, dtype=np.int64)), p99, counts


def _estimate_counts(neurons: int, source_rows: int, csr_pairs: int, whole_config: dict[str, Any]) -> ResourceEstimate:
    matrix_bytes = int(csr_pairs * (8 + 4) + (neurons + 1) * 4)
    temporary = int(source_rows * (8 + 4 + 4) * 2 + neurons * (8 + 8 + 4 + 4 + 1 + 8))
    event_bytes = int(whole_config["events"]["max_recorded_events"]) * 8
    required = matrix_bytes + temporary + event_bytes
    available = _available_memory_bytes()
    budget = None if available is None else int(available * float(whole_config["memory"]["available_memory_fraction"]))
    return ResourceEstimate(required, available, budget, matrix_bytes, temporary, event_bytes, budget is None or required <= budget)


def select_tier(data_dir: Path, whole_config: dict[str, Any], phase4_config: dict[str, Any], tier_name: str, *, graph: tuple[dict[str, Any], dict[str, np.ndarray]] | None = None) -> WholeTier:
    validate_whole_config(whole_config); validate_config(phase4_config)
    if tier_name not in whole_config["tiers"]:
        raise NeuralDynamicsError(f"Unknown scaling tier: {tier_name}")
    if tier_name == "reference":
        started = time.perf_counter()
        reference = select_subgraph(data_dir, phase4_config, graph=graph)
        return WholeTier("reference", reference.root_ids, reference.full_indices, reference.hop.astype(np.int16), np.flatnonzero(reference.hop == 0).astype(np.uint32), reference.matrix, reference.row_edge_count, reference.represented_synapses, reference.csr_pair_count, reference.p99_syn_count, reference.sign_counts, reference.graph_metadata, {"phase4_reference_selection_and_matrix_seconds": time.perf_counter() - started})
    graph_started = time.perf_counter()
    metadata, arrays = _load_graph(data_dir, phase4_config) if graph is None else graph
    graph_seconds = time.perf_counter() - graph_started
    _, inputs = _input_indices(data_dir, arrays, phase4_config)
    selection_started = time.perf_counter()
    target = int(whole_config["tiers"][tier_name])
    if tier_name == "whole":
        full_indices = np.arange(WHOLE_NODES, dtype=np.uint32)
        if arrays["root_ids"].size != WHOLE_NODES:
            raise NeuralDynamicsError("Whole tier does not have exactly 139,255 canonical roots.")
        rows = int(metadata["source_statistics"]["retained_edges"]); synapses = int(metadata["source_statistics"]["retained_synapses"]); expected_pairs = int(metadata["csr_pair_count"])
        preflight = _estimate_counts(WHOLE_NODES, rows, expected_pairs, whole_config)
        if not preflight.allowed:
            hop = np.full(WHOLE_NODES, -1, dtype=np.int16); hop[inputs] = 0
            return WholeTier(tier_name, np.asarray(arrays["root_ids"], dtype=np.int64), full_indices, hop, inputs, sp.csr_matrix((WHOLE_NODES, WHOLE_NODES)), rows, synapses, expected_pairs, float("nan"), {"preflight": rows}, metadata, {"graph_load_seconds": graph_seconds, "selection_seconds": time.perf_counter() - selection_started, "matrix_build_seconds": 0.0})
        labels = _distance_labels(inputs, arrays, int(whole_config["analysis"]["max_distance_hops"]))
        hop = labels[full_indices]
        matrix_started = time.perf_counter()
        matrix, rows, synapses, p99, signs = _build_weighted_matrix(arrays, full_indices, phase4_config, whole=True)
    else:
        selected = set(map(int, inputs)); hops = {int(value): 0 for value in inputs}; frontier = inputs
        distance = 0
        while len(selected) < target:
            distance += 1
            frontier = _rank_frontier(frontier, selected, arrays, target - len(selected))
            if frontier.size == 0:
                raise NeuralDynamicsError(f"Real-connectome frontier exhausted before {tier_name} target.")
            for value in frontier:
                selected.add(int(value)); hops[int(value)] = distance
        full_indices = np.array(sorted(selected, key=lambda value: (hops[value], value)), dtype=np.uint32)
        hop = np.array([hops[int(value)] for value in full_indices], dtype=np.int16)
        rough = _estimate_counts(full_indices.size, int(metadata["source_statistics"]["retained_edges"]), int(metadata["csr_pair_count"]), whole_config)
        if not rough.allowed:
            return WholeTier(tier_name, np.asarray(arrays["root_ids"][full_indices], dtype=np.int64), full_indices, hop, np.flatnonzero(np.isin(full_indices, inputs)).astype(np.uint32), sp.csr_matrix((full_indices.size, full_indices.size)), 0, 0, 0, float("nan"), {"preflight": 0}, metadata, {"graph_load_seconds": graph_seconds, "selection_seconds": time.perf_counter() - selection_started, "matrix_build_seconds": 0.0})
        matrix_started = time.perf_counter()
        matrix, rows, synapses, p99, signs = _build_weighted_matrix(arrays, full_indices, phase4_config, whole=False)
    input_local = np.flatnonzero(np.isin(full_indices, inputs)).astype(np.uint32)
    if input_local.size != inputs.size:
        raise NeuralDynamicsError("Tier omitted a required ORN_DA1 input.")
    return WholeTier(tier_name, np.asarray(arrays["root_ids"][full_indices], dtype=np.int64), full_indices, hop, input_local, matrix, rows, synapses, int(matrix.nnz), p99, signs, metadata, {"graph_load_seconds": graph_seconds, "selection_seconds": matrix_started - selection_started, "matrix_build_seconds": time.perf_counter() - matrix_started})


def _available_memory_bytes() -> int | None:
    try:
        if os.name == "nt":
            class MemoryStatus(ctypes.Structure):
                _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong), ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong), ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong), ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong), ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]
            status = MemoryStatus(); status.dwLength = ctypes.sizeof(status)
            return int(status.ullAvailPhys) if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)) else None
    except Exception:
        return None
    return None


def estimate_resources(tier: WholeTier, whole_config: dict[str, Any]) -> ResourceEstimate:
    return _estimate_counts(tier.root_ids.size, tier.row_edge_count, tier.csr_pair_count, whole_config)


def _peak_rss_bytes() -> int | None:
    try:
        if os.name == "nt":
            class Counters(ctypes.Structure):
                # PROCESS_MEMORY_COUNTERS_EX.  The former layout omitted a
                # field, so Windows correctly rejected the structure and all
                # artifacts misleadingly reported the measurement unavailable.
                _fields_ = [("cb", ctypes.c_ulong), ("PageFaultCount", ctypes.c_ulong), ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t), ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t), ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t), ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t), ("PrivateUsage", ctypes.c_size_t)]
            counters = Counters(); counters.cb = ctypes.sizeof(counters)
            get_current_process = ctypes.windll.kernel32.GetCurrentProcess
            get_current_process.restype = ctypes.c_void_p
            get_memory_info = ctypes.windll.psapi.GetProcessMemoryInfo
            get_memory_info.argtypes = [ctypes.c_void_p, ctypes.POINTER(Counters), ctypes.c_ulong]
            get_memory_info.restype = ctypes.c_int
            ok = get_memory_info(get_current_process(), ctypes.byref(counters), counters.cb)
            return int(counters.PeakWorkingSetSize) if ok else None
    except Exception:
        return None
    return None


def run_tier(tier: WholeTier, phase4_config: dict[str, Any], whole_config: dict[str, Any]) -> WholeResult:
    estimate = estimate_resources(tier, whole_config)
    if not estimate.allowed or "preflight" in tier.sign_counts:
        return WholeResult("resource_preflight_failed", f"estimated {estimate.required_bytes} bytes exceeds budget {estimate.budget_bytes}", np.empty(0, dtype=np.uint32), np.empty(0, dtype=np.uint32), False, np.zeros(tier.root_ids.size, dtype=np.uint32), np.full(tier.root_ids.size, -1, dtype=np.int32), np.empty(0, dtype=np.uint32), 0.0, np.empty(0, dtype=np.uint32), np.empty((0, 0), dtype=np.float32), np.empty(0, dtype=np.float64), 0, {"peak_rss_bytes": _peak_rss_bytes()})
    lif, stimulus, safety = phase4_config["lif"], phase4_config["stimulus"], phase4_config["safety"]
    dt = float(lif["dt_ms"]); total_steps = int(round(sum(float(stimulus[name]) for name in ("baseline_ms", "duration_ms", "recovery_ms")) / dt))
    baseline = int(round(float(stimulus["baseline_ms"]) / dt)); duration = int(round(float(stimulus["duration_ms"]) / dt))
    n = tier.root_ids.size; membrane = np.full(n, float(lif["v_rest_mV"])); current = np.zeros(n); refractory = np.zeros(n, dtype=np.int32); previous = np.zeros(n)
    spike_counts = np.zeros(n, dtype=np.uint32); first_steps = np.full(n, -1, dtype=np.int32); population = np.zeros(total_steps, dtype=np.uint32)
    cap = int(whole_config["events"]["max_recorded_events"]); event_steps = np.empty(cap, dtype=np.uint32); event_neurons = np.empty(cap, dtype=np.uint32); event_cursor = 0; truncated = False
    trace_indices = tier.input_local[: min(8, tier.input_local.size)]; traces = np.empty((total_steps, trace_indices.size), dtype=np.float32)
    input_mask = np.zeros(n, dtype=bool); input_mask[tier.input_local] = True; decay = math.exp(-dt / float(lif["tau_syn_ms"])); refractory_steps = int(math.ceil(float(lif["refractory_ms"]) / dt))
    status, reason, consecutive, peak_fraction = "completed", None, 0, 0.0; propagation_seconds = lif_seconds = bookkeeping_seconds = event_recording_seconds = 0.0; started = time.perf_counter()
    for step in range(total_steps):
        propagation_started = time.perf_counter(); current *= decay; current += np.asarray(tier.matrix.T @ previous).reshape(-1) / dt; propagation_seconds += time.perf_counter() - propagation_started
        update_started = time.perf_counter(); external = float(stimulus["external_current_mV_per_ms"]) if baseline <= step < baseline + duration else 0.0; refractory_mask = refractory > 0; refractory[refractory_mask] -= 1; membrane[refractory_mask] = float(lif["v_reset_mV"]); active = ~refractory_mask; membrane[active] += dt * (-(membrane[active] - float(lif["v_rest_mV"])) / float(lif["tau_m_ms"]) + current[active] + external * input_mask[active]); candidate_peak = float(np.max(np.abs(membrane))); spikes = active & (membrane >= float(lif["v_threshold_mV"])); membrane[spikes] = float(lif["v_reset_mV"]); refractory[spikes] = refractory_steps; previous = spikes.astype(np.float64); lif_seconds += time.perf_counter() - update_started
        book_started = time.perf_counter(); indices = np.flatnonzero(spikes).astype(np.uint32); count = int(indices.size); population[step] = count; spike_counts += spikes; unseen = spikes & (first_steps < 0); first_steps[unseen] = step; peak_fraction = max(peak_fraction, count / max(n, 1)); available = cap - event_cursor; retained = min(available, count)
        event_started = time.perf_counter()
        if retained: event_steps[event_cursor:event_cursor + retained] = step; event_neurons[event_cursor:event_cursor + retained] = indices[:retained]; event_cursor += retained
        if retained < count: truncated = True
        event_recording_seconds += time.perf_counter() - event_started
        traces[step] = membrane[trace_indices]; bookkeeping_seconds += time.perf_counter() - book_started
        invalid = not (np.isfinite(membrane).all() and np.isfinite(current).all()) or candidate_peak > float(safety["max_abs_membrane_mV"]); simultaneous = count / max(n, 1) >= float(safety["max_simultaneous_spike_fraction"]); consecutive = consecutive + 1 if simultaneous else 0
        if invalid: status, reason = "numerical_failure", "non-finite or pre-reset out-of-range membrane state"
        elif consecutive >= int(safety["max_consecutive_synchronous_steps"]): status, reason = "runaway_activity", "repeated high simultaneous spike fraction"
        elif int(spike_counts.max(initial=0)) > int(safety["max_spikes_per_neuron"]): status, reason = "runaway_activity", "per-neuron spike limit exceeded"
        if status != "completed": total_steps = step + 1; population = population[:total_steps]; traces = traces[:total_steps]; break
    elapsed = time.perf_counter() - started
    peak_rss = _peak_rss_bytes()
    return WholeResult(status, reason, event_steps[:event_cursor].copy(), event_neurons[:event_cursor].copy(), truncated, spike_counts, first_steps, population, peak_fraction, trace_indices, traces, membrane, total_steps, {"simulation_wall_seconds": elapsed, "average_step_seconds": elapsed / max(total_steps, 1), "sparse_propagation_seconds": propagation_seconds, "lif_update_seconds": lif_seconds, "spike_bookkeeping_seconds": bookkeeping_seconds, "event_recording_seconds": event_recording_seconds, "peak_rss_bytes": peak_rss, "peak_rss_available": peak_rss is not None})


def _parameter_delta(phase4_config: dict[str, Any]) -> dict[str, Any]:
    baseline = load_config(default_config_path())
    changed: dict[str, Any] = {}
    def walk(left: Any, right: Any, path: str = "") -> None:
        if isinstance(left, dict) and isinstance(right, dict):
            for key in sorted(set(left) | set(right)):
                walk(left.get(key), right.get(key), f"{path}.{key}" if path else key)
        elif left != right:
            changed[path] = {"phase4_default": left, "resolved": right}
    walk(baseline, phase4_config)
    return changed


def summarize_tier(tier: WholeTier, result: WholeResult, phase4_config: dict[str, Any], whole_config: dict[str, Any], estimate: ResourceEstimate) -> dict[str, Any]:
    n = tier.root_ids.size; dt = float(phase4_config["lif"]["dt_ms"]); total_spikes = int(np.sum(result.spike_count_by_neuron, dtype=np.uint64)); active = result.spike_count_by_neuron > 0
    activation = float(active.mean()) if n else 0.0
    if result.status == "resource_preflight_failed": classification = "resource_preflight_failed"
    elif result.status == "runaway_activity": classification = "runaway_activity"
    elif result.status == "numerical_failure": classification = "numerical_failure"
    elif total_spikes == 0: classification = "silence"
    elif activation >= float(whole_config["analysis"]["pathological_activation_fraction"]): classification = "pathological_saturation"
    elif activation >= float(whole_config["analysis"]["broad_activation_fraction"]): classification = "broad_stable_propagation"
    else: classification = "successful_propagation"
    valid_first = result.first_step_by_neuron[result.first_step_by_neuron >= 0]
    elapsed = float(result.timings.get("simulation_wall_seconds", 0.0))
    rates = result.spike_count_by_neuron.astype(np.float64) / max(result.total_steps * dt / 1000.0, 1e-12)
    top_indices = np.argsort(-result.spike_count_by_neuron, kind="stable")[: int(whole_config["analysis"]["top_active_limit"])]
    return {"format": FORMAT, "status": result.status, "reason": result.reason, "classification": classification, "events_truncated": result.events_truncated, "total_recorded_events": int(result.spike_steps.size), "total_spikes": total_spikes, "unique_active_neurons": int(active.sum()), "activation_fraction": activation, "peak_simultaneous_spike_fraction": result.peak_simultaneous_fraction, "simulated_ms": result.total_steps * dt, "first_spike_ms": {"min": None if valid_first.size == 0 else float(valid_first.min() * dt), "median": None if valid_first.size == 0 else float(np.median(valid_first) * dt), "max": None if valid_first.size == 0 else float(valid_first.max() * dt)}, "firing_rate_hz": {"mean": float(rates.mean()) if rates.size else 0.0, "max": float(rates.max(initial=0.0))}, "tier": {"name": tier.name, "neurons": int(n), "source_edge_rows": tier.row_edge_count, "represented_synapses": tier.represented_synapses, "csr_pairs": tier.csr_pair_count, "p99_syn_count": tier.p99_syn_count, "sign_counts": tier.sign_counts}, "resource_estimate": estimate.__dict__, "performance": {**tier.initialization_timings, **result.timings, "simulated_ms_per_wall_second": result.total_steps * dt / max(elapsed, 1e-12)}, "top_active_local_indices": [int(value) for value in top_indices if result.spike_count_by_neuron[value] > 0], "parameter_delta_from_phase4": _parameter_delta(phase4_config)}


def _annotation_map(data_dir: Path) -> dict[int, dict[str, str]]:
    return {int(row["root_id"]): row for row in _annotation_rows(data_dir, {})}


def _write_csv(path: Path, fields: list[str], rows: list[dict[str, Any]]) -> None:
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields); writer.writeheader(); writer.writerows(rows)


def _plots(directory: Path, result: WholeResult, summary: dict[str, Any], phase4_config: dict[str, Any]) -> None:
    os.environ.setdefault("MPLCONFIGDIR", str(repository_root() / ".matplotlib-cache"))
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    dt = float(phase4_config["lif"]["dt_ms"]); times = np.arange(result.total_steps) * dt
    fig, ax = plt.subplots(figsize=(8, 3)); ax.plot(times, result.population_spikes); ax.set(xlabel="time (ms)", ylabel="spikes / step", title="Whole-tier activity"); fig.tight_layout(); fig.savefig(directory / "activity_over_time.png", dpi=140); plt.close(fig)
    fig, ax = plt.subplots(figsize=(7, 3)); ax.hist(result.spike_count_by_neuron[result.spike_count_by_neuron > 0], bins=40); ax.set(xlabel="spikes per active neuron", ylabel="count", title="Firing-count distribution"); fig.tight_layout(); fig.savefig(directory / "firing_rate_distribution.png", dpi=140); plt.close(fig)
    fig, ax = plt.subplots(figsize=(8, 3));
    if result.trace_indices.size:
        for column, index in enumerate(result.trace_indices): ax.plot(times, result.traces[:, column], label=str(int(index)))
        ax.legend(fontsize=6)
    else:
        ax.text(0.5, 0.5, "No simulation traces: preflight failed", ha="center", va="center", transform=ax.transAxes)
    ax.set(xlabel="time (ms)", ylabel="membrane potential (mV)", title="Input membrane traces"); fig.tight_layout(); fig.savefig(directory / "membrane_traces.png", dpi=140); plt.close(fig)


def _validate_event_aggregate_contract(spikes: Any, aggregates: Any, summary: dict[str, Any]) -> None:
    """Prove that compact events are an ordered prefix of exact aggregates."""
    steps, neurons = np.asarray(spikes["step"]), np.asarray(spikes["neuron_index"])
    population = np.asarray(aggregates["population_spikes"])
    counts, first = np.asarray(aggregates["spike_count_by_neuron"]), np.asarray(aggregates["first_step_by_neuron"])
    if steps.ndim != 1 or neurons.ndim != 1 or steps.shape != neurons.shape or population.ndim != 1:
        raise NeuralDynamicsError("Whole-brain compact event arrays are malformed.")
    if steps.size and (int(steps.max()) >= population.size or np.any(steps[1:] < steps[:-1]) or np.any(neurons[1:][steps[1:] == steps[:-1]] < neurons[:-1][steps[1:] == steps[:-1]])):
        raise NeuralDynamicsError("Whole-brain events are outside the run or not in deterministic prefix order.")
    retained_population = np.bincount(steps, minlength=population.size).astype(population.dtype, copy=False)
    retained_counts = np.bincount(neurons, minlength=counts.size).astype(counts.dtype, copy=False)
    if np.any(retained_population > population) or np.any(retained_counts > counts):
        raise NeuralDynamicsError("Whole-brain aggregates cannot contain fewer spikes than retained events.")
    retained_first = np.full(counts.size, -1, dtype=np.int64)
    for step, neuron in zip(steps, neurons, strict=True):
        if retained_first[int(neuron)] < 0:
            retained_first[int(neuron)] = int(step)
    present = retained_first >= 0
    if np.any(first[present] != retained_first[present]):
        raise NeuralDynamicsError("Whole-brain first-spike aggregate disagrees with retained event prefix.")
    truncated = bool(spikes["truncated"][0])
    if truncated != bool(summary.get("events_truncated")):
        raise NeuralDynamicsError("Whole-brain event truncation contract mismatch.")
    if not truncated and (not np.array_equal(retained_population, population) or not np.array_equal(retained_counts, counts) or int(steps.size) != int(summary.get("total_spikes", -1))):
        raise NeuralDynamicsError("Untruncated whole-brain events must exactly equal aggregate activity.")


def write_whole_brain_experiment(data_dir: Path, experiment_id: str, tier: WholeTier, result: WholeResult, phase4_config: dict[str, Any], whole_config: dict[str, Any]) -> Path:
    if not experiment_id or any(char not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_" for char in experiment_id):
        raise NeuralDynamicsError("experiment_id may contain only letters, digits, hyphen, and underscore.")
    parent = data_dir / "experiments" / "whole-brain-v1"; target = parent / experiment_id; staging = parent / f".{experiment_id}.staging"
    if target.exists() or staging.exists():
        raise NeuralDynamicsError(f"Whole-brain experiment already exists: {target}")
    parent.mkdir(parents=True, exist_ok=True); staging.mkdir()
    try:
        artifact_started = time.perf_counter()
        estimate = estimate_resources(tier, whole_config); summary = summarize_tier(tier, result, phase4_config, whole_config, estimate)
        (staging / "phase4_config.json").write_text(json.dumps(phase4_config, indent=2) + "\n", encoding="utf-8")
        (staging / "whole_brain_config.json").write_text(json.dumps(whole_config, indent=2) + "\n", encoding="utf-8")
        np.save(staging / "selected_neurons.npy", tier.root_ids); np.save(staging / "selected_hops.npy", tier.hop)
        np.savez_compressed(staging / "spikes.npz", step=result.spike_steps, neuron_index=result.spike_neurons, truncated=np.array([result.events_truncated], dtype=bool))
        np.savez_compressed(staging / "aggregate_activity.npz", spike_count_by_neuron=result.spike_count_by_neuron, first_step_by_neuron=result.first_step_by_neuron, population_spikes=result.population_spikes)
        annotations = _annotation_map(data_dir); selected_rows = [{"local_index": index, "root_id": int(root), "hop": int(tier.hop[index]), "cell_type": annotations.get(int(root), {}).get("cell_type", ""), "super_class": annotations.get(int(root), {}).get("super_class", "")} for index, root in enumerate(tier.root_ids)]
        _write_csv(staging / "selected_neurons.csv", ["local_index", "root_id", "hop", "cell_type", "super_class"], selected_rows)
        _write_csv(staging / "population_activity.csv", ["step", "time_ms", "spikes"], [{"step": step, "time_ms": step * float(phase4_config["lif"]["dt_ms"]), "spikes": int(value)} for step, value in enumerate(result.population_spikes)])
        top_rows = []
        for index in summary["top_active_local_indices"]:
            root = int(tier.root_ids[index]); annotation = annotations.get(root, {})
            top_rows.append({"root_id": root, "local_index": index, "hop": int(tier.hop[index]), "cell_type": annotation.get("cell_type", ""), "super_class": annotation.get("super_class", ""), "spike_count": int(result.spike_count_by_neuron[index]), "first_spike_ms": None if result.first_step_by_neuron[index] < 0 else float(result.first_step_by_neuron[index] * phase4_config["lif"]["dt_ms"])})
        _write_csv(staging / "top_active_neurons.csv", ["root_id", "local_index", "hop", "cell_type", "super_class", "spike_count", "first_spike_ms"], top_rows)
        serialization_seconds = time.perf_counter() - artifact_started
        diagnostics_started = time.perf_counter()
        _plots(staging, result, summary, phase4_config)
        summary["performance"].update({"artifact_serialization_seconds": serialization_seconds, "diagnostic_plot_seconds": time.perf_counter() - diagnostics_started, "artifact_write_wall_seconds": time.perf_counter() - artifact_started})
        (staging / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
        metadata = {"format": FORMAT, "experiment_id": experiment_id, "tier": tier.name, "dataset": tier.graph_metadata["dataset"], "source_manifest_sha256": tier.graph_metadata["source_manifest_sha256"], "phase3_graph_format": tier.graph_metadata["format"], "phase3_graph_policy": tier.graph_metadata["policy"], "canonical_whole_graph": tier.name == "whole", "input_root_ids": [int(tier.root_ids[index]) for index in tier.input_local], "backend": "numpy-scipy-cpu", "seed": phase4_config["seed"], "status": result.status, "events_truncated": result.events_truncated, "platform": {"python": sys.version, "platform": platform.platform(), "numpy": np.__version__, "scipy": scipy.__version__}, "note": "Simplified LIF scaling experiment; FlyWire supplies structural data, not membrane dynamics or calibrated weights."}
        (staging / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
        staging.rename(target)
    except Exception:
        shutil.rmtree(staging, ignore_errors=True); raise
    return target


def validate_whole_brain_experiment(path: Path, data_dir: Path | None = None) -> dict[str, Any]:
    data_dir = default_data_dir() if data_dir is None else data_dir
    required = {"metadata.json", "phase4_config.json", "whole_brain_config.json", "selected_neurons.npy", "selected_hops.npy", "selected_neurons.csv", "spikes.npz", "aggregate_activity.npz", "population_activity.csv", "top_active_neurons.csv", "summary.json", "activity_over_time.png", "firing_rate_distribution.png", "membrane_traces.png"}
    missing = sorted(name for name in required if not (path / name).is_file())
    if missing: raise NeuralDynamicsError(f"Incomplete whole-brain artifacts: {missing}")
    metadata, phase4, whole, summary = _read_json(path / "metadata.json"), _read_json(path / "phase4_config.json"), _read_json(path / "whole_brain_config.json"), _read_json(path / "summary.json")
    validate_config(phase4); validate_whole_config(whole)
    manifest = data_dir / "manifest.json"
    if metadata.get("format") != FORMAT or metadata.get("dataset") != "FlyWire FAFB v783" or not manifest.is_file() or metadata.get("source_manifest_sha256") != __import__("sparse_graph").sha256(manifest):
        raise NeuralDynamicsError("Whole-brain experiment provenance mismatch.")
    roots, hops = np.load(path / "selected_neurons.npy", allow_pickle=False), np.load(path / "selected_hops.npy", allow_pickle=False); spikes, aggregates = np.load(path / "spikes.npz", allow_pickle=False), np.load(path / "aggregate_activity.npz", allow_pickle=False)
    if roots.dtype != np.int64 or roots.ndim != 1 or hops.shape != roots.shape or aggregates["spike_count_by_neuron"].shape != roots.shape or aggregates["first_step_by_neuron"].shape != roots.shape or spikes["step"].shape != spikes["neuron_index"].shape:
        raise NeuralDynamicsError("Whole-brain array shape/dtype mismatch.")
    if spikes["neuron_index"].size and int(spikes["neuron_index"].max()) >= roots.size: raise NeuralDynamicsError("Whole-brain event references an absent neuron.")
    if metadata.get("tier") != summary.get("tier", {}).get("name") or metadata.get("status") != summary.get("status") or bool(metadata.get("events_truncated")) != bool(summary.get("events_truncated")) or int(summary.get("tier", {}).get("neurons", -1)) != roots.size or int(summary.get("total_spikes", -1)) != int(np.sum(aggregates["spike_count_by_neuron"], dtype=np.uint64)):
        raise NeuralDynamicsError("Whole-brain summary/aggregate mismatch.")
    _validate_event_aggregate_contract(spikes, aggregates, summary)
    if metadata.get("tier") == "whole":
        graph_metadata = _read_json(data_dir / "derived" / "flytrader-sparse-v1" / "metadata.json")
        if roots.size != WHOLE_NODES or not np.array_equal(roots, np.load(data_dir / "derived" / "flytrader-sparse-v1" / "root_ids.npy", mmap_mode="r")) or int(summary["tier"]["source_edge_rows"]) != int(graph_metadata["source_statistics"]["retained_edges"]) or int(summary["tier"]["represented_synapses"]) != int(graph_metadata["source_statistics"]["retained_synapses"]) or int(summary["tier"]["csr_pairs"]) != int(graph_metadata["csr_pair_count"]) or metadata.get("phase3_graph_format") != graph_metadata.get("format") or metadata.get("phase3_graph_policy") != graph_metadata.get("policy"):
            raise NeuralDynamicsError("Whole tier does not match exact Phase 3 canonical graph identity.")
    if float(phase4["stimulus"]["external_current_mV_per_ms"]) == 0 and int(summary["total_spikes"]) != 0:
        raise NeuralDynamicsError("Zero-drive whole-brain control contains spikes.")
    return {"experiment_id": metadata.get("experiment_id"), "tier": metadata.get("tier"), "status": summary.get("status"), "neurons": int(roots.size), "total_spikes": int(summary.get("total_spikes", 0)), "events_truncated": bool(summary.get("events_truncated"))}
