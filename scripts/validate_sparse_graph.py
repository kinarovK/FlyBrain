from __future__ import annotations
import argparse, json, sys
from pathlib import Path
from sparse_graph import SparseGraphError, validate
from flywire_data import default_data_dir
parser=argparse.ArgumentParser(description="Validate a real derived FlyWire v783 sparse graph."); parser.add_argument("--data-dir",type=Path,default=default_data_dir()); parser.add_argument("--samples",type=int,default=64); args=parser.parse_args()
try: print(json.dumps(validate(args.data_dir,samples=args.samples),indent=2))
except SparseGraphError as exc: print(f"VALIDATION FAILED: {exc}",file=sys.stderr); raise SystemExit(2)
