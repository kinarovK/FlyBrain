"""Run one real FlyWire v783 Phase 5 scaling experiment."""
from __future__ import annotations
import argparse, json
from pathlib import Path
from neural_dynamics import default_config_path, default_data_dir, load_config
from whole_brain_scaling import default_whole_config_path, load_whole_config, run_tier, select_tier, write_whole_brain_experiment

def main() -> None:
    parser = argparse.ArgumentParser(description="Run a real v783 Phase 5 CPU scaling tier.")
    parser.add_argument("--tier", required=True, choices=("reference", "tier_5k", "tier_20k", "tier_50k", "whole"))
    parser.add_argument("--experiment-id", required=True)
    parser.add_argument("--data-dir", type=Path, default=default_data_dir())
    parser.add_argument("--phase4-config", type=Path, default=default_config_path())
    parser.add_argument("--whole-config", type=Path, default=default_whole_config_path())
    parser.add_argument("--synapse-sign-mode", choices=("unsigned", "transmitter"))
    parser.add_argument("--no-stimulus-control", action="store_true")
    args = parser.parse_args()
    phase4 = load_config(args.phase4_config, sign_mode=args.synapse_sign_mode, no_stimulus=args.no_stimulus_control)
    whole = load_whole_config(args.whole_config); tier = select_tier(args.data_dir, whole, phase4, args.tier); result = run_tier(tier, phase4, whole)
    path = write_whole_brain_experiment(args.data_dir, args.experiment_id, tier, result, phase4, whole)
    print(json.dumps({"experiment_dir": str(path), "tier": args.tier, "status": result.status, "recorded_events": int(result.spike_steps.size), "events_truncated": result.events_truncated}, indent=2))

if __name__ == "__main__": main()
