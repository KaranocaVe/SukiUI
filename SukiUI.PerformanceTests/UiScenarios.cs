using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using System.Runtime.InteropServices;
using Dock.Avalonia.Controls;
using Dock.Model.Avalonia;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Core;
using SukiUI.Controls;
using SukiUI.Controls.Gauges.HorizontalBarMeter;
using SukiUI.Controls.Gauges;
using SukiUI.Enums;

namespace SukiUI.PerformanceTests;

internal static class UiScenarios
{
    public static IEnumerable<Scenario> All()
    {
        foreach (var theme in new[] { "simple", "suki" })
        foreach (var n in new[] { 10, 100, 1000 })
        {
            foreach (var kind in new[] { "button", "textbox", "toggle", "slider", "mixed" })
                yield return new($"mount/{kind}/{theme}/{n}", "mount", n, theme, () => Mount(n, kind));
            yield return new($"layout/resize/{theme}/{n}", "layout", n, theme, () => Resize(n));
            yield return new($"interaction/enable/{theme}/{n}", "interaction", n, theme, () => Enable(n));
            yield return new($"theme/base/{theme}/{n}", "theme", n, theme, () => SwitchTheme(n, "base"));
        }
        foreach (var n in new[] { 10, 100, 1000 })
        {
            foreach (var kind in new[] { "glasscard", "infobar", "circle", "wave", "bar", "gauge", "expander" })
                yield return new($"mount/{kind}/suki/{n}", "mount", n, "suki", () => Mount(n, kind));
            yield return new($"theme/accent/suki/{n}", "theme", n, "suki", () => SwitchTheme(n, "accent"));
            yield return new($"theme/locale/suki/{n}", "theme", n, "suki", () => SwitchTheme(n, "locale"));
            yield return new($"theme/base/shadcn/{n}", "theme", n, "shadcn", () => SwitchTheme(n, "base"));
            yield return new($"navigation/search/suki/{n}", "navigation", n, "suki", () => Search(n));
        }
        foreach (var theme in new[] { "simple", "suki" })
        foreach (var n in new[] { 100, 1000, 10000, 100000 })
        {
            yield return new($"list/mount/{theme}/{n}", "list", n, theme, () => List(n, false));
            yield return new($"list/scroll/{theme}/{n}", "list", n, theme, () => List(n, true));
        }
        foreach (var n in new[] { 100, 1000, 10000, 100000 })
        {
            yield return new($"datagrid/mount/suki/{n}", "datagrid", n, "suki", () => DataGrid(n, false));
            yield return new($"datagrid/scroll/suki/{n}", "datagrid", n, "suki", () => DataGrid(n, true));
        }
        foreach (var n in new[] { 1, 5, 20 })
        {
            yield return new($"colorpicker/mount/suki/{n}", "colorpicker", n, "suki", () => Mount(n, "colorpicker"));
            yield return new($"colorpicker/change/suki/{n}", "colorpicker", n, "suki", () => Colors(n));
        }
        foreach (var n in new[] { 5, 20, 100 })
        {
            yield return new($"dock/mount/suki/{n}", "dock", n, "suki", () => Dock(n, false));
            yield return new($"dock/switch/suki/{n}", "dock", n, "suki", () => Dock(n, true));
        }
        foreach (var theme in new[] { "simple", "suki" })
        foreach (var n in new[] { 10, 100, 500 })
            yield return new($"render/mixed/{theme}/{n}", "render", n, theme, () => Render(n, "mixed"));
        foreach (var n in new[] { 10, 100, 500 })
            yield return new($"render/glasscard/suki/{n}", "render", n, "suki", () => Render(n, "glasscard"));
        // This exercises the real composited software FALLBACK (a solid fill), not the GPU shader.
        foreach (var style in Enum.GetValues<SukiBackgroundStyle>())
            yield return new($"background/software/{style.ToString().ToLowerInvariant()}/1", "background", 1,
                "suki", () => Background(style));
    }

    private static Control Make(string kind, int i) => kind switch
    {
        "button" => new Button { Content = $"Action {i}" },
        "textbox" => new TextBox { Text = $"Editable value {i}", PlaceholderText = "Enter value" },
        "toggle" => new ToggleSwitch { Content = $"Toggle {i}", IsChecked = i % 2 == 0 },
        "slider" => new Slider { Value = i % 100, Maximum = 100 },
        "glasscard" => new GlassCard { Content = new TextBlock { Text = $"Card {i}" } },
        "infobar" => new InfoBar { Title = $"Notice {i}", Message = "Representative message" },
        "circle" => new CircleProgressBar { Value = i % 100, Width = 64, Height = 64 },
        "wave" => new WaveProgress { Value = i % 100, Width = 64, Height = 64 },
        "bar" => new HorizontalBarMeter { Value = i % 100, Height = 48 },
        "gauge" => new RadialGauge { Value = i % 100, Width = 100, Height = 100 },
        "expander" => new Expander { Header = $"Section {i}", IsExpanded = true, Content = new TextBlock { Text = "Expanded body" } },
        "colorpicker" => new ColorView { Color = Color.FromRgb((byte)i, 128, 200) },
        "mixed" => Make(new[] { "button", "textbox", "toggle", "slider" }[i % 4], i),
        _ => throw new ArgumentException(kind)
    };

    private static UniformGrid Panel(int n, string kind)
    {
        var panel = new UniformGrid { Columns = kind == "colorpicker" ? 2 : 10 };
        for (var i = 0; i < n; i++)
        {
            var c = Make(kind, i);
            c.Margin = new Thickness(2);
            panel.Children.Add(c);
        }
        return panel;
    }

    private static Workload Mount(int n, string kind)
    {
        Window? window = null;
        Dictionary<string, double> observed = [];
        return new Workload
        {
            Run = () => window = Ui.Open(Panel(n, kind)),
            Validate = () =>
            {
                observed = Ui.Visuals(window!);
                Ui.Require(observed["visuals"] > n, "Controls did not attach/apply templates.");
            },
            AfterIteration = () => { Ui.Close(window); window = null; },
            Observe = () => observed,
            Cleanup = () => Ui.Close(window)
        };
    }

    private static Workload Resize(int n)
    {
        var panel = Panel(n, "mixed");
        var window = Ui.Open(panel);
        var alternate = false;
        return new Workload
        {
            Run = () => { panel.Width = (alternate = !alternate) ? 1100 : 1200; Ui.Flush(window); },
            Validate = () => Ui.Require(panel.Bounds.Width == panel.Width, "Resize did not reach layout."),
            Observe = () => Ui.Visuals(window),
            Cleanup = () => Ui.Close(window)
        };
    }

    private static Workload Enable(int n)
    {
        var panel = Panel(n, "button");
        var window = Ui.Open(panel);
        var enabled = true;
        return new Workload
        {
            Run = () =>
            {
                enabled = !enabled;
                foreach (var c in panel.Children) c.IsEnabled = enabled;
                Ui.Flush(window);
            },
            Validate = () => Ui.Require(panel.Children.All(c => c.IsEnabled == enabled), "Enable change failed."),
            Observe = () => Ui.Visuals(window),
            Cleanup = () => Ui.Close(window)
        };
    }

    private static Workload SwitchTheme(int n, string mode)
    {
        var window = Ui.Open(Panel(n, "mixed"));
        var app = Application.Current!;
        var alternate = false;
        var styleCount = app.Styles.Count;
        return new Workload
        {
            Run = () =>
            {
                alternate = !alternate;
                switch (mode)
                {
                    case "base":
                        var variant = alternate ? ThemeVariant.Dark : ThemeVariant.Light;
                        if (PerfApp.Selected.Theme == "simple") app.RequestedThemeVariant = variant;
                        else SukiTheme.GetInstance().ChangeBaseTheme(variant);
                        break;
                    case "accent": SukiTheme.GetInstance().ChangeColorTheme(alternate ? SukiColor.Red : SukiColor.Blue); break;
                    case "locale": SukiTheme.GetInstance().Locale = alternate ? "zh-CN" : "en-US"; break;
                }
                Ui.Flush(window);
            },
            Validate = () =>
            {
                if (mode == "base")
                    Ui.Require(app.ActualThemeVariant == (alternate ? ThemeVariant.Dark : ThemeVariant.Light), "Theme change failed.");
                Ui.Require(app.Styles.Count <= styleCount + 1, "Theme switch accumulated styles.");
            },
            Observe = () => { var d = Ui.Visuals(window); d["applicationStyles"] = app.Styles.Count; return d; },
            Cleanup = () => Ui.Close(window)
        };
    }

    private static Workload Search(int n)
    {
        var menu = new SukiSideMenu { IsSearchEnabled = true };
        for (var i = 0; i < n; i++)
            menu.Items.Add(new SukiSideMenuItem { Header = $"Page {i:D5}", PageContent = $"Body {i}" });
        var window = Ui.Open(menu);
        var alternate = false;
        return new Workload
        {
            Run = () => { menu.SearchText = (alternate = !alternate) ? "Page 00000" : "Page"; Ui.Flush(window); },
            Observe = () =>
            {
                var d = Ui.Visuals(window);
                d["expandedMenuItemBorders"] = menu.GetVisualDescendants().OfType<Border>()
                    .Count(b => b.Name == "PART_Border" && b.GetBaseValue(Layoutable.MaxHeightProperty).Value > 0);
                return d;
            },
            Validate = () =>
            {
                // Show/Hide animate the template border's MaxHeight, not IsVisible.
                var borders = menu.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "PART_Border").ToArray();
                Ui.Require(borders.Length == n, "Menu item templates did not materialize.");
                Ui.Require(borders.Count(b => b.GetBaseValue(Layoutable.MaxHeightProperty).Value > 0) == (alternate ? 1 : n),
                    "Search did not change the intended item targets.");
            },
            Cleanup = () => Ui.Close(window)
        };
    }

    private static Workload List(int n, bool scroll)
    {
        var rows = Enumerable.Range(0, n).Select(i => $"Row {i:D6}").ToArray();
        ListBox? list = null;
        Window? window = null;
        Dictionary<string, double> observed = [];
        void Open()
        {
            list = new ListBox { ItemsSource = rows, ItemTemplate = new FuncDataTemplate<string>((x, _) => new TextBlock { Text = x, Height = 28 }) };
            window = Ui.Open(list);
        }
        if (scroll) Open();
        var alternate = false;
        return new Workload
        {
            Run = () =>
            {
                if (!scroll) Open();
                else { list!.ScrollIntoView((alternate = !alternate) ? n - 1 : 0); Ui.Flush(window!); }
            },
            Validate = () =>
            {
                observed = Ui.Visuals(window!);
                var realized = list!.GetRealizedContainers().Count();
                observed["realizedItems"] = realized;
                Ui.Require(realized > 0 && realized < n, "Expected bounded ListBox virtualization.");
                if (scroll)
                    Ui.Require(list.ContainerFromIndex(alternate ? n - 1 : 0) is not null, "Target row was not realized after scroll.");
            },
            AfterIteration = scroll ? null : () => { Ui.Close(window); window = null; },
            Observe = () => observed,
            Cleanup = () => Ui.Close(window)
        };
    }

    private static Workload DataGrid(int n, bool scroll)
    {
        var rows = Enumerable.Range(0, n).Select(i => new GridRow(i, $"Item {i:D6}", i * 1.25, i % 2 == 0)).ToArray();
        DataGrid? grid = null;
        Window? window = null;
        Dictionary<string, double> observed = [];
        void Open()
        {
            grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, RowHeight = 28 };
            foreach (var name in new[] { "Id", "Name", "Amount", "Enabled" })
                grid.Columns.Add(new DataGridTextColumn { Header = name, Binding = new Binding(name) });
            window = Ui.Open(grid);
        }
        if (scroll) Open();
        var alternate = false;
        return new Workload
        {
            Run = () =>
            {
                if (!scroll) Open();
                else { grid!.ScrollIntoView(rows[(alternate = !alternate) ? n - 1 : 0], grid.Columns[0]); Ui.Flush(window!); }
            },
            Validate = () =>
            {
                var visualRows = grid!.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.IsVisible).ToArray();
                observed = Ui.Visuals(window!);
                observed["realizedRows"] = visualRows.Length;
                Ui.Require(visualRows.Length > 0 && visualRows.Length < n, "Expected DataGrid row virtualization.");
                if (scroll)
                    Ui.Require(visualRows.Any(r => ReferenceEquals(r.DataContext, rows[alternate ? n - 1 : 0])), "DataGrid target row not realized.");
            },
            AfterIteration = scroll ? null : () => { Ui.Close(window); window = null; },
            Observe = () => observed,
            Cleanup = () => Ui.Close(window)
        };
    }

    private static Workload Colors(int n)
    {
        var panel = Panel(n, "colorpicker");
        var window = Ui.Open(panel);
        var alternate = false;
        return new Workload
        {
            Run = () =>
            {
                var color = (alternate = !alternate) ? Avalonia.Media.Colors.Red : Avalonia.Media.Colors.Blue;
                foreach (var picker in panel.Children.OfType<ColorView>()) picker.Color = color;
                Ui.Flush(window);
            },
            Validate = () => Ui.Require(panel.Children.OfType<ColorView>().All(p => p.Color == (alternate ? Avalonia.Media.Colors.Red : Avalonia.Media.Colors.Blue)), "Color update failed."),
            Observe = () => Ui.Visuals(window),
            Cleanup = () => Ui.Close(window)
        };
    }

    private static Workload Dock(int n, bool change)
    {
        Window? window = null;
        DocumentDock? documentDock = null;
        IDockable[] documents = [];
        Dictionary<string, double> observed = [];
        void Open()
        {
            var factory = new Factory();
            documents = Enumerable.Range(0, n).Select(i => (IDockable)new Document
            {
                Id = $"doc{i}", Title = $"Document {i}", Content = new TextBlock { Text = $"Content {i}" }
            }).ToArray();
            documentDock = new DocumentDock { VisibleDockables = factory.CreateList(documents), ActiveDockable = documents[0] };
            var root = new RootDock { VisibleDockables = factory.CreateList<IDockable>(documentDock), ActiveDockable = documentDock, DefaultDockable = documentDock };
            var control = new DockControl { Factory = factory, Layout = root, InitializeFactory = true, InitializeLayout = true };
            window = Ui.Open(control);
        }
        if (change) Open();
        var alternate = false;
        return new Workload
        {
            Run = () =>
            {
                if (!change) Open();
                else { documentDock!.ActiveDockable = documents[(alternate = !alternate) ? n - 1 : 0]; Ui.Flush(window!); }
            },
            Validate = () =>
            {
                observed = Ui.Visuals(window!);
                Ui.Require(observed["visuals"] > n, "Dock templates did not apply.");
                if (change)
                    Ui.Require(window!.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == $"Content {(alternate ? n - 1 : 0)}"), "Active document content was not mounted.");
            },
            AfterIteration = change ? null : () => { Ui.Close(window); window = null; },
            Observe = () => observed,
            Cleanup = () => Ui.Close(window)
        };
    }

    private static Workload Render(int n, string kind)
    {
        var panel = Panel(n, kind);
        var window = Ui.Open(panel);
        var bitmap = new RenderTargetBitmap(new PixelSize(1280, 800), new Vector(96, 96));
        var pixels = Marshal.AllocHGlobal(1280 * 800 * 4);
        var paintedSamples = 0;
        var alternate = false;
        return new Workload
        {
            Run = () =>
            {
                // Change a rendered property so this is never an unchanged-scene cache hit.
                panel.Opacity = (alternate = !alternate) ? 0.99 : 1;
                bitmap.Render(panel);
            },
            Validate = () =>
            {
                bitmap.CopyPixels(new PixelRect(0, 0, 1280, 800), pixels, 1280 * 800 * 4, 1280 * 4);
                paintedSamples = 0;
                for (var y = 10; y < 800; y += 31)
                for (var x = 10; x < 1280; x += 31)
                    if (((uint)Marshal.ReadInt32(pixels, (y * 1280 + x) * 4) >> 24) > 0) paintedSamples++;
                Ui.Require(paintedSamples > 0, "Render target remained transparent.");
            },
            Observe = () => { var d = Ui.Visuals(window); d["pixels"] = 1280 * 800; d["paintedGridSamples"] = paintedSamples; return d; },
            Cleanup = () => { Marshal.FreeHGlobal(pixels); bitmap.Dispose(); Ui.Close(window); }
        };
    }

    private static Workload Background(SukiBackgroundStyle style)
    {
        var background = new SukiBackground { Style = style, AnimationEnabled = false, ForceSoftwareRendering = true };
        var window = Ui.Open(background);
        Bitmap? frame = null;
        var alternate = false;
        return new Workload
        {
            Run = () =>
            {
                SukiTheme.GetInstance().ChangeBaseTheme((alternate = !alternate) ? ThemeVariant.Dark : ThemeVariant.Light);
                Ui.Flush(window);
                frame = window.CaptureRenderedFrame();
            },
            Validate = () => Ui.Require(frame is not null && frame.PixelSize.Width == 1280, "Composited frame was not produced."),
            AfterIteration = () => { frame?.Dispose(); frame = null; },
            Observe = () => new() { ["pixels"] = 1280 * 800, ["gpuShaderMeasured"] = 0 },
            Cleanup = () => { frame?.Dispose(); Ui.Close(window); }
        };
    }

    public sealed record GridRow(int Id, string Name, double Amount, bool Enabled);
}
