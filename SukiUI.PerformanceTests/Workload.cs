using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SukiUI.PerformanceTests;

internal sealed record Scenario(string Id, string Area, int Scale, string Theme, Func<Workload> Create);

// Preparation, validation, observation and teardown are deliberately outside the stopwatch.
// A sample is one complete Run, not an inferred per-control or per-frame measurement.
internal sealed class Workload : IDisposable
{
    public Action? BeforeIteration { get; init; }
    public required Action Run { get; init; }
    public Action? AfterIteration { get; init; }
    public Action? Validate { get; init; }
    public Func<Dictionary<string, double>>? Observe { get; init; }
    public Func<Dictionary<string, double>>? Metrics { get; init; }
    public Action? Cleanup { get; init; }
    public void Dispose() => Cleanup?.Invoke();
}

internal static class Ui
{
    public static Window Open(Control content)
    {
        var window = new Window { Width = 1280, Height = 800, Content = content };
        window.Show();
        Flush(window);
        return window;
    }

    public static void Flush(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    public static void Close(Window? window)
    {
        if (window is null) return;
        window.Content = null;
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    public static Dictionary<string, double> Visuals(Window window) => new()
    {
        ["visuals"] = window.GetVisualDescendants().Count(),
        ["width"] = window.Bounds.Width,
        ["height"] = window.Bounds.Height
    };

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
