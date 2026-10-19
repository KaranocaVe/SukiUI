# Batched theme resource updates

The production fix is commit `e9d6dd284ab7de344e2ceefec92e39af629c87ff`, based on upstream `c2bf6bcaeef4a735f1d5f05c299262cff47ac194`. It changes only `SukiUI/Theme/Index.axaml.cs`. This benchmark branch holds the test harness and recorded evidence separately from the upstream PR.

## Results

Each operation updates an already mounted tree of 1,000 mixed controls. Times are the median of three process medians, with seven measured samples per process.

| Operation | Before (ms) | After (ms) | Time reduction | UI allocations before → after (MiB) | Application resource notifications |
|---|---:|---:|---:|---:|---:|
| Light/dark theme | 1126.153 | 240.813 | 78.6% | 80.680 → 27.280 | 30 → 1 |
| Accent color | 954.762 | 79.499 | 91.7% | 63.737 → 8.768 | 30 → 1 |
| Shadcn light/dark theme | 2068.327 | 386.667 | 81.3% | 144.256 → 35.883 | 60 → 2 |
| Reapply the current theme object | 880.763 | 26.592 | 97.0% | 56.862 → 1.900 | 30 → 1 |

Color callback counts are unchanged: one for the Suki cases and two for Shadcn. Reapplying the current theme still overwrites all palette entries, including entries changed externally.

- Raw samples: [before](baseline.json), [after](final.json), [comparison and process medians](comparison.json).
- [DLL manifests](dll-manifests.json): each frozen runtime contains 50 DLLs; only `SukiUI.dll` differs between the two runtimes.
- [Source provenance](provenance.json), [production patch](candidate-final.patch), [paired run script](run-paired.py).

## Method

Apple M5, 32 GiB RAM, macOS 27.0.1 arm64, .NET 8.0.32, Avalonia 12.1.1, Release. Child processes disable tiered compilation. These are Avalonia.Headless + Skia CPU stress measurements, not native GPU frame times or an FPS claim. UI allocations cover the measuring thread, not native allocations or other threads.

Four scenarios × two versions × three fresh processes = 24 processes and 168 measured samples. Each process first performs three warmups. The block order is before-1, after-1, after-2, before-2, before-3, after-3; corresponding blocks use the same scenario-order seed. No builds or other test runs overlapped these measurements.

The timed operation includes the synchronous theme change and `Ui.Flush` (queued UI work, layout, and headless CPU rendering). Application initialization, building the control tree, first layout, assertions, and cleanup are excluded. The runner and all other dependencies are identical in both frozen runtimes. The source snapshot includes the independently developed ConditionalXAML fix, held constant on both sides; it is not part of this theme PR.

All measured samples validate the 30 palette colors, a real ToggleSwitch's dynamic Foreground, resource dictionary identity, and visual-tree observations. Non-Shadcn cases also assert the visual instance sequence remains unchanged. Shadcn records replacement counts instead.

Process medians vary: light/dark is 1084.499–1191.183 ms before and 223.598–242.704 ms after; accent is 954.654–1025.457 ms before and 77.389–85.395 ms after. Shadcn after is 381.274–544.908 ms. The table is not a per-operation latency guarantee.

## Correctness checks

- [151/151 UI scenarios](ui-regression.json): mounting, layout, interaction, theme/color/locale changes, navigation, virtualized lists, DataGrid, color picker, docking, software rendering, backgrounds, and all 20 theme diagnostic cases.
- [14/14 targeted regressions](focused-regression.json), followed by [14/14 after integration](primary-integration-regression.json): all built-in and transparent custom palettes in light/dark mode; reapplication; deferred resources; arbitrary `Equals`; custom dictionary interface dispatch and lazy theme-variant reads; inherited resource overrides; complete-palette notifications; color callback reentry; callback exception propagation/recovery; Suki/Shadcn callback counts; actual dynamic resources.
- Real macOS windows: [baseline Suki](native-baseline.json), [final Suki](native-suki.json), [final Shadcn](native-shadcn.json). Each performs six steps, each with a base-theme change and a color change, and checks 30 palette keys, dynamic Foreground, a native handle, resource identity, and style count. [Native test source](native-smoke/Program.cs).
- Release library and Gallery builds passed. Existing warnings remain.

The UI and targeted suites use one warmup plus three measured/validated iterations per case. These are functional regression runs; their timings are not used for the performance table. The targeted cases were registered after freezing the paired runner; they use the same production `SukiUI.dll`.

Two earlier native startup attempts failed inside Avalonia's render timer before theme initialization. Their [direct](native-initial-startup-failure.json) and [bundle](native-bundle-startup-failure.json) records are retained. Subsequent baseline/final confirmations passed; both final modes passed on their first confirmation attempt. Native checks are smoke tests, not long-duration stability tests.

## Behavioral boundary

Batching applies only when the runtime type is exactly Avalonia's `ResourceDictionary`. Custom dictionaries and all subclasses retain incremental interface-indexer writes and lazy evaluation. The fix does not inspect old resources, invoke their deferred factories, compare their values, replace the dictionary, or change palette math and color callback ordering.

For the batched path, observers receive one notification after the complete palette has been installed. Notification counts and observable intermediate states intentionally change. `SetItems` is not a rollback transaction; handler exceptions still propagate. Synchronous theme changes reentered from `ResourcesChanged` retain a pre-existing consistency risk and are not made safe by this patch.

## Reproduce

Check out this evidence branch. The harness is outside the upstream PR and can be run directly:

```sh
dotnet build SukiUI.PerformanceTests -c Release -t:Rebuild
dotnet run --project SukiUI.PerformanceTests -c Release --no-build -- \
  --filter theme-regression/ --warmup 1 --iterations 3 --output artifacts/theme-regression
```

The branch's CI also checks out the exact production commit, adds only the harness and its Headless package reference, and runs all 14 targeted cases against that commit's dependencies.

To repeat the paired benchmark without modifying your checkout, run the following from the evidence branch (requires Python 3 and a .NET SDK supporting C# 13, plus the .NET 8 runtime):

```sh
task_dir=$(mktemp -d)
git clone --quiet --no-hardlinks --branch benchmarks/theme-resource-batching . "$task_dir/source"
cp "$task_dir/source/SukiUI/Theme/Index.axaml.cs" "$task_dir/final-theme.cs"
git show c2bf6bcaeef4a735f1d5f05c299262cff47ac194:SukiUI/Theme/Index.axaml.cs > "$task_dir/source/SukiUI/Theme/Index.axaml.cs"
dotnet build "$task_dir/source/SukiUI/SukiUI.csproj" -c Release -t:Rebuild
cp "$task_dir/source/artifacts/bin/SukiUI/release/SukiUI.dll" "$task_dir/baseline.dll"
cp "$task_dir/source/artifacts/bin/SukiUI/release/SukiUI.pdb" "$task_dir/baseline.pdb"
cp "$task_dir/final-theme.cs" "$task_dir/source/SukiUI/Theme/Index.axaml.cs"
dotnet build "$task_dir/source/SukiUI.PerformanceTests" -c Release -t:Rebuild
mkdir -p "$task_dir/frozen/final" "$task_dir/frozen/baseline"
cp -R "$task_dir/source/artifacts/bin/SukiUI.PerformanceTests/release/." "$task_dir/frozen/final/"
cp -R "$task_dir/frozen/final/." "$task_dir/frozen/baseline/"
cp "$task_dir/baseline.dll" "$task_dir/frozen/baseline/SukiUI.dll"
cp "$task_dir/baseline.pdb" "$task_dir/frozen/baseline/SukiUI.pdb"
cp docs/performance/theme-resource-validation-2026-10-19/run-paired.py "$task_dir/"
python3 - "$task_dir" <<'PY'
import hashlib, json, pathlib, sys
root = pathlib.Path(sys.argv[1])
manifests = {mode: {str(p.relative_to(root / 'frozen' / mode)): hashlib.sha256(p.read_bytes()).hexdigest()
                    for p in (root / 'frozen' / mode).rglob('*.dll')}
             for mode in ('baseline', 'final')}
assert manifests['baseline'].keys() == manifests['final'].keys()
assert [p for p in manifests['baseline'] if manifests['baseline'][p] != manifests['final'][p]] == ['SukiUI.dll']
(root / 'dll-manifests.json').write_text(json.dumps({'manifests': manifests}, indent=2))
PY
python3 "$task_dir/run-paired.py"
# Copy paired-*.json and dll-manifests.json to a permanent results directory, then clean up:
rm -rf "$task_dir"
```

Regenerating the manifests deliberately records your local binaries; it does not claim byte identity with the historical run. The recorded manifests and raw reports above remain unchanged. Avoid concurrent builds or benchmarks. Review or preserve your new JSON results before the final cleanup command.
