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

    // Telegram UI Elements
    private readonly PasswordBox _botToken = new() { PlaceholderText = "Bot Token (e.g. 123456:ABC-DEF...)", PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly TextBox _chatId = UiKit.TextBox("Authorized Chat ID", "Authorized Chat ID");
    private readonly ToggleSwitch _telegramEnabled = new() { Header = "Enable Telegram Listener", Margin = new Thickness(0, 10, 0, 10) };
    private readonly TextBlock _telegramStatusText = UiKit.Text("Checking status...", 14);
    private readonly Border _telegramStatusBadge = new() { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = UiKit.BrushFromHex("#64748B"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

    // WhatsApp UI Elements
    private readonly PasswordBox _waAccessToken = new() { PlaceholderText = "WhatsApp Access Token", PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly TextBox _waPhoneId = UiKit.TextBox("Phone Number ID", "Phone Number ID");
    private readonly PasswordBox _waVerifyToken = new() { PlaceholderText = "Webhook Verify Token", PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly TextBox _waWebhookPort = UiKit.TextBox("Webhook Port (e.g. 5000)", "5000");
    private readonly ToggleSwitch _waEnabled = new() { Header = "Enable WhatsApp Webhook", Margin = new Thickness(0, 10, 0, 10) };
    private readonly TextBlock _waStatusText = UiKit.Text("Checking status...", 14);
    private readonly Border _waStatusBadge = new() { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = UiKit.BrushFromHex("#64748B"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

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
        header.Children.Add(UiKit.Text("Social & Integrations", 24, Microsoft.UI.Text.FontWeights.SemiBold));
        header.Children.Add(UiKit.Muted("Control your autonomous agent remotely via chat integrations.", 14));
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

        // Left: Settings Cards (Telegram + WhatsApp)
        var leftColumn = new StackPanel { Spacing = 24 };

        // Telegram Card
        var tgForm = new StackPanel { Spacing = 14 };
        tgForm.Children.Add(UiKit.Text("Telegram Configuration", 18, Microsoft.UI.Text.FontWeights.SemiBold));
        tgForm.Children.Add(UiKit.Muted("Setup credentials for incoming Telegram messages.", 12));

        tgForm.Children.Add(Labeled("Bot Token", _botToken));
        tgForm.Children.Add(Labeled("Authorized Chat ID", _chatId));
        tgForm.Children.Add(_telegramEnabled);

        var tgButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 10, 0, 0) };
        tgButtons.Children.Add(UiKit.PrimaryButton("Save", Symbol.Save, async (_, _) => await SaveTelegramAsync()));
        tgButtons.Children.Add(UiKit.Button("Refresh Status", Symbol.Sync, async (_, _) => await RefreshTelegramStatusAsync()));
        tgForm.Children.Add(tgButtons);

        var tgStatusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        tgStatusRow.Children.Add(_telegramStatusBadge);
        tgStatusRow.Children.Add(_telegramStatusText);
        tgForm.Children.Add(tgStatusRow);

        var tgCard = UiKit.Card(tgForm);
        leftColumn.Children.Add(tgCard);

        // WhatsApp Card
        var waForm = new StackPanel { Spacing = 14 };
        waForm.Children.Add(UiKit.Text("WhatsApp Cloud API Configuration", 18, Microsoft.UI.Text.FontWeights.SemiBold));
        waForm.Children.Add(UiKit.Muted("Setup local webhook for Meta WhatsApp Business API.", 12));

        waForm.Children.Add(Labeled("Access Token", _waAccessToken));
        waForm.Children.Add(Labeled("Phone Number ID", _waPhoneId));
        waForm.Children.Add(Labeled("Webhook Verify Token", _waVerifyToken));
        waForm.Children.Add(Labeled("Local Webhook Port", _waWebhookPort));
        waForm.Children.Add(_waEnabled);

        var waButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 10, 0, 0) };
        waButtons.Children.Add(UiKit.PrimaryButton("Save", Symbol.Save, async (_, _) => await SaveWhatsAppAsync()));
        waButtons.Children.Add(UiKit.Button("Refresh Status", Symbol.Sync, async (_, _) => await RefreshWhatsAppStatusAsync()));
        waForm.Children.Add(waButtons);

        var waStatusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        waStatusRow.Children.Add(_waStatusBadge);
        waStatusRow.Children.Add(_waStatusText);
        waForm.Children.Add(waStatusRow);

        var waCard = UiKit.Card(waForm);
        leftColumn.Children.Add(waCard);

        Grid.SetColumn(leftColumn, 0);
        body.Children.Add(leftColumn);

        // Right: Help Guide Card
        var guide = new StackPanel { Spacing = 12 };
        guide.Children.Add(UiKit.Text("Setup Instructions", 18, Microsoft.UI.Text.FontWeights.SemiBold));
        
        var bulletPoints = new StackPanel { Spacing = 8, Margin = new Thickness(4, 4, 0, 4) };
        bulletPoints.Children.Add(UiKit.Text("Telegram Setup", 14, Microsoft.UI.Text.FontWeights.SemiBold));
        bulletPoints.Children.Add(Bullet("1. Create a bot using Telegram's @BotFather and copy the Bot Token."));
        bulletPoints.Children.Add(Bullet("2. Get your Chat ID using @userinfobot."));
        bulletPoints.Children.Add(Bullet("3. Paste the Token and Chat ID above, enable the listener, and save."));
        
        bulletPoints.Children.Add(new Border { Height = 1, Background = UiKit.BrushFromHex("#333333"), Margin = new Thickness(0, 10, 0, 10) });
        
        bulletPoints.Children.Add(UiKit.Text("WhatsApp Setup", 14, Microsoft.UI.Text.FontWeights.SemiBold));
        bulletPoints.Children.Add(Bullet("1. Create an app in the Meta Developer Portal and add WhatsApp."));
        bulletPoints.Children.Add(Bullet("2. Expose your local port (e.g. 5000) using ngrok: `ngrok http 5000`."));
        bulletPoints.Children.Add(Bullet("3. Configure the Meta Webhook to point to your ngrok URL (`https://your-ngrok.app/webhook/whatsapp`)."));
        bulletPoints.Children.Add(Bullet("4. Copy the Access Token and Phone ID, set your Verify Token, and save."));

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
        tb.TextWrapping = TextWrapping.Wrap;
        return tb;
    }

    private async Task LoadAsync()
    {
        try
        {
            var tgSettings = await _pipe.RequestAsync<TelegramSettings>("telegram.get");
            if (tgSettings != null)
            {
                _botToken.Password = tgSettings.BotToken;
                _chatId.Text = tgSettings.ChatId;
                _telegramEnabled.IsOn = tgSettings.IsEnabled;
            }

            var waSettings = await _pipe.RequestAsync<WhatsAppSettings>("whatsapp.get");
            if (waSettings != null)
            {
                _waAccessToken.Password = waSettings.AccessToken;
                _waPhoneId.Text = waSettings.PhoneNumberId;
                _waVerifyToken.Password = waSettings.VerifyToken;
                _waWebhookPort.Text = waSettings.WebhookPort;
                _waEnabled.IsOn = waSettings.IsEnabled;
            }

            await RefreshTelegramStatusAsync();
            await RefreshWhatsAppStatusAsync();
        }
        catch (Exception ex)
        {
            _notice("Failed to load settings", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task SaveTelegramAsync()
    {
        try
        {
            var settings = new TelegramSettings(_botToken.Password.Trim(), _chatId.Text.Trim(), _telegramEnabled.IsOn);
            var result = await _pipe.RequestAsync<CommandResult>("telegram.save", settings);
            if (result != null && result.Success)
            {
                _notice("Telegram saved", result.Message, InfoBarSeverity.Success);
                await RefreshTelegramStatusAsync();
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

    private async Task SaveWhatsAppAsync()
    {
        try
        {
            var settings = new WhatsAppSettings(_waAccessToken.Password.Trim(), _waPhoneId.Text.Trim(), _waVerifyToken.Password.Trim(), _waWebhookPort.Text.Trim(), _waEnabled.IsOn);
            var result = await _pipe.RequestAsync<CommandResult>("whatsapp.save", settings);
            if (result != null && result.Success)
            {
                _notice("WhatsApp saved", result.Message, InfoBarSeverity.Success);
                await RefreshWhatsAppStatusAsync();
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

    private async Task RefreshTelegramStatusAsync()
    {
        try
        {
            _telegramStatusText.Text = "Checking connection status...";
            _telegramStatusBadge.Background = UiKit.BrushFromHex("#64748B");

            var status = await _pipe.RequestAsync<CommandResult>("telegram.status");
            if (status != null)
            {
                _telegramStatusText.Text = status.Message;
                _telegramStatusBadge.Background = status.Success ? UiKit.BrushFromHex("#16A34A") : UiKit.BrushFromHex("#DC2626");
            }
        }
        catch (Exception ex)
        {
            _telegramStatusText.Text = $"Error: {ex.Message}";
            _telegramStatusBadge.Background = UiKit.BrushFromHex("#DC2626");
        }
    }

    private async Task RefreshWhatsAppStatusAsync()
    {
        try
        {
            _waStatusText.Text = "Checking connection status...";
            _waStatusBadge.Background = UiKit.BrushFromHex("#64748B");

            var status = await _pipe.RequestAsync<CommandResult>("whatsapp.status");
            if (status != null)
            {
                _waStatusText.Text = status.Message;
                _waStatusBadge.Background = status.Success ? UiKit.BrushFromHex("#16A34A") : UiKit.BrushFromHex("#DC2626");
            }
        }
        catch (Exception ex)
        {
            _waStatusText.Text = $"Error: {ex.Message}";
            _waStatusBadge.Background = UiKit.BrushFromHex("#DC2626");
        }
    }
}
