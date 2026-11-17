"""Reproducible Phase 4 LIF experiments over a bounded real FlyWire subgraph.

This module models dynamics; FlyWire supplies structural connectivity,
annotations, and transmitter prediction probabilities only.  It contains no
trading, behavioral interpretation, or external service integration.
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
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import numpy as np
import scipy.sparse as sp

try:  # Supports both ``python scripts/...`` and package imports by the service.
    from .sparse_graph import FORMAT as SPARSE_FORMAT, POLICY as SPARSE_POLICY, graph_dir, sha256
except ImportError:  # pragma: no cover - direct script execution compatibility
    from sparse_graph import FORMAT as SPARSE_FORMAT, POLICY as SPARSE_POLICY, graph_dir, sha256

FORMAT = "flytrader-neural-v1"
ANNOTATION_FIELDS = ("root_id", "flow", "super_class", "cell_class", "cell_sub_class", "supertype", "cell_type", "top_nt", "top_nt_conf", "side")
TRANSMITTERS = ("gaba", "ach", "glut", "oct", "ser", "da")


class NeuralDynamicsError(RuntimeError):
    """A configuration, provenance, or numerical experiment failure."""


@dataclass(frozen=True)
class SelectedSubgraph:
    root_ids: np.ndarray
    full_indices: np.ndarray
    hop: np.ndarray
    annotations: list[dict[str, str]]
    matrix: sp.csr_matrix
    row_edge_count: int
    represented_synapses: int
    csr_pair_count: int
    p99_syn_count: float
    sign_counts: dict[str, int]
    source_manifest_sha256: str
    graph_metadata: dict[str, Any]


@dataclass(frozen=True)
class SimulationResult:
    spike_steps: np.ndarray
    spike_neurons: np.ndarray
    status: str
    reason: str | None
    population_spikes: np.ndarray
    trace_indices: np.ndarray
    traces: np.ndarray
    final_membrane: np.ndarray
    total_steps: int
    timings: dict[str, float]


@dataclass
class DynamicState:
    """Mutable LIF state shared by batch experiments and the service adapter."""
    membrane: np.ndarray
    current: np.ndarray
    refractory: np.ndarray
    previous_spikes: np.ndarray
    step: int = 0


def initialize_dynamic_state(neuron_count: int, config: dict[str, Any]) -> DynamicState:
    """Create the accepted Phase 4 initial state without graph or HTTP policy."""
    validate_config(config)
    if neuron_count < 1:
        raise NeuralDynamicsError("Dynamic state requires at least one neuron.")
    return DynamicState(
        membrane=np.full(neuron_count, float(config["lif"]["v_rest_mV"]), dtype=np.float64),
        current=np.zeros(neuron_count, dtype=np.float64),
        refractory=np.zeros(neuron_count, dtype=np.int32),
        previous_spikes=np.zeros(neuron_count, dtype=np.float64),
    )


def repository_root() -> Path:
    return Path(__file__).resolve().parents[1]


def default_config_path() -> Path:
    return repository_root() / "brain" / "configs" / "neural-v1.default.json"


def default_data_dir() -> Path:
    return repository_root() / "data" / "flywire"


def _read_json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise NeuralDynamicsError(f"Malformed JSON configuration/artifact: {path}") from exc
    if not isinstance(value, dict):
        raise NeuralDynamicsError(f"Expected JSON object: {path}")
    return value


def _merge(base: dict[str, Any], override: dict[str, Any], path: str = "") -> dict[str, Any]:
    merged = dict(base)
    for key, value in override.items():
        if key not in base:
            raise NeuralDynamicsError(f"Unknown configuration key: {path}{key}")
        if isinstance(base[key], dict):
            if not isinstance(value, dict):
                raise NeuralDynamicsError(f"Expected object for configuration key: {path}{key}")
            merged[key] = _merge(base[key], value, f"{path}{key}.")
        else:
            merged[key] = value
    return merged


def _finite_number(value: Any, name: str, *, positive: bool = False, nonnegative: bool = False) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(float(value)):
        raise NeuralDynamicsError(f"{name} must be a finite number.")
    result = float(value)
    if positive and result <= 0:
        raise NeuralDynamicsError(f"{name} must be positive.")
    if nonnegative and result < 0:
        raise NeuralDynamicsError(f"{name} must be non-negative.")
    return result


def validate_config(config: dict[str, Any]) -> None:
    required = {"format", "version", "seed", "source", "selection", "lif", "weights", "transmitter", "stimulus", "safety", "output"}
    if set(config) != required or config.get("format") != FORMAT or config.get("version") != 1:
        raise NeuralDynamicsError("Unsupported neural configuration format/version.")
    if not isinstance(config["seed"], int) or isinstance(config["seed"], bool) or config["seed"] < 0:
        raise NeuralDynamicsError("seed must be a non-negative integer.")
    source = config["source"]
    if source.get("dataset") != "FlyWire FAFB v783" or source.get("graph_format") != SPARSE_FORMAT or source.get("graph_policy") != SPARSE_POLICY:
        raise NeuralDynamicsError("Configuration source identity must be FlyWire FAFB v783 Phase 3 graph.")
    selection = config["selection"]
    if not isinstance(selection.get("query"), dict) or not selection["query"] or not isinstance(selection.get("input_limit"), int) or selection["input_limit"] < 1:
        raise NeuralDynamicsError("Invalid selection query or input_limit.")
    caps = selection.get("hop_caps")
    if not isinstance(caps, list) or len(caps) != 3 or any(not isinstance(item, int) or item < 1 for item in caps):
        raise NeuralDynamicsError("hop_caps must contain three positive integers.")
    lif = config["lif"]
    for name in ("dt_ms", "tau_m_ms", "tau_syn_ms", "refractory_ms"):
        _finite_number(lif.get(name), f"lif.{name}", positive=True)
    for name in ("v_rest_mV", "v_reset_mV", "v_threshold_mV"):
        _finite_number(lif.get(name), f"lif.{name}")
    if lif["v_threshold_mV"] <= lif["v_rest_mV"]:
        raise NeuralDynamicsError("lif.v_threshold_mV must exceed lif.v_rest_mV.")
    if not isinstance(lif.get("justification"), dict) or set(lif["justification"]) != {"dt_ms", "v_rest_mV", "v_reset_mV", "v_threshold_mV", "tau_m_ms", "tau_syn_ms", "refractory_ms"}:
        raise NeuralDynamicsError("Each LIF default needs a justification.")
    weights = config["weights"]
    if weights.get("transform") not in {"linear", "log_saturating", "normalized_saturating"}:
        raise NeuralDynamicsError("Unsupported weights.transform.")
    _finite_number(weights.get("synaptic_scale_mV"), "weights.synaptic_scale_mV", positive=True)
    percentile = _finite_number(weights.get("percentile"), "weights.percentile", positive=True)
    if percentile > 100:
        raise NeuralDynamicsError("weights.percentile must be at most 100.")
    transmitter = config["transmitter"]
    if transmitter.get("mode") not in {"unsigned", "transmitter"}:
        raise NeuralDynamicsError("Unsupported transmitter.mode.")
    confidence = _finite_number(transmitter.get("confidence_threshold"), "transmitter.confidence_threshold", nonnegative=True)
    if confidence > 1:
        raise NeuralDynamicsError("transmitter.confidence_threshold must be within [0, 1].")
    if _finite_number(transmitter.get("fallback_sign"), "transmitter.fallback_sign") not in {-1.0, 1.0}:
        raise NeuralDynamicsError("transmitter.fallback_sign must be -1 or 1.")
    stimulus = config["stimulus"]
    for name in ("baseline_ms", "duration_ms", "recovery_ms"):
        _finite_number(stimulus.get(name), f"stimulus.{name}", nonnegative=True)
    _finite_number(stimulus.get("external_current_mV_per_ms"), "stimulus.external_current_mV_per_ms", nonnegative=True)
    if sum(float(stimulus[name]) for name in ("baseline_ms", "duration_ms", "recovery_ms")) <= 0:
        raise NeuralDynamicsError("Stimulus protocol cannot have zero total duration.")
    safety = config["safety"]
    fraction = _finite_number(safety.get("max_simultaneous_spike_fraction"), "safety.max_simultaneous_spike_fraction", positive=True)
    if fraction > 1:
        raise NeuralDynamicsError("safety.max_simultaneous_spike_fraction must be at most one.")
    for name in ("max_consecutive_synchronous_steps", "max_spikes_per_neuron"):
        if not isinstance(safety.get(name), int) or safety[name] < 1:
            raise NeuralDynamicsError(f"safety.{name} must be a positive integer.")
    _finite_number(safety.get("max_abs_membrane_mV"), "safety.max_abs_membrane_mV", positive=True)
    if not isinstance(config["output"].get("trace_neuron_limit"), int) or config["output"]["trace_neuron_limit"] < 1:
        raise NeuralDynamicsError("output.trace_neuron_limit must be a positive integer.")
    for section in ("selection", "weights", "transmitter", "stimulus", "safety", "output"):
        if not isinstance(config[section].get("justification"), (str, dict)):
            raise NeuralDynamicsError(f"{section}.justification is required.")


def load_config(path: Path | None = None, *, override: dict[str, Any] | None = None, sign_mode: str | None = None, no_stimulus: bool = False) -> dict[str, Any]:
    config = _read_json(default_config_path())
    if path is not None and path.resolve() != default_config_path().resolve():
        config = _merge(config, _read_json(path))
    if override:
        config = _merge(config, override)
    if sign_mode is not None:
        config["transmitter"]["mode"] = sign_mode
    if no_stimulus:
        config["stimulus"]["external_current_mV_per_ms"] = 0.0
    validate_config(config)
    return config


def _load_graph(data_dir: Path, config: dict[str, Any]) -> tuple[dict[str, Any], dict[str, np.ndarray]]:
    directory = graph_dir(data_dir)
    metadata = _read_json(directory / "metadata.json")
    if metadata.get("format") != config["source"]["graph_format"] or metadata.get("dataset") != config["source"]["dataset"] or metadata.get("materialization") != 783 or metadata.get("policy") != config["source"]["graph_policy"]:
        raise NeuralDynamicsError("Phase 3 graph identity/policy mismatch.")
    manifest = data_dir / "manifest.json"
    if not manifest.is_file() or metadata.get("source_manifest_sha256") != sha256(manifest):
        raise NeuralDynamicsError("Phase 3 graph source-manifest hash drift.")
    arrays = {name: np.load(directory / f"{name}.npy", mmap_mode="r") for name in ("root_ids", "edge_pre_index", "edge_post_index", "edge_syn_count", "edge_transmitter_avg", "csr_indptr", "csr_indices", "csr_syn_count")}
    if arrays["root_ids"].dtype != np.int64 or arrays["root_ids"].size != 139_255 or np.any(np.diff(arrays["root_ids"]) <= 0):
        raise NeuralDynamicsError("Invalid Phase 3 root-ID mapping.")
    return metadata, arrays


def _annotation_rows(data_dir: Path, query: dict[str, str]) -> list[dict[str, str]]:
    path = data_dir / "annotations" / "Supplemental_file1_neuron_annotations.tsv"
    try:
        with path.open(encoding="utf-8", newline="") as handle:
            reader = csv.DictReader(handle, delimiter="\t")
            if reader.fieldnames is None or not set(query).issubset(reader.fieldnames) or "root_id" not in reader.fieldnames:
                raise NeuralDynamicsError("Annotation TSV lacks required selection fields.")
            rows = [{field: str(row.get(field, "")) for field in ANNOTATION_FIELDS} for row in reader if all(str(row.get(key, "")) == str(value) for key, value in query.items())]
    except OSError as exc:
        raise NeuralDynamicsError(f"Missing annotation TSV: {path}") from exc
    rows.sort(key=lambda row: int(row["root_id"]))
    if not rows:
        raise NeuralDynamicsError("Annotation query selected no neurons.")
    return rows


def _rank_frontier(frontier: np.ndarray, selected: set[int], arrays: dict[str, np.ndarray], limit: int) -> np.ndarray:
    indptr, indices, weights = arrays["csr_indptr"], arrays["csr_indices"], arrays["csr_syn_count"]
    target_parts = [indices[indptr[int(node)]:indptr[int(node) + 1]] for node in frontier]
    weight_parts = [weights[indptr[int(node)]:indptr[int(node) + 1]] for node in frontier]
    if not target_parts:
        return np.empty(0, dtype=np.uint32)
    targets = np.concatenate(target_parts)
    strengths = np.concatenate(weight_parts)
    unseen = ~np.isin(targets, np.fromiter(selected, dtype=np.uint32, count=len(selected)), assume_unique=False)
    targets, strengths = targets[unseen], strengths[unseen]
    if targets.size == 0:
        return np.empty(0, dtype=np.uint32)
    unique, inverse = np.unique(targets, return_inverse=True)
    aggregate = np.zeros(unique.size, dtype=np.uint64)
    np.add.at(aggregate, inverse, strengths)
    order = np.lexsort((unique, -aggregate.astype(np.int64)))
    return unique[order[:limit]].astype(np.uint32, copy=False)


def _transmitter_signs(values: np.ndarray, config: dict[str, Any]) -> tuple[np.ndarray, dict[str, int]]:
    fallback = float(config["transmitter"]["fallback_sign"])
    signs = np.ones(values.shape[0], dtype=np.float64)
    counts = {"unsigned": int(values.shape[0]), "gaba_negative": 0, "ach_positive": 0, "fallback": 0}
    if config["transmitter"]["mode"] == "unsigned":
        return signs, counts
    signs.fill(fallback)
    threshold = float(config["transmitter"]["confidence_threshold"])
    finite = np.isfinite(values)
    safe = np.where(finite, values, -np.inf)
    argmax = safe.argmax(axis=1)
    maximum = safe.max(axis=1)
    unique_max = (safe == maximum[:, None]).sum(axis=1) == 1
    confident = finite.all(axis=1) & unique_max & (maximum >= threshold)
    gaba = confident & (argmax == 0)
    ach = confident & (argmax == 1)
    signs[gaba] = -1.0
    signs[ach] = 1.0
    counts = {"unsigned": 0, "gaba_negative": int(gaba.sum()), "ach_positive": int(ach.sum()), "fallback": int((~(gaba | ach)).sum())}
    return signs, counts


def _row_weights(syn_count: np.ndarray, config: dict[str, Any]) -> tuple[np.ndarray, float]:
    if np.any(syn_count < 1):
        raise NeuralDynamicsError("Selected subgraph contains non-positive structural synapse count.")
    scale = float(config["weights"]["synaptic_scale_mV"])
    transform = config["weights"]["transform"]
    if transform == "linear":
        return syn_count.astype(np.float64) * scale, float("nan")
    logged = np.log1p(syn_count.astype(np.float64))
    if transform == "log_saturating":
        return logged * scale, float("nan")
    p99 = float(np.percentile(syn_count, float(config["weights"]["percentile"])))
    if not math.isfinite(p99) or p99 <= 0:
        raise NeuralDynamicsError("Invalid selected subgraph synapse-count percentile.")
    return np.minimum(1.0, logged / math.log1p(p99)) * scale, p99


def select_subgraph(data_dir: Path, config: dict[str, Any], *, graph: tuple[dict[str, Any], dict[str, np.ndarray]] | None = None) -> SelectedSubgraph:
    validate_config(config)
    metadata, arrays = _load_graph(data_dir, config) if graph is None else graph
    rows = _annotation_rows(data_dir, config["selection"]["query"])
    input_rows = rows[: config["selection"]["input_limit"]]
    if len(input_rows) != config["selection"]["input_limit"]:
        raise NeuralDynamicsError("Annotation query returned fewer roots than input_limit.")
    roots = arrays["root_ids"]
    input_roots = np.array([int(row["root_id"]) for row in input_rows], dtype=np.int64)
    frontier = np.searchsorted(roots, input_roots).astype(np.uint32)
    if np.any(frontier >= roots.size) or not np.array_equal(roots[frontier], input_roots):
        raise NeuralDynamicsError("Selected annotation root is absent from validated graph mapping.")
    selected = set(map(int, frontier))
    hops: dict[int, int] = {int(index): 0 for index in frontier}
    for distance, cap in enumerate(config["selection"]["hop_caps"], start=1):
        frontier = _rank_frontier(frontier, selected, arrays, cap)
        if frontier.size == 0:
            raise NeuralDynamicsError(f"Downstream expansion ended before hop {distance}.")
        for index in frontier:
            selected.add(int(index)); hops[int(index)] = distance
    full_indices = np.array(sorted(selected, key=lambda value: (hops[value], value)), dtype=np.uint32)
    hop = np.array([hops[int(index)] for index in full_indices], dtype=np.uint8)
    local_by_full = np.full(roots.size, -1, dtype=np.int32)
    local_by_full[full_indices] = np.arange(full_indices.size, dtype=np.int32)
    pre, post, syn = arrays["edge_pre_index"], arrays["edge_post_index"], arrays["edge_syn_count"]
    mask = (local_by_full[pre] >= 0) & (local_by_full[post] >= 0)
    local_pre = local_by_full[pre[mask]]
    local_post = local_by_full[post[mask]]
    local_syn = syn[mask]
    row_weights, p99 = _row_weights(local_syn, config)
    signs, sign_counts = _transmitter_signs(np.asarray(arrays["edge_transmitter_avg"][mask]), config)
    matrix = sp.coo_matrix((row_weights * signs, (local_pre, local_post)), shape=(full_indices.size, full_indices.size), dtype=np.float64).tocsr()
    matrix.sum_duplicates()
    selected_annotations = {int(row["root_id"]): row for row in _annotation_rows(data_dir, {})}
    annotations = []
    for root in roots[full_indices]:
        annotations.append(selected_annotations.get(int(root), {"root_id": str(int(root))}))
    return SelectedSubgraph(
        root_ids=np.asarray(roots[full_indices], dtype=np.int64), full_indices=full_indices, hop=hop,
        annotations=annotations, matrix=matrix, row_edge_count=int(mask.sum()),
        represented_synapses=int(np.sum(local_syn, dtype=np.int64)), csr_pair_count=int(matrix.nnz),
        p99_syn_count=p99, sign_counts=sign_counts,
        source_manifest_sha256=str(metadata["source_manifest_sha256"]), graph_metadata=metadata,
    )


def _rss_bytes() -> int | None:
    try:
        if os.name == "nt":
            class Counters(ctypes.Structure):
                _fields_ = [("cb", ctypes.c_ulong), ("PageFaultCount", ctypes.c_ulong), ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t), ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t), ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t), ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t)]
            counters = Counters(); counters.cb = ctypes.sizeof(counters)
            ok = ctypes.windll.psapi.GetProcessMemoryInfo(ctypes.windll.kernel32.GetCurrentProcess(), ctypes.byref(counters), counters.cb)
            return int(counters.WorkingSetSize) if ok else None
    except Exception:
        return None
    return None


def lif_step(membrane: np.ndarray, current: np.ndarray, refractory: np.ndarray, previous_spikes: np.ndarray, matrix: sp.csr_matrix, external_mask: np.ndarray, external_current: float | np.ndarray, config: dict[str, Any]) -> tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray, float]:
    """Advance one vectorized LIF step and expose the pre-reset voltage peak."""
    lif = config["lif"]
    dt = float(lif["dt_ms"])
    next_current = current * math.exp(-dt / float(lif["tau_syn_ms"]))
    next_current += np.asarray(matrix.T @ previous_spikes).reshape(-1) / dt
    next_membrane = membrane.copy()
    next_refractory = refractory.copy()
    refractory_mask = next_refractory > 0
    next_refractory[refractory_mask] -= 1
    next_membrane[refractory_mask] = float(lif["v_reset_mV"])
    active = ~refractory_mask
    drive = external_current * external_mask[active] if np.isscalar(external_current) else np.asarray(external_current)[active] * external_mask[active]
    next_membrane[active] += dt * (-(next_membrane[active] - float(lif["v_rest_mV"])) / float(lif["tau_m_ms"]) + next_current[active] + drive)
    candidate_peak = float(np.max(np.abs(next_membrane)))
    spikes = active & (next_membrane >= float(lif["v_threshold_mV"]))
    next_membrane[spikes] = float(lif["v_reset_mV"])
    next_refractory[spikes] = int(math.ceil(float(lif["refractory_ms"]) / dt))
    return next_membrane, next_current, next_refractory, spikes, candidate_peak


def advance_dynamic_state(state: DynamicState, matrix: sp.csr_matrix, external_current: float | np.ndarray, config: dict[str, Any], *, external_mask: np.ndarray | None = None) -> tuple[np.ndarray, float]:
    """Advance exactly one accepted Phase 4 LIF step in-place.

    ``external_current`` is either the existing scalar/profile drive or a
    service-owned dense vector constructed from a validated sparse request.
    The sparse matrix update, delay, refractory ordering, and threshold/reset
    behavior remain solely in :func:`lif_step`.
    """
    mask = np.ones(state.membrane.size, dtype=bool) if external_mask is None else external_mask
    membrane, current, refractory, spikes, candidate_peak = lif_step(
        state.membrane, state.current, state.refractory, state.previous_spikes,
        matrix, mask, external_current, config,
    )
    state.membrane, state.current, state.refractory = membrane, current, refractory
    state.previous_spikes = spikes.astype(np.float64)
    state.step += 1
    return spikes, candidate_peak


def evaluate_safety(*, membrane: np.ndarray, current: np.ndarray, candidate_peak: float, spike_count: int, neuron_spike_counts: np.ndarray, consecutive_synchronous_steps: int, config: dict[str, Any]) -> tuple[str | None, str | None, int]:
    """Apply the accepted Phase 4 safety policy for one completed step."""
    safety = config["safety"]
    invalid = not (np.isfinite(membrane).all() and np.isfinite(current).all()) or candidate_peak > float(safety["max_abs_membrane_mV"])
    simultaneous = spike_count / max(membrane.size, 1) >= float(safety["max_simultaneous_spike_fraction"])
    consecutive = consecutive_synchronous_steps + 1 if simultaneous else 0
    if invalid:
        return "numerical_failure", "non-finite or pre-reset out-of-range state", consecutive
    if consecutive >= int(safety["max_consecutive_synchronous_steps"]):
        return "runaway_activity", "repeated high simultaneous spike fraction", consecutive
    if int(neuron_spike_counts.max(initial=0)) > int(safety["max_spikes_per_neuron"]):
        return "runaway_activity", "per-neuron spike limit exceeded", consecutive
    return None, None, consecutive


def simulate(subgraph: SelectedSubgraph, config: dict[str, Any]) -> SimulationResult:
    """Run vectorized LIF steps. A spike affects targets on the next step."""
    lif, stimulus, safety = config["lif"], config["stimulus"], config["safety"]
    dt = float(lif["dt_ms"])
    total_ms = sum(float(stimulus[key]) for key in ("baseline_ms", "duration_ms", "recovery_ms"))
    total_steps = int(round(total_ms / dt))
    baseline_steps = int(round(float(stimulus["baseline_ms"]) / dt))
    stimulus_steps = int(round(float(stimulus["duration_ms"]) / dt))
    n = subgraph.root_ids.size
    membrane = np.full(n, float(lif["v_rest_mV"]), dtype=np.float64)
    current = np.zeros(n, dtype=np.float64)
    refractory = np.zeros(n, dtype=np.int32)
    previous_spikes = np.zeros(n, dtype=np.float64)
    spike_counts = np.zeros(n, dtype=np.uint32)
    trace_indices = np.arange(min(n, int(config["output"]["trace_neuron_limit"])), dtype=np.uint32)
    traces = np.empty((total_steps, trace_indices.size), dtype=np.float32)
    population = np.zeros(total_steps, dtype=np.uint32)
    step_values: list[np.ndarray] = []
    neuron_values: list[np.ndarray] = []
    status, reason, consecutive = "completed", None, 0
    input_mask = subgraph.hop == 0
    propagation_seconds = 0.0
    started = time.perf_counter()
    for step in range(total_steps):
        propagation_started = time.perf_counter()
        external = float(stimulus["external_current_mV_per_ms"]) if baseline_steps <= step < baseline_steps + stimulus_steps else 0.0
        membrane, current, refractory, spikes, candidate_peak = lif_step(membrane, current, refractory, previous_spikes, subgraph.matrix, input_mask, external, config)
        propagation_seconds += time.perf_counter() - propagation_started
        previous_spikes = spikes.astype(np.float64)
        spike_counts += spikes
        indices = np.flatnonzero(spikes).astype(np.uint32)
        if indices.size:
            step_values.append(np.full(indices.size, step, dtype=np.uint32)); neuron_values.append(indices)
        population[step] = indices.size
        traces[step] = membrane[trace_indices]
        invalid = not (np.isfinite(membrane).all() and np.isfinite(current).all()) or candidate_peak > float(safety["max_abs_membrane_mV"])
        simultaneous = indices.size / max(n, 1) >= float(safety["max_simultaneous_spike_fraction"])
        consecutive = consecutive + 1 if simultaneous else 0
        if invalid:
            status, reason = "numerical_failure", "non-finite or out-of-range state"
        elif consecutive >= int(safety["max_consecutive_synchronous_steps"]):
            status, reason = "runaway_activity", "repeated high simultaneous spike fraction"
        elif int(spike_counts.max(initial=0)) > int(safety["max_spikes_per_neuron"]):
            status, reason = "runaway_activity", "per-neuron spike limit exceeded"
        if status != "completed":
            total_steps = step + 1; traces = traces[:total_steps]; population = population[:total_steps]
            break
    elapsed = time.perf_counter() - started
    return SimulationResult(
        spike_steps=np.concatenate(step_values) if step_values else np.empty(0, dtype=np.uint32),
        spike_neurons=np.concatenate(neuron_values) if neuron_values else np.empty(0, dtype=np.uint32),
        status=status, reason=reason, population_spikes=population, trace_indices=trace_indices, traces=traces,
        final_membrane=membrane, total_steps=total_steps,
        timings={"simulation_wall_seconds": elapsed, "average_step_seconds": elapsed / max(total_steps, 1), "propagation_seconds": propagation_seconds, "peak_rss_bytes": _rss_bytes()},
    )


def summarize(subgraph: SelectedSubgraph, result: SimulationResult, config: dict[str, Any]) -> dict[str, Any]:
    dt = float(config["lif"]["dt_ms"])
    onset_step = int(round(float(config["stimulus"]["baseline_ms"]) / dt))
    first = np.full(subgraph.root_ids.size, -1, dtype=np.int64)
    for step, neuron in zip(result.spike_steps, result.spike_neurons):
        if first[int(neuron)] < 0:
            first[int(neuron)] = int(step)
    hop_summary: list[dict[str, Any]] = []
    for distance in range(4):
        nodes = np.flatnonzero(subgraph.hop == distance)
        if nodes.size == 0:
            hop_summary.append({"hop": distance, "neurons": 0, "spikes": 0, "active_neurons": 0, "first_spike_latency_ms": None, "firing_rate_hz": 0.0, "membrane_final_min_mV": None, "membrane_final_mean_mV": None, "membrane_final_max_mV": None})
            continue
        mask = np.isin(result.spike_neurons, nodes)
        spike_count = int(mask.sum())
        active = first[nodes] >= 0
        after_onset = first[nodes][first[nodes] >= onset_step]
        hop_summary.append({"hop": distance, "neurons": int(nodes.size), "spikes": spike_count, "active_neurons": int(active.sum()), "first_spike_latency_ms": None if after_onset.size == 0 else float((after_onset.min() - onset_step) * dt), "firing_rate_hz": spike_count / max(nodes.size * result.total_steps * dt / 1000.0, 1e-12), "membrane_final_min_mV": float(result.final_membrane[nodes].min()), "membrane_final_mean_mV": float(result.final_membrane[nodes].mean()), "membrane_final_max_mV": float(result.final_membrane[nodes].max())})
    downstream = np.flatnonzero(subgraph.hop > 0)
    activated_downstream = downstream[first[downstream] >= onset_step]
    coo = subgraph.matrix.tocoo()
    witnessed: list[int] = []
    for target in activated_downstream:
        inbound = coo.col == target
        if np.any((first[coo.row[inbound]] >= onset_step) & (first[coo.row[inbound]] < first[target])):
            witnessed.append(int(target))
    propagation_demonstrated = bool(result.status == "completed" and activated_downstream.size and len(witnessed) == activated_downstream.size)
    return {"format": FORMAT, "status": result.status, "reason": result.reason, "propagation_demonstrated": propagation_demonstrated, "activated_downstream_neurons": int(activated_downstream.size), "propagation_witnessed_downstream_neurons": len(witnessed), "total_spikes": int(result.spike_steps.size), "simulated_ms": result.total_steps * dt, "silent": bool(result.spike_steps.size == 0), "subgraph": {"neurons": int(subgraph.root_ids.size), "source_edge_rows": subgraph.row_edge_count, "represented_synapses": subgraph.represented_synapses, "csr_pairs": subgraph.csr_pair_count, "p99_syn_count": subgraph.p99_syn_count, "sign_counts": subgraph.sign_counts}, "hop_activity": hop_summary, "performance": {**result.timings, "simulated_ms_per_wall_second": (result.total_steps * dt) / max(float(result.timings["simulation_wall_seconds"]), 1e-12)}}


def _write_csv(path: Path, fieldnames: list[str], rows: list[dict[str, Any]]) -> None:
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames); writer.writeheader(); writer.writerows(rows)


def _plots(directory: Path, result: SimulationResult, summary: dict[str, Any], config: dict[str, Any]) -> None:
    os.environ.setdefault("MPLCONFIGDIR", str(repository_root() / ".matplotlib-cache"))
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    dt = float(config["lif"]["dt_ms"])
    fig, ax = plt.subplots(figsize=(8, 3)); ax.scatter(result.spike_steps * dt, result.spike_neurons, s=1); ax.set(xlabel="time (ms)", ylabel="local neuron index", title="Spike raster"); fig.tight_layout(); fig.savefig(directory / "spike_raster.png", dpi=140); plt.close(fig)
    fig, ax = plt.subplots(figsize=(8, 3)); ax.plot(np.arange(result.total_steps) * dt, result.population_spikes); ax.set(xlabel="time (ms)", ylabel="spikes / step", title="Population activity"); fig.tight_layout(); fig.savefig(directory / "population_activity.png", dpi=140); plt.close(fig)
    fig, ax = plt.subplots(figsize=(8, 3));
    for column, index in enumerate(result.trace_indices): ax.plot(np.arange(result.total_steps) * dt, result.traces[:, column], label=str(int(index)))
    ax.set(xlabel="time (ms)", ylabel="membrane potential (mV)", title="Bounded membrane traces"); ax.legend(fontsize=6, ncol=2); fig.tight_layout(); fig.savefig(directory / "membrane_traces.png", dpi=140); plt.close(fig)
    hops = summary["hop_activity"]; fig, ax = plt.subplots(figsize=(6, 3)); ax.bar([item["hop"] for item in hops], [item["spikes"] for item in hops]); ax.set(xlabel="graph hop", ylabel="spikes", title="Activity by hop"); fig.tight_layout(); fig.savefig(directory / "activity_by_hop.png", dpi=140); plt.close(fig)


def write_experiment(data_dir: Path, experiment_id: str, subgraph: SelectedSubgraph, result: SimulationResult, config: dict[str, Any]) -> Path:
    if not experiment_id or any(character not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_" for character in experiment_id):
        raise NeuralDynamicsError("experiment_id may contain only letters, digits, hyphen, and underscore.")
    parent = data_dir / "experiments" / "neural-v1"; target = parent / experiment_id; staging = parent / f".{experiment_id}.staging"
    if target.exists() or staging.exists():
        raise NeuralDynamicsError(f"Experiment output already exists: {target}")
    parent.mkdir(parents=True, exist_ok=True); staging.mkdir()
    try:
        summary = summarize(subgraph, result, config)
        (staging / "config.json").write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
        np.save(staging / "selected_neurons.npy", subgraph.root_ids)
        fields = list(ANNOTATION_FIELDS) + ["hop", "local_index"]
        rows = [{**{field: item.get(field, "") for field in ANNOTATION_FIELDS}, "hop": int(subgraph.hop[index]), "local_index": index} for index, item in enumerate(subgraph.annotations)]
        _write_csv(staging / "selected_neurons.csv", fields, rows)
        np.savez_compressed(staging / "spikes.npz", step=result.spike_steps, neuron_index=result.spike_neurons)
        _write_csv(staging / "population_activity.csv", ["step", "time_ms", "spikes"], [{"step": step, "time_ms": step * float(config["lif"]["dt_ms"]), "spikes": int(value)} for step, value in enumerate(result.population_spikes)])
        _write_csv(staging / "activity_by_hop.csv", list(summary["hop_activity"][0]), summary["hop_activity"])
        _write_csv(staging / "membrane_traces.csv", ["step", "time_ms", *[f"neuron_{int(index)}_mV" for index in result.trace_indices]], [{"step": step, "time_ms": step * float(config["lif"]["dt_ms"]), **{f"neuron_{int(index)}_mV": float(result.traces[step, column]) for column, index in enumerate(result.trace_indices)}} for step in range(result.total_steps)])
        (staging / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
        _plots(staging, result, summary, config)
        metadata = {"format": FORMAT, "experiment_id": experiment_id, "dataset": "FlyWire FAFB v783", "source_manifest_sha256": subgraph.source_manifest_sha256, "phase3_graph_format": subgraph.graph_metadata["format"], "phase3_graph_policy": subgraph.graph_metadata["policy"], "annotation_commit": subgraph.graph_metadata["source_statistics"]["source_manifest"]["annotation_commit"], "annotation_query": config["selection"]["query"], "input_root_ids": [int(root) for root in subgraph.root_ids[subgraph.hop == 0]], "selection": {"input_limit": config["selection"]["input_limit"], "hop_caps": config["selection"]["hop_caps"]}, "selected_counts": summary["subgraph"], "seed": config["seed"], "status": result.status, "note": "Simplified experimental LIF model; FlyWire provides structural data, not membrane dynamics or calibrated weights.", "platform": {"python": sys.version, "platform": platform.platform(), "numpy": np.__version__}}
        (staging / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
        staging.rename(target)
    except Exception:
        shutil.rmtree(staging, ignore_errors=True)
        raise
    return target


def validate_experiment(path: Path, *, require_real: bool = True, data_dir: Path | None = None) -> dict[str, Any]:
    required = {"metadata.json", "config.json", "selected_neurons.npy", "selected_neurons.csv", "spikes.npz", "summary.json", "activity_by_hop.csv", "population_activity.csv", "membrane_traces.csv", "spike_raster.png", "population_activity.png", "membrane_traces.png", "activity_by_hop.png"}
    missing = sorted(name for name in required if not (path / name).is_file())
    if missing:
        raise NeuralDynamicsError(f"Incomplete experiment artifacts: {missing}")
    metadata, config, summary = _read_json(path / "metadata.json"), _read_json(path / "config.json"), _read_json(path / "summary.json")
    validate_config(config)
    if metadata.get("format") != FORMAT or summary.get("format") != FORMAT:
        raise NeuralDynamicsError("Experiment format mismatch.")
    if metadata.get("dataset") != config["source"]["dataset"] or metadata.get("phase3_graph_format") != config["source"]["graph_format"] or metadata.get("phase3_graph_policy") != config["source"]["graph_policy"] or metadata.get("annotation_query") != config["selection"]["query"] or metadata.get("seed") != config["seed"]:
        raise NeuralDynamicsError("Experiment metadata/configuration provenance mismatch.")
    if require_real:
        local_data = default_data_dir() if data_dir is None else data_dir
        manifest = local_data / "manifest.json"
        if metadata.get("dataset") != "FlyWire FAFB v783" or not manifest.is_file() or metadata.get("source_manifest_sha256") != sha256(manifest):
            raise NeuralDynamicsError("Production validation rejects a non-real or manifest-mismatched experiment identity.")
    roots = np.load(path / "selected_neurons.npy", allow_pickle=False)
    spikes = np.load(path / "spikes.npz", allow_pickle=False)
    if roots.dtype != np.int64 or roots.ndim != 1 or spikes["step"].dtype != np.uint32 or spikes["neuron_index"].dtype != np.uint32 or spikes["step"].shape != spikes["neuron_index"].shape:
        raise NeuralDynamicsError("Malformed compact experiment arrays.")
    if spikes["neuron_index"].size and int(spikes["neuron_index"].max()) >= roots.size:
        raise NeuralDynamicsError("Spike references an unselected neuron.")
    if int(summary.get("total_spikes", -1)) != int(spikes["step"].size) or int(summary.get("subgraph", {}).get("neurons", -1)) != int(roots.size):
        raise NeuralDynamicsError("Experiment summary does not match compact spike/selection arrays.")
    selected_counts = metadata.get("selected_counts", {})
    if int(selected_counts.get("neurons", -1)) != int(roots.size) or selected_counts != summary.get("subgraph"):
        raise NeuralDynamicsError("Experiment metadata selected counts do not match summary.")
    with (path / "selected_neurons.csv").open(encoding="utf-8", newline="") as handle:
        selected_rows = list(csv.DictReader(handle))
    if len(selected_rows) != roots.size or [int(row["root_id"]) for row in selected_rows] != roots.tolist():
        raise NeuralDynamicsError("Selected-neuron CSV does not match selected root-ID array.")
    if float(config["stimulus"]["external_current_mV_per_ms"]) == 0.0 and (spikes["step"].size != 0 or not bool(summary.get("silent"))):
        raise NeuralDynamicsError("No-stimulus control contains spikes or is not marked silent.")
    if bool(summary.get("propagation_demonstrated")) and (int(summary.get("activated_downstream_neurons", 0)) < 1 or int(summary.get("propagation_witnessed_downstream_neurons", 0)) != int(summary.get("activated_downstream_neurons", -1))):
        raise NeuralDynamicsError("Propagation claim lacks complete actual-edge witnesses.")
    return {"experiment_id": metadata.get("experiment_id"), "status": summary.get("status"), "neurons": int(roots.size), "spikes": int(spikes["step"].size), "propagation_demonstrated": bool(summary.get("propagation_demonstrated"))}
