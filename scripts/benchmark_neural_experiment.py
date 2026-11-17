"""Measure real-data Phase 4 selection and simulation performance without writing output."""
from __future__ import annotations
import argparse, json, time
from pathlib import Path
from neural_dynamics import default_config_path, default_data_dir, load_config, select_subgraph, simulate

def main() -> None:
    parser = argparse.ArgumentParser(); parser.add_argument("--data-dir", type=Path, default=default_data_dir()); parser.add_argument("--config", type=Path, default=default_config_path()); parser.add_argument("--synapse-sign-mode", choices=("unsigned", "transmitter")); args = parser.parse_args()
    config = load_config(args.config, sign_mode=args.synapse_sign_mode); started = time.perf_counter(); subgraph = select_subgraph(args.data_dir, config); initialization = time.perf_counter() - started; result = simulate(subgraph, config)
    print(json.dumps({"status": result.status, "neurons": int(subgraph.root_ids.size), "source_edge_rows": subgraph.row_edge_count, "represented_synapses": subgraph.represented_synapses, "initialization_seconds": initialization, **result.timings, "simulated_ms": result.total_steps * config["lif"]["dt_ms"], "simulated_ms_per_wall_second": result.total_steps * config["lif"]["dt_ms"] / max(result.timings["simulation_wall_seconds"], 1e-12)}, indent=2))

if __name__ == "__main__": main()
