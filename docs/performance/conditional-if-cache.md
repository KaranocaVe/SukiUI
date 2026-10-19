# ConditionalXAML `If`: compiled evaluator cache

`If` previously parsed and compiled its condition for every `PropertyChanged`
notification, including notifications unrelated to the expression. With 1,000
attached controls, a fresh paired test measured **1,450.780 ms and 136.207 MiB of
UI-thread allocation per unrelated notification**. Caching the evaluator reduces
that operation to **0.0388 ms and 24 bytes** on the same machine.

## Change and compatibility

Each `If` owns one compiled `Func<object, bool>`, keyed by the exact condition text
and the DataContext's runtime type. The expression takes the current DataContext as
an argument, so replacing a model with another instance of the same type reads the
new instance. The typed adapter also removes `DynamicInvoke`'s argument array and
boxed boolean result.

Every notification still invokes the expression. This preserves conditions that
call methods, read getters with side effects, depend on nested objects, or use
notifications with unrelated, empty or null property names. This change optimizes
compilation, without attempting to infer property dependencies.

Changing the condition or context type clears the single-entry cache. Empty
conditions and null contexts clear it and hide the control. Parse failures continue
to be retried on subsequent updates. Runtime invocation failures still hide the
control, and a later notification can recover with the same evaluator.

Sixteen Avalonia headless regression tests pass on both the original implementation
and the fix. They cover related/unrelated notifications, getter/method evaluation,
empty/null notification names, model and type replacement, boxed value types,
condition changes, null/empty states, parse and runtime failures, detach/reattach,
explicit `x` parameters and nested member access.

## Paired performance results

All numbers are **one complete synchronous model notification delivered to N attached
controls**, not per-control or per-frame latency. Table values are the median of
three independent process medians, each based on seven samples after three warmups.
The process order uses seed 1729 in both runs.

| Scenario | N | Before, ms | After, ms | Before UI allocation | After UI allocation |
|---|---:|---:|---:|---:|---:|
| `If`, unrelated notification | 10 | 14.5935 | 0.0002 | 1.351 MiB | 24 B |
| `If`, unrelated notification | 100 | 145.1443 | 0.0029 | 13.578 MiB | 24 B |
| `If`, unrelated notification | 1,000 | 1,450.7801 | 0.0388 | 136.207 MiB | 24 B |
| `If`, condition-related notification | 10 | 14.4505 | 0.0035 | 1.353 MiB | 904 B |
| `If`, condition-related notification | 100 | 144.9264 | 0.0349 | 13.549 MiB | 6,664 B |
| `If`, condition-related notification | 1,000 | 1,444.5327 | 0.4142 | 135.484 MiB | 64,264 B |
| Binding, unrelated notification | 1,000 | 0.0132 | 0.0132 | 56 B | 56 B |
| Binding, condition-related notification | 1,000 | 0.2679 | 0.2618 | 144,296 B | 144,296 B |

At N=1,000, the unrelated `If` operation is approximately **37,000× faster** and the
related operation approximately **3,500× faster**. The binding controls remain close
to their original timings and have identical allocation counts. These ratios apply
to this notification workload; they are not whole-application speedups.

Process-median ranges at N=1,000:

| `If` notification | Before range, ms | After range, ms |
|---|---:|---:|
| Unrelated | 1,418.0482–1,455.8621 | 0.0381–0.0449 |
| Condition-related | 1,443.0162–1,569.7474 | 0.3820–0.4292 |

The complete 12-scenario matrix (both implementations, both notification types,
three scales) passes before and after: **72 independent processes and 504 measured
samples**, with no errors or timeouts. Every iteration checks actual `IsVisible`.

## Environment and measurement boundaries

- Apple M5, 10 logical processors, 32 GiB, arm64; macOS 27.0.1 (26A434).
- .NET 8.0.32, SDK 10.0.402, Avalonia 12.1.1; Release, tiered compilation disabled.
- Avalonia.Headless with Skia CPU and SukiTheme, 1,280×800 viewport. All N controls
  share a notifying model and remain attached, including those outside the viewport.
- Baseline source: `c2bf6bcaeef4a735f1d5f05c299262cff47ac194`. The benchmark executable,
  SukiUI DLL and Motion DLL are byte-identical across the paired runs; the
  ConditionalXAML DLL changes. Reports record their SHA-256 fingerprints and the
  [provenance file](conditional-if-cache/provenance.json) records source hashes.
- Setup, expression compilation during setup, warmup, assertions, layout and cleanup
  are outside timing. Both `If` and binding measure synchronous notification delivery
  and drain layout in validation. The related scenario alternates both directions.
- A full GC occurs after warmup, outside timing. GC during a measured operation is
  included. Allocations cover managed memory on the UI thread, excluding native and
  background-thread allocations. Peak RSS is not a retained-memory measurement.
- The machine remained on AC power without recorded thermal warnings, with normal
  desktop applications running. Initial load averages were 3.19/3.88/3.87 before and
  3.28/3.72/3.77 after. Absolute timings remain machine/load dependent.
- This tests repeated notifications after compilation. Cold parse/compile latency,
  long-session memory behavior, native rendering, GPU performance and display FPS
  are outside these measurements. Very short timings and seven-sample p95s should
  not be used as absolute CI thresholds.

## Raw evidence

- [Before samples and process metadata](conditional-if-cache/before.json)
- [After samples and process metadata](conditional-if-cache/after.json)
- [All scenario comparisons and process-median ranges](conditional-if-cache/comparison.csv)
- [Before host inventory](conditional-if-cache/before-host.json) and
  [after host inventory](conditional-if-cache/after-host.json)

The JSON exports preserve every raw sample, observation and failure field, with one
process result per line to keep the data diff compact.

## Reproduction

Use a scratch checkout of this PR so the baseline source can be restored without
affecting other work. The same performance runner must be used for both variants.

```sh
# Original implementation, with this branch's benchmark and regression tests.
git show c2bf6bcaeef4a735f1d5f05c299262cff47ac194:SukiUI.ConditionalXAML/Helpers/ConditionalXAML/If.cs > SukiUI.ConditionalXAML/Helpers/ConditionalXAML/If.cs
dotnet test SukiUI.ConditionalXAML.Tests -c Release
dotnet build SukiUI.PerformanceTests -c Release -t:Rebuild
dotnet run --project SukiUI.PerformanceTests -c Release --no-build -- \
  --iterations 7 --warmup 3 --repetitions 3 --seed 1729 \
  --output artifacts/performance/before

# Cached implementation.
git restore --source=HEAD -- SukiUI.ConditionalXAML/Helpers/ConditionalXAML/If.cs
dotnet test SukiUI.ConditionalXAML.Tests -c Release
dotnet build SukiUI.PerformanceTests -c Release -t:Rebuild
dotnet run --project SukiUI.PerformanceTests -c Release --no-build -- \
  --iterations 7 --warmup 3 --repetitions 3 --seed 1729 \
  --output artifacts/performance/after

python3 SukiUI.PerformanceTests/compare.py \
  artifacts/performance/before/results.json artifacts/performance/after/results.json \
  --output artifacts/performance/comparison.csv
```

The [runner README](../../SukiUI.PerformanceTests/README.md) documents all options.
The ConditionalXAML workflow runs the regression tests and a four-scenario performance
smoke test, then uploads the raw smoke results without enforcing latency thresholds.
