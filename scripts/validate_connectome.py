#!/usr/bin/env python
"""Fail-loudly validation and inspection for real FlyWire FAFB v783 files."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from flywire_data import ConnectomeError, default_data_dir, print_report, validate_real_connectome


def main() -> int:
    parser = argparse.ArgumentParser(description="Validate local real FlyWire FAFB v783 bootstrap data.")
    parser.add_argument("--data-dir", type=Path, default=default_data_dir())
    parser.add_argument("--full-synapses", action="store_true", help="Also require the optional individual-synapse table.")
    parser.add_argument("--skip-checksums", action="store_true", help="Skip MD5 rehashing; intended only for development diagnostics.")
    parser.add_argument("--mock-brain", action="store_true", help="Restricted to an explicit test fixture; never reports FlyWire data.")
    args = parser.parse_args()
    if args.mock_brain:
        print("MOCK — NOT FLYWIRE. Mock-brain validation is restricted to automated-test fixtures.")
        return 0
    try:
        report = validate_real_connectome(args.data_dir, include_full_synapses=args.full_synapses, check_checksums=not args.skip_checksums)
    except ConnectomeError as exc:
        print(f"VALIDATION FAILED: {exc}", file=sys.stderr)
        return 2
    print_report(report)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
