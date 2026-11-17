"""Build Phase 5 scale-study diagnostics from validated experiment artifacts."""
from __future__ import annotations
import argparse, json, os
from pathlib import Path
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from whole_brain_scaling import default_data_dir, validate_whole_brain_experiment

def main() -> None:
    parser = argparse.ArgumentParser(); parser.add_argument("--study-id", required=True); parser.add_argument("--experiment-dir", type=Path, action="append", required=True); parser.add_argument("--data-dir", type=Path, default=default_data_dir()); args = parser.parse_args()
    parent = args.data_dir / "experiments" / "whole-brain-v1"; target = parent / f"scaling-study-{args.study_id}"
    if target.exists(): raise SystemExit(f"Study output exists: {target}")
    os.environ.setdefault("MPLCONFIGDIR", str(Path(__file__).resolve().parents[1] / ".matplotlib-cache")); target.mkdir(parents=True)
    rows = []
    for path in args.experiment_dir:
        validate_whole_brain_experiment(path, args.data_dir); summary = json.loads((path / "summary.json").read_text()); rows.append({"experiment_id": path.name, "tier": summary["tier"]["name"], "neurons": summary["tier"]["neurons"], "activation_fraction": summary["activation_fraction"], "runtime_seconds": summary["performance"].get("simulation_wall_seconds"), "peak_rss_bytes": summary["performance"].get("peak_rss_bytes"), "classification": summary["classification"]})
    rows.sort(key=lambda item: item["neurons"])
    (target / "scaling_summary.json").write_text(json.dumps(rows, indent=2) + "\n")
    with (target / "scaling_summary.csv").open("w", encoding="utf-8") as handle:
        handle.write("experiment_id,tier,neurons,activation_fraction,runtime_seconds,peak_rss_bytes,classification\n")
        for row in rows: handle.write(",".join(str(row[key]) for key in ("experiment_id", "tier", "neurons", "activation_fraction", "runtime_seconds", "peak_rss_bytes", "classification")) + "\n")
    x = [row["neurons"] for row in rows]
    for filename, values, ylabel in (("runtime_vs_neurons.png", [row["runtime_seconds"] for row in rows], "simulation runtime (s)"), ("activation_vs_neurons.png", [row["activation_fraction"] for row in rows], "activation fraction")):
        fig, ax = plt.subplots(figsize=(6, 3)); ax.plot(x, values, marker="o"); ax.set(xlabel="neurons", ylabel=ylabel, title=filename.removesuffix(".png").replace("_", " ")); fig.tight_layout(); fig.savefig(target / filename, dpi=140); plt.close(fig)
    rss_rows = [row for row in rows if row["peak_rss_bytes"] is not None]
    if rss_rows:
        fig, ax = plt.subplots(figsize=(6, 3)); ax.plot([row["neurons"] for row in rss_rows], [row["peak_rss_bytes"] for row in rss_rows], marker="o"); ax.set(xlabel="neurons", ylabel="peak RSS bytes", title="rss vs neurons"); fig.tight_layout(); fig.savefig(target / "rss_vs_neurons.png", dpi=140); plt.close(fig)
    else:
        (target / "rss_measurement_unavailable.txt").write_text("Peak RSS was unavailable for these legacy artifacts; no zero-valued RSS plot was fabricated.\n", encoding="utf-8")
    print(json.dumps({"study_dir": str(target), "tiers": len(rows)}, indent=2))

if __name__ == "__main__": main()
