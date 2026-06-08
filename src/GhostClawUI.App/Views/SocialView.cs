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
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowSpacing = 24;

        // Header
        var header = new StackPanel { Spacing = 6 };
        header.Children.Add(UiKit.Text("Social & Integrations", 24, Microsoft.UI.Text.FontWeights.SemiBold));
        header.Children.Add(UiKit.Muted("Control your autonomous agent remotely via chat integrations.", 14));
        root.Children.Add(header);

        var body = new StackPanel
        {
            Spacing = 24
        };

        // Telegram Card
        var tgGrid = new Grid();
        tgGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header (Toggle/Status)
        tgGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Content
        tgGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Footer

        var tgHeader = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        tgHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tgHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        
        _telegramEnabled.Margin = new Thickness(0);
        Grid.SetColumn(_telegramEnabled, 0);
        tgHeader.Children.Add(_telegramEnabled);
        
        var tgStatusIndicators = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        tgStatusIndicators.Children.Add(_telegramStatusBadge);
        tgStatusIndicators.Children.Add(_telegramStatusText);
        Grid.SetColumn(tgStatusIndicators, 1);
        tgHeader.Children.Add(tgStatusIndicators);

        Grid.SetRow(tgHeader, 0);
        tgGrid.Children.Add(tgHeader);

        var tgContent = new StackPanel { Spacing = 16 };
        var tgForm = new StackPanel { Spacing = 8 };
        tgForm.Children.Add(UiKit.Text("Telegram Configuration", 18, Microsoft.UI.Text.FontWeights.SemiBold));
        tgForm.Children.Add(UiKit.Muted("Setup credentials for incoming Telegram messages.", 12));
        tgForm.Children.Add(Labeled("Bot Token", _botToken));
        tgForm.Children.Add(Labeled("Authorized Chat ID", _chatId));
        tgContent.Children.Add(tgForm);

        var tgGuide = new StackPanel { Spacing = 8, Margin = new Thickness(12, 12, 12, 12) };
        tgGuide.Children.Add(Bullet("1. Create a bot using Telegram's @BotFather and copy the Bot Token."));
        tgGuide.Children.Add(Bullet("2. Get your Chat ID using @userinfobot."));
        tgGuide.Children.Add(Bullet("3. Paste the Token and Chat ID above, enable the listener, and save."));
        
        var tgExpander = new Expander
        {
            Header = "Setup Instructions",
            Content = tgGuide,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        tgContent.Children.Add(tgExpander);

        Grid.SetRow(tgContent, 1);
        tgGrid.Children.Add(tgContent);

        var tgFooter = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        tgFooter.Children.Add(UiKit.PrimaryButton("Save", Symbol.Save, async (_, _) => await SaveTelegramAsync()));
        tgFooter.Children.Add(UiKit.Button("Refresh", Symbol.Sync, async (_, _) => await RefreshTelegramStatusAsync()));
        
        Grid.SetRow(tgFooter, 2);
        tgGrid.Children.Add(tgFooter);

        var tgCard = UiKit.Card(tgGrid);
        tgCard.Padding = new Thickness(24);
        tgCard.HorizontalAlignment = HorizontalAlignment.Stretch;
        tgCard.VerticalAlignment = VerticalAlignment.Stretch;
        body.Children.Add(tgCard);

        // WhatsApp Card
        var waGrid = new Grid();
        waGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
        waGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Content
        waGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Footer

        var waHeader = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        waHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        waHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        
        _waEnabled.Margin = new Thickness(0);
        Grid.SetColumn(_waEnabled, 0);
        waHeader.Children.Add(_waEnabled);
        
        var waStatusIndicators = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        waStatusIndicators.Children.Add(_waStatusBadge);
        waStatusIndicators.Children.Add(_waStatusText);
        Grid.SetColumn(waStatusIndicators, 1);
        waHeader.Children.Add(waStatusIndicators);
        
        Grid.SetRow(waHeader, 0);
        waGrid.Children.Add(waHeader);

        var waContent = new StackPanel { Spacing = 16 };
        var waForm = new StackPanel { Spacing = 6 };
        waForm.Children.Add(UiKit.Text("WhatsApp Cloud API Configuration", 18, Microsoft.UI.Text.FontWeights.SemiBold));
        waForm.Children.Add(UiKit.Muted("Setup local webhook for Meta WhatsApp Business API.", 12));

        var waCreds = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        waCreds.Children.Add(UiKit.Text("Credentials", 14, Microsoft.UI.Text.FontWeights.SemiBold));
        waCreds.Children.Add(Labeled("Access Token", _waAccessToken));
        waCreds.Children.Add(Labeled("Phone Number ID", _waPhoneId));
        waForm.Children.Add(waCreds);
        
        var waWebhook = new StackPanel { Spacing = 6, Margin = new Thickness(0, 12, 0, 0) };
        waWebhook.Children.Add(UiKit.Text("Webhook Setup", 14, Microsoft.UI.Text.FontWeights.SemiBold));
        waWebhook.Children.Add(Labeled("Webhook Verify Token", _waVerifyToken));
        waWebhook.Children.Add(Labeled("Local Webhook Port", _waWebhookPort));
        waForm.Children.Add(waWebhook);

        waContent.Children.Add(waForm);

        var waGuide = new StackPanel { Spacing = 8, Margin = new Thickness(12, 12, 12, 12) };
        waGuide.Children.Add(Bullet("1. Create an app in the Meta Developer Portal and add WhatsApp."));
        waGuide.Children.Add(Bullet("2. Expose your local port (e.g. 5000) using ngrok: `ngrok http 5000`."));
        waGuide.Children.Add(Bullet("3. Configure the Meta Webhook to point to your ngrok URL (`https://your-ngrok.app/webhook/whatsapp`)."));
        waGuide.Children.Add(Bullet("4. Copy the Access Token and Phone ID, set your Verify Token, and save."));
        
        var waExpander = new Expander
        {
            Header = "Setup Instructions",
            Content = waGuide,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        waContent.Children.Add(waExpander);

        Grid.SetRow(waContent, 1);
        waGrid.Children.Add(waContent);

        var waFooter = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        waFooter.Children.Add(UiKit.PrimaryButton("Save", Symbol.Save, async (_, _) => await SaveWhatsAppAsync()));
        waFooter.Children.Add(UiKit.Button("Refresh", Symbol.Sync, async (_, _) => await RefreshWhatsAppStatusAsync()));
        
        Grid.SetRow(waFooter, 2);
        waGrid.Children.Add(waFooter);

        var waCard = UiKit.Card(waGrid);
        waCard.Padding = new Thickness(24);
        waCard.HorizontalAlignment = HorizontalAlignment.Stretch;
        waCard.VerticalAlignment = VerticalAlignment.Stretch;
        body.Children.Add(waCard);

        var scrollViewer = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scrollViewer, 1);
        root.Children.Add(scrollViewer);

        return root;
    }

    private static StackPanel Labeled(string label, FrameworkElement element) =>
        new()
        {
            Spacing = 2,
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
            if (ex.Message.Contains("Unknown command") || ex.Message.Contains("connection"))
            {
                _telegramStatusText.Text = "Service starting...";
                _telegramStatusBadge.Background = UiKit.BrushFromHex("#EAB308");
            }
            else
            {
                _telegramStatusText.Text = $"Error: {ex.Message}";
                _telegramStatusBadge.Background = UiKit.BrushFromHex("#DC2626");
            }
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
            if (ex.Message.Contains("Unknown command") || ex.Message.Contains("connection"))
            {
                _waStatusText.Text = "Service starting...";
                _waStatusBadge.Background = UiKit.BrushFromHex("#EAB308");
            }
            else
            {
                _waStatusText.Text = $"Error: {ex.Message}";
                _waStatusBadge.Background = UiKit.BrushFromHex("#DC2626");
            }
        }
    }
}
