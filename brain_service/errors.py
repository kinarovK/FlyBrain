from __future__ import annotations

from dataclasses import dataclass
from typing import Any


@dataclass
class ServiceError(Exception):
    code: str
    message: str
    status_code: int
    details: dict[str, Any] | None = None

    def body(self) -> dict[str, Any]:
        return {"error": {"code": self.code, "message": self.message, "details": self.details or {}}}
