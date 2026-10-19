using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Simple;
using SukiUI.Theme.Shadcn;

namespace SukiUI.PerformanceTests;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [STAThread]
    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var options = Options.Parse(args);
            var cases = UiScenarios.All().Concat(ServiceScenarios.All()).Concat(MotionScenarios.All())
                .Concat(ThemeDiagnostics.All()).Concat(ThemeRegressionScenarios.All()).ToArray();
            if (cases.Select(c => c.Id).Distinct().Count() != cases.Length)
                throw new InvalidOperationException("Scenario IDs must be unique.");
            if (options.Case is not null)
                return RunChild(cases.Single(c => c.Id == options.Case), options);
            var selected = cases.Where(c => options.Filter is null || options.Filter.Split(',')
                .Any(f => c.Id.Contains(f, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (options.Smoke)
                selected = selected.GroupBy(c => (c.Area, c.Theme, Family(c.Id)))
                    .Select(g => g.MinBy(c => c.Scale)!).ToArray();
            if (selected.Length == 0) throw new ArgumentException("No scenarios matched the filter.");
            if (options.List)
            {
                foreach (var c in selected) Console.WriteLine($"{c.Id,-66} {c.Area,-16} {c.Scale,7} {c.Theme}");
                Console.WriteLine($"{selected.Length} scenarios");
                return 0;
            }
#if DEBUG
            throw new InvalidOperationException("Performance measurements require a Release build.");
#else
            return RunSuite(selected, options).GetAwaiter().GetResult();
#endif
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    private static string Family(string id) => string.Join('/', id.Split('/').Where(s => !int.TryParse(s, out _)));

    private static int RunChild(Scenario scenario, Options options)
    {
        try
        {
            PerfApp.Selected = scenario;
            var startup = Stopwatch.StartNew();
            AppBuilder.Configure<PerfApp>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions
                {
                    UseHeadlessDrawing = false,
                    ShouldRenderOnUIThread = true
                })
                .WithInterFont()
                .SetupWithoutStarting();
            startup.Stop();
            using var workload = scenario.Create();
            for (var i = 0; i < options.Warmup; i++)
            {
                workload.BeforeIteration?.Invoke();
                workload.Run();
                workload.Validate?.Invoke();
                workload.AfterIteration?.Invoke();
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var samples = new List<Sample>();
            using var process = Process.GetCurrentProcess();
            for (var i = 0; i < options.Iterations; i++)
            {
                workload.BeforeIteration?.Invoke();
                var g0 = GC.CollectionCount(0);
                var g1 = GC.CollectionCount(1);
                var g2 = GC.CollectionCount(2);
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                workload.Run();
                var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                samples.Add(new Sample(elapsed, bytes, GC.CollectionCount(0) - g0,
                    GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2, workload.Metrics?.Invoke()));
                workload.Validate?.Invoke();
                workload.AfterIteration?.Invoke();
            }
            var observations = workload.Observe?.Invoke() ?? new Dictionary<string, double>();
            process.Refresh();
            var result = new CaseResult(scenario.Id, scenario.Area, scenario.Scale, scenario.Theme,
                options.Replica, startup.Elapsed.TotalMilliseconds, process.PeakWorkingSet64,
                samples, observations, null);
            File.WriteAllText(options.Result!, JsonSerializer.Serialize(result, Json));
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(options.Result!, JsonSerializer.Serialize(new CaseResult(scenario.Id,
                scenario.Area, scenario.Scale, scenario.Theme, options.Replica, 0, 0, [], [], e.ToString()), Json));
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    private static async Task<int> RunSuite(Scenario[] cases, Options options)
    {
        var output = Path.GetFullPath(options.Output);
        if ((output + Path.DirectorySeparatorChar).StartsWith(AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Results must be outside the executable directory.");
        Directory.CreateDirectory(output);
        var jobs = cases.SelectMany(c => Enumerable.Range(1, options.Repetitions).Select(r => (Case: c, Replica: r))).ToArray();
        // Reproducible random order reduces systematic thermal/order bias. Measurements never overlap.
        var rng = new Random(options.Seed);
        rng.Shuffle(jobs);
        var results = new List<CaseResult>();
        var started = DateTimeOffset.Now;
        var metadata = GetMetadata(options);
        // Build outputs can be rebuilt by an IDE or another contributor during a long
        // run. Freeze the complete runtime closure once; all measured children use it.
        var runner = Path.Combine(output, ".runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runner);
        try
        {
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(runner, Path.GetRelativePath(AppContext.BaseDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            var executable = Path.Combine(runner, Path.GetFileName(Assembly.GetExecutingAssembly().Location));
            foreach (var name in new[] { "SukiUI.PerformanceTests", "SukiUI", "SukiUI.Motion", "SukiUI.ConditionalXAML" })
                metadata[name + "Sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(runner, name + ".dll"))));
            Console.WriteLine($"{cases.Length} scenarios, {options.Repetitions} independent process(es) each; {options.Warmup} warmups + {options.Iterations} measured samples.\n{output}");
            foreach (var (scenario, replica) in jobs)
            {
                var stem = scenario.Id.Replace('/', '_') + $".r{replica}";
                var resultPath = Path.Combine(output, stem + ".json");
                var start = new ProcessStartInfo("dotnet")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                foreach (var arg in new[] { executable, "--case", scenario.Id,
                             "--result", resultPath, "--warmup", options.Warmup.ToString(), "--iterations",
                             options.Iterations.ToString(), "--replica", replica.ToString() })
                    start.ArgumentList.Add(arg);
                start.Environment["DOTNET_TieredCompilation"] = "0";
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start child.");
                var stdout = child.StandardOutput.ReadToEndAsync();
                var stderr = child.StandardError.ReadToEndAsync();
                string? failure = null;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
                    await child.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                    failure = $"Timeout after {options.TimeoutSeconds} seconds";
                }
                await File.WriteAllTextAsync(Path.Combine(output, stem + ".log"), await stdout + await stderr);
                CaseResult? result = File.Exists(resultPath)
                    ? JsonSerializer.Deserialize<CaseResult>(await File.ReadAllTextAsync(resultPath)) : null;
                if (failure is not null || result is null || (child.ExitCode != 0 && result.Error is null))
                    result = new CaseResult(scenario.Id, scenario.Area, scenario.Scale, scenario.Theme, replica,
                        0, 0, [], [], failure ?? $"Child exited {child.ExitCode}; see {stem}.log");
                results.Add(result);
                var status = result.Error is null ? $"p50 {Percentile(result.Samples.Select(s => s.Ms), .5):F3} ms" : "FAILED";
                Console.WriteLine($"[{results.Count}/{jobs.Length}] {scenario.Id} r{replica}: {status}");
                var report = new RunReport(started, DateTimeOffset.Now, metadata, results);
                await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(report, Json));
                await File.WriteAllTextAsync(Path.Combine(output, "summary.md"), Summarize(report));
            }
            Console.WriteLine($"Done: {results.Count(r => r.Error is null)} passed, {results.Count(r => r.Error is not null)} failed.");
            return results.All(r => r.Error is null) ? 0 : 1;
        }
        finally
        {
            Directory.Delete(runner, recursive: true);
        }
    }

    private static Dictionary<string, string> GetMetadata(Options options) => new()
    {
        ["commit"] = ReadCommand("git", "rev-parse", "HEAD"),
        ["workingTree"] = ReadCommand("git", "status", "--short"),
        ["os"] = RuntimeInformation.OSDescription,
        ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
        ["runtime"] = RuntimeInformation.FrameworkDescription,
        ["cpu"] = OperatingSystem.IsMacOS() ? ReadCommand("sysctl", "-n", "machdep.cpu.brand_string") : Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "See host inventory",
        ["logicalProcessors"] = Environment.ProcessorCount.ToString(),
        ["avalonia"] = typeof(Application).Assembly.GetName().Version?.ToString() ?? "unknown",
        ["configuration"] = "Release",
        ["tieredCompilation"] = "disabled in child processes",
        ["renderer"] = "Avalonia.Headless + Skia CPU; no GPU or native window",
        ["iterations"] = options.Iterations.ToString(),
        ["warmups"] = options.Warmup.ToString(),
        ["repetitions"] = options.Repetitions.ToString(),
        ["seed"] = options.Seed.ToString(),
        ["command"] = Environment.CommandLine
    };

    private static string ReadCommand(string file, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output.Trim();
        }
        catch { return "unavailable"; }
    }

    internal static double Percentile(IEnumerable<double> values, double p)
    {
        var a = values.Order().ToArray();
        if (a.Length == 0) return double.NaN;
        var index = (a.Length - 1) * p;
        var lower = (int)index;
        return a[lower] + (a[(int)Math.Ceiling(index)] - a[lower]) * (index - lower);
    }

    private static string Summarize(RunReport report)
    {
        var b = new StringBuilder("# SukiUI performance baseline\n\n");
        b.AppendLine($"Started: {report.Started:O}; updated: {report.Finished:O}.\n");
        foreach (var (key, value) in report.Metadata.Where(p => p.Key != "workingTree"))
            b.AppendLine($"- {key}: `{value}`");
        b.AppendLine("\nOne sample is the complete operation named by the scenario. Setup, validation, observation and cleanup are excluded. Allocations cover the UI thread only. Peak RSS includes startup and warmup. p95 from small sample counts is exploratory. No wall-clock FPS or GPU claims.\n");
        b.AppendLine("| Scenario | Replica | p50 ms | p95 ms | Max ms | UI KB/sample (median) | Gen0/1/2 | Peak RSS MB | Observations |\n|---|---:|---:|---:|---:|---:|---|---:|---|");
        foreach (var r in report.Results.OrderBy(r => r.Id).ThenBy(r => r.Replica))
        {
            if (r.Error is not null)
            {
                b.AppendLine($"| {r.Id} | {r.Replica} | FAILED | | | | | | {r.Error.Split('\n')[0].Replace('|', '/')} |");
                continue;
            }
            b.AppendLine($"| {r.Id} | {r.Replica} | {Percentile(r.Samples.Select(s => s.Ms), .5):F3} | {Percentile(r.Samples.Select(s => s.Ms), .95):F3} | {r.Samples.Max(s => s.Ms):F3} | {Percentile(r.Samples.Select(s => (double)s.UiBytes), .5) / 1024:F2} | {r.Samples.Sum(s => s.Gen0)}/{r.Samples.Sum(s => s.Gen1)}/{r.Samples.Sum(s => s.Gen2)} | {r.PeakWorkingSetBytes / 1048576.0:F1} | {string.Join(", ", r.Observations.Select(p => $"{p.Key}={p.Value:G5}"))} |");
        }
        return b.ToString();
    }
}

internal sealed record Sample(double Ms, long UiBytes, int Gen0, int Gen1, int Gen2,
    Dictionary<string, double>? Metrics = null);
internal sealed record CaseResult(string Id, string Area, int Scale, string Theme, int Replica,
    double AppSetupMs, long PeakWorkingSetBytes, List<Sample> Samples,
    Dictionary<string, double> Observations, string? Error);
internal sealed record RunReport(DateTimeOffset Started, DateTimeOffset Finished,
    Dictionary<string, string> Metadata, List<CaseResult> Results);

public sealed class PerfApp : Application
{
    internal static Scenario Selected { get; set; } = null!;
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        if (Selected.Theme == "simple") Styles.Add(new SimpleTheme());
        else
        {
            Styles.Add(new SukiTheme { Locale = "en-US" });
            if (Selected.Theme == "shadcn") Shadcn.Configure(this, ThemeVariant.Light);
        }
        if (Selected.Area == "datagrid") Include("avares://SukiUI.DataGrid/Theme/Index.axaml");
        if (Selected.Area == "colorpicker")
        {
            Include("avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml");
            Include("avares://SukiUI.ColorPicker/Theme/Index.axaml");
        }
        if (Selected.Area == "dock") Include("avares://SukiUI.Dock/Index.axaml");
    }

    private void Include(string uri) => Styles.Add(new StyleInclude(new Uri(uri)) { Source = new Uri(uri) });
}

internal sealed class Options
{
    public int Iterations { get; private set; } = 15;
    public int Warmup { get; private set; } = 5;
    public int Repetitions { get; private set; } = 1;
    public int Replica { get; private set; } = 1;
    public int Seed { get; private set; } = 1729;
    public int TimeoutSeconds { get; private set; } = 180;
    public string Output { get; private set; } = Path.Combine("artifacts", "performance", DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
    public string? Filter { get; private set; }
    public string? Case { get; private set; }
    public string? Result { get; private set; }
    public bool Smoke { get; private set; }
    public bool List { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
            switch (args[i])
            {
                case "--iterations": o.Iterations = int.Parse(Value()); break;
                case "--warmup": o.Warmup = int.Parse(Value()); break;
                case "--repetitions": o.Repetitions = int.Parse(Value()); break;
                case "--replica": o.Replica = int.Parse(Value()); break;
                case "--seed": o.Seed = int.Parse(Value()); break;
                case "--timeout": o.TimeoutSeconds = int.Parse(Value()); break;
                case "--output": o.Output = Value(); break;
                case "--filter": o.Filter = Value(); break;
                case "--case": o.Case = Value(); break;
                case "--result": o.Result = Value(); break;
                case "--list": o.List = true; break;
                case "--smoke": o.Smoke = true; break;
                default: throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }
        if (o.Smoke) { o.Iterations = 3; o.Warmup = 1; }
        if (o.Iterations < 3 || o.Warmup < 1 || o.Repetitions < 1 || o.TimeoutSeconds < 1)
            throw new ArgumentException("Require iterations >= 3, warmup >= 1, repetitions >= 1, timeout >= 1.");
        if (o.Case is not null && o.Result is null) throw new ArgumentException("--case requires --result.");
        return o;
    }
}
