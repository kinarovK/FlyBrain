from __future__ import annotations
import argparse, json, sys
from pathlib import Path
from brain_positions import PositionsError, validate
from flywire_data import default_data_dir
parser = argparse.ArgumentParser(description="Validate derived FlyWire v783 neuron positions."); parser.add_argument("--data-dir", type=Path, default=default_data_dir()); args = parser.parse_args()
try: print(json.dumps(validate(args.data_dir), indent=2))
except PositionsError as exc: print(f"VALIDATION FAILED: {exc}", file=sys.stderr); raise SystemExit(2)
