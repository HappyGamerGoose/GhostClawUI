using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
using System.IO;
using System.IO.Compression;
using Microsoft.UI.Xaml.Controls.Primitives;
using GhostClawUI.App.Services;
using GhostClawUI.App.Ui;
using GhostClawUI.Shared;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace GhostClawUI.App.Views;

internal sealed partial class ChatView
{

    private Border Trace(AgentTraceCard trace)
    {
        var panel = new StackPanel { Spacing = 10 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(new FontIcon { Glyph = "\uE774", FontSize = 16, Foreground = UiKit.AccentBrush });
        header.Children.Add(UiKit.Text(trace.Title, 14, FontWeights.SemiBold));
        header.Children.Add(new FontIcon { Glyph = "\uE930", FontSize = 10, Foreground = UiKit.BrushFromHex("#22C55E") });
        panel.Children.Add(header);
        panel.Children.Add(UiKit.Muted(trace.Detail, 13));

        return new Border
        {
            Child = panel,
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = ResourceBrush("CardStrokeColorDefaultBrush", "#D1D5DB"),
            Background = ResourceBrush("CardBackgroundFillColorDefaultBrush", "#FFFFFF"),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 1100, // Widened trace bounds to match expanded assistant bubbles!
            Margin = new Thickness(50, 0, 0, 0)
        };
    }


    private void StartPollingActiveTraces()
    {
        _lastTraces = null;
        _isPollingActive = true;
        if (_pollingTimer != null)
        {
            _pollingTimer.Stop();
        }
        else
        {
            _pollingTimer = DispatcherQueue.CreateTimer();
            _pollingTimer.Interval = TimeSpan.FromMilliseconds(800);
            _pollingTimerHandler ??= async (_, _) => await PollActiveTracesTickAsync();
            _pollingTimer.Tick += _pollingTimerHandler;
        }

        SetBusy(true);
        _pollingTimer.Start();
    }


    private void StopPollingActiveTraces()
    {
        _isPollingActive = false;
        _lastTraces = null;
        if (_pollingTimer != null)
        {
            _pollingTimer.Stop();
            if (_pollingTimerHandler != null)
            {
                _pollingTimer.Tick -= _pollingTimerHandler;
            }
            _pollingTimer = null;
        }

        // Remove any live run row
        if (_liveRunRow != null)
        {
            _messages.Children.Remove(_liveRunRow);
            _liveRunRow = null;
        }

        SetBusy(false);
    }


    private async Task PollActiveTracesTickAsync()
    {
        if (!_isPollingActive || string.IsNullOrWhiteSpace(_conversationId))
        {
            StopPollingActiveTraces();
            return;
        }

        try
        {
            var active = await _pipe.RequestAsync<ActiveTracesResponse>("chat.activeTraces", new SimpleIdRequest(_conversationId));
            if (!_isPollingActive)
            {
                return;
            }

            if (active == null || !active.IsRunning)
            {
                // Run not started yet or completed.
                // Do not stop polling here; let SendAsync stop it when the request completes.
                return;
            }

            // Draw or update the live traces UI
            ShowLiveRunUI(active.Traces);
        }
        catch
        {
            // Ignore polling errors
        }
    }


    private void ShowLiveRunUI(IReadOnlyList<AgentTraceCard> traces)
    {
        if (traces.Count == 0)
        {
            // If there are no traces yet, we still want to show a spinner row
            var thinkingTraces = new List<AgentTraceCard> { new AgentTraceCard("Thinking", "Initializing agent runner...", "running") };
            traces = thinkingTraces;
        }

        if (_lastTraces != null && _lastTraces.Count == traces.Count)
        {
            bool match = true;
            for (int i = 0; i < traces.Count; i++)
            {
                if (_lastTraces[i].Title != traces[i].Title ||
                    _lastTraces[i].Detail != traces[i].Detail ||
                    _lastTraces[i].State != traces[i].State)
                {
                    match = false;
                    break;
                }
            }
            if (match && _liveRunRow != null)
            {
                return; // Prevent duplicate layout rendering and flickering!
            }
        }
        _lastTraces = traces.ToList();

        var expander = TracesExpander(traces);
        expander.IsExpanded = _settings().Verbosity == "Expanded"; // Expanded based on setting

        if (_liveRunRow != null)
        {
            // Update in-place to avoid visual layout flickering
            if (_liveRunRow.Children.LastOrDefault() is Border bubble && bubble.Child is StackPanel panel)
            {
                panel.Children.Clear();
                panel.Children.Add(expander);
                _ = ScrollToEndAsync();
                return;
            }
        }

        var bubbleBorder = new Border
        {
            Child = new StackPanel
            {
                Spacing = 8,
                Children = { expander }
            },
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = StrokeBrush(),
            Background = AssistantBubbleBrush(),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 1100
        };

        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            ColumnSpacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 8)
        };

        row.Children.Add(Avatar("GhostClaw"));
        Grid.SetColumn(bubbleBorder, 1);
        bubbleBorder.HorizontalAlignment = HorizontalAlignment.Left;
        bubbleBorder.MaxWidth = 1100;
        row.Children.Add(bubbleBorder);

        _liveRunRow = row;
        _messages.Children.Add(_liveRunRow);
        _ = ScrollToEndAsync();
    }


    private Expander TracesExpander(IReadOnlyList<AgentTraceCard> traces)
    {
        var expander = new Expander
        {
            IsExpanded = _settings().Verbosity == "Expanded",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 4)
        };

        var headerLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

        var runningCount = traces.Count(t => t.State == "running");
        var failedCount = traces.Count(t => t.State == "failed");

        FrameworkElement statusIcon;
        if (runningCount > 0)
        {
            statusIcon = new ProgressRing { Width = 14, Height = 14, IsActive = true, Margin = new Thickness(0, 0, 4, 0) };
        }
        else
        {
            statusIcon = new FontIcon
            {
                Glyph = failedCount > 0 ? "\uE814" : "\uE73E",
                FontSize = 14,
                Foreground = failedCount > 0 ? UiKit.BrushFromHex("#EF4444") : UiKit.BrushFromHex("#22C55E")
            };
        }
        headerLeft.Children.Add(statusIcon);

        var text = $"Agent Reasoning & Execution Loop ({traces.Count} trace{(traces.Count == 1 ? "" : "s")})";
        if (failedCount > 0) text += " - Terminated with Errors";
        else if (runningCount > 0) text += " - Running Autonomously...";
        else text += " - Run Complete";

        var headerText = UiKit.Text(text, 12, FontWeights.SemiBold);
        headerText.Foreground = runningCount > 0 ? UiKit.AccentBrush : (failedCount > 0 ? UiKit.BrushFromHex("#EF4444") : UiKit.BrushFromHex("#22C55E"));
        headerLeft.Children.Add(headerText);

        expander.Header = headerLeft;

        var dashboardStack = new StackPanel { Spacing = 12, Padding = new Thickness(8) };

        var planTrace = traces.FirstOrDefault(t => t.Title == "Active Plan");
        var reasoningTrace = traces.FirstOrDefault(t => t.Title == "Reasoning");
        var regularTraces = traces.Where(t => t.Title != "Active Plan" && t.Title != "Reasoning").ToList();

        if (planTrace != null && !string.IsNullOrWhiteSpace(planTrace.Detail))
        {
            var planCard = RenderActivePlanCard(planTrace.Detail);
            if (planCard != null)
            {
                dashboardStack.Children.Add(planCard);
            }
        }

        if (reasoningTrace != null && !string.IsNullOrWhiteSpace(reasoningTrace.Detail))
        {
            var reasoningCard = RenderReasoningCard(reasoningTrace.Detail);
            if (reasoningCard != null)
            {
                dashboardStack.Children.Add(reasoningCard);
            }
        }

        if (regularTraces.Count > 0)
        {
            var executionLabel = UiKit.Text("EXECUTION TRACES & LOGS", 10, FontWeights.Bold);
            executionLabel.Foreground = SecondaryTextBrush();
            executionLabel.Margin = new Thickness(0, 8, 0, 0);
            dashboardStack.Children.Add(executionLabel);

            foreach (var trace in regularTraces)
            {
                dashboardStack.Children.Add(RenderAgentExecutionCard(trace));
            }
        }
        else if (planTrace == null && reasoningTrace == null)
        {
            foreach (var trace in traces)
            {
                dashboardStack.Children.Add(TraceRow(trace));
            }
        }

        expander.Content = new Border { Padding = new Thickness(0, 8, 0, 0), Child = dashboardStack };
        return expander;
    }


    private Border? RenderReasoningCard(string reasoning)
    {
        var stack = new StackPanel { Spacing = 6 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new FontIcon { Glyph = "\uEA80", FontSize = 12, Foreground = UiKit.BrushFromHex("#F97316") });
        header.Children.Add(UiKit.Text("AGENT REASONING", 10, FontWeights.Bold));
        stack.Children.Add(header);

        var thoughtText = new TextBlock
        {
            Text = reasoning,
            FontSize = 12,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResourceBrush("TextFillColorSecondaryBrush", "#4B5563")
        };
        stack.Children.Add(thoughtText);

        return new Border
        {
            Background = ResourceBrush("CardBackgroundFillColorDefaultBrush", "#FFFFFF"),
            BorderBrush = ResourceBrush("CardStrokeColorDefaultBrush", "#E5E7EB"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 4),
            Child = stack
        };
    }


    private static Border RenderAgentExecutionCard(AgentTraceCard trace)
    {
        var stack = new StackPanel { Spacing = 6 };

        var headerGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8
        };

        var titleLower = trace.Title.ToLowerInvariant();
        var glyph = "\uE9A1";
        var iconColor = UiKit.AccentBrush;

        if (titleLower.Contains("terminal") || titleLower.Contains("bash"))
        {
            glyph = "\uE756";
            iconColor = UiKit.BrushFromHex("#10B981");
        }
        else if (titleLower.Contains("web") || titleLower.Contains("search"))
        {
            glyph = "\uE12B";
            iconColor = UiKit.BrushFromHex("#3B82F6");
        }
        else if (titleLower.Contains("data") || titleLower.Contains("file"))
        {
            glyph = "\uE8C3";
            iconColor = UiKit.BrushFromHex("#8B5CF6");
        }
        else if (titleLower.Contains("planner"))
        {
            glyph = "\uE73E";
            iconColor = UiKit.BrushFromHex("#F59E0B");
        }

        var agentIcon = new FontIcon { Glyph = glyph, FontSize = 13, Foreground = iconColor };
        headerGrid.Children.Add(agentIcon);
        Grid.SetColumn(agentIcon, 0);

        var agentTitle = UiKit.Text(trace.Title, 12, FontWeights.SemiBold);
        headerGrid.Children.Add(agentTitle);
        Grid.SetColumn(agentTitle, 1);

        FrameworkElement statusElement;
        if (trace.State == "done")
        {
            statusElement = new FontIcon { Glyph = "\uE73E", FontSize = 11, Foreground = UiKit.BrushFromHex("#22C55E") };
        }
        else if (trace.State == "failed")
        {
            statusElement = new FontIcon { Glyph = "\uE814", FontSize = 11, Foreground = UiKit.BrushFromHex("#EF4444") };
        }
        else
        {
            statusElement = new ProgressRing { Width = 11, Height = 11, IsActive = true };
        }
        headerGrid.Children.Add(statusElement);
        Grid.SetColumn(statusElement, 2);

        stack.Children.Add(headerGrid);

        if (!string.IsNullOrWhiteSpace(trace.Detail))
        {
            var isTerminalCode = titleLower.Contains("terminal") || titleLower.Contains("bash") || trace.Detail.Contains("Executing:") || trace.Detail.Contains("Reading file:");

            if (isTerminalCode)
            {
                var codeText = new TextBlock
                {
                    Text = trace.Detail,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas, Courier New"),
                    FontSize = 11,
                    Foreground = UiKit.BrushFromHex("#D4D4D4"),
                    TextWrapping = TextWrapping.NoWrap
                };

                var scroll = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = codeText,
                    MaxHeight = 150,
                    Padding = new Thickness(8)
                };

                var terminalHeader = new Border
                {
                    Background = UiKit.BrushFromHex("#2D2D2D"),
                    Padding = new Thickness(8, 4, 8, 4),
                    CornerRadius = new CornerRadius(6, 6, 0, 0),
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Children =
                        {
                            new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 10, Height = 10, Fill = UiKit.BrushFromHex("#FF5F56") },
                            new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 10, Height = 10, Fill = UiKit.BrushFromHex("#FFBD2E") },
                            new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 10, Height = 10, Fill = UiKit.BrushFromHex("#27C93F") },
                            new TextBlock { Text = "TERMINAL", FontSize = 9, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI"), Foreground = UiKit.BrushFromHex("#888888"), Margin = new Thickness(8,0,0,0), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold }
                        }
                    }
                };

                var terminalContainer = new StackPanel { Spacing = 0 };
                terminalContainer.Children.Add(terminalHeader);

                var terminalContent = new Border
                {
                    Background = UiKit.BrushFromHex("#1E1E1E"),
                    CornerRadius = new CornerRadius(0, 0, 6, 6),
                    Child = scroll
                };
                terminalContainer.Children.Add(terminalContent);

                var terminalBorder = new Border
                {
                    BorderBrush = UiKit.BrushFromHex("#333333"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Child = terminalContainer,
                    Margin = new Thickness(0, 8, 0, 4)
                };

                stack.Children.Add(terminalBorder);
            }
            else
            {
                var detailText = UiKit.Muted(trace.Detail, 11);
                detailText.Margin = new Thickness(4, 0, 0, 0);
                stack.Children.Add(detailText);
            }
        }

        var isFailed = trace.State == "failed";
        var isRunning = trace.State == "running";

        var borderBrush = isFailed ? UiKit.BrushFromHex("#EF4444")
            : isRunning ? UiKit.AccentBrush
            : ResourceBrush("CardStrokeColorDefaultBrush", "#E5E7EB");

        var background = isFailed ? UiKit.BrushFromHex("#FEF2F2")
            : isRunning ? ResourceBrush("CardBackgroundFillColorSecondaryBrush", "#FDF8F6")
            : ResourceBrush("CardBackgroundFillColorDefaultBrush", "#FFFFFF");

        return new Border
        {
            Background = background,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(isRunning ? 2 : 1),
            Padding = new Thickness(12, 10, 12, 12),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack
        };
    }


    private StackPanel TraceRow(AgentTraceCard trace)
    {
        var row = new StackPanel { Spacing = 4, Margin = new Thickness(0, 2, 0, 2) };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        FrameworkElement icon;
        if (trace.State == "done")
        {
            icon = new FontIcon { Glyph = "\uE73E", FontSize = 12, Foreground = UiKit.BrushFromHex("#22C55E") };
        }
        else if (trace.State == "failed")
        {
            icon = new FontIcon { Glyph = "\uE814", FontSize = 12, Foreground = UiKit.BrushFromHex("#EF4444") };
        }
        else
        {
            icon = new ProgressRing { Width = 12, Height = 12, IsActive = true };
        }

        header.Children.Add(icon);
        header.Children.Add(UiKit.Text(trace.Title, 12, FontWeights.SemiBold));
        row.Children.Add(header);

        if (!string.IsNullOrWhiteSpace(trace.Detail))
        {
            row.Children.Add(new Border { Margin = new Thickness(20, 0, 0, 0), Child = UiKit.Muted(trace.Detail, 11) });
        }
        return row;
    }

}
