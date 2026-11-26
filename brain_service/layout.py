from __future__ import annotations

import threading
from collections.abc import Callable
from pathlib import Path

import numpy as np

from brain_service.errors import ServiceError
from scripts.brain_positions import BrainPositions, PositionsError, load


class LayoutProvider:
    """Loads the derived positions once, on first use, and caches them immutably.

    Brain readiness never depends on the positions: a missing or invalid file
    only makes layout requests fail with ``LAYOUT_NOT_READY``. Failures are not
    cached, so positions exported while the service runs are picked up by the
    next layout request.
    """

    def __init__(self, data_dir: Path, canonical_root_ids: Callable[[], np.ndarray]) -> None:
        self._data_dir = data_dir
        self._canonical_root_ids = canonical_root_ids
        self._positions: BrainPositions | None = None
        self._lock = threading.Lock()

    def positions(self) -> BrainPositions:
        cached = self._positions
        if cached is not None:
            return cached
        with self._lock:
            if self._positions is None:
                try:
                    self._positions = load(self._data_dir, expected_root_ids=self._canonical_root_ids())
                except (PositionsError, OSError, ValueError) as exc:
                    raise ServiceError("LAYOUT_NOT_READY", "Neuron positions are not available; run scripts/export_positions.py.", 503) from exc
            return self._positions
