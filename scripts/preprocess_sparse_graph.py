from __future__ import annotations
import argparse, json, sys
from pathlib import Path
from sparse_graph import SparseGraphError, build
from flywire_data import default_data_dir
parser=argparse.ArgumentParser(description="Build the real FlyWire v783 induced sparse graph."); parser.add_argument("--data-dir",type=Path,default=default_data_dir()); parser.add_argument("--replace",action="store_true"); args=parser.parse_args()
try: print(json.dumps(build(args.data_dir,replace=args.replace),indent=2))
except SparseGraphError as exc: print(f"PREPROCESS FAILED: {exc}",file=sys.stderr); raise SystemExit(2)
