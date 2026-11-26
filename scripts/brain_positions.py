"""Neuron positions for the canonical FlyWire v783 graph (task 0011, sprint V0).

This module performs data transformation only; it contains no neural dynamics.
It converts the FlyWire annotation anchor coordinates (``pos_x/y/z``) and soma
coordinates (``soma_x/y/z``) from 4 x 4 x 40 nm voxel space to micrometres, in
the canonical root order of the ``flytrader-sparse-v1`` graph.

Canonical roots without an annotation row get ``has_position = False`` and a
placeholder position: the mean position of their annotated graph neighbours
(pre- and postsynaptic partners). Placeholders are listed in the metadata and
must never be presented as real positions.
"""
from __future__ import annotations

import csv
import hashlib
import json
import shutil
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import numpy as np

try:  # Supports direct scripts and package imports by the service runtime.
    from .flywire_data import ANNOTATIONS, DATASET_NAME, default_data_dir, file_md5
except ImportError:  # pragma: no cover - direct script execution compatibility
    from flywire_data import ANNOTATIONS, DATASET_NAME, default_data_dir, file_md5

FORMAT = "flybrain-positions-v1"
DIRECTORY = "positions-v1"
GRAPH_FORMAT = "flytrader-sparse-v1"
COORDINATE_UNIT = "um"
VOXEL_SIZE_NM = (4.0, 4.0, 40.0)
VOXEL_SIZE_SOURCE = {
    "statement": "`pos_x`, `pos_y`, `pos_z` are anchor coordinates in 4x4x40nm voxel space (typically on the backbone of the neuron) for given neuron; `soma_x`, `soma_y`, `soma_z` are soma coordinates in 4x4x40nm voxel space",
    "url": "https://github.com/flyconnectome/flywire_annotations/blob/main/supplemental_files/README.md",
    "checked": "2026-11-26",
}
# Plausibility window for the whole-brain bounding box of an adult fly brain
# (central brain plus optic lobes) in FAFB space, in micrometres. A wrong voxel
# size (for example nm read as voxels, or 4 nm on z) falls far outside it.
PLAUSIBLE_EXTENT_UM = {"x": (500.0, 1000.0), "y": (200.0, 600.0), "z": (200.0, 400.0)}
CLASS_FIELDS = ("super_class", "cell_class", "cell_type", "side")
POSITION_COLUMNS = ("pos_x", "pos_y", "pos_z")
SOMA_COLUMNS = ("soma_x", "soma_y", "soma_z")
ARRAYS = ("root_ids", "xyz_um", "soma_xyz_um", "has_position")
DTYPES = {"root_ids": np.dtype("int64"), "xyz_um": np.dtype("float32"), "soma_xyz_um": np.dtype("float32"), "has_position": np.dtype("bool")}


class PositionsError(RuntimeError):
    pass


@dataclass(frozen=True)
class BrainPositions:
    """Immutable, validated positions in canonical (ascending root ID) order."""

    root_ids: np.ndarray
    xyz_um: np.ndarray
    has_position: np.ndarray
    classes: dict[str, tuple[list[str], np.ndarray]]
    metadata: dict[str, Any]

    def indices_of(self, root_ids: np.ndarray) -> np.ndarray:
        """Canonical indices of the given root IDs; every ID must be present."""
        indices = np.searchsorted(self.root_ids, root_ids)
        clipped = np.minimum(indices, self.root_ids.size - 1)
        if root_ids.size and (np.any(indices >= self.root_ids.size) or np.any(self.root_ids[clipped] != root_ids)):
            raise PositionsError("Root IDs are not part of the positions file.")
        return indices

    def class_value(self, field: str, index: int) -> str | None:
        categories, codes = self.classes[field]
        value = categories[int(codes[index])]
        return value or None


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def positions_dir(data_dir: Path) -> Path:
    return data_dir / "derived" / DIRECTORY


def graph_dir(data_dir: Path) -> Path:
    return data_dir / "derived" / GRAPH_FORMAT


def _graph(data_dir: Path) -> tuple[dict[str, Any], np.ndarray, np.ndarray, np.ndarray]:
    directory = graph_dir(data_dir)
    metadata_path = directory / "metadata.json"
    if not metadata_path.is_file():
        raise PositionsError(f"Missing sparse graph metadata: {metadata_path}; build the sparse graph first.")
    metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    if metadata.get("format") != GRAPH_FORMAT or metadata.get("dataset") != DATASET_NAME:
        raise PositionsError("Sparse graph identity mismatch.")
    roots = np.load(directory / "root_ids.npy", allow_pickle=False)
    if roots.ndim != 1 or roots.dtype != np.int64 or roots.size == 0 or np.any(np.diff(roots) <= 0):
        raise PositionsError("Sparse graph root IDs must be unique ascending int64 values.")
    indptr = np.load(directory / "csr_indptr.npy", mmap_mode="r")
    indices = np.load(directory / "csr_indices.npy", mmap_mode="r")
    if indptr.shape != (roots.size + 1,):
        raise PositionsError("Sparse graph CSR row pointer does not match the root count.")
    return metadata, roots, indptr, indices


def _annotation_md5_from_manifest(data_dir: Path) -> str | None:
    path = data_dir / "manifest.json"
    if not path.is_file():
        return None
    files = json.loads(path.read_text(encoding="utf-8")).get("files", {})
    for key in (ANNOTATIONS.as_posix(), str(ANNOTATIONS), str(ANNOTATIONS).replace("/", "\\")):
        if key in files:
            return files[key].get("md5")
    return None


def _read_annotations(path: Path) -> tuple[dict[int, dict[str, str]], int]:
    if not path.is_file():
        raise PositionsError(f"Missing FlyWire annotations: {path}")
    required = {"root_id", *POSITION_COLUMNS, *SOMA_COLUMNS, *CLASS_FIELDS}
    rows: dict[int, dict[str, str]] = {}
    with path.open(encoding="utf-8", newline="") as handle:
        reader = csv.DictReader(handle, delimiter="\t")
        if missing := required.difference(reader.fieldnames or ()):
            raise PositionsError(f"Annotation table lacks columns {sorted(missing)}")
        count = 0
        for row in reader:
            count += 1
            root = int(row["root_id"])
            if root in rows:
                raise PositionsError(f"Duplicate annotation root_id {root}.")
            rows[root] = {key: row[key] for key in required}
    return rows, count


def _coordinates(row: dict[str, str], columns: tuple[str, ...]) -> tuple[float, float, float] | None:
    values = [row[column].strip() for column in columns]
    if all(value == "" for value in values):
        return None
    if any(value == "" for value in values):
        raise PositionsError(f"Partially missing coordinates {columns} for root {row['root_id']}.")
    parsed = tuple(float(value) for value in values)
    if not all(np.isfinite(parsed)):
        raise PositionsError(f"Non-finite coordinates {columns} for root {row['root_id']}.")
    return parsed  # type: ignore[return-value]


def _neighbours(index: int, indptr: np.ndarray, indices: np.ndarray) -> np.ndarray:
    outgoing = np.asarray(indices[int(indptr[index]) : int(indptr[index + 1])], dtype=np.int64)
    incoming_positions = np.flatnonzero(np.asarray(indices) == index)
    incoming = np.searchsorted(np.asarray(indptr), incoming_positions, side="right").astype(np.int64) - 1
    return np.unique(np.concatenate([outgoing, incoming]))


def bounding_box(xyz_um: np.ndarray, mask: np.ndarray) -> dict[str, dict[str, float]]:
    points = xyz_um[mask].astype(np.float64)
    if points.size == 0:
        raise PositionsError("No annotated positions to bound.")
    low, high = points.min(axis=0), points.max(axis=0)
    return {
        "min": {axis: float(low[i]) for i, axis in enumerate("xyz")},
        "max": {axis: float(high[i]) for i, axis in enumerate("xyz")},
        "extent": {axis: float(high[i] - low[i]) for i, axis in enumerate("xyz")},
    }


def check_plausible(box: dict[str, dict[str, float]]) -> dict[str, Any]:
    failures = [axis for axis, (low, high) in PLAUSIBLE_EXTENT_UM.items() if not low <= box["extent"][axis] <= high]
    if failures:
        raise PositionsError(f"Whole-brain bounding box is implausible on {failures}: extent {box['extent']} um, expected {PLAUSIBLE_EXTENT_UM}.")
    return {"plausible_extent_um": {axis: list(bounds) for axis, bounds in PLAUSIBLE_EXTENT_UM.items()}, "passed": True}


def build(data_dir: Path, *, replace: bool = False) -> dict[str, Any]:
    started = time.perf_counter()
    target = positions_dir(data_dir)
    if target.exists() and not replace:
        raise PositionsError(f"Positions already exist: {target}; use --replace explicitly.")
    graph_metadata, roots, indptr, indices = _graph(data_dir)
    annotation_path = data_dir / ANNOTATIONS
    expected_md5 = _annotation_md5_from_manifest(data_dir)
    if expected_md5 is not None and file_md5(annotation_path) != expected_md5:
        raise PositionsError("Annotation table checksum does not match the acquisition manifest.")
    rows, row_count = _read_annotations(annotation_path)

    scale = np.array(VOXEL_SIZE_NM, dtype=np.float64) / 1000.0
    n = roots.size
    xyz = np.zeros((n, 3), dtype=np.float64)
    soma = np.full((n, 3), np.nan, dtype=np.float64)
    has_position = np.zeros(n, dtype=bool)
    class_values: dict[str, list[str]] = {field: [""] * n for field in CLASS_FIELDS}
    for index, root in enumerate(roots.tolist()):
        row = rows.get(root)
        if row is None:
            continue
        position = _coordinates(row, POSITION_COLUMNS)
        if position is None:
            continue
        xyz[index] = np.array(position) * scale
        has_position[index] = True
        if (soma_position := _coordinates(row, SOMA_COLUMNS)) is not None:
            soma[index] = np.array(soma_position) * scale
        for field in CLASS_FIELDS:
            class_values[field][index] = row[field].strip()

    root_set = set(roots.tolist())
    not_in_graph = sorted(root for root in rows if root not in root_set)
    centroid = xyz[has_position].mean(axis=0)
    placeholders = []
    for index in np.flatnonzero(~has_position).tolist():
        neighbours = _neighbours(index, indptr, indices)
        positioned = neighbours[has_position[neighbours]]
        if positioned.size:
            xyz[index] = xyz[positioned].mean(axis=0)
            method = "mean_of_annotated_neighbours"
        else:
            xyz[index] = centroid
            method = "whole_brain_centroid"
        placeholders.append({"root_id": int(roots[index]), "neighbour_count": int(neighbours.size), "annotated_neighbour_count": int(positioned.size), "placeholder_method": method})

    xyz_um = xyz.astype(np.float32)
    soma_um = soma.astype(np.float32)
    box = bounding_box(xyz_um, has_position)
    plausibility = check_plausible(box)

    staging = target.with_name(target.name + ".staging")
    if staging.exists():
        shutil.rmtree(staging)
    staging.mkdir(parents=True)
    np.save(staging / "root_ids.npy", roots)
    np.save(staging / "xyz_um.npy", xyz_um)
    np.save(staging / "soma_xyz_um.npy", soma_um)
    np.save(staging / "has_position.npy", has_position)
    classes = {}
    for field in CLASS_FIELDS:
        categories = sorted(set(class_values[field]) | {""})
        lookup = {value: code for code, value in enumerate(categories)}
        classes[field] = {"categories": categories, "codes": [lookup[value] for value in class_values[field]]}
    (staging / "classes.json").write_text(json.dumps({"fields": list(CLASS_FIELDS), "empty_means": "unknown or not annotated", "classes": classes}, separators=(",", ":")) + "\n", encoding="utf-8")
    files = {path.name: {"bytes": path.stat().st_size, "sha256": sha256(path)} for path in sorted(staging.iterdir()) if path.is_file()}
    metadata = {
        "format": FORMAT,
        "dataset": DATASET_NAME,
        "materialization": 783,
        "graph_format": GRAPH_FORMAT,
        "graph_metadata_sha256": sha256(graph_dir(data_dir) / "metadata.json"),
        "source_annotations": {"path": ANNOTATIONS.as_posix(), "sha256": sha256(annotation_path), "md5": file_md5(annotation_path), "manifest_md5_verified": expected_md5 is not None, "rows": row_count},
        "coordinate_unit": COORDINATE_UNIT,
        "voxel_size_nm": list(VOXEL_SIZE_NM),
        "unit_conversion": "um = voxel * voxel_size_nm / 1000, per axis",
        "voxel_size_verification": {"documentation": VOXEL_SIZE_SOURCE, "bounding_box_check": plausibility},
        "position_columns": list(POSITION_COLUMNS),
        "soma_columns": list(SOMA_COLUMNS),
        "counts": {
            "canonical_roots": int(n),
            "with_position": int(has_position.sum()),
            "without_annotation": len(placeholders),
            "with_soma": int(np.isfinite(soma_um[:, 0]).sum()),
            "annotation_rows_not_in_graph": len(not_in_graph),
        },
        "bounding_box_um": box,
        "roots_without_annotation": placeholders,
        "annotation_roots_not_in_graph": not_in_graph,
        "placeholder_note": "Roots without annotation have has_position=false; their xyz is a placeholder, never a real position.",
        "files": files,
        "export_seconds": time.perf_counter() - started,
    }
    (staging / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    if target.exists():
        shutil.rmtree(target)
    staging.rename(target)
    return metadata


def load(data_dir: Path | None = None, *, expected_root_ids: np.ndarray | None = None) -> BrainPositions:
    """Loads and checks the derived positions (checksums, shapes, dtypes, identity)."""
    directory = positions_dir(default_data_dir() if data_dir is None else data_dir)
    metadata_path = directory / "metadata.json"
    if not metadata_path.is_file():
        raise PositionsError(f"Missing positions metadata: {metadata_path}")
    try:
        metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise PositionsError(f"Malformed positions metadata: {exc}") from exc
    if metadata.get("format") != FORMAT or metadata.get("dataset") != DATASET_NAME or metadata.get("coordinate_unit") != COORDINATE_UNIT or metadata.get("voxel_size_nm") != list(VOXEL_SIZE_NM):
        raise PositionsError("Positions identity, unit or voxel size mismatch.")
    for filename, record in metadata.get("files", {}).items():
        path = directory / filename
        if not path.is_file() or sha256(path) != record.get("sha256"):
            raise PositionsError(f"Positions artifact checksum failure: {filename}")
    if set(metadata.get("files", {})) != {*(f"{name}.npy" for name in ARRAYS), "classes.json"}:
        raise PositionsError("Positions file list is incomplete.")
    arrays = {name: np.load(directory / f"{name}.npy", allow_pickle=False) for name in ARRAYS}
    for name, dtype in DTYPES.items():
        if arrays[name].dtype != dtype:
            raise PositionsError(f"Invalid dtype for {name}: {arrays[name].dtype}, expected {dtype}.")
    roots = arrays["root_ids"]; n = roots.size
    if roots.ndim != 1 or n == 0 or np.any(np.diff(roots) <= 0):
        raise PositionsError("Positions root IDs must be unique and ascending.")
    if expected_root_ids is not None and not np.array_equal(roots, expected_root_ids):
        raise PositionsError("Positions root IDs do not match the canonical graph.")
    if arrays["xyz_um"].shape != (n, 3) or arrays["soma_xyz_um"].shape != (n, 3) or arrays["has_position"].shape != (n,):
        raise PositionsError("Positions array shapes do not match the root count.")
    if not np.all(np.isfinite(arrays["xyz_um"])):
        raise PositionsError("Positions contain non-finite coordinates.")
    has_position = arrays["has_position"]
    listed = {int(entry["root_id"]) for entry in metadata.get("roots_without_annotation", [])}
    if listed != set(roots[~has_position].tolist()):
        raise PositionsError("Roots without position do not match the metadata list.")
    counts = metadata.get("counts", {})
    if counts.get("canonical_roots") != n or counts.get("with_position") != int(has_position.sum()):
        raise PositionsError("Positions counts do not match the metadata.")
    try:
        class_document = json.loads((directory / "classes.json").read_text(encoding="utf-8"))
        classes = {}
        for field in CLASS_FIELDS:
            entry = class_document["classes"][field]
            categories, codes = list(entry["categories"]), np.asarray(entry["codes"], dtype=np.int64)
            if codes.shape != (n,) or (n and (codes.min() < 0 or codes.max() >= len(categories))):
                raise PositionsError(f"Invalid class codes for {field}.")
            classes[field] = (categories, codes)
    except (KeyError, TypeError, ValueError) as exc:
        raise PositionsError(f"Malformed classes.json: {exc}") from exc
    for array in arrays.values():
        array.setflags(write=False)
    return BrainPositions(roots, arrays["xyz_um"], has_position, classes, metadata)


def validate(data_dir: Path | None = None) -> dict[str, Any]:
    """Full validation against the current sparse graph and annotation source."""
    data = default_data_dir() if data_dir is None else data_dir
    _, roots, _, _ = _graph(data)
    positions = load(data, expected_root_ids=roots)
    metadata = positions.metadata
    if metadata.get("graph_metadata_sha256") != sha256(graph_dir(data) / "metadata.json"):
        raise PositionsError("Sparse graph changed since the positions were exported.")
    if metadata.get("source_annotations", {}).get("sha256") != sha256(data / ANNOTATIONS):
        raise PositionsError("Annotation table changed since the positions were exported.")
    box = bounding_box(positions.xyz_um, positions.has_position)
    if box != metadata.get("bounding_box_um"):
        raise PositionsError("Bounding box does not match the metadata.")
    check_plausible(box)
    soma = np.load(positions_dir(data) / "soma_xyz_um.npy", allow_pickle=False)
    if int(np.isfinite(soma[:, 0]).sum()) != metadata["counts"]["with_soma"] or np.any(np.isfinite(soma[~positions.has_position])):
        raise PositionsError("Soma coordinates do not match the metadata.")
    return {"format": FORMAT, "canonical_roots": int(roots.size), "with_position": int(positions.has_position.sum()), "without_annotation": int((~positions.has_position).sum()), "coordinate_unit": COORDINATE_UNIT, "voxel_size_nm": list(VOXEL_SIZE_NM), "bounding_box_um": box}
