#!/usr/bin/env python
"""Download static, real FlyWire FAFB v783 bootstrap data.

The acknowledgement flag intentionally makes acceptance of the FlyWire data
principles explicit. This script never contacts an exchange or downloads EM
volume data.
"""

from __future__ import annotations

import argparse
import json
import shutil
import sys
import urllib.error
import urllib.request
from pathlib import Path

from flywire_data import ANNOTATIONS, ANNOTATION_REPOSITORY, ARTIFACTS, DATASET_NAME, default_data_dir, file_md5, utc_now

PRINCIPLES_URL = "https://edit.flywire.ai/principles.html"
GITHUB_COMMIT_API = "https://api.github.com/repos/flyconnectome/flywire_annotations/commits/"
ANNOTATION_RAW = "https://raw.githubusercontent.com/flyconnectome/flywire_annotations/{ref}/supplemental_files/Supplemental_file1_neuron_annotations.tsv"


def request(url: str, headers: dict[str, str] | None = None):
    request_headers = {"User-Agent": "FlyTrader-data-bootstrap/0.1"}
    if headers:
        request_headers.update(headers)
    return urllib.request.urlopen(urllib.request.Request(url, headers=request_headers), timeout=60)


def resolve_annotation_ref(ref: str) -> str:
    try:
        with request(GITHUB_COMMIT_API + ref) as response:
            payload = json.load(response)
    except (urllib.error.URLError, urllib.error.HTTPError, json.JSONDecodeError) as exc:
        raise RuntimeError(f"Could not resolve annotation revision {ref!r} from GitHub: {exc}") from exc
    sha = payload.get("sha")
    if not isinstance(sha, str) or len(sha) < 12:
        raise RuntimeError(f"GitHub did not return a commit SHA for annotation ref {ref!r}.")
    return sha


def download(url: str, destination: Path, expected_md5: str | None) -> dict[str, str | int]:
    destination.parent.mkdir(parents=True, exist_ok=True)
    partial = destination.with_name(destination.name + ".part")
    if destination.exists() and expected_md5 and file_md5(destination).lower() == expected_md5.lower():
        print(f"Verified existing {destination.name}; skipping download.", flush=True)
        return {"url": url, "md5": expected_md5, "bytes": destination.stat().st_size}
    offset = partial.stat().st_size if partial.exists() else 0
    print(f"Downloading {destination.name} from {url} (resume offset {offset:,} bytes)", flush=True)
    try:
        headers = {"Range": f"bytes={offset}-"} if offset else None
        with request(url, headers) as response:
            # A server that ignores Range must not append a full response to a
            # partial file; restart it safely instead.
            status = getattr(response, "status", response.getcode())
            mode = "ab" if offset and status == 206 else "wb"
            if offset and status != 206:
                print("Server did not honor Range; restarting this artifact.", flush=True)
            with partial.open(mode) as output:
                shutil.copyfileobj(response, output, length=1024 * 1024)
    except (OSError, urllib.error.URLError, urllib.error.HTTPError) as exc:
        raise RuntimeError(
            f"Download failed for {destination.name}: {exc}. "
            "Any .part file is retained for a resumable re-run."
        ) from exc
    actual = file_md5(partial)
    if expected_md5 and actual.lower() != expected_md5.lower():
        partial.unlink(missing_ok=True)
        raise RuntimeError(f"Checksum failure for {destination.name}: expected {expected_md5}, got {actual}")
    partial.replace(destination)
    return {"url": url, "md5": actual, "bytes": destination.stat().st_size}


def plan(profile: str, annotation_ref: str) -> list[tuple[Path, dict[str, str]]]:
    names = ["proofread_root_ids_783.npy", "proofread_connections_783.feather"]
    if profile == "full":
        names.append("optional/flywire_synapses_783.feather")
    downloads = [(Path(name), ARTIFACTS[name]) for name in names]
    downloads.append(
        (
            ANNOTATIONS,
            {
                "url": ANNOTATION_RAW.format(ref=annotation_ref),
                "md5": "",
                "expected_size": "varies by pinned Git revision",
            },
        )
    )
    return downloads


def main() -> int:
    parser = argparse.ArgumentParser(description="Acquire static real FlyWire FAFB v783 bootstrap artifacts.")
    parser.add_argument("--profile", choices=("compact", "full"), default="compact")
    parser.add_argument("--data-dir", type=Path, default=default_data_dir())
    parser.add_argument("--annotation-ref", default="main", help="Git ref to resolve and pin (default: main).")
    parser.add_argument("--agree-flywire-principles", action="store_true", help="Confirm you read and agree to FlyWire data principles.")
    parser.add_argument("--dry-run", action="store_true", help="Print sources without downloading.")
    args = parser.parse_args()

    if not args.agree_flywire_principles:
        parser.error(f"Refusing download until you read and accept FlyWire principles: {PRINCIPLES_URL}. Re-run with --agree-flywire-principles.")
    if args.profile == "full":
        print("Full profile includes the optional ~9.5 GB individual-synapse table.")
    if args.dry_run:
        for relative, metadata in plan(args.profile, args.annotation_ref):
            print(f"{relative}: {metadata['url']} ({metadata['expected_size']})")
        return 0

    try:
        annotation_sha = resolve_annotation_ref(args.annotation_ref)
        downloaded = {}
        for relative, metadata in plan(args.profile, annotation_sha):
            downloaded[str(relative)] = download(metadata["url"], args.data_dir / relative, metadata["md5"] or None)
    except RuntimeError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2

    manifest = {
        "dataset": DATASET_NAME,
        "materialization": 783,
        "profile": args.profile,
        "retrieved_at": utc_now(),
        "flywire_principles_acknowledged": True,
        "flywire_principles_url": PRINCIPLES_URL,
        "annotation_repository": ANNOTATION_REPOSITORY,
        "annotation_requested_ref": args.annotation_ref,
        "annotation_commit": annotation_sha,
        "files": downloaded,
    }
    args.data_dir.mkdir(parents=True, exist_ok=True)
    (args.data_dir / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Downloaded {DATASET_NAME} {args.profile} profile to {args.data_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
