from __future__ import annotations
import argparse, json, sys
from pathlib import Path
from brain_positions import PositionsError, build, validate
from flywire_data import default_data_dir
parser = argparse.ArgumentParser(description="Export FlyWire v783 neuron positions (um) in canonical graph order.")
parser.add_argument("--data-dir", type=Path, default=default_data_dir())
parser.add_argument("--replace", action="store_true", help="Replace existing positions.")
parser.add_argument("--validate", action="store_true", help="Only validate existing positions; write nothing.")
args = parser.parse_args()
try:
    result = validate(args.data_dir) if args.validate else build(args.data_dir, replace=args.replace)
    print(json.dumps(result, indent=2))
except PositionsError as exc:
    print(f"{'VALIDATION' if args.validate else 'EXPORT'} FAILED: {exc}", file=sys.stderr); raise SystemExit(2)
