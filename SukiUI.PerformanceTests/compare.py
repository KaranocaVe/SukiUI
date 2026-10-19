#!/usr/bin/env python3
"""Compare process medians without pooling raw samples from different processes."""
import argparse
import csv
import json
import statistics
from collections import defaultdict
from pathlib import Path


def summarize(path):
    report = json.loads(path.read_text())
    groups = defaultdict(list)
    for result in report["Results"]:
        if result["Error"] or not result["Samples"]:
            raise ValueError(f"Invalid process: {result['Id']} r{result['Replica']}: {result['Error']}")
        groups[result["Id"]].append(result)
    summary = {}
    for name, replicas in groups.items():
        times = [statistics.median(s["Ms"] for s in r["Samples"]) for r in replicas]
        allocations = [statistics.median(s["UiBytes"] for s in r["Samples"]) for r in replicas]
        summary[name] = {
            "processes": len(replicas),
            "samples": sum(len(r["Samples"]) for r in replicas),
            "median_ms": statistics.median(times),
            "min_process_median_ms": min(times),
            "max_process_median_ms": max(times),
            "median_ui_bytes": statistics.median(allocations),
        }
    return report["Metadata"], summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("before", type=Path)
    parser.add_argument("after", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    before_metadata, before = summarize(args.before)
    after_metadata, after = summarize(args.after)
    for key in ("os", "architecture", "cpu", "runtime", "avalonia", "iterations", "warmups", "repetitions", "seed"):
        if before_metadata[key] != after_metadata[key]:
            raise ValueError(f"Incompatible run metadata: {key}")
    if before.keys() != after.keys():
        raise ValueError("Reports must contain the same scenario IDs")
    rows = []
    for name in sorted(before):
        b, a = before[name], after[name]
        if (b["processes"], b["samples"]) != (a["processes"], a["samples"]):
            raise ValueError(f"Mismatched sample counts: {name}")
        rows.append({
            "id": name,
            **{"before_" + key: value for key, value in b.items()},
            **{"after_" + key: value for key, value in a.items()},
            "time_speedup": b["median_ms"] / a["median_ms"] if a["median_ms"] else None,
            "ui_allocation_reduction_percent": 100 * (1 - a["median_ui_bytes"] / b["median_ui_bytes"])
            if b["median_ui_bytes"] else None,
        })
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=rows[0].keys())
        writer.writeheader()
        writer.writerows(rows)
    print(f"Compared {len(rows)} matching scenario IDs; process medians preserved")
    for row in rows:
        print(f"{row['id']:<45} {row['before_median_ms']:10.4f} -> {row['after_median_ms']:10.4f} ms")


if __name__ == "__main__":
    main()
