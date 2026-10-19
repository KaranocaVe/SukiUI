using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Skia;
using Avalonia.Styling;
using SukiUI;
using SukiUI.Enums;
using SukiUI.Extensions;
using SukiUI.Theme.Shadcn;

if (args.Length != 2 || args[0] is not ("suki" or "shadcn") || !Path.IsPathFullyQualified(args[1]))
{
    Console.Error.WriteLine("Usage: ThemeNativeSmoke <suki|shadcn> <absolute-output.json>");
    return 2;
}

var mode = args[0];
var outputPath = args[1];
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
var app = new SmokeApp(mode, outputPath);
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
try
{
    return AppBuilder.Configure(() => app)
        .UsePlatformDetect()
        .UseSkia()
        .WithInterFont()
        .StartWithClassicDesktopLifetime(Array.Empty<string>());
}
catch (Exception error)
{
    File.WriteAllText(outputPath, JsonSerializer.Serialize(new { mode, passed = false, error = error.ToString() }, jsonOptions));
    Console.Error.WriteLine(error);
    return 1;
}

internal sealed class SmokeApp(string mode, string outputPath) : Application
{
    private IClassicDesktopStyleApplicationLifetime? _lifetime;
    private Window? _window;
    private ToggleSwitch? _toggle;
    private int _colorCallbacks;
    private int _baseCallbacks;
    private object? _appResources;
    private int _initialStyleCount;
    private readonly HashSet<string> _verifiedPaletteKeys = new(StringComparer.Ordinal);

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new SukiTheme { Locale = "en-US" });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime)
            throw new InvalidOperationException("Classic desktop lifetime was not initialized.");

        _lifetime = lifetime;
        lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _toggle = new ToggleSwitch { Content = "Live theme", IsChecked = true };
        var content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new Button { Content = "Native button" },
                new TextBox { Text = "Native text box", Width = 240 },
                _toggle,
                new Slider { Minimum = 0, Maximum = 100, Value = 35, Width = 240 }
            }
        };
        _window = new Window { Title = $"Suki theme smoke ({mode})", Width = 420, Height = 360, Content = content };
        lifetime.MainWindow = _window;

        if (mode == "shadcn")
            Shadcn.Configure(this, ThemeVariant.Light);

        var theme = SukiTheme.GetInstance(this);
        theme.OnColorThemeChanged += _ => _colorCallbacks++;
        theme.OnBaseThemeChanged += _ => _baseCallbacks++;
        _appResources = Resources;
        _initialStyleCount = Styles.Count;
        _window.Opened += OnOpened;
        base.OnFrameworkInitializationCompleted();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        var checks = new List<object>();
        var passed = false;
        string? error = null;
        try
        {
            var window = _window!;
            var theme = SukiTheme.GetInstance(this);
            Require(window.TryGetPlatformHandle() is { Handle: var handle } && handle != IntPtr.Zero,
                "The window has no native platform handle.");

            for (var i = 0; i < 6; i++)
            {
                var variant = i % 2 == 0 ? ThemeVariant.Dark : ThemeVariant.Light;
                theme.ChangeBaseTheme(variant);
                await Task.Delay(120);
                Require(ActualThemeVariant == variant, $"Actual theme variant did not become {variant}.");
                VerifyLiveState(theme, variant);

                if (mode == "shadcn")
                {
                    var expectedName = variant == ThemeVariant.Dark ? "White" : "Black";
                    Require(theme.ActiveColorTheme?.DisplayName == expectedName,
                        $"Shadcn base-theme callback did not restore its {expectedName} palette.");
                }
                checks.Add(new { step = i, stage = "base", variant = variant.ToString(), palette = theme.ActiveColorTheme?.DisplayName });

                var color = i % 2 == 0 ? SukiColor.Red : SukiColor.Blue;
                theme.ChangeColorTheme(color);
                await Task.Delay(80);
                Require(theme.ActiveColorTheme == SukiTheme.DefaultColorThemes[color],
                    $"Active color theme did not become {color}.");
                VerifyLiveState(theme, variant);
                Require(Styles.Count <= _initialStyleCount + 1, "Theme styles accumulated across repeated changes.");
                checks.Add(new { step = i, stage = "color", variant = variant.ToString(), palette = theme.ActiveColorTheme?.DisplayName });
            }

            var expectedColorCallbacks = mode == "shadcn" ? 18 : 12;
            Require(_colorCallbacks == expectedColorCallbacks,
                $"Expected {expectedColorCallbacks} color callbacks, observed {_colorCallbacks}.");
            Require(_baseCallbacks == 6, $"Expected 6 base callbacks, observed {_baseCallbacks}.");
            Require(_verifiedPaletteKeys.Count == 30, "Did not verify all 30 primary/accent palette keys.");
            passed = true;
        }
        catch (Exception exception)
        {
            error = exception.ToString();
        }
        finally
        {
            var result = new
            {
                mode,
                passed,
                outputPath,
                nativeHandle = _window?.TryGetPlatformHandle()?.Handle.ToInt64(),
                colorCallbacks = _colorCallbacks,
                baseCallbacks = _baseCallbacks,
                paletteKeysVerified = _verifiedPaletteKeys.Count,
                styleCount = Styles.Count,
                initialStyleCount = _initialStyleCount,
                resourceDictionaryPreserved = ReferenceEquals(Resources, _appResources),
                checks,
                error
            };
            File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            _window?.Close();
            _lifetime?.Shutdown(passed ? 0 : 1);
        }
    }

    private void VerifyLiveState(SukiTheme theme, ThemeVariant variant)
    {
        Require(ReferenceEquals(Resources, _appResources), "Application resource dictionary was replaced.");
        Require(_window!.TryGetPlatformHandle() is { Handle: var handle } && handle != IntPtr.Zero,
            "The window lost its native platform handle.");
        Require(_window.ActualThemeVariant == variant, $"Window theme variant is not {variant}.");

        var active = theme.ActiveColorTheme ?? throw new InvalidOperationException("No active color theme.");
        var primary = GetColor("SukiPrimaryColor");
        Require(primary == active.Primary, "SukiPrimaryColor does not match ActiveColorTheme.Primary.");
        Require(_toggle!.Foreground is ISolidColorBrush foreground && foreground.Color == primary,
            "The live ToggleSwitch foreground does not match SukiPrimaryColor.");
        VerifyPalette("SukiPrimaryColor", active.Primary, active.PrimaryDark, variant);
        VerifyPalette("SukiAccentColor", active.Accent, active.AccentDark, variant);
        Require(GetColor("SukiPrimaryDarkColor") == active.PrimaryDark, "SukiPrimaryDarkColor is stale.");
        Require(GetColor("SukiAccentDarkColor") == active.AccentDark, "SukiAccentDarkColor is stale.");
        Require(Styles.Count <= _initialStyleCount + 1, "Theme styles accumulated across repeated changes.");
    }

    private void VerifyPalette(string prefix, Color color, Color dark, ThemeVariant variant)
    {
        var suffixes = new[] { "", "75", "50", "25", "20", "15", "10", "7", "5", "3", "1", "0", "120", "150" };
        var alpha = new Dictionary<string, double>
        {
            ["75"] = 0.75, ["50"] = 0.50, ["25"] = 0.25, ["20"] = 0.20,
            ["15"] = 0.15, ["10"] = 0.10, ["7"] = 0.07, ["5"] = 0.05,
            ["3"] = 0.03, ["1"] = 0.005, ["0"] = 0.00
        };
        foreach (var suffix in suffixes)
        {
            var actual = GetColor(prefix + suffix);
            if (suffix.Length == 0)
                Require(actual == color, $"{prefix} base value is stale.");
            else if (alpha.TryGetValue(suffix, out var opacity))
                Require(actual == color.WithAlpha(opacity), $"{prefix}{suffix} alpha value is wrong.");
        }

        var expected120 = variant == ThemeVariant.Dark ? SukiTheme.Lighten(color, 0.7) : color;
        var expected150 = variant == ThemeVariant.Dark ? SukiTheme.Lighten(color, 1) : color;
        Require(GetColor(prefix + "120") == expected120, $"{prefix}120 value is wrong.");
        Require(GetColor(prefix + "150") == expected150, $"{prefix}150 value is wrong.");
        Require(GetColor(prefix[..^5] + "DarkColor") == dark, $"{prefix} dark value is wrong.");
    }

    private Color GetColor(string key)
    {
        _verifiedPaletteKeys.Add(key);
        return Resources[key] is Color color
            ? color
            : throw new InvalidOperationException($"Resource {key} is missing or is not a Color.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
