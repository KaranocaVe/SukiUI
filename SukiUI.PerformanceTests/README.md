# SukiUI performance tests

This executable measures Release builds of the local projects, with Avalonia.Headless
12.1.1 and real Skia CPU drawing. It is a screening suite for locating expensive paths
and scaling problems, not a desktop frame-rate certification.

From the repository root:

```sh
dotnet build SukiUI.PerformanceTests -c Release -t:Rebuild
dotnet run --project SukiUI.PerformanceTests -c Release --no-build -- --list
dotnet run --project SukiUI.PerformanceTests -c Release --no-build -- --smoke
dotnet run --project SukiUI.PerformanceTests -c Release --no-build -- --output artifacts/performance/full
```

Rebuild when switching from a different target framework: the solution's demo targets
.NET 10 while these tests and the libraries target .NET 8. The two Avalonia asset sets
can have identical file sizes/timestamps, which can fool incremental output copying.

Confirm a finding in fresh processes with more samples and a different execution order:

```sh
dotnet run --project SukiUI.PerformanceTests -c Release --no-build -- \
  --filter conditional,toast,theme --iterations 30 --warmup 10 \
  --repetitions 3 --seed 2718 --output artifacts/performance/confirmation
```

Options:

| Option | Default | Meaning |
|---|---|---|
| `--list` | off | List matching scenarios without initializing Avalonia |
| `--filter text[,text]` | all | Match scenario ID substrings, OR between terms |
| `--smoke` | off | Smallest scale of each family; 1 warmup, 3 samples |
| `--iterations N` | 15 | Measured samples per independent process, minimum 3 |
| `--warmup N` | 5 | Unmeasured operations before sampling, minimum 1 |
| `--repetitions N` | 1 | Fresh processes per case |
| `--seed N` | 1729 | Reproducible randomized case order |
| `--timeout seconds` | 180 | Timeout per process; kill its process tree on expiry |
| `--output path` | timestamp under `artifacts/performance` | Results directory |

The suite returns a nonzero exit code for exceptions, invalid observations or timeouts.
Every child has its own JSON and diagnostic log. `results.json` contains all raw samples,
environment information and failures; `summary.md` provides a sortable text table.
Partial results are saved after each child exits.

Generate CSV summaries with the Python standard library. Plotting additionally needs
Matplotlib; it is not required for running the suite:

```sh
python3 SukiUI.PerformanceTests/summarize.py artifacts/performance/full/results.json --output artifacts/performance/analysis
python3 SukiUI.PerformanceTests/plot_baseline.py artifacts/performance/full/results.json --output artifacts/performance/analysis
```

## Measurement contract

- One sample is one complete named operation on N objects. It is **not** latency per
  control, per row or per animation frame. Manager-only workloads exclude visual hosts.
- The runner snapshots its complete program/dependency directory before starting children,
  records SHA-256 fingerprints and removes that temporary copy on completion. Rebuilding
  the working checkout cannot replace the program used by an active run.
- Each case runs serially in its own process. UI initialization and scenario setup are
  excluded. Tiered compilation is disabled in children; no measurements overlap.
  Do not build, profile or run other benchmarks concurrently with a measured suite.
- Warmups exercise the actual operation. Full GC runs once after warmup, outside the
  stopwatch; measured collections remain included in operation latency. Preparation,
  semantic assertions, visual-tree inspection and teardown are outside timing.
- `mount` includes constructing controls, attaching them to a 1280x800 headless window,
  applying templates and draining layout. All controls are intentionally mounted; this
  is a stress workload even when they do not all fit onscreen.
- `layout`, theme and normal interaction cases drain dispatcher/layout within timing.
  Conditional/binding cases measure synchronous property-notification delivery only;
  their validation drains layout outside timing on both sides of the comparison.
- ListBox/DataGrid input arrays are prepared outside timing. Mount cases include
  container realization; scroll cases alternate first/last rows. Assertions verify
  bounded container counts and realization of the requested row.
- Theme, resize, color and scroll cases alternate directions. The raw distribution
  includes both directions; inspect samples if these operations have asymmetric costs.
- UI allocations use `GC.GetAllocatedBytesForCurrentThread` on Avalonia's UI thread.
  They exclude native allocations and allocations on render/background threads. GC
  counts are process-wide. Peak RSS includes initialization, setup and warmup; it is
  neither retained memory nor evidence of a leak.
- `render` uses `RenderTargetBitmap.Render` at 1280x800, checks painted pixels, and
  excludes setup/layout. It measures CPU rasterization, without the native compositor.
- Background cases capture real headless composited frames with software rendering
  forced. The framework's background **software fallback is a solid fill**, so these
  cases do not measure its SkSL/GPU shaders. GPU effects, native window chrome,
  real display pacing and input latency need desktop profiling separately.
- Motion cases separate attachment, synchronous gesture dispatch and deterministic
  timed/spring trajectory advancement. Gesture teardown pumps a render tick outside
  timing to compact cancelled subscriptions. Simulated frames are not wall-clock FPS.
- The `simple` control/list/layout cases use Avalonia SimpleTheme, which SukiTheme itself
  builds on. They are an attribution aid; different templates have different visual
  complexity, so a slowdown ratio alone does not establish a defect.

Use scaling curves, allocations and fresh-process repetitions to prioritize profiling.
Small-sample p95 and very short service timings are exploratory. Do not enforce absolute
latency thresholds on shared CI runners or compare results from different machines,
runtimes, renderers, power modes or library versions as though they were equivalent.

For real desktop press animation pacing, the demo already includes **Helpers → Press
Stress Lab** with 60-second runs and RAF, engine-dispatch, CPU and allocation probes.
That interactive workload complements these automated CPU tests.
