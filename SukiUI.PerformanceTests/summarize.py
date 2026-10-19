#!/usr/bin/env python3
"""Summarize raw suite results without discarding failed cases or process replicas."""
import argparse
import csv
import json
import math
import statistics
from collections import defaultdict
from pathlib import Path


def percentile(values, p):
    values = sorted(values)
    index = (len(values) - 1) * p
    lower = int(index)
    return values[lower] + (values[math.ceil(index)] - values[lower]) * (index - lower)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = json.loads(args.results.read_text())
    args.output.mkdir(parents=True, exist_ok=True)
    groups = defaultdict(list)
    failures = []
    for result in report["Results"]:
        if result["Error"]:
            failures.append({"id": result["Id"], "replica": result["Replica"], "error": result["Error"]})
        else:
            if not result["Samples"]:
                raise ValueError(f"Empty successful sample set: {result['Id']}")
            groups[result["Id"]].append(result)

    metrics = {}
    for name, replicas in sorted(groups.items()):
        medians = [statistics.median(s["Ms"] for s in r["Samples"]) for r in replicas]
        byte_medians = [statistics.median(s["UiBytes"] for s in r["Samples"]) for r in replicas]
        metrics[name] = {
            "id": name,
            "area": replicas[0]["Area"],
            "scale": replicas[0]["Scale"],
            "theme": replicas[0]["Theme"],
            "processes": len(replicas),
            "samples": sum(len(r["Samples"]) for r in replicas),
            "median_ms": statistics.median(medians),
            "min_process_median_ms": min(medians),
            "max_process_median_ms": max(medians),
            "median_process_p95_ms": statistics.median(percentile([s["Ms"] for s in r["Samples"]], .95) for r in replicas),
            "median_ui_bytes": statistics.median(byte_medians),
        }

    def write_csv(filename, rows):
        rows = list(rows)
        if not rows:
            return
        with (args.output / filename).open("w", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=rows[0].keys())
            writer.writeheader()
            writer.writerows(rows)

    write_csv("metrics.csv", metrics.values())
    comparisons = []
    for name, row in metrics.items():
        baseline = name.replace("/suki/", "/simple/")
        if baseline != name and baseline in metrics:
            comparisons.append({
                "candidate": name, "baseline": baseline,
                "time_ratio": row["median_ms"] / metrics[baseline]["median_ms"],
                "allocation_ratio": row["median_ui_bytes"] / metrics[baseline]["median_ui_bytes"]
                if metrics[baseline]["median_ui_bytes"] else None,
            })
        if name.startswith("conditional/if/"):
            baseline = name.replace("/if/", "/binding/")
            if baseline in metrics:
                comparisons.append({
                    "candidate": name, "baseline": baseline,
                    "time_ratio": row["median_ms"] / metrics[baseline]["median_ms"],
                    "allocation_ratio": row["median_ui_bytes"] / metrics[baseline]["median_ui_bytes"]
                    if metrics[baseline]["median_ui_bytes"] else None,
                })
    write_csv("comparisons.csv", comparisons)
    (args.output / "failures.json").write_text(json.dumps(failures, indent=2))
    print(f"{len(metrics)} successful scenario IDs, {len(failures)} failed processes")
    for row in sorted(metrics.values(), key=lambda r: r["median_ms"], reverse=True)[:15]:
        print(f"{row['id']:<55} {row['median_ms']:10.3f} ms  {row['median_ui_bytes'] / 1048576:8.2f} MiB")
    return int(bool(failures))


if __name__ == "__main__":
    raise SystemExit(main())
