"""Validate an existing Phase 4 neural experiment directory."""
from __future__ import annotations
import argparse, json
from pathlib import Path
from neural_dynamics import validate_experiment

def main() -> None:
    parser = argparse.ArgumentParser(); parser.add_argument("experiment_dir", type=Path); args = parser.parse_args()
    print(json.dumps(validate_experiment(args.experiment_dir), indent=2))

if __name__ == "__main__": main()
