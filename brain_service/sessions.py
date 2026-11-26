from __future__ import annotations

import threading
import time
import uuid
from dataclasses import dataclass, field

import numpy as np

from brain_service.config import ServiceConfig
from brain_service.errors import ServiceError
from brain_service.runtime import BrainRuntime, PreparedDomain
from scripts.neural_dynamics import DynamicState, advance_dynamic_state, evaluate_safety, initialize_dynamic_state


@dataclass
class BrainSession:
    session_id: str
    domain: PreparedDomain
    state: DynamicState
    total_spike_counts: np.ndarray
    first_spike_step: np.ndarray
    total_spikes: int = 0
    safety_status: str = "completed"
    safety_reason: str | None = None
    consecutive_synchronous_steps: int = 0
    lock: threading.Lock = field(default_factory=threading.Lock)
    sorted_root_ids: np.ndarray = field(init=False)

    def __post_init__(self) -> None:
        self.sorted_root_ids = np.sort(self.domain.root_ids.copy())

    def reset(self) -> None:
        self.state = initialize_dynamic_state(self.domain.root_ids.size, self.domain.phase4_config)
        self.total_spike_counts.fill(0); self.first_spike_step.fill(-1)
        self.total_spikes, self.safety_status, self.safety_reason, self.consecutive_synchronous_steps = 0, "completed", None, 0


class SessionManager:
    def __init__(self, runtime: BrainRuntime, config: ServiceConfig) -> None:
        self.runtime, self.config = runtime, config
        self._sessions: dict[str, BrainSession] = {}
        self._registry_lock = threading.Lock()
        self._whole_operation_lock = threading.Lock()

    def create(self, mode: str, transmitter_mode: str) -> BrainSession:
        with self._registry_lock:
            if len(self._sessions) >= self.config.max_sessions:
                raise ServiceError("RESOURCE_LIMIT_EXCEEDED", "Maximum active session limit reached.", 429)
            if mode == "whole" and sum(session.domain.mode == "whole" for session in self._sessions.values()) >= self.config.max_whole_brain_sessions:
                raise ServiceError("RESOURCE_LIMIT_EXCEEDED", "Maximum whole-brain session limit reached.", 429)
            domain = self.runtime.domain(mode, transmitter_mode)
            session = BrainSession(str(uuid.uuid4()), domain, initialize_dynamic_state(domain.root_ids.size, domain.phase4_config), np.zeros(domain.root_ids.size, dtype=np.uint64), np.full(domain.root_ids.size, -1, dtype=np.int64))
            self._sessions[session.session_id] = session
            return session

    def get(self, session_id: str) -> BrainSession:
        with self._registry_lock:
            session = self._sessions.get(session_id)
        if session is None:
            raise ServiceError("SESSION_NOT_FOUND", "The requested session does not exist.", 404)
        return session

    def delete(self, session_id: str) -> None:
        with self._registry_lock:
            if self._sessions.pop(session_id, None) is None:
                raise ServiceError("SESSION_NOT_FOUND", "The requested session does not exist.", 404)

    def reset(self, session_id: str) -> BrainSession:
        session = self.get(session_id)
        if not session.lock.acquire(blocking=False):
            raise ServiceError("SESSION_BUSY", "The session is executing another mutation.", 409)
        whole_acquired = False
        try:
            if session.domain.mode == "whole":
                whole_acquired = self._whole_operation_lock.acquire(blocking=False)
                if not whole_acquired:
                    raise ServiceError("RESOURCE_LIMIT_EXCEEDED", "A whole-brain operation is already active.", 429)
            session.reset(); return session
        finally:
            if whole_acquired:
                self._whole_operation_lock.release()
            session.lock.release()

    def read_snapshot(self, session_id: str, root_id: int | None = None) -> dict:
        session = self.get(session_id)
        if not session.lock.acquire(blocking=False):
            raise ServiceError("SESSION_BUSY", "The session is executing another mutation.", 409)
        try:
            if root_id is not None:
                local = session.domain.root_to_local.get(root_id)
                if local is None:
                    raise ServiceError("UNKNOWN_ROOT_ID", "Diagnostic root is not in this session domain.", 404, {"root_id": root_id})
                return {"session_id": session_id, "root_id": root_id, "membrane_mV": float(session.state.membrane[local]), "refractory_steps": int(session.state.refractory[local]), "cumulative_spike_count": int(session.total_spike_counts[local]), "first_spike_step": None if session.first_spike_step[local] < 0 else int(session.first_spike_step[local]), "note": "Diagnostic model state; not biological ground truth."}
            membrane = session.state.membrane.copy()
            return {"session_id": session_id, "simulation_step": session.state.step, "simulated_ms": session.state.step * float(session.domain.phase4_config["lif"]["dt_ms"]), "total_accumulated_spikes": session.total_spikes, "active_neuron_count": int((session.total_spike_counts > 0).sum()), "safety_status": session.safety_status, "membrane_mV": {"min": float(membrane.min()), "mean": float(membrane.mean()), "max": float(membrane.max())}}
        finally:
            session.lock.release()

    def read_root_page(self, session_id: str, offset: int, limit: int) -> dict:
        session = self.get(session_id)
        root_ids = session.sorted_root_ids[offset : offset + limit]
        return {
            "session_id": session.session_id,
            "offset": offset,
            "limit": limit,
            "returned_count": int(root_ids.size),
            "total_root_count": int(session.sorted_root_ids.size),
            "root_ids": [int(root_id) for root_id in root_ids],
        }

    def read_layout_page(self, session_id: str, offset: int, limit: int) -> dict:
        if limit > self.config.max_layout_page:
            raise ServiceError("RESOURCE_LIMIT_EXCEEDED", "Layout page exceeds the configured limit.", 429, {"max_layout_page": self.config.max_layout_page})
        session = self.get(session_id)
        positions = self.runtime.layout()
        root_ids = session.sorted_root_ids[offset : offset + limit]
        indices = positions.indices_of(root_ids)
        xyz = positions.xyz_um[indices]
        has_position = positions.has_position[indices]
        entries = [
            {
                "root_id": int(root_id), "x": float(point[0]), "y": float(point[1]), "z": float(point[2]), "has_position": bool(flag),
                **{field: positions.class_value(field, int(index)) for field in ("super_class", "cell_class", "cell_type", "side")},
            }
            for root_id, index, point, flag in zip(root_ids, indices, xyz, has_position)
        ]
        return {"session_id": session.session_id, "offset": offset, "limit": limit, "returned_count": len(entries), "total_root_count": int(session.sorted_root_ids.size), "coordinate_unit": "um", "entries": entries}

    def read_batch_readout(self, session_id: str, root_ids: list[int]) -> dict:
        if len(root_ids) > self.config.max_stimulus_entries:
            raise ServiceError("RESOURCE_LIMIT_EXCEEDED", "Readout root list exceeds the configured limit.", 429)
        session = self.get(session_id)
        if not session.lock.acquire(blocking=False):
            raise ServiceError("SESSION_BUSY", "The session is executing another mutation.", 409)
        try:
            locals_by_request: list[int] = []
            for root_id in root_ids:
                local = session.domain.root_to_local.get(root_id)
                if local is None:
                    raise ServiceError("UNKNOWN_ROOT_ID", "Readout root is not in this session domain.", 404, {"root_id": root_id})
                locals_by_request.append(local)

            return {
                "session_id": session.session_id,
                "simulation_step": session.state.step,
                "simulated_ms": session.state.step * float(session.domain.phase4_config["lif"]["dt_ms"]),
                "returned_count": len(root_ids),
                "neurons": [
                    {
                        "root_id": root_id,
                        "membrane_mV": float(session.state.membrane[local]),
                        "refractory_steps": int(session.state.refractory[local]),
                        "cumulative_spike_count": int(session.total_spike_counts[local]),
                        "first_spike_step": None if session.first_spike_step[local] < 0 else int(session.first_spike_step[local]),
                        "note": "Diagnostic model state; not biological ground truth.",
                    }
                    for root_id, local in zip(root_ids, locals_by_request)
                ],
            }
        finally:
            session.lock.release()

    def step(self, session_id: str, steps: int, stimulus: list[tuple[int, float]], max_events: int) -> dict:
        if steps > self.config.max_steps_per_request:
            raise ServiceError("INVALID_STEP_COUNT", "Requested steps exceed the configured limit.", 422, {"max_steps": self.config.max_steps_per_request})
        if len(stimulus) > self.config.max_stimulus_entries:
            raise ServiceError("RESOURCE_LIMIT_EXCEEDED", "Stimulus list exceeds the configured limit.", 429)
        session = self.get(session_id)
        if not session.lock.acquire(blocking=False):
            raise ServiceError("SESSION_BUSY", "The session is executing another mutation.", 409)
        whole_acquired = False
        try:
            if session.domain.mode == "whole":
                whole_acquired = self._whole_operation_lock.acquire(blocking=False)
                if not whole_acquired:
                    raise ServiceError("RESOURCE_LIMIT_EXCEEDED", "A whole-brain operation is already active.", 429)
            seen: set[int] = set(); external = np.zeros(session.domain.root_ids.size, dtype=np.float64)
            for root_id, amplitude in stimulus:
                if root_id in seen:
                    raise ServiceError("INVALID_CONFIGURATION", "Duplicate stimulus root IDs are not permitted.", 422, {"root_id": root_id})
                seen.add(root_id)
                if abs(amplitude) > self.config.max_abs_external_current_mV_per_ms:
                    raise ServiceError("RESOURCE_LIMIT_EXCEEDED", "Stimulus amplitude exceeds the configured limit.", 429)
                local = session.domain.root_to_local.get(root_id)
                if local is None:
                    raise ServiceError("UNKNOWN_ROOT_ID", "Stimulus root is not in this session domain.", 404, {"root_id": root_id})
                external[local] = amplitude
            start_step, started, peak_count, event_steps, event_neurons = session.state.step, time.perf_counter(), 0, [], []
            request_counts = np.zeros(session.domain.root_ids.size, dtype=np.uint32)
            for _ in range(steps):
                spikes, candidate_peak = advance_dynamic_state(session.state, session.domain.matrix, external, session.domain.phase4_config)
                indices = np.flatnonzero(spikes); count = int(indices.size); peak_count = max(peak_count, count)
                request_counts += spikes; session.total_spike_counts += spikes
                unseen = spikes & (session.first_spike_step < 0); session.first_spike_step[unseen] = session.state.step - 1
                session.total_spikes += count
                remaining = max_events - len(event_steps)
                if remaining > 0:
                    for index in indices[:remaining]: event_steps.append(session.state.step - 1); event_neurons.append(int(index))
                status, reason, consecutive = evaluate_safety(membrane=session.state.membrane, current=session.state.current, candidate_peak=candidate_peak, spike_count=count, neuron_spike_counts=session.total_spike_counts, consecutive_synchronous_steps=session.consecutive_synchronous_steps, config=session.domain.phase4_config)
                session.consecutive_synchronous_steps = consecutive
                if status is not None:
                    session.safety_status, session.safety_reason = status, reason
                    code = "NUMERICAL_FAILURE" if status == "numerical_failure" else "RUNAWAY_ACTIVITY"
                    raise ServiceError(code, reason or status, 409, {"ending_step": session.state.step})
            total_request_spikes = int(request_counts.sum(dtype=np.uint64))
            active = np.flatnonzero(request_counts > 0); dt = float(session.domain.phase4_config["lif"]["dt_ms"])
            return {"session_id": session_id, "starting_step": start_step, "ending_step": session.state.step, "simulated_ms": steps * dt, "steps_executed": session.state.step - start_step, "total_spikes": total_request_spikes, "unique_spiking_neurons": int(active.size), "active_fraction": float(active.size / session.domain.root_ids.size), "peak_simultaneous_spike_count": peak_count, "peak_simultaneous_spike_fraction": float(peak_count / session.domain.root_ids.size), "safety_status": session.safety_status, "events": [{"step": step, "root_id": int(session.domain.root_ids[index])} for step, index in zip(event_steps, event_neurons)], "events_truncated": total_request_spikes > len(event_steps), "returned_event_count": len(event_steps), "engine_wall_seconds": time.perf_counter() - started}
        finally:
            if whole_acquired:
                self._whole_operation_lock.release()
            session.lock.release()

    def close(self) -> None:
        with self._registry_lock:
            self._sessions.clear()
