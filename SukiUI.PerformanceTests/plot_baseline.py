#!/usr/bin/env python3
"""Plot the complete-operation scaling curves from one suite's raw JSON."""
import argparse
import json
import statistics
from collections import defaultdict
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = json.loads(args.results.read_text())
    results = defaultdict(list)
    for result in report["Results"]:
        if not result["Error"]:
            results[result["Id"]].append(result)
    panels = [
        ("Property notification: unrelated change", [10, 100, 1000], [
            ("conditional/if/unrelated/{}", "ConditionalXAML If"),
            ("conditional/binding/unrelated/{}", "Avalonia binding")]),
        ("Base theme switch + layout", [10, 100, 1000], [
            ("theme/base/suki/{}", "SukiTheme"),
            ("theme/base/simple/{}", "SimpleTheme")]),
        ("Mount mixed controls + layout", [10, 100, 1000], [
            ("mount/mixed/suki/{}", "SukiTheme"),
            ("mount/mixed/simple/{}", "SimpleTheme")]),
        ("Toast manager: drain all N entries", [100, 1000, 10000], [
            ("toast/dismiss-reference/{}", "Reference, reverse order"),
            ("toast/dismiss-indexfromend/{}", "Index, reverse order"),
            ("toast/dismiss-bulkrange/{}", "Bulk range")]),
    ]
    plt.rcParams.update({"font.size": 10, "axes.spines.top": False, "axes.spines.right": False})
    fig, axes = plt.subplots(2, 2, figsize=(12, 8), constrained_layout=True)
    for ax, (title, scales, series) in zip(axes.flat, panels):
        for pattern, label in series:
            rows = [results[pattern.format(n)] for n in scales]
            if any(not replicas for replicas in rows):
                raise ValueError(f"Incomplete result series: {pattern}")
            median = [statistics.median(statistics.median(s["Ms"] for s in r["Samples"])
                                       for r in replicas) for replicas in rows]
            ax.plot(scales, median, marker="o", linewidth=2, label=label)
        ax.set(xscale="log", yscale="log", title=title, xlabel="N objects", ylabel="Median ms / complete operation")
        ax.set_xticks(scales, [f"{n:,}" for n in scales])
        ax.grid(True, which="major", alpha=.2)
        ax.legend(fontsize=9)
    metadata = report["Metadata"]
    fig.suptitle(f"SukiUI CPU performance screening · {metadata['cpu']} · {metadata['runtime']}\n"
                 "Headless Skia; medians of process medians; see report for host conditions", fontsize=13)
    args.output.mkdir(parents=True, exist_ok=True)
    fig.savefig(args.output / "scaling.png", dpi=180)
    fig.savefig(args.output / "scaling.svg")
    plt.close(fig)


if __name__ == "__main__":
    main()
