from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
import pytest
import scipy.sparse as sp

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))

from neural_dynamics import NeuralDynamicsError, _rank_frontier, load_config
from whole_brain_scaling import WHOLE_NODES, ResourceEstimate, WholeTier, _parameter_delta, _validate_event_aggregate_contract, estimate_resources, load_whole_config, run_tier, select_tier, summarize_tier, write_whole_brain_experiment


def phase4(override=None):
    return load_config(override=override)


def whole(override=None):
    return load_whole_config(override=override)


def tier(matrix=None) -> WholeTier:
    matrix = sp.csr_matrix([[0.0, 10.0], [0.0, 0.0]]) if matrix is None else matrix
    return WholeTier("fixture", np.array([1001, 1002], dtype=np.int64), np.array([0, 1], dtype=np.uint32), np.array([0, 1], dtype=np.int16), np.array([0], dtype=np.uint32), matrix, 1, 3, matrix.nnz, 3.0, {"unsigned": 1, "gaba_negative": 0, "ach_positive": 0, "fallback": 0}, {"dataset": "FlyWire FAFB v783", "source_manifest_sha256": "fixture", "format": "flytrader-sparse-v1", "policy": "fixture"})


def test_tier_ladder_contract_rejects_fake_whole_size():
    with pytest.raises(NeuralDynamicsError):
        whole({"tiers": {"whole": 10}})


def test_bounded_event_recording_keeps_complete_aggregates_and_is_deterministic():
    p4 = phase4({"stimulus": {"baseline_ms": 0.0, "duration_ms": 10.0, "recovery_ms": 0.0, "external_current_mV_per_ms": 30.0}, "safety": {"max_simultaneous_spike_fraction": 1.0, "max_consecutive_synchronous_steps": 20, "max_spikes_per_neuron": 250, "max_abs_membrane_mV": 250.0}})
    wb = whole({"events": {"max_recorded_events": 1}}); first, second = run_tier(tier(), p4, wb), run_tier(tier(), p4, wb)
    assert first.events_truncated and first.spike_steps.size == 1
    assert int(first.spike_count_by_neuron.sum()) > first.spike_steps.size
    assert np.array_equal(first.spike_steps, second.spike_steps)
    assert np.array_equal(first.spike_count_by_neuron, second.spike_count_by_neuron)


def test_summary_accounts_for_events_and_profile_metrics():
    p4 = phase4({"stimulus": {"baseline_ms": 0.0, "duration_ms": 2.0, "recovery_ms": 0.0, "external_current_mV_per_ms": 20.0}}); wb = whole()
    current_tier = tier(); result = run_tier(current_tier, p4, wb); estimate = estimate_resources(current_tier, wb); summary = summarize_tier(current_tier, result, p4, wb, estimate)
    assert summary["tier"]["neurons"] == 2
    assert summary["performance"]["sparse_propagation_seconds"] >= 0
    assert summary["performance"]["event_recording_seconds"] >= 0
    assert summary["total_spikes"] == int(result.spike_count_by_neuron.sum())
    assert summary["classification"] in {"successful_propagation", "broad_stable_propagation", "pathological_saturation", "runaway_activity", "numerical_failure", "silence"}


def test_resource_estimate_covers_matrix_and_event_capacity():
    estimate = estimate_resources(tier(), whole({"events": {"max_recorded_events": 123}}))
    assert estimate.matrix_bytes > 0 and estimate.event_bytes == 123 * 8 and estimate.required_bytes >= estimate.matrix_bytes


def test_compact_event_validator_rejects_unordered_or_inconsistent_aggregates():
    summary = {"events_truncated": False, "total_spikes": 2}
    aggregates = {"population_spikes": np.array([1, 1], dtype=np.uint32), "spike_count_by_neuron": np.array([1, 1], dtype=np.uint32), "first_step_by_neuron": np.array([0, 1], dtype=np.int32)}
    good = {"step": np.array([0, 1], dtype=np.uint32), "neuron_index": np.array([0, 1], dtype=np.uint32), "truncated": np.array([False])}
    _validate_event_aggregate_contract(good, aggregates, summary)
    bad = {**good, "step": np.array([1, 0], dtype=np.uint32)}
    with pytest.raises(NeuralDynamicsError, match="prefix order"):
        _validate_event_aggregate_contract(bad, aggregates, summary)
    bad_aggregate = {**aggregates, "spike_count_by_neuron": np.array([0, 1], dtype=np.uint32)}
    with pytest.raises(NeuralDynamicsError, match="fewer spikes"):
        _validate_event_aggregate_contract(good, bad_aggregate, summary)


def test_dense_activity_hits_safety_limit_and_parameter_delta_is_explicit():
    p4 = phase4({"stimulus": {"baseline_ms": 0.0, "duration_ms": 2.0, "recovery_ms": 0.0, "external_current_mV_per_ms": 100.0}, "safety": {"max_simultaneous_spike_fraction": 0.5, "max_consecutive_synchronous_steps": 1, "max_spikes_per_neuron": 250, "max_abs_membrane_mV": 250.0}})
    result = run_tier(tier(sp.csr_matrix((2, 2))), p4, whole())
    assert result.status == "runaway_activity"
    assert "stimulus.external_current_mV_per_ms" in _parameter_delta(p4)


def test_frontier_ranking_is_stable_by_synapse_count_then_canonical_index():
    arrays = {"csr_indptr": np.array([0, 3, 3, 3, 3], dtype=np.int64), "csr_indices": np.array([3, 2, 1], dtype=np.uint32), "csr_syn_count": np.array([5, 5, 9], dtype=np.uint32)}
    assert np.array_equal(_rank_frontier(np.array([0], dtype=np.uint32), {0}, arrays, 3), np.array([1, 2, 3], dtype=np.uint32))


def test_preflight_refusal_is_an_explicit_non_simulation_status(monkeypatch):
    import whole_brain_scaling as scaling
    monkeypatch.setattr(scaling, "_available_memory_bytes", lambda: 1)
    result = run_tier(tier(), phase4(), whole())
    assert result.status == "resource_preflight_failed" and result.total_steps == 0


def test_public_whole_selector_is_exact_identity_on_controlled_graph(monkeypatch, tmp_path):
    import whole_brain_scaling as scaling
    roots = np.arange(WHOLE_NODES, dtype=np.int64)
    metadata = {"dataset": "FlyWire FAFB v783", "source_manifest_sha256": "fixture", "format": "flytrader-sparse-v1", "policy": "fixture", "source_statistics": {"retained_edges": 7, "retained_synapses": 11}, "csr_pair_count": 5}
    arrays = {"root_ids": roots, "csr_indptr": np.zeros(WHOLE_NODES + 1, dtype=np.int64), "csr_indices": np.empty(0, dtype=np.uint32)}
    monkeypatch.setattr(scaling, "_load_graph", lambda *_: (metadata, arrays))
    monkeypatch.setattr(scaling, "_input_indices", lambda *_: (np.array([0]), np.array([0], dtype=np.uint32)))
    monkeypatch.setattr(scaling, "_distance_labels", lambda *_: np.zeros(WHOLE_NODES, dtype=np.int16))
    monkeypatch.setattr(scaling, "_estimate_counts", lambda *_: ResourceEstimate(1, 1, 1, 0, 0, 0, True))
    monkeypatch.setattr(scaling, "_build_weighted_matrix", lambda *_args, **_kw: (sp.csr_matrix((WHOLE_NODES, WHOLE_NODES)), 7, 11, 1.0, {"unsigned": 7}))
    selected = select_tier(tmp_path, whole(), phase4(), "whole")
    assert np.array_equal(selected.root_ids, roots)
    assert np.array_equal(selected.full_indices, np.arange(WHOLE_NODES, dtype=np.uint32))
    assert (selected.row_edge_count, selected.represented_synapses, selected.csr_pair_count) == (7, 11, 0)


def test_writer_persists_metadata_and_complete_artifact_timing_schema(monkeypatch, tmp_path):
    import whole_brain_scaling as scaling
    monkeypatch.setattr(scaling, "_annotation_map", lambda *_: {})
    monkeypatch.setattr(scaling, "_plots", lambda *_: None)
    result = run_tier(tier(), phase4({"stimulus": {"baseline_ms": 0.0, "duration_ms": 0.1, "recovery_ms": 0.0, "external_current_mV_per_ms": 0.0}}), whole())
    target = write_whole_brain_experiment(tmp_path, "fixture", tier(), result, phase4({"stimulus": {"baseline_ms": 0.0, "duration_ms": 0.1, "recovery_ms": 0.0, "external_current_mV_per_ms": 0.0}}), whole())
    import json
    summary, metadata = json.loads((target / "summary.json").read_text()), json.loads((target / "metadata.json").read_text())
    assert {"artifact_serialization_seconds", "diagnostic_plot_seconds", "artifact_write_wall_seconds"} <= set(summary["performance"])
    assert metadata["platform"]["scipy"]
