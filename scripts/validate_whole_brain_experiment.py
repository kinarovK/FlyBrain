"""Validate a Phase 5 whole-brain-v1 experiment directory."""
from __future__ import annotations
import argparse, json
from pathlib import Path
from neural_dynamics import default_data_dir
from whole_brain_scaling import validate_whole_brain_experiment

def main() -> None:
    parser = argparse.ArgumentParser(); parser.add_argument("experiment_dir", type=Path); parser.add_argument("--data-dir", type=Path, default=default_data_dir()); args = parser.parse_args()
    print(json.dumps(validate_whole_brain_experiment(args.experiment_dir, args.data_dir), indent=2))

if __name__ == "__main__": main()
