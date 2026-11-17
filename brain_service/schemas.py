from __future__ import annotations

from typing import Annotated, Any, Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator


class PublicModel(BaseModel):
    model_config = ConfigDict(extra="forbid")


RootId = Annotated[int, Field(ge=0, le=9_223_372_036_854_775_807)]


class StimulusEntry(PublicModel):
    root_id: int = Field(ge=0, le=9_223_372_036_854_775_807)
    amplitude: float

    @field_validator("amplitude")
    @classmethod
    def finite(cls, value: float) -> float:
        if value != value or value in (float("inf"), float("-inf")):
            raise ValueError("amplitude must be finite")
        return value


class CreateSessionRequest(PublicModel):
    execution_mode: Literal["reference", "whole"] = "reference"
    transmitter_mode: Literal["unsigned", "transmitter"] = "unsigned"


class ResolvedConfigIdentity(PublicModel):
    neural_format: str
    neural_version: int
    transmitter_mode: Literal["unsigned", "transmitter"]
    execution_mode: Literal["reference", "whole"]


class StepRequest(PublicModel):
    steps: int = Field(ge=1)
    stimulus: list[StimulusEntry] = Field(default_factory=list)
    max_returned_events: int | None = Field(default=None, ge=1)


class ErrorDetail(PublicModel):
    code: str
    message: str
    details: dict[str, Any] = Field(default_factory=dict)


class ErrorEnvelope(PublicModel):
    error: ErrorDetail


class LiveResponse(PublicModel):
    status: Literal["live"]
    service_version: str


class ReadyResponse(PublicModel):
    status: str
    dataset: str | None
    graph_format: str | None
    canonical_neuron_count: int | None
    startup_timings: dict[str, float]
    service_version: str


class CreateSessionResponse(PublicModel):
    api_version: str
    session_id: str
    status: str
    execution_mode: Literal["reference", "whole"]
    neuron_count: int
    transmitter_mode: str
    resolved_configuration: ResolvedConfigIdentity


class SessionResponse(PublicModel):
    session_id: str
    status: str
    execution_mode: Literal["reference", "whole"]
    simulation_step: int
    neuron_count: int
    resolved_configuration: ResolvedConfigIdentity


class StepEvent(PublicModel):
    step: int
    root_id: int


class StepResponse(PublicModel):
    api_version: str
    session_id: str
    starting_step: int
    ending_step: int
    simulated_ms: float
    steps_executed: int
    total_spikes: int
    unique_spiking_neurons: int
    active_fraction: float
    peak_simultaneous_spike_count: int
    peak_simultaneous_spike_fraction: float
    safety_status: str
    events: list[StepEvent]
    events_truncated: bool
    returned_event_count: int
    engine_wall_seconds: float


class ResetResponse(PublicModel):
    session_id: str
    status: Literal["reset"]
    simulation_step: Literal[0]
    reset_wall_seconds: float


class MembraneSummary(PublicModel):
    min: float
    mean: float
    max: float


class StateResponse(PublicModel):
    session_id: str
    simulation_step: int
    simulated_ms: float
    total_accumulated_spikes: int
    active_neuron_count: int
    safety_status: str
    membrane_mV: MembraneSummary


class NeuronDiagnosticResponse(PublicModel):
    session_id: str
    root_id: int
    membrane_mV: float
    refractory_steps: int
    cumulative_spike_count: int
    first_spike_step: int | None
    note: str


class BatchReadoutRequest(PublicModel):
    root_ids: list[RootId] = Field(
        min_length=1,
        max_length=256,
        description="One to 256 unique session-domain root IDs. Request ordering is preserved in the response.",
    )

    @field_validator("root_ids")
    @classmethod
    def unique_root_ids(cls, value: list[int]) -> list[int]:
        if len(set(value)) != len(value):
            raise ValueError("root_ids must be unique")
        return value


class BatchReadoutNeuron(PublicModel):
    root_id: RootId
    membrane_mV: float
    refractory_steps: int
    cumulative_spike_count: int
    first_spike_step: int | None
    note: str


class BatchReadoutResponse(PublicModel):
    session_id: str
    simulation_step: int
    simulated_ms: float
    returned_count: int = Field(ge=1, le=256)
    neurons: list[BatchReadoutNeuron] = Field(
        description="Neuron diagnostics in exactly the same order as request.root_ids.",
    )


class SessionRootsResponse(PublicModel):
    session_id: str
    offset: int = Field(ge=0)
    limit: int = Field(ge=1, le=256)
    returned_count: int = Field(ge=0)
    total_root_count: int = Field(ge=0)
    root_ids: list[RootId]


class SourceBiologyInfo(PublicModel):
    dataset: str
    canonical_neuron_count: int
    source_edge_rows: int
    represented_synapses: int
    csr_pair_count: int
    graph_format: str


class ModelConfigurationInfo(PublicModel):
    model: str
    dt_ms: float
    weight_transform: str
    available_transmitter_modes: list[str]
    supported_execution_modes: list[str]
    external_stimulus_note: str


class BrainInfoResponse(PublicModel):
    api_version: str
    service_version: str
    source_biology: SourceBiologyInfo
    model_configuration: ModelConfigurationInfo
