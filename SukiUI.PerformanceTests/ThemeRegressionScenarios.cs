using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using SukiUI.Models;

namespace SukiUI.PerformanceTests;

internal static class ThemeRegressionScenarios
{
    public static IEnumerable<Scenario> All()
    {
        yield return Make("palette/all-colors", "suki", Palette);
        yield return Make("palette/reapply-repairs", "suki", ReapplyRepairsResource);
        yield return Make("palette/deferred-overwrite", "suki", DeferredFactoriesAreNotRun);
        yield return Make("palette/equals-is-not-consulted", "suki", MaliciousEqualsIsNotRun);
        yield return Make("palette/custom-dictionary-indexer", "suki", CustomDictionaryUsesIndexer);
        yield return Make("palette/custom-dictionary-lazy-variant", "suki", CustomDictionaryReadsVariantLazily);
        yield return Make("palette/inherited-same-values", "suki", InheritedValuesBecomeLocal);
        yield return Make("palette/complete-notification", "suki", CompletePaletteNotification);
        yield return Make("callback/color-reentry", "suki", ColorCallbackReentry);
        yield return Make("callback/color-throws-recovers", "suki", ColorCallbackThrowsThenRecovers);
        yield return Make("callback/resources-throws-recovers", "suki", ResourceCallbackThrowsThenRecovers);
        yield return Make("callback/count-suki", "suki", () => CallbackCount("suki"));
        yield return Make("callback/count-shadcn", "shadcn", () => CallbackCount("shadcn"));
        yield return Make("dynamic-resource/toggle-switch", "suki", ToggleSwitchTracksTheme);
    }

    private static Scenario Make(string suffix, string theme, Func<Workload> create) =>
        new($"theme-regression/{suffix}", "theme-regression", 1, theme, create);

    private static Workload Palette()
    {
        var app = Application.Current!;
        var theme = SukiTheme.GetInstance();
        var customThemes = new[]
        {
            new SukiColorTheme("Transparent", Color.FromArgb(0, 20, 40, 60), Color.FromArgb(0, 180, 160, 140)),
            new SukiColorTheme("Translucent", Color.FromArgb(63, 200, 30, 90), Color.FromArgb(127, 15, 120, 220))
        };
        var themes = theme.ColorThemes.Concat(customThemes).ToArray();

        return new Workload
        {
            Run = () =>
            {
                foreach (var colorTheme in themes)
                foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    theme.ChangeBaseTheme(variant);
                    theme.ChangeColorTheme(colorTheme);
                    ThemeDiagnostics.ValidatePalette(app, theme);
                }
            }
        };
    }

    private static Workload ReapplyRepairsResource()
    {
        var app = Application.Current!;
        var theme = SukiTheme.GetInstance();
        return new Workload
        {
            Run = () =>
            {
                var resources = app.Resources;
                var active = theme.ActiveColorTheme!;
                app.Resources["SukiPrimaryColor"] = Color.FromRgb(1, 2, 3);
                theme.ChangeColorTheme(active);
                Ui.Require(Equals(app.Resources["SukiPrimaryColor"], active.Primary),
                    "Reapplying the active color theme did not repair an externally changed resource.");
                Ui.Require(ReferenceEquals(resources, app.Resources), "Application resources were replaced.");
            }
        };
    }

    private static Workload DeferredFactoriesAreNotRun()
    {
        var app = Application.Current!;
        var theme = SukiTheme.GetInstance();
        var colorTheme = new SukiColorTheme("Deferred overwrite", Color.FromRgb(31, 71, 111), Color.FromRgb(151, 191, 231));
        return new Workload
        {
            Run = () =>
            {
                var original = app.Resources;
                var resources = new ResourceDictionary();
                var sharedRuns = 0;
                var notSharedRuns = 0;
                try
                {
                    resources.AddDeferred("SukiPrimaryColor", _ =>
                    {
                        sharedRuns++;
                        return Color.FromRgb(9, 8, 7);
                    });
                    resources.AddNotSharedDeferred("SukiAccentColor", new DeferredFactory(() =>
                    {
                        notSharedRuns++;
                        return Color.FromRgb(6, 5, 4);
                    }));

                    // Attaching a new application dictionary broadcasts a resource change and
                    // may resolve deferred entries independently of the palette update.
                    app.Resources = resources;
                    var sharedRunsBeforeUpdate = sharedRuns;
                    var notSharedRunsBeforeUpdate = notSharedRuns;
                    Ui.Require(notSharedRunsBeforeUpdate == 0,
                        "The not-shared deferred accent was already evaluated while attaching application resources.");
                    // A nonzero shared baseline means attachment already materialized that
                    // entry, so this iteration only checks for additional evaluation on update.
                    theme.ChangeColorTheme(colorTheme);
                    Ui.Require(sharedRuns == sharedRunsBeforeUpdate && notSharedRuns == notSharedRunsBeforeUpdate,
                        "Palette update evaluated a deferred factory while writing palette resources.");
                    Ui.Require(resources.TryGetValue("SukiPrimaryColor", out var primary) && Equals(primary, colorTheme.Primary),
                        "Primary resource did not contain the palette color after the update.");
                    Ui.Require(resources.TryGetValue("SukiAccentColor", out var accent) && Equals(accent, colorTheme.Accent),
                        "Accent resource did not contain the palette color after the update.");
                }
                finally
                {
                    app.Resources = original;
                }
            }
        };
    }

    private static Workload MaliciousEqualsIsNotRun()
    {
        var app = Application.Current!;
        var theme = SukiTheme.GetInstance();
        var colorTheme = new SukiColorTheme("Equals guard", Color.FromRgb(41, 81, 121), Color.FromRgb(161, 201, 241));
        return new Workload
        {
            Run = () =>
            {
                var original = app.Resources;
                var resources = new ResourceDictionary();
                var trap = new EqualityTrap();
                try
                {
                    resources["SukiPrimaryColor"] = trap;
                    // Resource-host notification on attachment can inspect existing values;
                    // isolate equality calls made by ChangeColorTheme itself.
                    app.Resources = resources;
                    var callsBeforeUpdate = trap.Calls;
                    theme.ChangeColorTheme(colorTheme);
                    Ui.Require(trap.Calls == callsBeforeUpdate,
                        "Theme application compared an existing resource value.");
                    Ui.Require(resources.TryGetValue("SukiPrimaryColor", out var primary) &&
                               primary is Color actual && actual == colorTheme.Primary,
                        "The existing non-Color resource was not replaced by the palette Color.");
                }
                finally
                {
                    app.Resources = original;
                }
            }
        };
    }

    private static Workload CustomDictionaryUsesIndexer()
    {
        var app = Application.Current!;
        var theme = SukiTheme.GetInstance();
        var colorTheme = new SukiColorTheme("Indexer fallback", Color.FromRgb(52, 92, 132), Color.FromRgb(172, 212, 252));
        return new Workload
        {
            Run = () =>
            {
                var original = app.Resources;
                var resources = new CountingDictionary();
                app.Resources = resources;
                try
                {
                    theme.ChangeColorTheme(colorTheme);
                    Ui.Require(resources.SetterCalls == 30,
                        $"Expected 30 interface indexer writes, got {resources.SetterCalls}.");
                    Ui.Require(ReferenceEquals(resources, app.Resources), "Application resources were replaced.");
                }
                finally
                {
                    app.Resources = original;
                }
            }
        };
    }

    private static Workload CustomDictionaryReadsVariantLazily()
    {
        var app = Application.Current!;
        var theme = SukiTheme.GetInstance();
        var colorTheme = new SukiColorTheme("Lazy variant", Color.FromRgb(53, 93, 133), Color.FromRgb(173, 213, 253));
        return new Workload
        {
            Run = () =>
            {
                app.RequestedThemeVariant = ThemeVariant.Light;
                var original = app.Resources;
                var resources = new VariantChangingDictionary(() => app.RequestedThemeVariant = ThemeVariant.Dark);
                app.Resources = resources;
                try
                {
                    theme.ChangeColorTheme(colorTheme);
                    Ui.Require(resources.SetterCalls == 30, "Custom dictionary did not receive all 30 writes.");
                    Ui.Require(theme.ActiveBaseTheme == ThemeVariant.Dark, "The custom setter did not switch the base variant.");
                    ThemeDiagnostics.ValidatePalette(app, theme);
                }
                finally
                {
                    app.Resources = original;
                    app.RequestedThemeVariant = ThemeVariant.Light;
                }
            }
        };
    }

    private static Workload InheritedValuesBecomeLocal()
    {
        var app = Application.Current!;
        var theme = SukiTheme.GetInstance();
        var colorTheme = new SukiColorTheme("Inherited equality", Color.FromRgb(54, 94, 134), Color.FromRgb(174, 214, 254));
        return new Workload
        {
            Run = () =>
            {
                app.RequestedThemeVariant = ThemeVariant.Light;
                var original = app.Resources;
                var resources = new ResourceDictionary();
                var merged = new ResourceDictionary { ["SukiPrimaryColor"] = colorTheme.Primary };
                var themed = new ResourceDictionary { ["SukiAccentColor"] = colorTheme.Accent };
                resources.MergedDictionaries.Add(merged);
                resources.ThemeDictionaries[ThemeVariant.Light] = themed;
                Ui.Require(resources.TryGetResource("SukiPrimaryColor", ThemeVariant.Light, out var inheritedPrimary) &&
                           Equals(inheritedPrimary, colorTheme.Primary), "Merged primary setup failed.");
                Ui.Require(resources.TryGetResource("SukiAccentColor", ThemeVariant.Light, out var inheritedAccent) &&
                           Equals(inheritedAccent, colorTheme.Accent), "Theme dictionary accent setup failed.");
                app.Resources = resources;
                try
                {
                    theme.ChangeColorTheme(colorTheme);
                    Ui.Require(resources.TryGetValue("SukiPrimaryColor", out var primary) && Equals(primary, colorTheme.Primary),
                        "Inherited same-value primary was not written locally.");
                    Ui.Require(resources.TryGetValue("SukiAccentColor", out var accent) && Equals(accent, colorTheme.Accent),
                        "Inherited same-value accent was not written locally.");
                    Ui.Require(ReferenceEquals(resources, app.Resources), "Application resources were replaced.");
                }
                finally
                {
                    app.Resources = original;
                }
            }
        };
    }

    private static Workload CompletePaletteNotification()
    {
        var app = Application.Current!;
        var theme = SukiTheme.GetInstance();
        var target = new SukiColorTheme("Complete batch", Colors.Crimson, Colors.Teal);
        return new Workload
        {
            Run = () =>
            {
                var notifications = 0;
                EventHandler<ResourcesChangedEventArgs> handler = (_, _) =>
                {
                    notifications++;
                    ThemeDiagnostics.ValidatePalette(app, target, theme.ActiveBaseTheme);
                };
                ((IResourceHost)app).ResourcesChanged += handler;
                try
                {
                    theme.ChangeColorTheme(target);
                    Ui.Require(notifications == 1, $"Expected one complete palette notification, got {notifications}.");
                    Ui.Require(ReferenceEquals(theme.ActiveColorTheme, target), "Active theme was not updated.");
                }
                finally
                {
                    ((IResourceHost)app).ResourcesChanged -= handler;
                }
            }
        };
    }

    private static Workload ColorCallbackReentry()
    {
        var app = Application.Current!;
        var theme = SukiTheme.GetInstance();
        var outer = new SukiColorTheme("Outer callback", Color.FromRgb(55, 95, 135), Color.FromRgb(175, 215, 255));
        var inner = new SukiColorTheme("Inner callback", Color.FromRgb(56, 96, 136), Color.FromRgb(176, 216, 254));
        return new Workload
        {
            Run = () =>
            {
                var previous = theme.OnColorThemeChanged;
                var reentered = false;
                theme.OnColorThemeChanged = color =>
                {
                    if (!reentered && ReferenceEquals(color, outer))
                    {
                        reentered = true;
                        theme.ChangeColorTheme(inner);
                    }
                    previous?.Invoke(color);
                };
                try
                {
                    theme.ChangeColorTheme(outer);
                    Ui.Require(reentered, "Color callback did not re-enter exactly once.");
                    Ui.Require(ReferenceEquals(theme.ActiveColorTheme, inner), "Inner color callback did not win.");
                    ThemeDiagnostics.ValidatePalette(app, theme);
                }
                finally
                {
                    theme.OnColorThemeChanged = previous;
                }
            }
        };
    }

    private static Workload ColorCallbackThrowsThenRecovers()
    {
        var app = Application.Current!;
        var theme = SukiTheme.GetInstance();
        var failing = new SukiColorTheme("Throwing callback", Color.FromRgb(57, 97, 137), Color.FromRgb(177, 217, 253));
        var recovered = new SukiColorTheme("Recovered callback", Color.FromRgb(58, 98, 138), Color.FromRgb(178, 218, 252));
        return new Workload
        {
            Run = () =>
            {
                var previous = theme.OnColorThemeChanged;
                var expected = new InvalidOperationException("Expected color callback failure.");
                theme.OnColorThemeChanged = _ => throw expected;
                try
                {
                    Exception? caught = null;
                    try { theme.ChangeColorTheme(failing); }
                    catch (Exception e) { caught = e; }
                    Ui.Require(ReferenceEquals(caught, expected), "Color callback exception was not propagated unchanged.");
                }
                finally
                {
                    theme.OnColorThemeChanged = previous;
                }

                theme.ChangeColorTheme(recovered);
                ThemeDiagnostics.ValidatePalette(app, theme);
            }
        };
    }

    private static Workload ResourceCallbackThrowsThenRecovers()
    {
        var app = Application.Current!;
        var theme = SukiTheme.GetInstance();
        var failing = new SukiColorTheme("Throwing resource event", Color.FromRgb(59, 99, 139), Color.FromRgb(179, 219, 251));
        var recovered = new SukiColorTheme("Recovered resource event", Color.FromRgb(60, 100, 140), Color.FromRgb(180, 220, 250));
        var host = (IResourceHost)app;
        return new Workload
        {
            Run = () =>
            {
                var expected = new InvalidOperationException("Expected resource event failure.");
                EventHandler<ResourcesChangedEventArgs> handler = (_, _) => throw expected;
                host.ResourcesChanged += handler;
                Exception? caught = null;
                try
                {
                    try { theme.ChangeColorTheme(failing); }
                    catch (Exception e) { caught = e; }
                }
                finally
                {
                    host.ResourcesChanged -= handler;
                }
                Ui.Require(ReferenceEquals(caught, expected), "Resource event exception was not propagated unchanged.");
                theme.ChangeColorTheme(recovered);
                ThemeDiagnostics.ValidatePalette(app, theme);
            }
        };
    }

    private static Workload CallbackCount(string selectedTheme)
    {
        var theme = SukiTheme.GetInstance();
        return new Workload
        {
            Run = () =>
            {
                theme.ChangeBaseTheme(ThemeVariant.Light);
                var previous = theme.OnColorThemeChanged;
                var calls = 0;
                theme.OnColorThemeChanged = color =>
                {
                    calls++;
                    previous?.Invoke(color);
                };
                try
                {
                    theme.ChangeBaseTheme(ThemeVariant.Dark);
                    var expected = selectedTheme == "shadcn" ? 2 : 1;
                    Ui.Require(calls == expected, $"Expected {expected} color callbacks for {selectedTheme}, got {calls}.");
                }
                finally
                {
                    theme.OnColorThemeChanged = previous;
                    theme.ChangeBaseTheme(ThemeVariant.Light);
                }
            }
        };
    }

    private static Workload ToggleSwitchTracksTheme()
    {
        var theme = SukiTheme.GetInstance();
        var first = new SukiColorTheme("Toggle first", Color.FromRgb(61, 101, 141), Color.FromRgb(181, 221, 249));
        var second = new SukiColorTheme("Toggle second", Color.FromRgb(62, 102, 142), Color.FromRgb(182, 222, 248));
        var toggle = new ToggleSwitch { Content = "Dynamic resource" };
        var window = Ui.Open(toggle);
        var alternate = false;
        return new Workload
        {
            Run = () =>
            {
                alternate = !alternate;
                var expected = alternate ? first : second;
                theme.ChangeColorTheme(expected);
                Ui.Flush(window);
                Ui.Require(toggle.Foreground is ISolidColorBrush brush && brush.Color == expected.Primary,
                    "ToggleSwitch Foreground did not follow SukiPrimaryColor.");
            },
            Cleanup = () => Ui.Close(window)
        };
    }

    private sealed class DeferredFactory(Func<object?> factory) : IDeferredContent
    {
        public object? Build(IServiceProvider? serviceProvider) => factory();
    }

    private sealed class EqualityTrap
    {
        public int Calls { get; private set; }
        public override bool Equals(object? obj) { Calls++; return true; }
        public override int GetHashCode() => 0;
    }

    private class CountingDictionary : ResourceDictionary, IResourceDictionary, IDictionary<object, object?>
    {
        public int SetterCalls { get; protected set; }

        object? IDictionary<object, object?>.this[object key]
        {
            get => base[key];
            set
            {
                SetterCalls++;
                base[key] = value;
            }
        }
    }

    private sealed class VariantChangingDictionary(Action onPrimaryWrite)
        : CountingDictionary, IResourceDictionary, IDictionary<object, object?>
    {
        private bool _changed;
        private readonly Action _onPrimaryWrite = onPrimaryWrite;

        // This property re-implements IDictionary's interface slot for this subclass;
        // ResourceDictionary's base indexer still performs the actual storage and notification.
        object? IDictionary<object, object?>.this[object key]
        {
            get => base[key];
            set
            {
                SetterCalls++;
                base[key] = value;
                if (!_changed && Equals(key, "SukiPrimaryColor"))
                {
                    _changed = true;
                    _onPrimaryWrite();
                }
            }
        }
    }
}
