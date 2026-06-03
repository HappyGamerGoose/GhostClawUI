using System;
using System.Threading.Tasks;
using GhostClawUI.App.Services;
using GhostClawUI.App.Ui;
using GhostClawUI.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GhostClawUI.App.Views;

internal sealed class SocialView : UserControl
{
    private readonly PipeClient _pipe;
    private readonly Action<string, string, InfoBarSeverity> _notice;

    private readonly PasswordBox _botToken = new() { PlaceholderText = "Bot Token (e.g. 123456:ABC-DEF...)", PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly TextBox _chatId = UiKit.TextBox("Authorized Chat ID", "Authorized Chat ID");
    private readonly ToggleSwitch _isEnabled = new() { Header = "Enable Telegram Listener", Margin = new Thickness(0, 10, 0, 10) };
    private readonly TextBlock _statusText = UiKit.Text("Checking status...", 14);
    private readonly Border _statusBadge = new() { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = UiKit.BrushFromHex("#64748B"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

    public SocialView(PipeClient pipe, Action<string, string, InfoBarSeverity> notice)
    {
        _pipe = pipe;
        _notice = notice;
        Content = Build();
        _ = LoadAsync();
    }

    private UIElement Build()
    {
        var root = UiKit.Page();
        root.MaxWidth = 1200;
        root.HorizontalAlignment = HorizontalAlignment.Center;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowSpacing = 20;

        // Header
        var header = new StackPanel { Spacing = 6 };
        header.Children.Add(UiKit.Text("Social", 24, Microsoft.UI.Text.FontWeights.SemiBold));
        header.Children.Add(UiKit.Muted("Control your autonomous agent remotely via Telegram chat integrations.", 14));
        root.Children.Add(header);

        // Body Grid
        var body = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(460) }
            },
            ColumnSpacing = 24
        };

        // Left: Settings Card
        var form = new StackPanel { Spacing = 14 };
        form.Children.Add(UiKit.Text("Telegram Configuration", 18, Microsoft.UI.Text.FontWeights.SemiBold));
        form.Children.Add(UiKit.Muted("Setup credentials for incoming chat messages.", 12));

        form.Children.Add(Labeled("Bot Token", _botToken));
        form.Children.Add(Labeled("Authorized Chat ID", _chatId));
        form.Children.Add(_isEnabled);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(UiKit.PrimaryButton("Save", Symbol.Save, async (_, _) => await SaveAsync()));
        buttons.Children.Add(UiKit.Button("Refresh Status", Symbol.Sync, async (_, _) => await RefreshStatusAsync()));
        form.Children.Add(buttons);

        // Status Indicator inside settings card
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        statusRow.Children.Add(_statusBadge);
        statusRow.Children.Add(_statusText);
        form.Children.Add(statusRow);

        var formCard = UiKit.Card(form);
        formCard.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(formCard, 0);
        body.Children.Add(formCard);

        // Right: Help Guide Card
        var guide = new StackPanel { Spacing = 12 };
        guide.Children.Add(UiKit.Text("Setup Instructions", 18, Microsoft.UI.Text.FontWeights.SemiBold));
        
        var bulletPoints = new StackPanel { Spacing = 8, Margin = new Thickness(4, 4, 0, 4) };
        bulletPoints.Children.Add(Bullet("1. Create a bot using Telegram's @BotFather and copy the Bot Token."));
        bulletPoints.Children.Add(Bullet("2. Send any message to your new bot."));
        bulletPoints.Children.Add(Bullet("3. Get your Chat ID using @userinfobot or checking bot updates."));
        bulletPoints.Children.Add(Bullet("4. Paste the Token and Chat ID above, enable the listener, and save."));
        bulletPoints.Children.Add(Bullet("5. Ask your bot commands like \"Create a python script to parse logs\" or \"Help me with writing a README.md\"."));
        bulletPoints.Children.Add(Bullet("6. GhostClaw will execute instructions using your active model & tools if the background service is active."));
        guide.Children.Add(bulletPoints);

        var guideCard = UiKit.Card(guide);
        guideCard.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(guideCard, 1);
        body.Children.Add(guideCard);

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

    private static TextBlock Bullet(string text)
    {
        var tb = UiKit.Text(text, 13);
        tb.Margin = new Thickness(0, 2, 0, 2);
        return tb;
    }

    private async Task LoadAsync()
    {
        try
        {
            var settings = await _pipe.RequestAsync<TelegramSettings>("telegram.get");
            if (settings != null)
            {
                _botToken.Password = settings.BotToken;
                _chatId.Text = settings.ChatId;
                _isEnabled.IsOn = settings.IsEnabled;
            }
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            _notice("Failed to load settings", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            var settings = new TelegramSettings(_botToken.Password.Trim(), _chatId.Text.Trim(), _isEnabled.IsOn);
            var result = await _pipe.RequestAsync<CommandResult>("telegram.save", settings);
            if (result != null && result.Success)
            {
                _notice("Settings saved", result.Message, InfoBarSeverity.Success);
                await RefreshStatusAsync();
            }
            else
            {
                _notice("Save failed", result?.Message ?? "Unknown error", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            _notice("Save failed", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            _statusText.Text = "Checking connection status...";
            _statusBadge.Background = UiKit.BrushFromHex("#64748B");

            var status = await _pipe.RequestAsync<CommandResult>("telegram.status");
            if (status != null)
            {
                if (status.Success)
                {
                    _statusText.Text = status.Message;
                    _statusBadge.Background = UiKit.BrushFromHex("#16A34A"); // Green
                }
                else
                {
                    _statusText.Text = status.Message;
                    _statusBadge.Background = UiKit.BrushFromHex("#DC2626"); // Red
                }
            }
            else
            {
                _statusText.Text = "No status response from backend.";
                _statusBadge.Background = UiKit.BrushFromHex("#D97706"); // Amber
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Error: {ex.Message}";
            _statusBadge.Background = UiKit.BrushFromHex("#DC2626");
        }
    }
}
