from __future__ import annotations

import json
import os
from dataclasses import dataclass
from pathlib import Path


FORMAT = "flytrader-brain-service-v1"


class ServiceConfigError(ValueError):
    pass


@dataclass(frozen=True)
class ServiceConfig:
    host: str
    port: int
    max_sessions: int
    max_whole_brain_sessions: int
    max_steps_per_request: int
    max_stimulus_entries: int
    max_returned_events: int
    max_abs_external_current_mV_per_ms: float
    shutdown_timeout_seconds: float
    log_level: str
    eager_whole_domain: bool = False
    max_layout_page: int = 4096


def default_config_path() -> Path:
    return Path(__file__).resolve().parents[1] / "brain" / "configs" / "brain-service-v1.default.json"


def load_service_config(path: Path | None = None) -> ServiceConfig:
    source = default_config_path() if path is None else path
    try:
        raw = json.loads(source.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ServiceConfigError("Invalid brain service configuration.") from exc
    if raw.get("format") != FORMAT or raw.get("version") != 1:
        raise ServiceConfigError("Unsupported brain service configuration format/version.")
    optional = {"eager_whole_domain", "max_layout_page"}
    values = {key: raw[key] for key in ServiceConfig.__dataclass_fields__ if key not in optional or key in raw}
    for env, key, caster in (
        ("FLYTRADER_BRAIN_HOST", "host", str), ("FLYTRADER_BRAIN_PORT", "port", int),
        ("FLYTRADER_BRAIN_MAX_SESSIONS", "max_sessions", int),
        ("FLYTRADER_BRAIN_MAX_WHOLE_SESSIONS", "max_whole_brain_sessions", int),
    ):
        if env in os.environ:
            values[key] = caster(os.environ[env])
    if "FLYBRAIN_EAGER_WHOLE" in os.environ:
        values["eager_whole_domain"] = os.environ["FLYBRAIN_EAGER_WHOLE"].strip().lower() in {"1", "true", "yes", "on"}
    config = ServiceConfig(**values)
    if config.host not in {"127.0.0.1", "localhost", "::1"} or config.port < 1 or config.port > 65535:
        raise ServiceConfigError("Service must use a valid localhost host and port.")
    if any(getattr(config, key) < 1 for key in ("max_sessions", "max_whole_brain_sessions", "max_steps_per_request", "max_stimulus_entries", "max_returned_events", "max_layout_page")) or config.max_whole_brain_sessions > config.max_sessions or config.max_abs_external_current_mV_per_ms <= 0 or config.shutdown_timeout_seconds <= 0:
        raise ServiceConfigError("Invalid service resource limits.")
    return config
