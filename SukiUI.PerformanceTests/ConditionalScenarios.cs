using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data;
using SukiUI.Helpers.ConditionalXAML;

namespace SukiUI.PerformanceTests;

internal static class ConditionalScenarios
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

}
