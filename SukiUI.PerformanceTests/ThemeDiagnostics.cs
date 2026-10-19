using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using SukiUI.Enums;
using SukiUI.Extensions;

namespace SukiUI.PerformanceTests;

internal static class ThemeDiagnostics
{
    public static IEnumerable<Scenario> All()
    {
        foreach (var n in new[] { 0, 100, 1000 })
        foreach (var mode in new[] { "base", "accent", "variant-only", "unused-write", "same-write" })
            yield return new($"theme-diag/{mode}/mixed/{n}", "theme-diag", n, "suki", () => Create(n, mode, false));
        foreach (var mode in new[] { "base", "accent", "unused-write" })
            yield return new($"theme-diag/{mode}/bare/1000", "theme-diag", 1000, "suki", () => Create(1000, mode, true));
        yield return new("theme-diag/base/shadcn/1000", "theme-diag", 1000, "shadcn", () => Create(1000, "base", false));
        yield return new("theme-diag/repeat-theme/mixed/1000", "theme-diag", 1000, "suki", () => Create(1000, "repeat-theme", false));
    }

    private static Workload Create(int n, string mode, bool bare)
    {
        var app = Application.Current!;
        var theme = SukiTheme.GetInstance();
        var panel = new UniformGrid { Columns = 10 };
        for (var i = 0; i < n; i++)
        {
            Control control = bare ? new Border { Width = 10, Height = 10 } : (i % 4) switch
            {
                0 => new Button { Content = $"Action {i}" },
                1 => new TextBox { Text = $"Editable value {i}", PlaceholderText = "Enter value" },
                2 => new ToggleSwitch { Content = $"Toggle {i}", IsChecked = i % 2 == 0 },
                _ => new Slider { Value = i % 100, Maximum = 100 }
            };
            control.Margin = new Thickness(2);
            panel.Children.Add(control);
        }
        var window = Ui.Open(panel);
        var initialVisuals = window.GetVisualDescendants().ToArray();
        var initialVisualSet = initialVisuals.ToHashSet();
        var originalResources = app.Resources;
        long applicationEvents = 0, windowEvents = 0, leafEvents = 0, themeEvents = 0;
        Action<SukiUI.Models.SukiColorTheme> colorChanged = _ => themeEvents++;
        theme.OnColorThemeChanged += colorChanged;
        EventHandler<ResourcesChangedEventArgs> appChanged = (_, _) => applicationEvents++;
        EventHandler<ResourcesChangedEventArgs> windowChanged = (_, _) => windowEvents++;
        EventHandler<ResourcesChangedEventArgs> leafChanged = (_, _) => leafEvents++;
        ((IResourceHost)app).ResourcesChanged += appChanged;
        window.ResourcesChanged += windowChanged;
        var leaf = initialVisuals.OfType<StyledElement>().LastOrDefault();
        if (leaf is not null) leaf.ResourcesChanged += leafChanged;
        var alternate = false;
        Dictionary<string, double> metrics = [];
        return new Workload
        {
            BeforeIteration = () => { applicationEvents = windowEvents = leafEvents = themeEvents = 0; },
            Run = () =>
            {
                alternate = !alternate;
                var bytes = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                switch (mode)
                {
                    case "base": theme.ChangeBaseTheme(alternate ? ThemeVariant.Dark : ThemeVariant.Light); break;
                    case "accent": theme.ChangeColorTheme(alternate ? SukiColor.Red : SukiColor.Blue); break;
                    case "variant-only": app.RequestedThemeVariant = alternate ? ThemeVariant.Dark : ThemeVariant.Light; break;
                    case "unused-write": app.Resources["ThemeDiagnosticUnused"] = alternate; break;
                    case "same-write": app.Resources["SukiPrimaryColor"] = theme.ActiveColorTheme!.Primary; break;
                    case "repeat-theme": theme.ChangeColorTheme(theme.ActiveColorTheme!); break;
                }
                var updateMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var updateBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
                bytes = GC.GetAllocatedBytesForCurrentThread();
                start = Stopwatch.GetTimestamp();
                Ui.Flush(window);
                var flushMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var flushBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
                metrics = new()
                {
                    ["updateMs"] = updateMs, ["flushMs"] = flushMs,
                    ["updateBytes"] = updateBytes, ["flushBytes"] = flushBytes,
                    ["applicationEvents"] = applicationEvents, ["windowEvents"] = windowEvents,
                    ["leafEvents"] = leafEvents, ["themeEvents"] = themeEvents
                };
            },
            Validate = () =>
            {
                if (mode is "base" or "variant-only")
                    Ui.Require(app.ActualThemeVariant == (alternate ? ThemeVariant.Dark : ThemeVariant.Light), "Wrong variant");
                if (mode == "accent")
                {
                    var color = SukiTheme.DefaultColorThemes[alternate ? SukiColor.Red : SukiColor.Blue].Primary;
                    Ui.Require(Equals(app.Resources["SukiPrimaryColor"], color), "Wrong resource color");
                    if (!bare && n > 2)
                        Ui.Require(panel.Children.OfType<ToggleSwitch>().First().Foreground is ISolidColorBrush brush && brush.Color == color,
                            "DynamicResource consumer did not update");
                }
                Ui.Require(ReferenceEquals(originalResources, app.Resources), "Application resource dictionary was replaced");
                if (mode != "variant-only") ValidatePalette(app, theme);
                var currentVisuals = window.GetVisualDescendants().ToArray();
                metrics["replacedVisuals"] = currentVisuals.Count(v => !initialVisualSet.Contains(v));
                if (PerfApp.Selected.Theme != "shadcn")
                    Ui.Require(initialVisuals.SequenceEqual(currentVisuals), "Visual instances were replaced");
            },
            Metrics = () => metrics,
            Observe = () => Ui.Visuals(window),
            Cleanup = () =>
            {
                ((IResourceHost)app).ResourcesChanged -= appChanged;
                window.ResourcesChanged -= windowChanged;
                if (leaf is not null) leaf.ResourcesChanged -= leafChanged;
                theme.OnColorThemeChanged -= colorChanged;
                Ui.Close(window);
            }
        };
    }

    internal static void ValidatePalette(Application app, SukiTheme theme)
    {
        ValidatePalette(app, theme.ActiveColorTheme!, theme.ActiveBaseTheme);
    }

    internal static void ValidatePalette(Application app, SukiUI.Models.SukiColorTheme palette, ThemeVariant baseTheme)
    {
        foreach (var (name, color) in new[] { ("SukiPrimaryColor", palette.Primary), ("SukiAccentColor", palette.Accent) })
        {
            Ui.Require(Equals(app.Resources[name], color), "Wrong base color");
            foreach (var (suffix, alpha) in new[] { ("75", .75), ("50", .50), ("25", .25), ("20", .20), ("15", .15), ("10", .10), ("7", .07), ("5", .05), ("3", .03), ("1", .005), ("0", .0) })
                Ui.Require(Equals(app.Resources[name + suffix], color.WithAlpha(alpha)), "Wrong opacity color");
            Ui.Require(Equals(app.Resources[name + "120"], baseTheme == ThemeVariant.Dark ? SukiTheme.Lighten(color, .7) : color), "Wrong 120 color");
            Ui.Require(Equals(app.Resources[name + "150"], baseTheme == ThemeVariant.Dark ? SukiTheme.Lighten(color, 1) : color), "Wrong 150 color");
        }
        Ui.Require(Equals(app.Resources["SukiPrimaryDarkColor"], palette.PrimaryDark), "Wrong primary dark color");
        Ui.Require(Equals(app.Resources["SukiAccentDarkColor"], palette.AccentDark), "Wrong accent dark color");
    }
}
