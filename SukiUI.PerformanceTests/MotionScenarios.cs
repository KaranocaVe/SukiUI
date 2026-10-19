using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using SukiUI.ControlsAnimation;
using SukiUI.Motion;
using MotionEngine = SukiUI.Motion.Motion;

namespace SukiUI.PerformanceTests;

internal static class MotionScenarios
{
    private static readonly int[] Scales = [100, 1_000, 5_000];

    public static IEnumerable<Scenario> All()
    {
        foreach (var count in Scales)
        {
            yield return new Scenario(
                $"motion/enable-disable/suki/{count}",
                "motion/attach-detach",
                count,
                "suki",
                () => CreateEnableDisable(count));

            yield return new Scenario(
                $"motion/simulated-press-release/suki/{count}",
                "motion/gesture-dispatch",
                count,
                "suki",
                () => CreateSimulatedGestures(count));

            yield return new Scenario(
                $"motion/timed-complete/suki/{count}",
                "motion/timed-complete",
                count,
                "suki",
                () => CreateTimedTrajectories(count));

            yield return new Scenario(
                $"motion/spring-advance/suki/{count}",
                "motion/spring-integrate",
                count,
                "suki",
                () => CreateSpringTrajectories(count));
        }
    }

    private static Workload CreateEnableDisable(int count)
    {
        var (window, buttons) = CreateWindow(count);
        return new Workload
        {
            Run = () =>
            {
                foreach (var button in buttons)
                    SukiPressMotion.SetEnable(button, true);
                foreach (var button in buttons)
                    SukiPressMotion.SetEnable(button, false);
            },
            Validate = () => Ui.Require(
                buttons.All(button => !SukiPressMotion.GetEnable(button)),
                "A motion remained enabled after the attach/detach workload."),
            Observe = () => Ui.Visuals(window),
            Cleanup = () =>
            {
                Disable(buttons);
                Ui.Close(window);
            }
        };
    }

    private static Workload CreateSimulatedGestures(int count)
    {
        var (window, buttons) = CreateWindow(count);
        var dispatchBase = SukiMotionStats.DispatchCount;
        foreach (var button in buttons)
            SukiPressMotion.SetEnable(button, true);
        Ui.Flush(window);

        return new Workload
        {
            // Reset outside the measured region. A headless run does not advance the
            // compositor's RAF clock, so each measured gesture covers trigger dispatch,
            // arbitration and the channel's synchronous initial advance only.
            BeforeIteration = () =>
            {
                foreach (var button in buttons)
                    SukiPressMotion.SetEnable(button, false);
                foreach (var button in buttons)
                    SukiPressMotion.SetEnable(button, true);
            },
            Run = () =>
            {
                foreach (var button in buttons)
                {
                    SukiPressMotion.Simulate(button, InputElement.PointerPressedEvent);
                    SukiPressMotion.Simulate(button, InputElement.PointerReleasedEvent);
                }
            },
            Validate = () => Ui.Require(
                buttons.All(button => SukiPressMotion.GetEnable(button)),
                "A simulated-gesture control lost its motion attachment."),
            AfterIteration = () =>
            {
                // Dispose the channels first, then dispatch the pending headless RAF so
                // SukiTicker compacts their removed subscriptions outside measurement.
                Disable(buttons);
                window.InvalidateVisual();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                Ui.Flush(window);
            },
            Observe = () =>
            {
                var observed = Ui.Visuals(window);
                observed["gestures"] = count;
                observed["ticker_dispatches"] = SukiMotionStats.DispatchCount - dispatchBase;
                return observed;
            },
            Cleanup = () => CloseWindow(window, buttons)
        };
    }

    private static Workload CreateTimedTrajectories(int count)
    {
        var (window, buttons) = CreateWindow(count);
        var targets = Enumerable.Repeat(0.96, count).ToArray();
        var programs = new TimedTrajectory[count];
        var channels = new Channel[count];
        var advanceCalls = 0;
        var phase = false;
        for (var i = 0; i < count; i++)
        {
            var index = i;
            channels[i] = MotionEngine.For(buttons[i]).Scale;
            programs[i] = channels[i]
                .To(() => targets[index])
                .Over(TimeSpan.FromMilliseconds(120))
                .Ease(new LinearEasing());
        }

        return new Workload
        {
            Run = () =>
            {
                phase = !phase;
                Array.Fill(targets, phase ? 1.0 : 0.96);
                foreach (var program in programs)
                    program.Start();

                var start = SukiMotionStats.Now;
                advanceCalls = 0;
                for (var frame = 1; frame <= 10; frame++)
                {
                    // All programs remain active through nine frames; the tenth is
                    // just beyond the 120ms duration for every individual Start time.
                    var now = start + TimeSpan.FromMilliseconds(frame * 13);
                    foreach (var program in programs)
                    {
                        program.Advance(now);
                        advanceCalls++;
                    }
                }
            },
            Validate = () =>
            {
                var expected = phase ? 1.0 : 0.96;
                Ui.Require(programs.All(program => program.Done), "A timed trajectory did not finish.");
                Ui.Require(channels.All(channel => Math.Abs(channel.Value - expected) < 1e-9),
                    "A timed trajectory did not write its target pose.");
            },
            Observe = () =>
            {
                var observed = Ui.Visuals(window);
                observed["advance_calls"] = advanceCalls;
                return observed;
            },
            Cleanup = () => CloseWindow(window, buttons)
        };
    }

    private static Workload CreateSpringTrajectories(int count)
    {
        var (window, buttons) = CreateWindow(count);
        var targets = Enumerable.Repeat(0.96, count).ToArray();
        var programs = new SpringTrajectory[count];
        var channels = new Channel[count];
        var advanceCalls = 0;
        var framesLastRun = 0;
        var phase = false;
        var spring = new Spring(20.0, 40.0);
        for (var i = 0; i < count; i++)
        {
            var index = i;
            channels[i] = MotionEngine.For(buttons[i]).Scale;
            programs[i] = channels[i].To(() => targets[index]).Spring(spring);
        }

        return new Workload
        {
            Run = () =>
            {
                phase = !phase;
                Array.Fill(targets, phase ? 1.0 : 0.96);
                advanceCalls = 0;
                framesLastRun = 0;
                foreach (var program in programs)
                    program.Start();

                var start = SukiMotionStats.Now;
                for (var frame = 1; frame <= 100; frame++)
                {
                    framesLastRun = frame;
                    var now = start + TimeSpan.FromMilliseconds(frame * 16);
                    foreach (var program in programs)
                    {
                        if (!program.Done)
                        {
                            program.Advance(now);
                            advanceCalls++;
                        }
                    }
                    if (programs.All(program => program.Done))
                        break;
                }
            },
            Validate = () =>
            {
                var expected = phase ? 1.0 : 0.96;
                Ui.Require(programs.All(program => program.Done), "A spring trajectory did not settle within the bounded advance window.");
                Ui.Require(channels.All(channel => Math.Abs(channel.Value - expected) < 1e-9),
                    "A spring trajectory did not settle at its target pose.");
            },
            Observe = () =>
            {
                var observed = Ui.Visuals(window);
                observed["advance_calls"] = advanceCalls;
                observed["frames"] = framesLastRun;
                return observed;
            },
            Cleanup = () => CloseWindow(window, buttons)
        };
    }

    private static (Window Window, Button[] Buttons) CreateWindow(int count)
    {
        var canvas = new Canvas();
        var buttons = new Button[count];
        for (var i = 0; i < count; i++)
        {
            buttons[i] = new Button { Width = 12, Height = 12 };
            Canvas.SetLeft(buttons[i], (i % 64) * 16);
            Canvas.SetTop(buttons[i], (i / 64) * 16);
            canvas.Children.Add(buttons[i]);
        }

        var window = Ui.Open(canvas);
        // Button styles may enable the motion by default. Set a local false value only
        // after styles have been applied so enable/disable scenarios control both edges.
        Disable(buttons);
        Ui.Flush(window);
        Ui.Require(buttons.All(button => button.IsAttachedToVisualTree()),
            "The workload controls were not attached to the headless window.");
        return (window, buttons);
    }

    private static void Disable(IEnumerable<Button> buttons)
    {
        foreach (var button in buttons)
            SukiPressMotion.SetEnable(button, false);
    }

    private static void CloseWindow(Window window, Button[] buttons)
    {
        Disable(buttons);
        Ui.Close(window);
    }
}
