from __future__ import annotations

import time
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import scipy.sparse as sp

from brain_service.config import ServiceConfig
from brain_service.errors import ServiceError
from scripts.neural_dynamics import NeuralDynamicsError, _load_graph, default_config_path, default_data_dir, load_config, validate_config
from scripts.whole_brain_scaling import WHOLE_NODES, WholeTier, load_whole_config, select_tier


@dataclass(frozen=True)
class PreparedDomain:
    mode: str
    root_ids: np.ndarray
    root_to_local: dict[int, int]
    matrix: sp.csr_matrix
    input_local: np.ndarray
    graph_metadata: dict
    row_edge_count: int
    represented_synapses: int
    csr_pair_count: int
    phase4_config: dict


class BrainRuntime:
    def __init__(self, service_config: ServiceConfig, data_dir: Path | None = None) -> None:
        self.config = service_config
        self.data_dir = default_data_dir() if data_dir is None else data_dir
        self.status = "starting"
        self.failure_reason: str | None = None
        self.started_at = time.perf_counter()
        self.startup_timings: dict[str, float] = {}
        self.phase4_config: dict | None = None
        self.whole_config: dict | None = None
        self.graph_metadata: dict | None = None
        self._graph: tuple[dict, dict] | None = None
        self._domains: dict[tuple[str, str], PreparedDomain] = {}

    def start(self) -> None:
        started = time.perf_counter()
        try:
            phase4 = load_config(default_config_path())
            whole = load_whole_config()
            validate_config(phase4)
            graph_started = time.perf_counter()
            metadata, arrays = _load_graph(self.data_dir, phase4)
            self.startup_timings["graph_validation_seconds"] = time.perf_counter() - graph_started
            if metadata.get("dataset") != "FlyWire FAFB v783" or int(arrays["root_ids"].size) != WHOLE_NODES:
                raise NeuralDynamicsError("Required real FAFB v783 canonical graph is unavailable.")
            self.phase4_config, self.whole_config, self.graph_metadata, self._graph = phase4, whole, metadata, (metadata, arrays)
            # Both supported domains are prepared before readiness, sharing the
            # single validated memory-mapped graph held above.
            self.status = "ready"
            self.domain("reference")
            self.domain("whole")
            self.startup_timings["startup_seconds"] = time.perf_counter() - started
        except Exception as exc:
            self.status, self.failure_reason = "failed", "real graph/configuration validation failed"
            self.startup_timings["startup_seconds"] = time.perf_counter() - started

    def ready_info(self) -> dict:
        metadata = self.graph_metadata or {}
        return {"status": self.status, "dataset": metadata.get("dataset"), "graph_format": metadata.get("format"), "canonical_neuron_count": None if self.status != "ready" else WHOLE_NODES, "startup_timings": self.startup_timings}

    def require_ready(self) -> None:
        if self.status != "ready" or self.phase4_config is None or self.whole_config is None:
            raise ServiceError("GRAPH_NOT_READY", "The validated real graph is not ready.", 503)

    def domain(self, mode: str, transmitter_mode: str = "unsigned") -> PreparedDomain:
        self.require_ready()
        key = (mode, transmitter_mode)
        if key in self._domains:
            return self._domains[key]
        try:
            phase4 = load_config(default_config_path(), sign_mode=transmitter_mode)
            tier: WholeTier = select_tier(self.data_dir, self.whole_config, phase4, mode, graph=self._graph)
            if "preflight" in tier.sign_counts:
                raise NeuralDynamicsError("whole-domain resource preflight refused preparation")
            domain = PreparedDomain(
                mode=mode, root_ids=tier.root_ids, root_to_local={int(root): index for index, root in enumerate(tier.root_ids)},
                matrix=tier.matrix, input_local=tier.input_local, graph_metadata=tier.graph_metadata,
                row_edge_count=tier.row_edge_count, represented_synapses=tier.represented_synapses,
                csr_pair_count=tier.csr_pair_count, phase4_config=phase4,
            )
            self._domains[key] = domain
            return domain
        except NeuralDynamicsError as exc:
            raise ServiceError("GRAPH_NOT_READY", "The requested real graph domain cannot be prepared.", 503) from exc

    def close(self) -> None:
        self.status = "stopping"
        self._domains.clear(); self._graph = None
