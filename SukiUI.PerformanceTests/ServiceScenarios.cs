using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using SukiUI.Dialogs;
using SukiUI.Helpers.ConditionalXAML;
using SukiUI.Toasts;

namespace SukiUI.PerformanceTests;

internal static class ServiceScenarios
{
    public static IEnumerable<Scenario> All()
    {
        foreach (var scale in new[] { 10, 100, 1000 })
        {
            yield return ConditionalScenario(scale, relevant: false);
            yield return ConditionalScenario(scale, relevant: true);
            yield return BoundVisibilityScenario(scale, relevant: false);
            yield return BoundVisibilityScenario(scale, relevant: true);
        }

        foreach (var scale in new[] { 100, 1000, 10000 })
        {
            yield return ToastQueueScenario(scale);
            yield return ToastDismissScenario(scale, ToastOperation.Reference);
            yield return ToastDismissScenario(scale, ToastOperation.IndexFromEnd);
            yield return ToastDismissScenario(scale, ToastOperation.BulkRange);
            yield return DialogScenario(scale);
        }
    }

    private static Scenario ConditionalScenario(int scale, bool relevant) =>
        new(
            $"conditional/if/{(relevant ? "relevant" : "unrelated")}/{scale}",
            "conditional",
            scale,
            "suki",
            () =>
            {
                var model = new ConditionalModel { Flag = true };
                var controls = new List<If>(scale);
                var panel = new StackPanel();
                for (var i = 0; i < scale; i++)
                {
                    var control = new If
                    {
                        Condition = "Flag == true",
                        Content = new Border { Width = 1, Height = 1 },
                        DataContext = model
                    };
                    controls.Add(control);
                    panel.Children.Add(control);
                }

                var window = Ui.Open(panel);
                var notifications = 0;
                model.PropertyChanged += (_, _) => notifications++;

                return new Workload
                {
                    Run = () =>
                    {
                        if (relevant)
                            model.Flag = !model.Flag;
                        else
                            model.Noise++;
                    },
                    Validate = () =>
                    {
                        Ui.Flush(window);
                        Ui.Require(notifications > 0, "Conditional model did not notify");
                        var expected = relevant ? model.Flag : true;
                        Ui.Require(controls.All(x => x.IsVisible == expected),
                            "Conditional controls diverged from the model");
                    },
                    Observe = () => new Dictionary<string, double>
                    {
                        ["visible"] = controls.Count(x => x.IsVisible),
                        ["notifications"] = notifications
                    },
                    Cleanup = () => Ui.Close(window)
                };
            });

    private static Scenario BoundVisibilityScenario(int scale, bool relevant) =>
        new(
            $"conditional/binding/{(relevant ? "relevant" : "unrelated")}/{scale}",
            "conditional",
            scale,
            "suki",
            () =>
            {
                var model = new ConditionalModel { Flag = true };
                var controls = new List<Control>(scale);
                var panel = new StackPanel();
                for (var i = 0; i < scale; i++)
                {
                    var control = new Border { Width = 1, Height = 1, DataContext = model };
                    control.Bind(Control.IsVisibleProperty, new Binding(nameof(ConditionalModel.Flag)));
                    controls.Add(control);
                    panel.Children.Add(control);
                }

                var window = Ui.Open(panel);
                var notifications = 0;
                model.PropertyChanged += (_, _) => notifications++;
                return new Workload
                {
                    Run = () =>
                    {
                        if (relevant)
                            model.Flag = !model.Flag;
                        else
                            model.Noise++;
                    },
                    Validate = () =>
                    {
                        Ui.Flush(window);
                        Ui.Require(notifications > 0, "Binding model did not notify");
                        var expected = relevant ? model.Flag : true;
                        Ui.Require(controls.All(x => x.IsVisible == expected),
                            "Bound visibility controls diverged from the model");
                    },
                    Observe = () => new Dictionary<string, double>
                    {
                        ["visible"] = controls.Count(x => x.IsVisible),
                        ["notifications"] = notifications
                    },
                    Cleanup = () => Ui.Close(window)
                };
            });

    private enum ToastOperation
    {
        Reference,
        IndexFromEnd,
        BulkRange
    }

    private static Scenario ToastQueueScenario(int scale) =>
        new(
            $"toast/queue/{scale}",
            "toast",
            scale,
            "suki",
            () =>
            {
                var manager = new SukiToastManager();
                var toasts = Enumerable.Range(0, scale).Select(_ => (ISukiToast)new TestToast()).ToArray();
                var queued = 0;
                var dismissed = 0;
                manager.OnToastQueued += (_, _) => queued++;
                manager.OnToastDismissed += (_, _) => dismissed++;

                return new Workload
                {
                    Run = () =>
                    {
                        foreach (var toast in toasts)
                            manager.Queue(toast);
                    },
                    BeforeIteration = () =>
                    {
                        manager.DismissAll();
                        queued = 0;
                        dismissed = 0;
                    },
                    AfterIteration = manager.DismissAll,
                    Validate = () => Ui.Require(queued == scale, "Toast queue event count differs from scale"),
                    Observe = () => new Dictionary<string, double>
                    {
                        ["queued"] = queued,
                        ["dismissed"] = dismissed
                    },
                    Cleanup = manager.Dispose
                };
            });

    private static Scenario ToastDismissScenario(int scale, ToastOperation operation) =>
        new(
            $"toast/dismiss-{operation.ToString().ToLowerInvariant()}/{scale}",
            "toast",
            scale,
            "suki",
            () =>
            {
                var manager = new SukiToastManager();
                var toasts = Enumerable.Range(0, scale).Select(_ => (ISukiToast)new TestToast()).ToArray();

                var dismissed = 0;
                manager.OnToastDismissed += (_, _) => dismissed++;

                return new Workload
                {
                    BeforeIteration = () =>
                    {
                        manager.DismissAll();
                        foreach (var toast in toasts)
                            manager.Queue(toast);
                        dismissed = 0;
                    },
                    Run = () =>
                    {
                        switch (operation)
                        {
                            case ToastOperation.Reference:
                                foreach (var toast in toasts.Reverse())
                                    manager.Dismiss(toast);
                                break;
                            case ToastOperation.IndexFromEnd:
                                for (var i = scale - 1; i >= 0; i--)
                                    manager.Dismiss(i);
                                break;
                            case ToastOperation.BulkRange:
                                manager.DismissRange(0, scale);
                                break;
                        }
                    },
                    Validate = () =>
                    {
                        Ui.Require(dismissed == scale, "Toast dismissal event count differs from scale");
                        Ui.Require(toasts.All(manager.IsDismissed), "Toast manager retained a dismissed toast");
                    },
                    Observe = () => new Dictionary<string, double>
                    {
                        ["dismissed"] = dismissed
                    },
                    Cleanup = manager.Dispose
                };
            });

    private static Scenario DialogScenario(int scale) =>
        new(
            $"dialog/show-dismiss/{scale}",
            "dialog",
            scale,
            "suki",
            () =>
            {
                var manager = new SukiDialogManager();
                var dialog = new TestDialog { Manager = manager };
                var shown = 0;
                var dismissed = 0;
                SukiDialogManagerEventHandler onShown = (_, _) => shown++;
                SukiDialogManagerEventHandler onDismissed = (_, _) => dismissed++;
                manager.OnDialogShown += onShown;
                manager.OnDialogDismissed += onDismissed;

                return new Workload
                {
                    BeforeIteration = () =>
                    {
                        shown = 0;
                        dismissed = 0;
                    },
                    Run = () =>
                    {
                        for (var i = 0; i < scale; i++)
                        {
                            manager.TryShowDialog(dialog);
                            manager.TryDismissDialog(dialog);
                        }
                    },
                    Validate = () =>
                    {
                        Ui.Require(shown == scale && dismissed == scale,
                            "Dialog manager show/dismiss event counts differ from scale");
                    },
                    Observe = () => new Dictionary<string, double>
                    {
                        ["shown"] = shown,
                        ["dismissed"] = dismissed
                    },
                    Cleanup = () =>
                    {
                        manager.OnDialogShown -= onShown;
                        manager.OnDialogDismissed -= onDismissed;
                    }
                };
            });

    private sealed class ConditionalModel : INotifyPropertyChanged
    {
        private bool _flag;
        private int _noise;

        public bool Flag
        {
            get => _flag;
            set
            {
                if (_flag == value) return;
                _flag = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Flag)));
            }
        }

        public int Noise
        {
            get => _noise;
            set
            {
                if (_noise == value) return;
                _noise = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Noise)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class TestToast : ISukiToast
    {
        public ISukiToastManager? Manager { get; set; }
        public double DismissStartTimestamp { get; set; }
        public string Title { get; set; } = string.Empty;
        public object? Content { get; set; }
        public object? Icon { get; set; }
        public IBrush? Foreground { get; set; }
        public bool CanDismissByClicking { get; set; }
        public bool CanDismissByTime { get; set; }
        public bool InterruptDismissTimerWhileHover { get; set; }
        public TimeSpan DismissTimeout { get; set; }
        public double DismissProgressValue { get; set; }
        public bool LoadingState { get; set; }
        public Action<ISukiToast, SukiToastDismissSource>? OnDismissed { get; set; }
        public Action<ISukiToast>? OnClicked { get; set; }
        public ObservableCollection<object> ActionButtons { get; } = new();

        public ISukiToast ResetToDefault()
        {
            Manager = null;
            DismissStartTimestamp = 0;
            Title = string.Empty;
            Content = null;
            Icon = null;
            Foreground = null;
            CanDismissByClicking = false;
            CanDismissByTime = false;
            InterruptDismissTimerWhileHover = false;
            DismissTimeout = default;
            DismissProgressValue = 0;
            LoadingState = false;
            OnDismissed = null;
            OnClicked = null;
            ActionButtons.Clear();
            return this;
        }
    }

    private sealed class TestDialog : ISukiDialog
    {
        public ISukiDialogManager? Manager { get; set; }
        public object? ViewModel { get; set; }
        public string? Title { get; set; }
        public object? Content { get; set; }
        public object? Icon { get; set; }
        public bool IsViewModelOnly { get; set; }
        public IBrush? IconColor { get; set; }
        public ObservableCollection<object> ActionButtons { get; } = new();
        public Action<ISukiDialog>? OnDismissed { get; set; }
        public bool CanDismissWithBackgroundClick { get; set; }
        public bool ShowCardBackground { get; set; }

        public void Dismiss() => Manager?.TryDismissDialog(this);

        public void ResetToDefault()
        {
            Manager = null;
            ViewModel = null;
            Title = null;
            Content = null;
            Icon = null;
            IsViewModelOnly = false;
            IconColor = null;
            ActionButtons.Clear();
            OnDismissed = null;
            CanDismissWithBackgroundClick = false;
            ShowCardBackground = true;
        }
    }
}
