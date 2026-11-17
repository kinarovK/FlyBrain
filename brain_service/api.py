from __future__ import annotations

import logging
import time
from contextlib import asynccontextmanager

from fastapi import FastAPI, Path as ApiPath, Query as ApiQuery, Request
from fastapi.encoders import jsonable_encoder
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse, Response

from brain_service import API_VERSION, SERVICE_VERSION
from brain_service.config import ServiceConfig, load_service_config
from brain_service.errors import ServiceError
from brain_service.runtime import BrainRuntime
from brain_service.schemas import BatchReadoutRequest, BatchReadoutResponse, BrainInfoResponse, CreateSessionRequest, CreateSessionResponse, ErrorEnvelope, LiveResponse, NeuronDiagnosticResponse, ReadyResponse, ResetResponse, SessionResponse, SessionRootsResponse, StateResponse, StepRequest, StepResponse
from brain_service.sessions import SessionManager


LOG = logging.getLogger("flytrader.brain_service")


def create_app(*, config: ServiceConfig | None = None, runtime: BrainRuntime | None = None) -> FastAPI:
    service_config = load_service_config() if config is None else config
    active_runtime = BrainRuntime(service_config) if runtime is None else runtime
    manager = SessionManager(active_runtime, service_config)

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        active_runtime.start()
        app.state.runtime, app.state.sessions = active_runtime, manager
        yield
        manager.close(); active_runtime.close()

    app = FastAPI(title="FlyTrader Brain Service", version=SERVICE_VERSION, lifespan=lifespan)

    @app.exception_handler(ServiceError)
    async def service_error(_: Request, exc: ServiceError) -> JSONResponse:
        return JSONResponse(exc.body(), status_code=exc.status_code)

    @app.exception_handler(RequestValidationError)
    async def validation_error(_: Request, exc: RequestValidationError) -> JSONResponse:
        return JSONResponse({"error": {"code": "INVALID_CONFIGURATION", "message": "Request validation failed.", "details": {"validation": jsonable_encoder(exc.errors())}}}, status_code=422)

    @app.exception_handler(Exception)
    async def internal_error(_: Request, exc: Exception) -> JSONResponse:
        LOG.exception("brain service internal error")
        return JSONResponse({"error": {"code": "INTERNAL_ERROR", "message": "An internal service error occurred.", "details": {}}}, status_code=500)

    @app.get("/health/live", response_model=LiveResponse)
    def live() -> LiveResponse:
        return {"status": "live", "service_version": SERVICE_VERSION}

    @app.get("/health/ready", response_model=ReadyResponse, responses={503: {"model": ReadyResponse}})
    def ready() -> JSONResponse:
        info = {**active_runtime.ready_info(), "service_version": SERVICE_VERSION}
        return JSONResponse(info, status_code=200 if active_runtime.status == "ready" else 503)

    @app.get("/api/v1/brain/info", response_model=BrainInfoResponse, responses={503: {"model": ErrorEnvelope}})
    def info() -> BrainInfoResponse:
        active_runtime.require_ready(); metadata = active_runtime.graph_metadata or {}; phase4 = active_runtime.phase4_config or {}
        return {"api_version": API_VERSION, "service_version": SERVICE_VERSION, "source_biology": {"dataset": metadata.get("dataset"), "canonical_neuron_count": 139255, "source_edge_rows": metadata["source_statistics"]["retained_edges"], "represented_synapses": metadata["source_statistics"]["retained_synapses"], "csr_pair_count": metadata["csr_pair_count"], "graph_format": metadata.get("format")}, "model_configuration": {"model": phase4.get("format"), "dt_ms": phase4.get("lif", {}).get("dt_ms"), "weight_transform": phase4.get("weights", {}).get("transform"), "available_transmitter_modes": ["unsigned", "transmitter"], "supported_execution_modes": ["reference", "whole"], "external_stimulus_note": "External current is an experimental model input, not a measured biological sensory signal."}}

    @app.post("/api/v1/brain/sessions", status_code=201, response_model=CreateSessionResponse, responses={404: {"model": ErrorEnvelope}, 422: {"model": ErrorEnvelope}, 429: {"model": ErrorEnvelope}, 503: {"model": ErrorEnvelope}})
    def create_session(request: CreateSessionRequest) -> CreateSessionResponse:
        session = manager.create(request.execution_mode, request.transmitter_mode)
        identity = {"neural_format": session.domain.phase4_config["format"], "neural_version": session.domain.phase4_config["version"], "transmitter_mode": request.transmitter_mode, "execution_mode": session.domain.mode}
        return {"api_version": API_VERSION, "session_id": session.session_id, "status": "ready", "execution_mode": session.domain.mode, "neuron_count": int(session.domain.root_ids.size), "transmitter_mode": request.transmitter_mode, "resolved_configuration": identity}

    @app.get("/api/v1/brain/sessions/{session_id}", response_model=SessionResponse, responses={404: {"model": ErrorEnvelope}, 422: {"model": ErrorEnvelope}})
    def get_session(session_id: str) -> SessionResponse:
        session = manager.get(session_id)
        return {"session_id": session.session_id, "status": session.safety_status, "execution_mode": session.domain.mode, "simulation_step": session.state.step, "neuron_count": int(session.domain.root_ids.size), "resolved_configuration": {"neural_format": session.domain.phase4_config["format"], "neural_version": session.domain.phase4_config["version"], "transmitter_mode": session.domain.phase4_config["transmitter"]["mode"], "execution_mode": session.domain.mode}}

    @app.delete("/api/v1/brain/sessions/{session_id}", status_code=204, responses={404: {"model": ErrorEnvelope}, 422: {"model": ErrorEnvelope}})
    def delete_session(session_id: str) -> Response:
        manager.delete(session_id); return Response(status_code=204)

    @app.post("/api/v1/brain/sessions/{session_id}/reset", response_model=ResetResponse, responses={404: {"model": ErrorEnvelope}, 409: {"model": ErrorEnvelope}, 422: {"model": ErrorEnvelope}})
    def reset(session_id: str) -> ResetResponse:
        started = time.perf_counter(); session = manager.reset(session_id)
        return {"session_id": session.session_id, "status": "reset", "simulation_step": 0, "reset_wall_seconds": time.perf_counter() - started}

    @app.post("/api/v1/brain/sessions/{session_id}/step", response_model=StepResponse, responses={404: {"model": ErrorEnvelope}, 409: {"model": ErrorEnvelope}, 422: {"model": ErrorEnvelope}, 429: {"model": ErrorEnvelope}})
    def step(session_id: str, request: StepRequest) -> StepResponse:
        max_events = min(request.max_returned_events or service_config.max_returned_events, service_config.max_returned_events)
        response = manager.step(session_id, request.steps, [(entry.root_id, entry.amplitude) for entry in request.stimulus], max_events)
        response["api_version"] = API_VERSION
        return response

    @app.get("/api/v1/brain/sessions/{session_id}/state", response_model=StateResponse, responses={404: {"model": ErrorEnvelope}, 409: {"model": ErrorEnvelope}, 422: {"model": ErrorEnvelope}})
    def state(session_id: str) -> StateResponse:
        return manager.read_snapshot(session_id)

    @app.get("/api/v1/brain/sessions/{session_id}/roots", response_model=SessionRootsResponse, responses={404: {"model": ErrorEnvelope}, 422: {"model": ErrorEnvelope}})
    def roots(
        session_id: str,
        offset: int = ApiQuery(default=0, ge=0),
        limit: int = ApiQuery(default=64, ge=1, le=256),
    ) -> SessionRootsResponse:
        return manager.read_root_page(session_id, offset, limit)

    @app.post("/api/v1/brain/sessions/{session_id}/readout", response_model=BatchReadoutResponse, responses={404: {"model": ErrorEnvelope}, 409: {"model": ErrorEnvelope}, 422: {"model": ErrorEnvelope}, 429: {"model": ErrorEnvelope}})
    def readout(session_id: str, request: BatchReadoutRequest) -> BatchReadoutResponse:
        return manager.read_batch_readout(session_id, request.root_ids)

    @app.get("/api/v1/brain/sessions/{session_id}/neurons/{root_id}", response_model=NeuronDiagnosticResponse, responses={404: {"model": ErrorEnvelope}, 422: {"model": ErrorEnvelope}})
    def neuron(session_id: str, root_id: int = ApiPath(ge=0, le=9_223_372_036_854_775_807)) -> NeuronDiagnosticResponse:
        return manager.read_snapshot(session_id, root_id)

    return app
