#!/usr/bin/env python
"""Write derived inspection metadata from a validated real FlyWire dataset."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from flywire_data import ConnectomeError, default_data_dir, validate_real_connectome, write_inspection


def main() -> int:
    parser = argparse.ArgumentParser(description="Create inspection metadata; never synthesizes a connectome.")
    parser.add_argument("--data-dir", type=Path, default=default_data_dir())
    args = parser.parse_args()
    try:
        report = validate_real_connectome(args.data_dir)
        output = write_inspection(args.data_dir, report)
    except ConnectomeError as exc:
        print(f"PREPROCESS FAILED: {exc}", file=sys.stderr)
        return 2
    print(f"Wrote validated inspection metadata: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
