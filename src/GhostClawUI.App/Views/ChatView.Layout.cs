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

    private Grid Build()
    {
        // Chat Panel Area
        var chatPanel = new Grid
        {
            Padding = new Thickness(22, 8, 22, 18),
            Background = ChatBackgroundBrush(),
            AllowDrop = true,
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }, // Messages
                new RowDefinition { Height = GridLength.Auto }  // Composer
            }
        };
        chatPanel.DragOver += OnDragOver;
        chatPanel.Drop += OnDrop;

        _scroll.Content = _messages;
        _scroll.MaxWidth = 1300;
        _scroll.HorizontalAlignment = HorizontalAlignment.Stretch;
        _scroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Grid.SetRow(_scroll, 0);
        chatPanel.Children.Add(_scroll);

        var composer = BuildComposer();
        Grid.SetRow(composer, 1);
        chatPanel.Children.Add(composer);

        return chatPanel;
    }


    private Grid BuildComposer()
    {
        // Use a container Grid to center the floating composer and give it some bottom spacing
        var outerContainer = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(24, 0, 24, 24),
            MaxWidth = 1000
        };

        var root = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = StrokeBrush(),
            Background = ComposerBackgroundBrush(),
            Padding = new Thickness(16, 8, 16, 12),
            CornerRadius = new CornerRadius(16)
        };
        UiKit.AddElevation(root, 24);
        _composerBorder = root;

        var mainLayout = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto }, // Textbox
                new RowDefinition { Height = GridLength.Auto }, // Attachment Tray
                new RowDefinition { Height = GridLength.Auto }  // Bottom Toolbar
            },
            RowSpacing = 8
        };

        // Text area
        _composer.AcceptsReturn = true;
        _composer.TextWrapping = TextWrapping.Wrap;
        _composer.MinHeight = 36;
        _composer.MaxHeight = 320;
        _composer.Padding = new Thickness(4, 8, 4, 8);
        _composer.BorderThickness = new Thickness(0);
        _composer.Background = new SolidColorBrush(Colors.Transparent);
        _composer.Foreground = PrimaryTextBrush();
        _composer.PlaceholderForeground = SecondaryTextBrush();
        _composer.PlaceholderText = "Ask anything...";
        _composer.VerticalAlignment = VerticalAlignment.Center;

        _composer.Resources["TextControlBackground"] = new SolidColorBrush(Colors.Transparent);
        _composer.Resources["TextControlBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
        _composer.Resources["TextControlBackgroundFocused"] = new SolidColorBrush(Colors.Transparent);
        _composer.Resources["TextControlBackgroundDisabled"] = new SolidColorBrush(Colors.Transparent);
        _composer.Resources["TextControlBorderBrush"] = new SolidColorBrush(Colors.Transparent);
        _composer.Resources["TextControlBorderBrushPointerOver"] = new SolidColorBrush(Colors.Transparent);
        _composer.Resources["TextControlBorderBrushFocused"] = new SolidColorBrush(Colors.Transparent);
        _composer.Resources["TextControlBorderBrushDisabled"] = new SolidColorBrush(Colors.Transparent);

        _composer.TextChanged += (s, e) => UpdateSendButtonState();
        _composer.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter &&
                !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                e.Handled = true;
                await SendAsync().ConfigureAwait(false);
            }
        };

        _composer.GotFocus += (s, e) =>
        {
            if (_composerBorder != null)
                _composerBorder.BorderBrush = UiKit.AccentBrush;
        };
        _composer.LostFocus += (s, e) =>
        {
            if (_composerBorder != null)
                _composerBorder.BorderBrush = StrokeBrush();
        };
        _composer.Paste += OnComposerPaste;

        Grid.SetRow(_composer, 0);
        mainLayout.Children.Add(_composer);

        // Attachment Tray
        _attachmentTrayScroll.Content = _attachmentTray;
        _attachmentTrayScroll.Visibility = Visibility.Collapsed;
        Grid.SetRow(_attachmentTrayScroll, 1);
        mainLayout.Children.Add(_attachmentTrayScroll);

        // Bottom row containing dropdown, attach, voice, and send buttons
        var bottomBar = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, // Left elements
                new ColumnDefinition { Width = GridLength.Auto }                      // Right elements
            },
            VerticalAlignment = VerticalAlignment.Center
        };

        // Left Side Controls: Providers and Models dropdowns
        var leftPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

        _providers.SelectionChanged += (_, _) => SyncModels();
        _providers.MinHeight = 32;
        _providers.Height = 32;
        _providers.Padding = new Thickness(8, 2, 8, 2);
        _providers.CornerRadius = new CornerRadius(4);
        _providers.Width = 140;
        _providers.BorderThickness = new Thickness(0);
        _providers.Background = new SolidColorBrush(Colors.Transparent);

        _models.SelectionChanged += (s, e) => UpdateHeaderModelInfo();
        _models.MinHeight = 32;
        _models.Height = 32;
        _models.Padding = new Thickness(8, 2, 8, 2);
        _models.CornerRadius = new CornerRadius(4);
        _models.Width = double.NaN;
        _models.MinWidth = 200;
        _models.MaxWidth = 450;
        _models.BorderThickness = new Thickness(0);
        _models.Background = new SolidColorBrush(Colors.Transparent);

        leftPanel.Children.Add(_providers);
        leftPanel.Children.Add(_models);



        // Progress sending ring
        _sending.HorizontalAlignment = HorizontalAlignment.Left;
        _sending.VerticalAlignment = VerticalAlignment.Center;
        _sending.Margin = new Thickness(4, 0, 0, 0);
        leftPanel.Children.Add(_sending);

        Grid.SetColumn(leftPanel, 0);
        bottomBar.Children.Add(leftPanel);

        // Right Side Controls: Attach, Mic, Export and Send Buttons
        var rightPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

        var attachBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE16C", FontSize = 14 },
            Width = 32,
            Height = 32,
            MinWidth = 32,
            MinHeight = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            BorderBrush = StrokeBrush(),
            Background = ControlSurfaceBrush(),
            Foreground = PrimaryTextBrush()
        };
        AutomationProperties.SetName(attachBtn, "Attach files");
        attachBtn.Click += async (_, _) => await AttachFilesAsync().ConfigureAwait(false);

        // Pointer visual feedback for attachBtn
        attachBtn.PointerEntered += (s, e) => { attachBtn.Background = IsDarkMode ? UiKit.BrushFromHex("#2C3540") : UiKit.BrushFromHex("#E5E7EB"); };
        attachBtn.PointerExited += (s, e) => { attachBtn.Background = ControlSurfaceBrush(); };
        attachBtn.PointerPressed += (s, e) => { attachBtn.Background = IsDarkMode ? UiKit.BrushFromHex("#1F2937") : UiKit.BrushFromHex("#D1D5DB"); };
        attachBtn.PointerReleased += (s, e) => { attachBtn.Background = IsDarkMode ? UiKit.BrushFromHex("#2C3540") : UiKit.BrushFromHex("#E5E7EB"); };

        rightPanel.Children.Add(attachBtn);

        // Sleek circular Send button with Right arrow
        _sendButton.Content = new FontIcon { Glyph = "\uE111", FontSize = 12, Foreground = new SolidColorBrush(Colors.White) };
        _sendButton.Width = 32;
        _sendButton.Height = 32;
        _sendButton.MinWidth = 32;
        _sendButton.MinHeight = 32;
        _sendButton.Padding = new Thickness(0);
        _sendButton.CornerRadius = new CornerRadius(16);
        _sendButton.Background = UiKit.AccentBrush;
        _sendButton.BorderBrush = UiKit.AccentBrush;

        // Pointer visual feedback for send button
        _sendButton.PointerEntered += (s, e) =>
        {
            var isBusy = _sending.IsActive;
            if (isBusy)
            {
                _sendButton.Background = UiKit.BrushFromHex("#EF4444");
            }
            else
            {
                var color = UiKit.AccentBrush.Color;
                _sendButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(220, color.R, color.G, color.B));
            }
        };
        _sendButton.PointerExited += (s, e) =>
        {
            var isBusy = _sending.IsActive;
            if (isBusy)
            {
                _sendButton.Background = UiKit.BrushFromHex("#DC2626");
            }
            else
            {
                _sendButton.Background = UiKit.AccentBrush;
            }
        };
        _sendButton.PointerPressed += (s, e) =>
        {
            var isBusy = _sending.IsActive;
            if (isBusy)
            {
                _sendButton.Background = UiKit.BrushFromHex("#B91C1C");
            }
            else
            {
                var color = UiKit.AccentBrush.Color;
                _sendButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(180, color.R, color.G, color.B));
            }
        };
        _sendButton.PointerReleased += (s, e) =>
        {
            var isBusy = _sending.IsActive;
            if (isBusy)
            {
                _sendButton.Background = UiKit.BrushFromHex("#EF4444");
            }
            else
            {
                var color = UiKit.AccentBrush.Color;
                _sendButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(220, color.R, color.G, color.B));
            }
        };

        rightPanel.Children.Add(_sendButton);

        Grid.SetColumn(rightPanel, 1);
        bottomBar.Children.Add(rightPanel);

        Grid.SetRow(bottomBar, 2);
        mainLayout.Children.Add(bottomBar);

        root.Child = mainLayout;
        outerContainer.Children.Add(root);

        UpdateSendButtonState();
        return outerContainer;
    }


    private void DrawEmptyState(string title)
    {
        _headerTitle.Text = "New Conversation";
        _messages.Children.Clear();

        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 16,
            Margin = new Thickness(0, 160, 0, 0),
            MaxWidth = 680
        };

        var logoIcon = new FontIcon
        {
            Glyph = "\uE9F5", // Cyber/Agent icon
            FontSize = 42,
            Foreground = UiKit.AccentBrush,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var headerText = UiKit.Text("How can I help you today?", 28, FontWeights.Bold);
        headerText.Foreground = PrimaryTextBrush();
        headerText.HorizontalAlignment = HorizontalAlignment.Center;

        var subText = UiKit.Muted(string.IsNullOrWhiteSpace(title) || title.Contains("ready", StringComparison.OrdinalIgnoreCase) || title.Contains("GhostClaw", StringComparison.OrdinalIgnoreCase) ? "GhostClaw Desktop Intelligence" : title, 14);
        subText.HorizontalAlignment = HorizontalAlignment.Center;
        subText.TextAlignment = TextAlignment.Center;

        panel.Children.Add(logoIcon);
        panel.Children.Add(headerText);
        panel.Children.Add(subText);

        _messages.Children.Add(panel);
    }


    private void UpdateSendButtonState()
    {
        bool busy = _sending.IsActive;
        bool uploading = _uploadingFiles.Count > 0;

        if (busy)
        {
            _sendButton.IsEnabled = true;
            _sendButton.Content = new FontIcon { Glyph = "\uE15B", FontSize = 12, Foreground = new SolidColorBrush(Colors.White) };
            _sendButton.Background = UiKit.BrushFromHex("#DC2626");
            _sendButton.BorderBrush = UiKit.BrushFromHex("#DC2626");
            AutomationProperties.SetName(_sendButton, "Stop generation");
        }
        else
        {
            _sendButton.IsEnabled = !uploading;
            _sendButton.Content = new FontIcon { Glyph = "\uE111", FontSize = 12, Foreground = new SolidColorBrush(Colors.White) };
            _sendButton.Background = UiKit.AccentBrush;
            _sendButton.BorderBrush = UiKit.AccentBrush;
            AutomationProperties.SetName(_sendButton, "Send message");
        }
    }

}
