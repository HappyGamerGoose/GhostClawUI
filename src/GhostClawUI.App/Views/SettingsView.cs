using GhostClawUI.App.Services;
using GhostClawUI.App.Ui;
using GhostClawUI.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GhostClawUI.App.Views;

internal sealed class SettingsView : UserControl
{
    private readonly PipeClient _pipe;
    private readonly nint _hwnd;
    private readonly Func<ExportResult, Task> _saveExport;
    private readonly Action<string, string, InfoBarSeverity> _notice;
    private AppSettings _settings;
    private readonly ComboBox _verbosity = UiKit.Combo("Verbosity");
    private readonly ToggleSwitch _fallback = new() { Header = "Fallback providers" };
    private readonly ToggleSwitch _silent = new() { Header = "Silent tool confirmations" };
    private readonly ToggleSwitch _updates = new() { Header = "Silent auto-update checks" };
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly Windows.Foundation.TypedEventHandler<DispatcherQueueTimer, object> _saveTimerHandler;
    private bool _loading;

    public SettingsView(PipeClient pipe, AppSettings settings, nint hwnd, Func<ExportResult, Task> saveExport, Action<string, string, InfoBarSeverity> notice)
    {
        _pipe = pipe;
        _settings = settings;
        _hwnd = hwnd;
        _saveExport = saveExport;
        _notice = notice;
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(700);
        _saveTimerHandler = async (_, _) =>
        {
            _saveTimer.Stop();
            try
            {
                await SaveAsync(showNotice: false);
            }
            catch (Exception ex)
            {
                _notice("Settings autosave failed", ex.Message, InfoBarSeverity.Warning);
            }
        };
        _saveTimer.Tick += _saveTimerHandler;

        Unloaded += (s, e) =>
        {
            _saveTimer.Stop();
            _saveTimer.Tick -= _saveTimerHandler;
        };

        Content = Build();
        Load();

        _verbosity.SelectionChanged += (_, _) => QueueSave();
        _fallback.Toggled += (_, _) => QueueSave();
        _silent.Toggled += (_, _) => QueueSave();
        _updates.Toggled += (_, _) => QueueSave();
    }

    private UIElement Build()
    {
        var root = UiKit.Page();
        root.MaxWidth = 1200;
        root.HorizontalAlignment = HorizontalAlignment.Center;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowSpacing = 20;

        var header = new StackPanel { Spacing = 6 };
        header.Children.Add(UiKit.Text("Settings", 24, Microsoft.UI.Text.FontWeights.SemiBold));
        header.Children.Add(UiKit.Muted("Tune runtime behavior without exposing low-level registry plumbing.", 14));
        root.Children.Add(header);

        var body = new StackPanel
        {
            MaxWidth = 680,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        _verbosity.ItemsSource = new[] { "Minimal", "Expanded" };

        var behavior = new StackPanel { Spacing = 12 };
        behavior.Children.Add(UiKit.Text("Runtime", 18, Microsoft.UI.Text.FontWeights.SemiBold));
        behavior.Children.Add(UiKit.Muted("Controls how much the agent explains and how quietly tools run.", 13));
        behavior.Children.Add(Labeled("Verbosity", _verbosity));
        behavior.Children.Add(_fallback);
        behavior.Children.Add(_silent);
        behavior.Children.Add(_updates);
        body.Children.Add(UiKit.Card(behavior));

        var data = new StackPanel { Spacing = 12, Margin = new Thickness(0, 16, 0, 0) };
        data.Children.Add(UiKit.Text("Data Management", 18, Microsoft.UI.Text.FontWeights.SemiBold));
        data.Children.Add(UiKit.Muted("Export settings and conversations, or purge all local database data.", 13));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(UiKit.Button("Export All Data", Symbol.Download, async (_, _) =>
        {
            try
            {
                var result = await _pipe.RequestAsync<ExportResult>("data.export");
                if (result is not null)
                {
                    await _saveExport(result);
                }
                else
                {
                    _notice("Export failed", "No data returned from service.", InfoBarSeverity.Error);
                }
            }
            catch (Exception ex)
            {
                _notice("Export failed", ex.Message, InfoBarSeverity.Error);
            }
        }));
        actions.Children.Add(UiKit.Button("Purge All Data", Symbol.Delete, async (_, _) =>
        {
            var dialog = new ContentDialog
            {
                Title = "Purge All Data",
                Content = "This will permanently delete all local settings, database records, and chat history. This action cannot be undone.",
                PrimaryButtonText = "Purge",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            var res = await dialog.ShowAsync();
            if (res == ContentDialogResult.Primary)
            {
                try
                {
                    var purgeRes = await _pipe.RequestAsync<CommandResult>("data.purge");
                    if (purgeRes?.Success == true)
                    {
                        _notice("Data purged", "All database tables cleared.", InfoBarSeverity.Success);
                        Load();
                    }
                    else
                    {
                        _notice("Purge failed", purgeRes?.Message ?? "Unknown error", InfoBarSeverity.Error);
                    }
                }
                catch (Exception ex)
                {
                    _notice("Purge failed", ex.Message, InfoBarSeverity.Error);
                }
            }
        }));
        data.Children.Add(actions);
        body.Children.Add(UiKit.Card(data));

        var scroller = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(scroller, 1);
        root.Children.Add(scroller);
        return root;
    }

    private static StackPanel Labeled(string label, FrameworkElement element) =>
        new()
        {
            Spacing = 5,
            Children =
            {
                UiKit.Text(label, 12, Microsoft.UI.Text.FontWeights.SemiBold),
                element
            }
        };

    private void Load()
    {
        _loading = true;
        _verbosity.SelectedItem = ((string[])_verbosity.ItemsSource).FirstOrDefault(x => string.Equals(x, _settings.Verbosity, StringComparison.OrdinalIgnoreCase)) ?? "Minimal";
        _fallback.IsOn = _settings.FallbackProvidersEnabled;
        _silent.IsOn = _settings.SilentToolConfirmations;
        _updates.IsOn = _settings.AutoUpdateEnabled;
        _loading = false;
    }

    private void QueueSave()
    {
        if (_loading)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveAsync(bool showNotice)
    {
        _settings = _settings with
        {
            Verbosity = _verbosity.SelectedItem as string ?? "Minimal",
            FallbackProvidersEnabled = _fallback.IsOn,
            SilentToolConfirmations = _silent.IsOn,
            AutoUpdateEnabled = _updates.IsOn
        };
        await _pipe.RequestAsync<CommandResult>("settings.update", _settings);
        if (showNotice)
        {
            _notice("Settings saved", "Configuration updated.", InfoBarSeverity.Success);
        }
    }
}



