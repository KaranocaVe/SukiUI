# ConditionalXAML performance tests

This focused runner compares `If` with Avalonia visibility bindings at 10, 100 and
1,000 attached controls. It measures notifications for both the condition's `Flag`
property and an unrelated `Noise` property, using SukiTheme in all cases.

```sh
dotnet test SukiUI.ConditionalXAML.Tests -c Release
dotnet build SukiUI.PerformanceTests -c Release -t:Rebuild
dotnet run --project SukiUI.PerformanceTests -c Release --no-build -- --list
dotnet run --project SukiUI.PerformanceTests -c Release --no-build -- \
  --iterations 7 --warmup 3 --repetitions 3 --seed 1729 \
  --output artifacts/performance/run
```

Use .NET SDK 9 or later (the repository specifies C# 13), with the .NET 8 runtime.
Rebuild after switching target frameworks: Avalonia has separate .NET 8/10 asset sets
that can have identical sizes/timestamps, confusing incremental output copying.

## Measurement contract

- A sample is **one complete synchronous property notification delivered to N
  controls**, not latency per control or display frame. Controls and input models
  are created before timing. Avalonia initialization, layout, correctness assertions,
  visual inspection and teardown are outside timing for both `If` and binding.
- Each case runs serially in a fresh process. The runner freezes its executable and
  dependencies for the whole run, records DLL SHA-256s, and deletes that temporary
  snapshot afterward. Do not build, profile or run other tests during measurement.
- Warmups execute real operations. A full GC occurs after warmup, outside timing;
  measured collections remain included. Tiered compilation is disabled in children.
- `GC.GetAllocatedBytesForCurrentThread` covers UI-thread managed allocation only.
  GC counts are process-wide. Peak RSS includes initialization and warmup; it is not
  retained memory or a leak measurement.
- Every iteration verifies actual `IsVisible` values. Runtime errors, failed
  assertions and child timeouts make the suite exit with a nonzero status. Partial
  results are saved after every completed child.
- This uses Avalonia.Headless and Skia CPU, without GPU/native-window measurements.
  The window is 1,280×800; every `If` is attached even when it falls outside the
  viewport. The model is shared across the controls.
- Compare process medians and control cases on the same machine/runtime/power mode.
  Short binding timings and small-sample p95 are exploratory, not CI latency gates.

`results.json` contains raw samples, failures, observations and environment details;
`summary.md` contains a full table. The accompanying workflow runs correctness tests
and `--smoke` (the smallest scale of each scenario family), and uploads raw results.

## Options

| Option | Default | Meaning |
|---|---|---|
| `--list` | off | List matching cases without initializing Avalonia |
| `--filter text[,text]` | all | Match scenario ID substrings, OR between terms |
| `--smoke` | off | Smallest scale per family; 1 warmup, 3 samples |
| `--iterations N` | 15 | Measured samples per process; minimum 3 |
| `--warmup N` | 5 | Unmeasured operations; minimum 1 |
| `--repetitions N` | 1 | Fresh processes per scenario |
| `--seed N` | 1729 | Reproducible randomized process order |
| `--timeout seconds` | 180 | Per-child timeout, including setup; kills the child's process tree |
| `--output path` | timestamp under `artifacts/performance` | Results directory |

Compare two raw reports using Python's standard library:

```sh
python3 SukiUI.PerformanceTests/compare.py before/results.json after/results.json \
  --output artifacts/performance/comparison.csv
```
