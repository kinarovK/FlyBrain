"""Shared, strict FlyWire FAFB v783 acquisition and validation helpers.

The normal path only accepts real files described by a local provenance
manifest.  Test fixtures may use the explicit mock mode; no function creates
random neurons or edges.
"""

from __future__ import annotations

import csv
import hashlib
import json
from collections import Counter
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, Iterable

import numpy as np

DATASET_NAME = "FlyWire FAFB v783"
ZENODO_RECORD = "https://zenodo.org/records/10676866"
ANNOTATION_REPOSITORY = "https://github.com/flyconnectome/flywire_annotations"

ROOT_IDS = "proofread_root_ids_783.npy"
CONNECTIONS = "proofread_connections_783.feather"
ANNOTATIONS = Path("annotations") / "Supplemental_file1_neuron_annotations.tsv"
FULL_SYNAPSES = Path("optional") / "flywire_synapses_783.feather"

ARTIFACTS = {
    ROOT_IDS: {
        "url": f"{ZENODO_RECORD}/files/{ROOT_IDS}?download=1",
        "md5": "e0e6c19732fd8c7a4e39a2d170105421",
        "expected_size": "1.1 MB",
    },
    CONNECTIONS: {
        "url": f"{ZENODO_RECORD}/files/{CONNECTIONS}?download=1",
        "md5": "f48f972d262323a102aed49af1396b8a",
        "expected_size": "852.0 MB",
    },
    str(FULL_SYNAPSES): {
        "url": f"{ZENODO_RECORD}/files/{FULL_SYNAPSES.name}?download=1",
        "md5": "f8f1b97c9d4b0ea9b4c8b287f6b99091",
        "expected_size": "9.5 GB",
    },
}

REQUIRED_EDGE_COLUMNS = {
    "pre_pt_root_id",
    "post_pt_root_id",
    "neuropil",
    "syn_count",
}
TRANSMITTER_COLUMNS = ("gaba_avg", "ach_avg", "glut_avg", "oct_avg", "ser_avg", "da_avg")
POPULATION_TERMS = ("sensory", "descending", "motor", "mushroom", "dopamin")


class ConnectomeError(RuntimeError):
    """Raised when files cannot substantiate a real FlyWire v783 dataset."""


@dataclass(frozen=True)
class ValidationReport:
    dataset: str
    profile: str
    neuron_count: int
    connection_count: int
    synapse_count: int
    pre_endpoints_in_root_ids: int
    post_endpoints_in_root_ids: int
    edge_columns: list[str]
    annotation_columns: list[str]
    neurotransmitter_columns: list[str]
    categories: dict[str, list[tuple[str, int]]]
    population_candidates: dict[str, list[str]]
    neurons: list[dict[str, str]]
    connections: list[dict[str, Any]]

    def to_dict(self) -> dict[str, Any]:
        return {
            "dataset": self.dataset,
            "profile": self.profile,
            "neuron_count": self.neuron_count,
            "connection_count": self.connection_count,
            "synapse_count": self.synapse_count,
            "pre_endpoints_in_root_ids": self.pre_endpoints_in_root_ids,
            "post_endpoints_in_root_ids": self.post_endpoints_in_root_ids,
            "edge_columns": self.edge_columns,
            "annotation_columns": self.annotation_columns,
            "neurotransmitter_columns": self.neurotransmitter_columns,
            "categories": self.categories,
            "population_candidates": self.population_candidates,
            "example_neurons": self.neurons,
            "example_connections": self.connections,
        }


def default_data_dir() -> Path:
    return Path(__file__).resolve().parents[1] / "data" / "flywire"


def manifest_path(data_dir: Path) -> Path:
    return data_dir / "manifest.json"


def file_md5(path: Path, chunk_size: int = 1024 * 1024) -> str:
    digest = hashlib.md5()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(chunk_size), b""):
            digest.update(chunk)
    return digest.hexdigest()


def utc_now() -> str:
    return datetime.now(UTC).isoformat()


def load_manifest(data_dir: Path) -> dict[str, Any]:
    path = manifest_path(data_dir)
    if not path.is_file():
        raise ConnectomeError(
            f"Missing {path}. Run download_connectome.py with "
            "--agree-flywire-principles; no mock data is accepted by default."
        )
    try:
        manifest = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ConnectomeError(f"Malformed provenance manifest {path}: {exc}") from exc
    if manifest.get("dataset") != DATASET_NAME or manifest.get("materialization") != 783:
        raise ConnectomeError(
            "Manifest does not identify the required real dataset "
            f"{DATASET_NAME}: {manifest.get('dataset')!r} / {manifest.get('materialization')!r}"
        )
    return manifest


def required_files(profile: str, include_full_synapses: bool = False) -> list[Path]:
    if profile not in {"compact", "full"}:
        raise ConnectomeError(f"Unknown profile {profile!r}; expected compact or full.")
    files = [Path(ROOT_IDS), Path(CONNECTIONS), ANNOTATIONS]
    if profile == "full" or include_full_synapses:
        files.append(FULL_SYNAPSES)
    return files


def require_files(data_dir: Path, profile: str, include_full_synapses: bool = False) -> None:
    absent = [str(data_dir / item) for item in required_files(profile, include_full_synapses) if not (data_dir / item).is_file()]
    if absent:
        raise ConnectomeError("Required FlyWire files are missing:\n  " + "\n  ".join(absent))


def _required_manifest_metadata(manifest: dict[str, Any], profile: str, include_full_synapses: bool) -> dict[str, dict[str, str]]:
    """Return immutable source expectations for every required local artifact."""
    expected: dict[str, dict[str, str]] = {}
    for relative in required_files(profile, include_full_synapses):
        key = str(relative)
        if key == str(ANNOTATIONS):
            commit = manifest.get("annotation_commit")
            if not isinstance(commit, str) or len(commit) != 40 or any(char not in "0123456789abcdef" for char in commit.lower()):
                raise ConnectomeError("Manifest has no valid immutable annotation_commit SHA.")
            expected[key] = {
                "url": f"https://raw.githubusercontent.com/flyconnectome/flywire_annotations/{commit}/supplemental_files/Supplemental_file1_neuron_annotations.tsv",
                "md5": "dynamic",
            }
        else:
            artifact = ARTIFACTS.get(key)
            if artifact is None:
                raise ConnectomeError(f"No canonical artifact definition exists for required file {key}.")
            expected[key] = {"url": artifact["url"], "md5": artifact["md5"]}
    return expected


def verify_manifest_checksums(
    data_dir: Path, manifest: dict[str, Any], profile: str, include_full_synapses: bool = False
) -> None:
    files = manifest.get("files")
    if not isinstance(files, dict):
        raise ConnectomeError("Manifest has no files section.")
    for relative, source in _required_manifest_metadata(manifest, profile, include_full_synapses).items():
        metadata = files.get(relative)
        if not isinstance(metadata, dict):
            raise ConnectomeError(f"Manifest omits required artifact metadata for {relative}.")
        if metadata.get("url") != source["url"]:
            raise ConnectomeError(f"Manifest source URL for {relative} does not match the canonical pinned source.")
        expected = metadata.get("md5")
        if not isinstance(expected, str) or len(expected) != 32 or any(char not in "0123456789abcdef" for char in expected.lower()):
            raise ConnectomeError(f"Manifest has no valid MD5 for required artifact {relative}.")
        if source["md5"] != "dynamic" and expected.lower() != source["md5"].lower():
            raise ConnectomeError(f"Manifest checksum for {relative} does not match the published canonical checksum.")
        path = data_dir / relative
        if not path.is_file():
            raise ConnectomeError(f"Manifested file is missing: {path}")
        actual = file_md5(path)
        if actual.lower() != expected.lower():
            raise ConnectomeError(
                f"Checksum failure for {path.name}: expected {expected}, received {actual}. "
                "Delete the file and re-run the downloader."
            )


def _annotation_rows(path: Path) -> tuple[list[str], list[dict[str, str]], dict[str, Counter[str]], dict[str, list[str]]]:
    try:
        with path.open("r", encoding="utf-8-sig", newline="") as handle:
            reader = csv.DictReader(handle, delimiter="\t")
            if not reader.fieldnames:
                raise ConnectomeError(f"Annotation file has no TSV header: {path}")
            columns = [field.strip() for field in reader.fieldnames]
            key_columns = [
                name
                for name in columns
                if name.lower()
                in {"root_id", "cell_type", "super_class", "superclass", "nt_type", "neurotransmitter", "top_nt", "known_nt"}
            ]
            counters = {name: Counter() for name in key_columns}
            examples: list[dict[str, str]] = []
            candidate_labels = {term: [] for term in POPULATION_TERMS}
            seen_candidates = {term: set() for term in POPULATION_TERMS}
            for row in reader:
                normalized = {key.strip(): (value or "").strip() for key, value in row.items() if key}
                for name in key_columns:
                    value = normalized.get(name, "")
                    if value:
                        counters[name][value] += 1
                text = " ".join(normalized.values()).lower()
                label = normalized.get("cell_type") or normalized.get("super_class") or normalized.get("root_id", "")
                for term in POPULATION_TERMS:
                    cell_type = normalized.get("cell_type", "")
                    is_mushroom_candidate = term == "mushroom" and (
                        term in text or cell_type.startswith(("KC", "MBON"))
                    )
                    is_dopamine_candidate = term == "dopamin" and (
                        term in text or cell_type.startswith(("PAM", "PPM", "PPL"))
                    )
                    if (
                        (term in text or is_mushroom_candidate or is_dopamine_candidate)
                        and label
                        and label not in seen_candidates[term]
                        and len(candidate_labels[term]) < 10
                    ):
                        candidate_labels[term].append(label)
                        seen_candidates[term].add(label)
                if len(examples) < 3:
                    examples.append({key: normalized.get(key, "") for key in columns[: min(8, len(columns))]})
    except UnicodeDecodeError as exc:
        raise ConnectomeError(f"Annotation TSV is not UTF-8 readable: {path}: {exc}") from exc
    if not any(name.lower() == "root_id" for name in columns):
        raise ConnectomeError(f"Annotation TSV lacks root_id column: found {columns}")
    return columns, examples, counters, candidate_labels


def _arrow_modules() -> tuple[Any, Any]:
    try:
        import pyarrow as pa
        import pyarrow.ipc as ipc
    except ImportError as exc:
        raise ConnectomeError(
            "PyArrow is required to inspect real FlyWire Feather files. "
            "Install brain/requirements.txt."
        ) from exc
    return pa, ipc


def _stream_edges(path: Path, root_ids: np.ndarray) -> tuple[list[str], int, int, int, int, list[dict[str, Any]]]:
    _, ipc = _arrow_modules()
    try:
        source = ipc.open_file(path)
    except Exception as exc:
        raise ConnectomeError(f"Cannot open Feather connectivity table {path}: {exc}") from exc
    columns = list(source.schema.names)
    missing = REQUIRED_EDGE_COLUMNS.difference(columns)
    if missing:
        raise ConnectomeError(f"Connectivity table lacks required columns {sorted(missing)}; found {columns}")
    sorted_root_ids = np.sort(root_ids)
    row_count = 0
    synapse_count = 0
    pre_endpoints_in_root_ids = 0
    post_endpoints_in_root_ids = 0
    examples: list[dict[str, Any]] = []
    try:
        for batch_index in range(source.num_record_batches):
            batch = source.get_batch(batch_index)
            names = batch.schema.names
            pre = batch.column(names.index("pre_pt_root_id")).to_numpy(zero_copy_only=False)
            post = batch.column(names.index("post_pt_root_id")).to_numpy(zero_copy_only=False)
            syn = batch.column(names.index("syn_count")).to_numpy(zero_copy_only=False)
            if len(pre) != len(post) or len(pre) != len(syn):
                raise ConnectomeError(f"Connectivity batch {batch_index} has mismatched column lengths.")
            if not np.issubdtype(pre.dtype, np.integer) or not np.issubdtype(post.dtype, np.integer):
                raise ConnectomeError("Connectivity root-ID columns must be integer typed.")
            if not np.issubdtype(syn.dtype, np.number) or np.any(syn < 1):
                raise ConnectomeError("Connectivity syn_count must be numeric and positive.")
            # The published compact relation table can contain endpoints not in
            # the companion proofread-root-ID array. Canonical checksums and
            # schema validate authenticity; record coverage without filtering.
            pre_endpoints_in_root_ids += int(np.count_nonzero(_root_id_membership(pre, sorted_root_ids)))
            post_endpoints_in_root_ids += int(np.count_nonzero(_root_id_membership(post, sorted_root_ids)))
            row_count += len(pre)
            synapse_count += int(np.sum(syn, dtype=np.int64))
            if len(examples) < 3:
                for row in batch.to_pylist()[: 3 - len(examples)]:
                    examples.append(row)
    except ConnectomeError:
        raise
    except Exception as exc:
        raise ConnectomeError(f"Malformed connectivity table {path}: {exc}") from exc
    if row_count == 0:
        raise ConnectomeError("Connectivity table contains no records.")
    return columns, row_count, synapse_count, pre_endpoints_in_root_ids, post_endpoints_in_root_ids, examples


def _root_id_membership(values: np.ndarray, sorted_root_ids: np.ndarray) -> np.ndarray:
    """Vectorized exact membership mask for every endpoint in a large batch."""
    positions = np.searchsorted(sorted_root_ids, values)
    in_range = positions < sorted_root_ids.size
    # `take(..., mode='clip')` keeps out-of-range positions safe to inspect;
    # `in_range` still marks them invalid.
    matched = np.take(sorted_root_ids, positions, mode="clip") == values
    return in_range & matched


def _validate_full_synapse_schema(path: Path) -> None:
    """Check the optional individual-synapse artifact without materializing it."""
    _, ipc = _arrow_modules()
    try:
        source = ipc.open_file(path)
        columns = set(source.schema.names)
    except Exception as exc:
        raise ConnectomeError(f"Cannot open optional individual-synapse table {path}: {exc}") from exc
    required = {"id", "pre_pt_root_id", "post_pt_root_id", "neuropil"}
    missing = required.difference(columns)
    if missing:
        raise ConnectomeError(
            f"Optional individual-synapse table lacks required columns {sorted(missing)}; found {sorted(columns)}"
        )


def validate_real_connectome(
    data_dir: Path, *, include_full_synapses: bool = False, check_checksums: bool = True
) -> ValidationReport:
    manifest = load_manifest(data_dir)
    profile = str(manifest.get("profile", "compact"))
    require_files(data_dir, profile, include_full_synapses)
    if check_checksums:
        verify_manifest_checksums(data_dir, manifest, profile, include_full_synapses)

    root_path = data_dir / ROOT_IDS
    try:
        root_ids = np.load(root_path, allow_pickle=False)
    except Exception as exc:
        raise ConnectomeError(f"Cannot load root-ID array {root_path}: {exc}") from exc
    if root_ids.ndim != 1 or root_ids.size == 0 or not np.issubdtype(root_ids.dtype, np.integer):
        raise ConnectomeError("proofread_root_ids_783.npy must be a non-empty one-dimensional integer array.")
    if np.unique(root_ids).size != root_ids.size:
        raise ConnectomeError("proofread_root_ids_783.npy contains duplicate IDs.")

    annotation_columns, neuron_examples, counters, population_candidates = _annotation_rows(data_dir / ANNOTATIONS)
    (
        edge_columns,
        connection_count,
        synapse_count,
        pre_endpoints_in_root_ids,
        post_endpoints_in_root_ids,
        connection_examples,
    ) = _stream_edges(data_dir / CONNECTIONS, root_ids)
    if include_full_synapses or profile == "full":
        _validate_full_synapse_schema(data_dir / FULL_SYNAPSES)
    categories = {name: counter.most_common(12) for name, counter in counters.items()}
    neurotransmitter_columns = [column for column in edge_columns if column in TRANSMITTER_COLUMNS]

    return ValidationReport(
        dataset=DATASET_NAME,
        profile=profile,
        neuron_count=int(root_ids.size),
        connection_count=connection_count,
        synapse_count=synapse_count,
        pre_endpoints_in_root_ids=pre_endpoints_in_root_ids,
        post_endpoints_in_root_ids=post_endpoints_in_root_ids,
        edge_columns=edge_columns,
        annotation_columns=annotation_columns,
        neurotransmitter_columns=neurotransmitter_columns,
        categories=categories,
        population_candidates=population_candidates,
        neurons=neuron_examples,
        connections=connection_examples,
    )


def write_inspection(data_dir: Path, report: ValidationReport) -> Path:
    output = data_dir / "inspection.json"
    output.write_text(json.dumps(report.to_dict(), indent=2, default=str) + "\n", encoding="utf-8")
    return output


def print_report(report: ValidationReport) -> None:
    print(f"Dataset: {report.dataset}")
    print(f"Profile: {report.profile} (real proofread connectivity; not a mock brain)")
    print(f"Neurons: {report.neuron_count:,}")
    print(f"Connections: {report.connection_count:,}")
    print(f"Synapses represented: {report.synapse_count:,}")
    print(
        "Proofread-root endpoint coverage: "
        f"pre {report.pre_endpoints_in_root_ids:,}/{report.connection_count:,}; "
        f"post {report.post_endpoints_in_root_ids:,}/{report.connection_count:,}"
    )
    print("Neurotransmitter edge columns: " + (", ".join(report.neurotransmitter_columns) or "none"))
    print("Annotation columns: " + ", ".join(report.annotation_columns))
    print("Neuron classes / categories:")
    for name, values in report.categories.items():
        pretty = ", ".join(f"{value} ({count:,})" for value, count in values[:8]) or "none"
        print(f"  {name}: {pretty}")
    print("Annotation candidate labels (for later research; not biological claims):")
    for term, values in report.population_candidates.items():
        suffix = " (includes KC/MBON naming heuristic)" if term == "mushroom" else ""
        print(f"  {term}: " + (", ".join(values) if values else "none found") + suffix)
    print("Example neuron records:")
    for row in report.neurons:
        print("  " + json.dumps(row, sort_keys=True))
    print("Example connectivity records:")
    for row in report.connections:
        print("  " + json.dumps(row, sort_keys=True, default=str))
