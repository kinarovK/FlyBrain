"""Profile a real Phase 5 CPU scaling tier without writing experiment artifacts."""
from __future__ import annotations
import argparse, json, time
from pathlib import Path
from neural_dynamics import default_config_path, default_data_dir, load_config
from whole_brain_scaling import default_whole_config_path, estimate_resources, load_whole_config, run_tier, select_tier, summarize_tier

def main() -> None:
    parser = argparse.ArgumentParser(); parser.add_argument("--tier", required=True, choices=("reference", "tier_5k", "tier_20k", "tier_50k", "whole")); parser.add_argument("--data-dir", type=Path, default=default_data_dir()); parser.add_argument("--phase4-config", type=Path, default=default_config_path()); parser.add_argument("--whole-config", type=Path, default=default_whole_config_path()); parser.add_argument("--output", type=Path, help="Write the profiled summary JSON atomically to this path."); args = parser.parse_args()
    config_started = time.perf_counter(); phase4 = load_config(args.phase4_config); whole = load_whole_config(args.whole_config); config_seconds = time.perf_counter() - config_started; tier = select_tier(args.data_dir, whole, phase4, args.tier); estimate = estimate_resources(tier, whole); result = run_tier(tier, phase4, whole); summary = summarize_tier(tier, result, phase4, whole, estimate); summary["performance"]["config_load_seconds"] = config_seconds
    output = json.dumps(summary, indent=2) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True); args.output.write_text(output, encoding="utf-8")
    print(output, end="")

if __name__ == "__main__": main()
