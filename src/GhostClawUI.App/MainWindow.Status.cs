using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using GhostClawUI.App.Services;
using GhostClawUI.App.Ui;
using GhostClawUI.App.Views;
using GhostClawUI.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace GhostClawUI.App;

internal sealed partial class MainWindow
{
    private async Task LoadSettingsAsync()
    {
        try
        {
            _settings = await _pipe.RequestAsync<AppSettings>("settings.get").ConfigureAwait(false) ?? _settings;
            ApplyAppearance(_settings.Appearance);
        }
        catch
        {
            ShowNotice("Service offline", "Settings will load when the service is available.", InfoBarSeverity.Warning);
        }
    }

    private void ApplyAppearance(AppearanceSettings appearance)
    {
        var accentColor = UiKit.BrushFromHex(appearance.AccentColor).Color;
        UiKit.AccentBrush.Color = accentColor;

        // Dynamic overrides for WinUI system accent color resources
        OverrideSystemAccentColor(accentColor);

        RootHost.RequestedTheme = appearance.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        SystemBackdrop = new MicaBackdrop();
        ApplyShellPalette();
    }

    private static void OverrideSystemAccentColor(Windows.UI.Color color)
    {
        var resources = Application.Current.Resources;
        resources["SystemAccentColor"] = color;
        resources["SystemAccentColorLight1"] = color;
        resources["SystemAccentColorLight2"] = color;
        resources["SystemAccentColorLight3"] = color;
        resources["SystemAccentColorDark1"] = color;
        resources["SystemAccentColorDark2"] = color;
        resources["SystemAccentColorDark3"] = color;
        resources["SystemAccentColorBrush"] = new SolidColorBrush(color);
        resources["SystemControlHighlightAccentBrush"] = new SolidColorBrush(color);

        if (resources.ThemeDictionaries != null)
        {
            foreach (var dictKey in resources.ThemeDictionaries.Keys)
            {
                if (resources.ThemeDictionaries[dictKey] is ResourceDictionary themeDict)
                {
                    themeDict["SystemAccentColor"] = color;
                    themeDict["SystemAccentColorLight1"] = color;
                    themeDict["SystemAccentColorLight2"] = color;
                    themeDict["SystemAccentColorLight3"] = color;
                    themeDict["SystemAccentColorDark1"] = color;
                    themeDict["SystemAccentColorDark2"] = color;
                    themeDict["SystemAccentColorDark3"] = color;
                    themeDict["SystemAccentColorBrush"] = new SolidColorBrush(color);
                    themeDict["SystemControlHighlightAccentBrush"] = new SolidColorBrush(color);
                }
            }
        }
    }

    private void ApplyShellPalette()
    {
        var dark = _settings.Appearance.Theme.Equals("Dark", StringComparison.OrdinalIgnoreCase) ||
                   RootHost.ActualTheme == ElementTheme.Dark;
        UiKit.SetShellPalette(dark);
        UpdateNavSelection();
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            _lastHealth = await _pipe.RequestAsync<ServiceHealthReport>("health.check").ConfigureAwait(false);
            var status = _lastHealth?.Status;
            _statusText.Text = status is null ? "Service unavailable" : BuildStatusText(_lastHealth);
            _settingsDot.Background = _lastHealth is { StoreWritable: true, PayloadPresent: true }
                ? status is { GhostClawRunning: true } ? UiKit.BrushFromHex("#16A34A") : UiKit.BrushFromHex("#F59E0B")
                : UiKit.BrushFromHex("#DC2626");
            _collapsedSettingsDot.Background = _settingsDot.Background;
        }
        catch
        {
            _statusText.Text = "Service unavailable";
            _settingsDot.Background = UiKit.BrushFromHex("#DC2626");
            _collapsedSettingsDot.Background = _settingsDot.Background;
        }
    }

    private static string BuildStatusText(ServiceHealthReport? health)
    {
        if (health is null)
        {
            return "Service unavailable";
        }

        if (!health.StoreWritable)
        {
            return "Service connected · storage blocked";
        }

        // Payload missing check removed per user request
        if (health.Status.RestartCount <= 0)
        {
            return health.Status.State;
        }
        return $"{health.Status.State} · restarts {health.Status.RestartCount}";
    }
}
