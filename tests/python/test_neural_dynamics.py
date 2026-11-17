from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
import pytest
import scipy.sparse as sp

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))

from neural_dynamics import (
    NeuralDynamicsError,
    SelectedSubgraph,
    _row_weights,
    _transmitter_signs,
    lif_step,
    load_config,
    simulate,
    summarize,
)


def config(override=None, **kwargs):
    return load_config(override=override, **kwargs)


def fixture_subgraph(matrix=None, hop=None) -> SelectedSubgraph:
    matrix = sp.csr_matrix([[0.0, 0.2], [0.0, 0.0]]) if matrix is None else matrix
    hop = np.array([0, 1], dtype=np.uint8) if hop is None else np.asarray(hop, dtype=np.uint8)
    return SelectedSubgraph(
        root_ids=np.array([101, 102], dtype=np.int64), full_indices=np.array([0, 1], dtype=np.uint32),
        hop=hop, annotations=[{"root_id": "101"}, {"root_id": "102"}], matrix=matrix,
        row_edge_count=1, represented_synapses=3, csr_pair_count=matrix.nnz, p99_syn_count=3.0,
        sign_counts={"unsigned": 1, "gaba_negative": 0, "ach_positive": 0, "fallback": 0},
        source_manifest_sha256="fixture", graph_metadata={"format": "flytrader-sparse-v1", "policy": "fixture"},
    )


def test_lif_leak_moves_voltage_toward_rest():
    cfg = config()
    membrane = np.array([-46.0]); current = np.zeros(1); refractory = np.zeros(1, dtype=np.int32)
    updated, _, _, spikes, _ = lif_step(membrane, current, refractory, np.zeros(1), sp.csr_matrix((1, 1)), np.array([False]), 0.0, cfg)
    assert -52.0 < updated[0] < -46.0
    assert not spikes[0]


def test_threshold_reset_and_refractory_hold():
    cfg = config()
    membrane = np.array([-46.0]); current = np.zeros(1); refractory = np.zeros(1, dtype=np.int32)
    updated, _, refractory, spikes, _ = lif_step(membrane, current, refractory, np.zeros(1), sp.csr_matrix((1, 1)), np.array([True]), 20.0, cfg)
    assert spikes[0] and updated[0] == cfg["lif"]["v_reset_mV"] and refractory[0] > 0
    updated, _, refractory, spikes, _ = lif_step(updated, np.zeros(1), refractory, np.zeros(1), sp.csr_matrix((1, 1)), np.array([True]), 20.0, cfg)
    assert not spikes[0] and updated[0] == cfg["lif"]["v_reset_mV"] and refractory[0] >= 0


def test_synaptic_propagation_is_one_step_delayed():
    cfg = config()
    membrane = np.array([-52.0, -52.0]); current = np.zeros(2); refractory = np.zeros(2, dtype=np.int32)
    updated, _, _, spikes, _ = lif_step(membrane, current, refractory, np.array([1.0, 0.0]), sp.csr_matrix([[0.0, 10.0], [0.0, 0.0]]), np.array([False, False]), 0.0, cfg)
    assert spikes.tolist() == [False, True]
    assert updated[1] == cfg["lif"]["v_reset_mV"]


def test_weight_transforms_are_positive_and_saturating():
    cfg = config()
    values, p99 = _row_weights(np.array([1, 10, 100], dtype=np.uint32), cfg)
    assert np.all(values > 0) and np.all(values <= cfg["weights"]["synaptic_scale_mV"])
    assert p99 > 0


def test_transmitter_policy_only_signs_confident_gaba():
    cfg = config(sign_mode="transmitter")
    values = np.array([[0.9, 0.05, 0, 0, 0, 0], [0.1, 0.8, 0, 0, 0, 0], [0.1, 0.2, 0.8, 0, 0, 0], [np.nan, 0, 0, 0, 0, 0]], dtype=np.float32)
    signs, counts = _transmitter_signs(values, cfg)
    assert signs.tolist() == [-1.0, 1.0, 1.0, 1.0]
    assert counts == {"unsigned": 0, "gaba_negative": 1, "ach_positive": 1, "fallback": 2}
    unsigned, _ = _transmitter_signs(values, config({"transmitter": {"fallback_sign": -1.0}}))
    assert np.all(unsigned == 1.0)


def test_deterministic_stimulus_and_no_stimulus_silence():
    override = {"stimulus": {"baseline_ms": 0.0, "duration_ms": 2.0, "recovery_ms": 1.0, "external_current_mV_per_ms": 20.0}}
    cfg = config(override); graph = fixture_subgraph()
    first, second = simulate(graph, cfg), simulate(graph, cfg)
    assert np.array_equal(first.spike_steps, second.spike_steps) and np.array_equal(first.spike_neurons, second.spike_neurons)
    silent = simulate(graph, config({"stimulus": {"baseline_ms": 1.0, "duration_ms": 0.0, "recovery_ms": 1.0, "external_current_mV_per_ms": 0.0}}))
    assert silent.spike_steps.size == 0 and silent.status == "completed"


def test_runaway_detection_and_summary_witness():
    cfg = config({"stimulus": {"baseline_ms": 0.0, "duration_ms": 10.0, "recovery_ms": 0.0, "external_current_mV_per_ms": 30.0}, "safety": {"max_simultaneous_spike_fraction": 0.1, "max_consecutive_synchronous_steps": 1, "max_spikes_per_neuron": 250, "max_abs_membrane_mV": 250.0}})
    result = simulate(fixture_subgraph(), cfg)
    assert result.status == "runaway_activity"
    safe_cfg = config({"stimulus": {"baseline_ms": 0.0, "duration_ms": 2.0, "recovery_ms": 1.0, "external_current_mV_per_ms": 20.0}})
    graph = fixture_subgraph(matrix=sp.csr_matrix([[0.0, 10.0], [0.0, 0.0]]))
    summary = summarize(graph, simulate(graph, safe_cfg), safe_cfg)
    assert summary["activated_downstream_neurons"] >= 1
    assert summary["propagation_demonstrated"]


def test_pre_reset_membrane_overshoot_is_numerical_failure():
    graph = fixture_subgraph(matrix=sp.csr_matrix((100, 100)), hop=np.array([0] + [1] * 99, dtype=np.uint8))
    graph = SelectedSubgraph(np.arange(100, 200, dtype=np.int64), np.arange(100, dtype=np.uint32), graph.hop, [{"root_id": str(item)} for item in range(100, 200)], graph.matrix, 0, 0, 0, 0.0, graph.sign_counts, "fixture", graph.graph_metadata)
    cfg = config({"stimulus": {"baseline_ms": 0.0, "duration_ms": 0.1, "recovery_ms": 0.0, "external_current_mV_per_ms": 1000000.0}, "safety": {"max_simultaneous_spike_fraction": 1.0, "max_consecutive_synchronous_steps": 5, "max_spikes_per_neuron": 250, "max_abs_membrane_mV": 250.0}})
    assert simulate(graph, cfg).status == "numerical_failure"


def test_malformed_configuration_rejected():
    with pytest.raises(NeuralDynamicsError):
        config({"lif": {"tau_m_ms": -1.0}})
    with pytest.raises(NeuralDynamicsError):
        config({"unknown": 1})
