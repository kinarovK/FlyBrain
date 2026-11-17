"""Run one real-data Phase 4 neural experiment."""
from __future__ import annotations
import argparse, json
from pathlib import Path
from neural_dynamics import default_config_path, default_data_dir, load_config, select_subgraph, simulate, write_experiment

def main() -> None:
    parser = argparse.ArgumentParser(description="Run a real FlyWire v783 Phase 4 LIF experiment.")
    parser.add_argument("--experiment-id", required=True)
    parser.add_argument("--data-dir", type=Path, default=default_data_dir())
    parser.add_argument("--config", type=Path, default=default_config_path())
    parser.add_argument("--synapse-sign-mode", choices=("unsigned", "transmitter"))
    parser.add_argument("--no-stimulus-control", action="store_true")
    args = parser.parse_args()
    config = load_config(args.config, sign_mode=args.synapse_sign_mode, no_stimulus=args.no_stimulus_control)
    subgraph = select_subgraph(args.data_dir, config); result = simulate(subgraph, config)
    path = write_experiment(args.data_dir, args.experiment_id, subgraph, result, config)
    print(json.dumps({"experiment_dir": str(path), "status": result.status, "spikes": int(result.spike_steps.size)}, indent=2))

if __name__ == "__main__": main()
