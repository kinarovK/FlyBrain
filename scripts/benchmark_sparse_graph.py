from __future__ import annotations
import argparse, json, sys
from pathlib import Path
from sparse_graph import SparseGraphError, benchmark
from flywire_data import default_data_dir
parser=argparse.ArgumentParser(description="Benchmark mathematical sparse FlyWire graph operations."); parser.add_argument("--data-dir",type=Path,default=default_data_dir()); parser.add_argument("--repeats",type=int,default=5); args=parser.parse_args()
try: print(json.dumps(benchmark(args.data_dir,repeats=args.repeats),indent=2))
except SparseGraphError as exc: print(f"BENCHMARK FAILED: {exc}",file=sys.stderr); raise SystemExit(2)
