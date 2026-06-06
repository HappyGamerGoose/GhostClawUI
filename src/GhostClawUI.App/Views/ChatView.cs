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

internal sealed class ChatView : UserControl, IDisposable
{
    public void Dispose()
    {
        _chatCts?.Dispose();
    }

    private readonly PipeClient _pipe;
    private readonly CredentialVault _vault;
    private readonly Func<AppSettings> _settings;
    private readonly nint _hwnd;
    private readonly Func<string, Task> _conversationChanged;
    private readonly Action<string, string, InfoBarSeverity> _notice;
    private readonly StackPanel _messages = new()
    {
        Spacing = 22,
        Padding = new Thickness(24, 18, 24, 32),
        MaxWidth = 1300,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Transitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection
        {
            new Microsoft.UI.Xaml.Media.Animation.RepositionThemeTransition(),
            new Microsoft.UI.Xaml.Media.Animation.AddDeleteThemeTransition()
        }
    };
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly ComboBox _providers = UiKit.Combo("Provider selector");
    private readonly ComboBox _models = UiKit.Combo("Model selector");
    private readonly TextBox _composer = UiKit.TextBox("Type your message...", "Message composer");
    private readonly StackPanel _attachmentTray = new() { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(0, 0, 0, 8) };
    private readonly ScrollViewer _attachmentTrayScroll = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Left
    };
    private readonly TextBlock _toolStatus = UiKit.Muted("Tools ready", 13);
    private readonly ProgressRing _sending = new() { Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed };
    private readonly Button _sendButton;
    private Border? _composerBorder;
    private readonly List<ChatAttachment> _attachments = new();
    private readonly Dictionary<string, CancellationTokenSource> _uploadingFiles = new();
    private readonly bool _whisperMode = false;
    private bool _agentMode = true;
    private bool _isSending = false;
    private CancellationTokenSource? _chatCts;
    private static readonly System.Net.Http.HttpClient _logoHttpClient = new();
    private string? _conversationId;

    private IReadOnlyList<ProviderProfile> _providerProfiles = Array.Empty<ProviderProfile>();
    private string? _injectedSkillContext; // Skill content to prepend to next message as system context
    private readonly TextBlock _headerTitle = new()
    {
        FontSize = 16,
        FontWeight = FontWeights.Bold,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = 550
    };
    private readonly TextBlock _headerModelName = new()
    {
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 0, 0)
    };
    private readonly Border _headerModelLogo = new()
    {
        Width = 20,
        Height = 20,
        CornerRadius = new CornerRadius(10),
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly TextBlock _skillBadge = new()
    {
        FontSize = 10,
        FontWeight = FontWeights.Bold,
        Foreground = UiKit.AccentBrush,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = 80,
        Visibility = Visibility.Collapsed,
        VerticalAlignment = VerticalAlignment.Center
    };
    private Grid? _liveRunRow;
    private DispatcherQueueTimer? _pollingTimer;
    private Windows.Foundation.TypedEventHandler<DispatcherQueueTimer, object>? _pollingTimerHandler;
    private List<AgentTraceCard>? _lastTraces;
    private bool _isPollingActive;


    public ChatView(
        PipeClient pipe,
        CredentialVault vault,
        Func<AppSettings> settings,
        nint hwnd,
        string? conversationId,
        Func<string, Task> conversationChanged,
        Action<string, string, InfoBarSeverity> notice)
    {
        _pipe = pipe;
        _vault = vault;
        _settings = settings;
        _hwnd = hwnd;
        _conversationId = conversationId;
        _conversationChanged = conversationChanged;
        _notice = notice;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        _sendButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE111", FontSize = 12, Foreground = new SolidColorBrush(Colors.White) },
            Width = 32,
            Height = 32,
            MinWidth = 32,
            MinHeight = 32,
            CornerRadius = new CornerRadius(16),
            Background = UiKit.AccentBrush,
            BorderBrush = UiKit.AccentBrush,
            Foreground = new SolidColorBrush(Colors.White)
        };
        AutomationProperties.SetName(_sendButton, "Send message");
        _sendButton.Click += async (_, _) => await SendAsync().ConfigureAwait(false);

        Content = Build();
        Unloaded += (s, e) => StopPollingActiveTraces();
        _ = LoadAsync();
    }

    public async Task SendQuickPromptAsync(string text)
    {
        _composer.Text = text;
        await SendAsync().ConfigureAwait(false);
    }

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
                new RowDefinition { Height = GridLength.Auto }, // Date
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }, // Messages
                new RowDefinition { Height = GridLength.Auto }  // Composer
            }
        };
        chatPanel.DragOver += OnDragOver;
        chatPanel.Drop += OnDrop;

        var dateText = UiKit.Muted("Today", 12);
        dateText.Foreground = SecondaryTextBrush();
        var date = new Border
        {
            Child = dateText,
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = StrokeBrush(),
            Background = SurfaceBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 6)
        };
        Grid.SetRow(date, 0);
        chatPanel.Children.Add(date);

        _scroll.Content = _messages;
        _scroll.MaxWidth = 1300;
        _scroll.HorizontalAlignment = HorizontalAlignment.Stretch;
        _scroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Grid.SetRow(_scroll, 1);
        chatPanel.Children.Add(_scroll);

        var composer = BuildComposer();
        Grid.SetRow(composer, 2);
        chatPanel.Children.Add(composer);

        return chatPanel;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Drop to attach files";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var maxCharacters = GetModelMaxCharacterLimit();
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var files = items.OfType<StorageFile>().ToList();
                if (files.Count > 0)
                {
                    await ProcessStorageFilesAsync(files).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _notice("Drop handling failed", ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                deferral.Complete();
            }
        }
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

    private async Task LoadAsync()
    {
        try
        {
            _providerProfiles = await _pipe.RequestAsync<IReadOnlyList<ProviderProfile>>("providers.list").ConfigureAwait(false) ?? Array.Empty<ProviderProfile>();
            _providers.ItemsSource = _providerProfiles;
            _providers.DisplayMemberPath = nameof(ProviderProfile.Name);

            var defaultProvIndex = -1;
            if (!string.IsNullOrEmpty(_settings().DefaultProviderId))
            {
                for (int i = 0; i < _providerProfiles.Count; i++)
                {
                    if (_providerProfiles[i].Id == _settings().DefaultProviderId)
                    {
                        defaultProvIndex = i;
                        break;
                    }
                }
            }
            _providers.SelectedIndex = defaultProvIndex >= 0 ? defaultProvIndex : (_providerProfiles.Count > 0 ? 0 : -1);
            SyncModels();

            if (string.IsNullOrWhiteSpace(_conversationId))
            {
                _headerTitle.Text = "New Conversation";
                DrawEmptyState("Start a conversation when you're ready.");
                return;
            }

            var conversation = await _pipe.RequestAsync<ConversationDetail>("conversations.get", new SimpleIdRequest(_conversationId)).ConfigureAwait(false);
            if (conversation is null)
            {
                _headerTitle.Text = "New Conversation";
                DrawEmptyState("Start a conversation when you're ready.");
                return;
            }

            _conversationId = conversation.Summary.Id;
            _headerTitle.Text = conversation.Summary.Title;
            await _conversationChanged(conversation.Summary.Id).ConfigureAwait(false);
            DrawMessages(conversation.Messages);

            var lastMessageWithModel = conversation.Messages
                .LastOrDefault(m => !string.IsNullOrEmpty(m.ProviderId) && !string.IsNullOrEmpty(m.Model));
            if (lastMessageWithModel != null)
            {
                SelectProviderAndModel(lastMessageWithModel.ProviderId, lastMessageWithModel.Model);
            }

            try
            {
                var active = await _pipe.RequestAsync<ActiveTracesResponse>("chat.activeTraces", new SimpleIdRequest(_conversationId)).ConfigureAwait(false);
                if (active != null && active.IsRunning)
                {
                    StartPollingActiveTraces();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking active traces: {ex}");
            }
        }
        catch (Exception ex)
        {
            _headerTitle.Text = "Connection Error";
            _notice("Chat unavailable", Explain(ex), InfoBarSeverity.Warning);
            DrawEmptyState("Service connection unavailable.");
        }
    }

    private void SyncModels()
    {
        _models.Items.Clear();
        if (_providers.SelectedItem is not ProviderProfile provider)
        {
            _models.PlaceholderText = "No provider";
            _toolStatus.Text = "Add provider";
            return;
        }

        _models.PlaceholderText = provider.Models.Count == 0 ? "No models" : "Model";
        ComboBoxItem? preferredItem = null;
        var preferredModelName = (_settings().DefaultProviderId == provider.Id && !string.IsNullOrEmpty(_settings().DefaultModelId) && provider.Models.Contains(_settings().DefaultModelId))
            ? _settings().DefaultModelId
            : (provider.DefaultModel ?? provider.Models.FirstOrDefault());

        foreach (var modelName in provider.Models)
        {
            var item = CreateModelComboBoxItem(modelName);
            _models.Items.Add(item);
            if (modelName.Equals(preferredModelName, StringComparison.OrdinalIgnoreCase))
            {
                preferredItem = item;
            }
        }

        if (preferredItem is not null)
        {
            _models.SelectedItem = preferredItem;
        }
        else if (_models.Items.Count > 0)
        {
            _models.SelectedIndex = 0;
        }

        _toolStatus.Text = _vault.ReadProviderKey(provider.Id) is null ? "Key missing" : _whisperMode ? "Whisper" : "Tools ready";
        UpdateHeaderModelInfo();
    }

    private void UpdateHeaderModelInfo()
    {
        string? modelCode = null;
        if (_models.SelectedItem is ComboBoxItem cbi)
        {
            modelCode = cbi.Tag as string;
        }
        else
        {
            modelCode = _models.SelectedItem as string;
        }

        if (!string.IsNullOrEmpty(modelCode))
        {
            ModelClassifier.Resolve(modelCode, out var brand, out var resolvedFriendlyName);
            var name = ModelClassifier.FormatFriendlyName(resolvedFriendlyName);
            _headerModelName.Text = name;
            _headerModelLogo.Background = GetNativeBrandBackground(brand);
            _headerModelLogo.Child = GetNativeBrandLogoElement(brand, fontSize: 10);
            _headerModelLogo.Visibility = Visibility.Visible;
            _headerModelName.Visibility = Visibility.Visible;
        }
        else
        {
            _headerModelLogo.Visibility = Visibility.Collapsed;
            _headerModelName.Visibility = Visibility.Collapsed;
        }
    }

    private void SelectProviderAndModel(string? providerId, string? modelCode)
    {
        if (string.IsNullOrEmpty(providerId)) return;

        var provider = _providerProfiles.FirstOrDefault(p => p.Id == providerId);
        if (provider != null)
        {
            _providers.SelectedItem = provider;

            if (_models.Items.Count == 0)
            {
                SyncModels();
            }

            if (!string.IsNullOrEmpty(modelCode))
            {
                foreach (var item in _models.Items)
                {
                    if (item is ComboBoxItem cbi && cbi.Tag is string tag && tag.Equals(modelCode, StringComparison.OrdinalIgnoreCase))
                    {
                        _models.SelectedItem = cbi;
                        break;
                    }
                    else if (item is string s && s.Equals(modelCode, StringComparison.OrdinalIgnoreCase))
                    {
                        _models.SelectedItem = item;
                        break;
                    }
                }
            }
        }
    }

    private void DrawMessages(IReadOnlyList<ChatMessage> messages)
    {
        _messages.Children.Clear();
        var visible = messages.Where(message => message.Kind != "status").ToList();
        if (visible.Count == 0)
        {
            DrawEmptyState("Ask GhostClaw anything.");
            return;
        }

        foreach (var message in visible)
        {
            _messages.Children.Add(MessageRow(message));
        }

        _ = ScrollToEndAsync();
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

    private Grid MessageRow(ChatMessage message)
    {
        var isUser = message.Role.Equals("user", StringComparison.OrdinalIgnoreCase);
        var isError = message.Kind.Equals("error", StringComparison.OrdinalIgnoreCase);
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            ColumnSpacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var bubble = Bubble(message, isUser, isError);
        if (isUser)
        {
            bubble.HorizontalAlignment = HorizontalAlignment.Right;
            bubble.MaxWidth = 960;
            Grid.SetColumn(bubble, 1);
            row.Children.Add(bubble);
            return row;
        }

        row.Children.Add(Avatar(message.Model));
        Grid.SetColumn(bubble, 1);
        bubble.HorizontalAlignment = HorizontalAlignment.Left;
        bubble.MaxWidth = 1100;
        row.Children.Add(bubble);
        return row;
    }

    private StackPanel Bubble(ChatMessage message, bool isUser, bool isError)
    {
        Border? bubbleBorder = null;
        var panel = new StackPanel { Spacing = 8 };
        if (!isUser)
        {
            panel.Children.Add(UiKit.Muted(message.CreatedAt.ToLocalTime().ToString("t"), 12));
        }

        var visibleContent = isUser ? message.Content : ResponseTextSanitizer.CleanForDisplay(message.Content);
        if (!isUser && HasGeneratedFiles(message.Metadata))
        {
            visibleContent = StripFileGenerationCode(visibleContent);
        }

        if (!isUser && message.Metadata != null && message.Metadata["traces"] is JsonNode tracesNode)
        {
            try
            {
                var traces = JsonSerializer.Deserialize<List<AgentTraceCard>>(tracesNode.ToJsonString(), PipeJson.Options);
                if (traces != null && traces.Count > 0)
                {
                    // Clean up any lingering "running" states for completed/static database messages
                    for (int i = 0; i < traces.Count; i++)
                    {
                        if (traces[i].State == "running")
                        {
                            traces[i] = traces[i] with { State = "done" };
                        }
                    }

                    var filteredTraces = traces.Where(t =>
                        !(t.Title == "Reasoning" && string.Equals(t.Detail?.Trim(), visibleContent?.Trim(), StringComparison.OrdinalIgnoreCase)) &&
                        !(t.Title == "Thinking" && string.Equals(t.Detail?.Trim(), visibleContent?.Trim(), StringComparison.OrdinalIgnoreCase))
                    ).ToList();

                    if (filteredTraces.Count > 0)
                    {
                        panel.Children.Add(TracesExpander(filteredTraces));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error deserializing traces: {ex}");
            }
        }

        RenderContent(panel, visibleContent, isUser);
        if (!isUser)
        {
            DetectAndAddLocalFiles(panel, message.Content);
        }
        foreach (var attachment in ReadAttachments(message.Metadata))
        {
            panel.Children.Add(AttachmentPreview(attachment, isUser, removable: false));
        }

        if (isUser)
        {
            var time = UiKit.Text(message.CreatedAt.ToLocalTime().ToString("t") + "  \u2713", 11);
            time.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(210, 255, 255, 255));
            time.HorizontalAlignment = HorizontalAlignment.Right;
            panel.Children.Add(time);
        }

        var actionPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
            Opacity = 0,
            IsHitTestVisible = false
        };

        if (isUser)
        {
            var editBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE70F", FontSize = 10 },
                Width = 24,
                Height = 24,
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255)),
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Colors.White)
            };
            AutomationProperties.SetName(editBtn, "Edit prompt");
            HookClick(editBtn, (_, _) =>
            {
                if (bubbleBorder != null)
                {
                    ShowInlineEditor(bubbleBorder, message, isUser: true);
                }
            });
            actionPanel.Children.Add(editBtn);
        }
        else if (isError)
        {
            var retryBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE72C", FontSize = 10 },
                Width = 24,
                Height = 24,
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(15, 255, 255, 255)),
                BorderThickness = new Thickness(0),
                Foreground = SecondaryTextBrush()
            };
            AutomationProperties.SetName(retryBtn, "Retry request");
            HookClick(retryBtn, async (_, _) =>
            {
                string? lastUserPrompt = null;
                string? lastUserId = null;
                try
                {
                    var conversation = await _pipe.RequestAsync<ConversationDetail>("conversations.get", new SimpleIdRequest(_conversationId ?? string.Empty)).ConfigureAwait(false);
                    if (conversation is not null)
                    {
                        var lastUser = conversation.Messages.LastOrDefault(m => m.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
                        if (lastUser is not null)
                        {
                            lastUserPrompt = lastUser.Content;
                            lastUserId = lastUser.Id;
                        }
                    }
                }
                catch
                {
                }

                if (!string.IsNullOrEmpty(lastUserPrompt) && !string.IsNullOrEmpty(lastUserId))
                {
                    try
                    {
                        // Roll back conversation from this user prompt (inclusive)
                        await _pipe.RequestAsync<CommandResult>("conversations.deleteMessagesAfter", new DeleteMessagesAfterRequest(_conversationId ?? string.Empty, lastUserId)).ConfigureAwait(false);

                        // Immediately reload in the UI to wipe off the old response!
                        await LoadAsync().ConfigureAwait(false);

                        // Resubmit the prompt
                        await SendQuickPromptAsync(lastUserPrompt).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _notice("Re-generation failed", ex.Message, InfoBarSeverity.Error);
                    }
                }
                else
                {
                    _notice("Re-generation unavailable", "Could not locate the previous user message.", InfoBarSeverity.Warning);
                }
            });
            actionPanel.Children.Add(retryBtn);
        }
        else
        {
            var copyBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uF0E3", FontSize = 10 },
                Width = 24,
                Height = 24,
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(15, 255, 255, 255)),
                BorderThickness = new Thickness(0),
                Foreground = SecondaryTextBrush()
            };
            AutomationProperties.SetName(copyBtn, "Copy plain text");
            HookClick(copyBtn, (_, _) =>
            {
                var cleanText = CleanMarkdownForClipboard(message.Content);
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(cleanText);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                _notice("Copied to Clipboard", "Message text copied in plain text format.", InfoBarSeverity.Success);
            });
            actionPanel.Children.Add(copyBtn);

            var editBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE70F", FontSize = 10 },
                Width = 24,
                Height = 24,
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(15, 255, 255, 255)),
                BorderThickness = new Thickness(0),
                Foreground = SecondaryTextBrush()
            };
            AutomationProperties.SetName(editBtn, "Edit response");
            HookClick(editBtn, (_, _) =>
            {
                if (bubbleBorder != null)
                {
                    ShowInlineEditor(bubbleBorder, message, isUser: false);
                }
            });
            actionPanel.Children.Add(editBtn);

            var retryBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE72C", FontSize = 10 },
                Width = 24,
                Height = 24,
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(15, 255, 255, 255)),
                BorderThickness = new Thickness(0),
                Foreground = SecondaryTextBrush()
            };
            AutomationProperties.SetName(retryBtn, "Re-generate response");
            HookClick(retryBtn, async (_, _) =>
            {
                string? lastUserPrompt = null;
                string? lastUserId = null;
                try
                {
                    var conversation = await _pipe.RequestAsync<ConversationDetail>("conversations.get", new SimpleIdRequest(_conversationId ?? string.Empty)).ConfigureAwait(false);
                    if (conversation is not null)
                    {
                        var lastUser = conversation.Messages.LastOrDefault(m => m.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
                        if (lastUser is not null)
                        {
                            lastUserPrompt = lastUser.Content;
                            lastUserId = lastUser.Id;
                        }
                    }
                }
                catch
                {
                }

                if (!string.IsNullOrEmpty(lastUserPrompt) && !string.IsNullOrEmpty(lastUserId))
                {
                    try
                    {
                        // Roll back conversation from this user prompt (inclusive)
                        await _pipe.RequestAsync<CommandResult>("conversations.deleteMessagesAfter", new DeleteMessagesAfterRequest(_conversationId ?? string.Empty, lastUserId)).ConfigureAwait(false);

                        // Immediately reload in the UI to wipe off the old response!
                        await LoadAsync().ConfigureAwait(false);

                        // Resubmit the prompt
                        await SendQuickPromptAsync(lastUserPrompt).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _notice("Re-generation failed", ex.Message, InfoBarSeverity.Error);
                    }
                }
                else
                {
                    _notice("Re-generation unavailable", "Could not locate the previous user message.", InfoBarSeverity.Warning);
                }
            });
            actionPanel.Children.Add(retryBtn);
        }

        bubbleBorder = new Border
        {
            Child = panel,
            Padding = new Thickness(20, 16, 20, 16),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            BorderBrush = isError
                ? UiKit.BrushFromHex("#FB923C")
                : isUser ? UserBubbleBorderBrush() : StrokeBrush(),
            Background = isError
                ? ErrorSurfaceBrush()
                : isUser ? UserBubbleBrush() : AssistantBubbleBrush()
        };

        var container = new StackPanel
        {
            Spacing = 4,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), // Intercept hover events continuously across gaps
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Transitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection
            {
                new Microsoft.UI.Xaml.Media.Animation.EntranceThemeTransition { FromVerticalOffset = 16, IsStaggeringEnabled = false }
            }
        };
        container.Children.Add(bubbleBorder);

        actionPanel.HorizontalAlignment = HorizontalAlignment.Right;
        actionPanel.Margin = new Thickness(0, 0, 4, 0);
        container.Children.Add(actionPanel);

        System.Threading.CancellationTokenSource? hideCts = null;

        HookPointer(container,
            (s, e) =>
            {
                hideCts?.Cancel();
                hideCts = null;
                actionPanel.Opacity = 1;
                actionPanel.IsHitTestVisible = true;
            },
            (s, e) =>
            {
                hideCts?.Cancel();
                hideCts = new System.Threading.CancellationTokenSource();
                var token = hideCts.Token;
                Task.Delay(350, token).ContinueWith(t =>
                {
                    if (!t.IsCanceled && !token.IsCancellationRequested)
                    {
                        container.DispatcherQueue.TryEnqueue(() =>
                        {
                            if (!token.IsCancellationRequested)
                            {
                                actionPanel.Opacity = 0;
                                actionPanel.IsHitTestVisible = false;
                            }
                        });
                    }
                }, TaskScheduler.Default);
            });

        return container;
    }

    private void DetectAndAddLocalFiles(StackPanel panel, string messageContent)
    {
        if (string.IsNullOrWhiteSpace(messageContent)) return;

        // Run detection and file existence checks in the background
        _ = Task.Run(() =>
        {
            try
            {
                var regex = new System.Text.RegularExpressions.Regex(
                    @"(?i)(?:""([^""]+?\.(?:pptx|docx|xlsx|pdf|txt|csv|png|jpg|jpeg|zip))""|'([^']+?\.(?:pptx|docx|xlsx|pdf|txt|csv|png|jpg|jpeg|zip))'|\`([^\`]+?\.(?:pptx|docx|xlsx|pdf|txt|csv|png|jpg|jpeg|zip))\`|\b([a-zA-Z]:[\\/][^:\*\?""<>\|\s]+?\.(?:pptx|docx|xlsx|pdf|txt|csv|png|jpg|jpeg|zip))\b|\b([^:\*\?""<>\|\s\u201c\u201d\u2018\u2019]+?\.(?:pptx|docx|xlsx|pdf|txt|csv|png|jpg|jpeg|zip))\b)",
                    System.Text.RegularExpressions.RegexOptions.Compiled);

                var matches = regex.Matches(messageContent);
                var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var attachmentsToAdd = new List<ChatAttachment>();

                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    // Extract the path from the matched group
                    string potPath = string.Empty;
                    for (int i = 1; i <= 5; i++)
                    {
                        if (match.Groups[i].Success)
                        {
                            potPath = match.Groups[i].Value;
                            break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(potPath)) continue;

                    // Trim leading/trailing punctuation and markdown
                    potPath = potPath.Trim(' ', '\t', '\r', '\n', '.', ',', '!', '?', ':', '*', '(', ')', '[', ']', '{', '}');

                    string? resolvedPath = ResolveLocalFilePath(potPath);
                    if (resolvedPath != null && !addedPaths.Contains(resolvedPath))
                    {
                        addedPaths.Add(resolvedPath);
                        try
                        {
                            var fileInfo = new System.IO.FileInfo(resolvedPath);
                            var name = fileInfo.Name;
                            var ext = fileInfo.Extension.ToLowerInvariant();
                            var contentType = ext switch
                            {
                                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                                ".pdf" => "application/vnd.openxmlformats-officedocument.pdf",
                                ".png" => "image/png",
                                ".jpg" => "image/jpeg",
                                ".jpeg" => "image/jpeg",
                                ".txt" => "text/plain",
                                ".csv" => "text/csv",
                                ".zip" => "application/zip",
                                _ => "application/octet-stream"
                            };

                            attachmentsToAdd.Add(new ChatAttachment(name, resolvedPath, contentType, fileInfo.Length, null));
                        }
                        catch
                        {
                            // Ignore errors building preview
                        }
                    }
                }

                if (attachmentsToAdd.Count > 0)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        foreach (var attachment in attachmentsToAdd)
                        {
                            var card = AttachmentPreview(attachment, isUser: false, removable: false);
                            panel.Children.Add(card);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error detecting local files: {ex}");
            }
        });
    }

    private static string? ResolveLocalFilePath(string p)
    {
        // 1. Try absolute path directly
        if (System.IO.File.Exists(p)) return System.IO.Path.GetFullPath(p);

        // 2. If it looks absolute, check if we can substitute Admin/username with actual username
        if (System.IO.Path.IsPathRooted(p))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // Replace C:\Users\<name>\ with userProfile
            var match = System.Text.RegularExpressions.Regex.Match(p, @"^(?i)c:\\users\\[^\\]+(.*)$");
            if (match.Success)
            {
                var relativePart = match.Groups[1].Value;
                var subPath = System.IO.Path.Combine(userProfile, relativePart.TrimStart('\\'));
                if (System.IO.File.Exists(subPath)) return System.IO.Path.GetFullPath(subPath);
            }
        }

        // 3. Check in relative directories
        var fileName = System.IO.Path.GetFileName(p);

        // A. Documents folder
        var docsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var docsPath = System.IO.Path.Combine(docsDir, fileName);
        if (System.IO.File.Exists(docsPath)) return System.IO.Path.GetFullPath(docsPath);

        // B. Active workspace directory
        var workspaceDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "GhostClawUI");
        var workspacePath = System.IO.Path.Combine(workspaceDir, fileName);
        if (System.IO.File.Exists(workspacePath)) return System.IO.Path.GetFullPath(workspacePath);

        // C. Agent's main runtime directory
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var agentDir = System.IO.Path.Combine(localData, "GhostClawUI", "Runtime", "ghostclaw", "groups", "main");
        var agentPath = System.IO.Path.Combine(agentDir, fileName);
        if (System.IO.File.Exists(agentPath)) return System.IO.Path.GetFullPath(agentPath);

        // D. Current/Base Directory
        var curDir = System.IO.Path.Combine(Directory.GetCurrentDirectory(), fileName);
        if (System.IO.File.Exists(curDir)) return System.IO.Path.GetFullPath(curDir);

        var baseDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
        if (System.IO.File.Exists(baseDir)) return System.IO.Path.GetFullPath(baseDir);

        return null;
    }

    private void ShowInlineEditor(Border bubbleBorder, ChatMessage message, bool isUser)
    {
        var currentText = message.Content;

        var editBox = new TextBox
        {
            Text = currentText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 60,
            MaxHeight = 300,
            Width = isUser ? 520 : 740,
            FontSize = _settings().Appearance.FontSize,
            FontFamily = new FontFamily(_settings().Appearance.FontFamily),
            Background = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255)) : ControlSurfaceBrush(),
            Foreground = isUser ? new SolidColorBrush(Colors.White) : PrimaryTextBrush(),
            BorderThickness = new Thickness(1),
            BorderBrush = StrokeBrush()
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var saveButton = new Button
        {
            Content = isUser ? "Save & Submit" : "Save",
            Background = UiKit.AccentBrush,
            Foreground = new SolidColorBrush(Colors.White),
            CornerRadius = new CornerRadius(6)
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = StrokeBrush(),
            BorderThickness = new Thickness(1),
            Foreground = isUser ? new SolidColorBrush(Colors.White) : PrimaryTextBrush(),
            CornerRadius = new CornerRadius(6)
        };

        buttonPanel.Children.Add(saveButton);
        buttonPanel.Children.Add(cancelButton);

        var editorPanel = new StackPanel { Spacing = 4 };
        editorPanel.Children.Add(editBox);
        editorPanel.Children.Add(buttonPanel);

        var originalContent = bubbleBorder.Child;
        var originalBackground = bubbleBorder.Background;
        var originalBorderBrush = bubbleBorder.BorderBrush;

        bubbleBorder.Child = editorPanel;
        bubbleBorder.Background = isUser ? UiKit.AccentBrush : AssistantBubbleBrush();

        HookClick(cancelButton, (_, _) =>
        {
            bubbleBorder.Child = originalContent;
            bubbleBorder.Background = originalBackground;
            bubbleBorder.BorderBrush = originalBorderBrush;
        });

        HookClick(saveButton, async (_, _) =>
        {
            var newText = editBox.Text.Trim();
            if (string.IsNullOrEmpty(newText)) return;

            if (newText == currentText)
            {
                bubbleBorder.Child = originalContent;
                bubbleBorder.Background = originalBackground;
                bubbleBorder.BorderBrush = originalBorderBrush;
                return;
            }

            saveButton.IsEnabled = false;
            cancelButton.IsEnabled = false;

            try
            {
                if (isUser)
                {
                    // Sync provider and model to the original message's selection
                    SelectProviderAndModel(message.ProviderId, message.Model);

                    // User prompt edit: Delete this message and subsequent ones, and send new prompt
                    await _pipe.RequestAsync<CommandResult>("conversations.deleteMessagesAfter", new DeleteMessagesAfterRequest(message.ConversationId, message.Id)).ConfigureAwait(false);
                    _composer.Text = newText;
                    await SendAsync().ConfigureAwait(false);
                }
                else
                {
                    // Assistant response edit: Update DB and redraw in place
                    await _pipe.RequestAsync<CommandResult>("messages.update", new MessageUpdateRequest(message.Id, newText)).ConfigureAwait(false);
                    _notice("Message Updated", "Assistant response has been updated successfully.", InfoBarSeverity.Success);
                    await LoadAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _notice("Edit failed", ex.Message, InfoBarSeverity.Error);
                saveButton.IsEnabled = true;
                cancelButton.IsEnabled = true;
            }
        });
    }

    private void RenderContent(StackPanel panel, string content, bool isUser)
    {
        var foreground = isUser ? new SolidColorBrush(Colors.White) : PrimaryTextBrush();
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        var paragraph = new StringBuilder();
        var code = new StringBuilder();
        var math = new StringBuilder();
        var think = new StringBuilder();
        var inCode = false;
        var inMath = false;
        var inThink = false;

        void FlushParagraph()
        {
            if (paragraph.Length == 0)
            {
                return;
            }

            var block = MarkdownText(paragraph.ToString().TrimEnd(), foreground, _settings().Appearance.FontSize, FontWeights.Normal, isUser);
            block.Margin = new Thickness(0, 4, 0, 4); // Premium airy spacing
            panel.Children.Add(block);
            paragraph.Clear();
        }

        void FlushCode()
        {
            var block = CodeBlock(code.ToString().TrimEnd(), isUser);
            block.Margin = new Thickness(0, 8, 0, 8);
            panel.Children.Add(block);
            code.Clear();
        }

        void FlushMath()
        {
            var block = MathBlock(math.ToString().TrimEnd(), isUser);
            block.Margin = new Thickness(0, 8, 0, 8);
            panel.Children.Add(block);
            math.Clear();
        }

        void FlushThink()
        {
            if (think.Length == 0)
            {
                return;
            }

            var block = ThinkBlock(think.ToString().TrimEnd(), isUser);
            block.Margin = new Thickness(0, 8, 0, 8);
            panel.Children.Add(block);
            think.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var line = rawLine.TrimEnd();
            var trimmed = line.TrimStart();

            if (inThink)
            {
                var closeTags = new[] { "</thinking>", "</thought>", "</think>" };
                var matchedClose = closeTags.FirstOrDefault(t => line.Contains(t, StringComparison.OrdinalIgnoreCase));
                if (matchedClose != null)
                {
                    var closeIdx = line.IndexOf(matchedClose, StringComparison.OrdinalIgnoreCase);
                    think.AppendLine(line[..closeIdx]);
                    FlushThink();
                    inThink = false;
                    var remaining = line[(closeIdx + matchedClose.Length)..];
                    if (!string.IsNullOrWhiteSpace(remaining))
                    {
                        paragraph.Append(remaining);
                    }
                }
                else
                {
                    think.AppendLine(line);
                }
                continue;
            }

            if (inCode)
            {
                if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    FlushCode();
                    inCode = false;
                }
                else
                {
                    code.AppendLine(line);
                }
                continue;
            }

            if (inMath)
            {
                if (trimmed.Equals("$$", StringComparison.Ordinal) || trimmed.Equals("\\]", StringComparison.Ordinal))
                {
                    FlushMath();
                    inMath = false;
                }
                else
                {
                    math.AppendLine(line);
                }
                continue;
            }

            var openTags = new[] { "<thinking>", "<thought>", "<think>" };
            var matchedOpen = openTags.FirstOrDefault(t => line.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (matchedOpen != null)
            {
                var openIdx = line.IndexOf(matchedOpen, StringComparison.OrdinalIgnoreCase);
                var before = line[..openIdx];
                if (!string.IsNullOrEmpty(before))
                {
                    paragraph.Append(before);
                }
                FlushParagraph();
                inThink = true;

                var closeTag = matchedOpen.Insert(1, "/");
                var closeIdx = line.IndexOf(closeTag, openIdx, StringComparison.OrdinalIgnoreCase);
                if (closeIdx >= 0)
                {
                    think.Append(line[(openIdx + matchedOpen.Length)..closeIdx]);
                    FlushThink();
                    inThink = false;
                    var remaining = line[(closeIdx + closeTag.Length)..];
                    if (!string.IsNullOrWhiteSpace(remaining))
                    {
                        paragraph.Append(remaining);
                    }
                }
                else
                {
                    think.AppendLine(line[(openIdx + matchedOpen.Length)..]);
                }
                continue;
            }

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                inCode = true;
                continue;
            }

            if (trimmed.Equals("$$", StringComparison.Ordinal) || trimmed.Equals("\\[", StringComparison.Ordinal))
            {
                FlushParagraph();
                inMath = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            if (TryAddMarkdownImage(panel, line))
            {
                FlushParagraph();
                continue;
            }

            if (IsTableLine(line) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                FlushParagraph();
                var table = new List<string> { line };
                i += 2;
                while (i < lines.Length && IsTableLine(lines[i]))
                {
                    table.Add(lines[i]);
                    i++;
                }

                i--;
                var tableBlock = TableBlock(table, isUser);
                tableBlock.Margin = new Thickness(0, 10, 0, 10);
                panel.Children.Add(tableBlock);
                continue;
            }

            if (trimmed == "---" || trimmed == "***" || trimmed == "___")
            {
                FlushParagraph();
                var hr = new Border
                {
                    Height = 1,
                    Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    Margin = new Thickness(0, 16, 0, 16)
                };
                panel.Children.Add(hr);
                continue;
            }

            if (TryHeading(trimmed, out var headingText, out var headingLevel))
            {
                FlushParagraph();
                var bump = headingLevel == 1 ? 5 : headingLevel == 2 ? 3 : headingLevel == 3 ? 2 : 1;
                var headingBlock = MarkdownText(headingText, foreground, _settings().Appearance.FontSize + bump, FontWeights.SemiBold, isUser);
                headingBlock.Margin = new Thickness(0, 12, 0, 6);
                panel.Children.Add(headingBlock);
                continue;
            }

            if (TryBullet(trimmed, out var bulletText))
            {
                FlushParagraph();
                var listItemBlock = ListItem("\u2022", bulletText, foreground, isUser);
                listItemBlock.Margin = new Thickness(12, 3, 0, 3);
                panel.Children.Add(listItemBlock);
                continue;
            }

            if (TryNumbered(trimmed, out var number, out var numbered))
            {
                FlushParagraph();
                var listItemBlock = ListItem(number, numbered, foreground, isUser);
                listItemBlock.Margin = new Thickness(12, 3, 0, 3);
                panel.Children.Add(listItemBlock);
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph();
                var quoteBlock = QuoteBlock(trimmed[2..], isUser);
                quoteBlock.Margin = new Thickness(0, 8, 0, 8);
                panel.Children.Add(quoteBlock);
                continue;
            }

            if (trimmed.StartsWith("\\[", StringComparison.Ordinal) && trimmed.EndsWith("\\]", StringComparison.Ordinal) && trimmed.Length > 4)
            {
                FlushParagraph();
                var mathBlock = MathBlock(trimmed[2..^2], isUser);
                mathBlock.Margin = new Thickness(0, 8, 0, 8);
                panel.Children.Add(mathBlock);
                continue;
            }

            if (trimmed.StartsWith("$$", StringComparison.Ordinal) && trimmed.EndsWith("$$", StringComparison.Ordinal) && trimmed.Length > 4)
            {
                FlushParagraph();
                var mathBlock = MathBlock(trimmed[2..^2], isUser);
                mathBlock.Margin = new Thickness(0, 8, 0, 8);
                panel.Children.Add(mathBlock);
                continue;
            }

            if (trimmed.StartsWith('$') && trimmed.EndsWith('$') && trimmed.Length > 2)
            {
                FlushParagraph();
                var mathBlock = MathBlock(trimmed[1..^1], isUser);
                mathBlock.Margin = new Thickness(0, 8, 0, 8);
                panel.Children.Add(mathBlock);
                continue;
            }

            if (paragraph.Length > 0)
            {
                paragraph.AppendLine();
            }

            paragraph.Append(line);
        }

        if (inCode)
        {
            FlushCode();
        }

        if (inMath)
        {
            FlushMath();
        }

        if (inThink)
        {
            FlushThink();
        }

        FlushParagraph();
    }

    private TextBlock MarkdownText(string text, Brush foreground, double size, Windows.UI.Text.FontWeight weight, bool isUser = false)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            FontFamily = new FontFamily(_settings().Appearance.FontFamily),
            FontWeight = weight,
            LineHeight = Math.Max(size * _settings().Appearance.LineHeight, size + 4),
            Foreground = foreground,
            IsTextSelectionEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AddMarkdownInlines(block, text, foreground, weight);
        return block;
    }

    private Grid ListItem(string marker, string text, Brush foreground, bool isUser = false)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            ColumnSpacing = 10
        };
        row.Children.Add(new TextBlock
        {
            Text = marker,
            FontSize = _settings().Appearance.FontSize,
            FontFamily = new FontFamily(_settings().Appearance.FontFamily),
            Foreground = foreground,
            Margin = new Thickness(0, 1, 0, 0),
            IsTextSelectionEnabled = true
        });
        var body = MarkdownText(text, foreground, _settings().Appearance.FontSize, FontWeights.Normal, isUser);
        Grid.SetColumn(body, 1);
        row.Children.Add(body);
        return row;
    }

    private Expander ThinkBlock(string text, bool isUser)
    {
        var expander = new Expander
        {
            IsExpanded = _settings().Verbosity == "Expanded",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 4)
        };

        var headerLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        headerLeft.Children.Add(new FontIcon
        {
            Glyph = "\uEA80", // Lightbulb icon
            FontSize = 14,
            Foreground = UiKit.BrushFromHex("#F97316")
        });
        var headerText = UiKit.Text("Thinking Process", 12, FontWeights.SemiBold);
        headerText.Foreground = UiKit.BrushFromHex("#F97316");
        headerLeft.Children.Add(headerText);

        expander.Header = headerLeft;

        var bodyBlock = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = Math.Max(12, _settings().Appearance.FontSize - 1),
            FontFamily = new FontFamily(_settings().Appearance.FontFamily),
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            Foreground = SecondaryTextBrush(),
            IsTextSelectionEnabled = true
        };

        expander.Content = new Border { Padding = new Thickness(0, 8, 0, 0), Child = bodyBlock };
        return expander;
    }

    private Border CodeBlock(string text, bool isUser)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.NoWrap,
            FontSize = 13,
            FontFamily = new FontFamily("Cascadia Mono"),
            Foreground = isUser ? new SolidColorBrush(Colors.White) : PrimaryTextBrush(),
            IsTextSelectionEnabled = true
        };
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = block
        };
        return new Border
        {
            Child = scroll,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(80, 255, 255, 255)) : StrokeBrush(),
            Background = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(35, 255, 255, 255)) : CodeBackgroundBrush()
        };
    }

    private Border MathBlock(string text, bool isUser)
    {
        return new Border
        {
            Child = new TextBlock
            {
                Text = text.Trim(),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                FontFamily = new FontFamily("Cambria Math"),
                Foreground = isUser ? new SolidColorBrush(Colors.White) : PrimaryTextBrush(),
                IsTextSelectionEnabled = true
            },
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(80, 255, 255, 255)) : UiKit.AccentBrush,
            Background = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255)) : AccentSubtleBrush()
        };
    }

    private Border QuoteBlock(string text, bool isUser)
    {
        var block = MarkdownText(text, isUser ? new SolidColorBrush(Colors.White) : PrimaryTextBrush(), _settings().Appearance.FontSize, FontWeights.Normal, isUser);
        return new Border
        {
            Child = block,
            Padding = new Thickness(12, 8, 12, 8),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(120, 255, 255, 255)) : UiKit.AccentBrush,
            Background = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(24, 255, 255, 255)) : SubtleBrush()
        };
    }

    private Border TableBlock(IReadOnlyList<string> tableLines, bool isUser)
    {
        var rows = tableLines.Select(SplitTableRow).Where(row => row.Count > 0).ToList();
        var columnCount = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
        var grid = new Grid();
        for (var column = 0; column < columnCount; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = rows[rowIndex];
            for (var column = 0; column < columnCount; column++)
            {
                var cellText = column < row.Count ? row[column] : string.Empty;
                var block = MarkdownText(cellText, isUser ? new SolidColorBrush(Colors.White) : PrimaryTextBrush(), Math.Max(12, _settings().Appearance.FontSize - 1), rowIndex == 0 ? FontWeights.SemiBold : FontWeights.Normal, isUser);
                block.MaxWidth = 300;

                var cell = new Border
                {
                    Padding = new Thickness(10, 8, 10, 8),
                    BorderThickness = new Thickness(column == 0 ? 0 : 1, rowIndex == 0 ? 0 : 1, 0, 0),
                    BorderBrush = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(70, 255, 255, 255)) : StrokeBrush(),
                    Background = rowIndex == 0
                        ? isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(32, 255, 255, 255)) : SubtleBrush()
                        : new SolidColorBrush(Colors.Transparent),
                    Child = block
                };
                Grid.SetRow(cell, rowIndex);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = grid
        };

        return new Border
        {
            Child = scroll,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(80, 255, 255, 255)) : StrokeBrush(),
            Background = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255)) : SurfaceBrush()
        };
    }

    private static bool TryBullet(string line, out string text)
    {
        foreach (var marker in new[] { "- ", "* ", "\u2022 ", "\u2013 ", "\u2014 " })
        {
            if (line.StartsWith(marker, StringComparison.Ordinal))
            {
                text = line[marker.Length..].TrimStart();
                return text.Length > 0;
            }
        }

        text = string.Empty;
        return false;
    }

    private static bool TryHeading(string line, out string text, out int level)
    {
        text = string.Empty;
        level = 0;
        var count = 0;
        while (count < line.Length && count < 6 && line[count] == '#')
        {
            count++;
        }

        if (count == 0)
        {
            return false;
        }

        var rest = line[count..].Trim();
        if (rest.Length == 0)
        {
            return false;
        }

        text = rest.TrimEnd('#').Trim();
        level = count;
        return text.Length > 0;
    }

    private static bool TryNumbered(string line, out string number, out string text)
    {
        number = string.Empty;
        text = string.Empty;
        var dot = line.IndexOfAny(new[] { '.', ')' });
        if (dot is <= 0 or > 3)
        {
            return false;
        }

        if (!line[..dot].All(char.IsDigit) || dot + 1 >= line.Length || line[dot + 1] != ' ')
        {
            return false;
        }

        number = line[..(dot + 1)];
        text = line[(dot + 2)..];
        return true;
    }

    private static bool IsTableLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Contains('|') && trimmed.Count(character => character == '|') >= 2;
    }

    private static bool IsTableSeparator(string line)
    {
        if (!IsTableLine(line))
        {
            return false;
        }

        var cells = SplitTableRow(line);
        return cells.Count > 0 && cells.All(cell => cell.Length > 0 && cell.All(character => character is '-' or ':' or ' '));
    }

    private static List<string> SplitTableRow(string line) =>
        line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToList();

    private static void AddMarkdownInlines(TextBlock block, string text, Brush foreground, Windows.UI.Text.FontWeight baseWeight)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (TryReadDelimited(text, index, "`", out var code, out var codeEnd))
            {
                block.Inlines.Add(new Run
                {
                    Text = code,
                    Foreground = foreground,
                    FontFamily = new FontFamily("Cascadia Mono"),
                    FontSize = Math.Max(11, block.FontSize - 1),
                    FontWeight = baseWeight
                });
                index = codeEnd;
                continue;
            }

            if (TryReadDelimited(text, index, "**", out var bold, out var boldEnd) ||
                TryReadDelimited(text, index, "__", out bold, out boldEnd))
            {
                block.Inlines.Add(new Run
                {
                    Text = bold,
                    Foreground = foreground,
                    FontWeight = FontWeights.SemiBold
                });
                index = boldEnd;
                continue;
            }

            if (TryReadLink(text, index, out var label, out var url, out var linkEnd))
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    var hyperlink = new Hyperlink { NavigateUri = uri, Foreground = UiKit.AccentBrush };
                    hyperlink.Inlines.Add(new Run { Text = label });
                    block.Inlines.Add(hyperlink);
                }
                else
                {
                    block.Inlines.Add(new Run { Text = $"{label} ({url})", Foreground = foreground });
                }

                index = linkEnd;
                continue;
            }

            if (TryReadInlineMath(text, index, out var math, out var mathEnd))
            {
                block.Inlines.Add(new Run
                {
                    Text = math,
                    Foreground = foreground,
                    FontFamily = new FontFamily("Cambria Math"),
                    FontWeight = baseWeight
                });
                index = mathEnd;
                continue;
            }

            if ((text[index] == '*' || text[index] == '_') &&
                (index + 1 >= text.Length || text[index + 1] != text[index]) &&
                TryReadDelimited(text, index, text[index].ToString(), out var italic, out var italicEnd))
            {
                block.Inlines.Add(new Run
                {
                    Text = italic,
                    Foreground = foreground,
                    FontStyle = Windows.UI.Text.FontStyle.Italic,
                    FontWeight = baseWeight
                });
                index = italicEnd;
                continue;
            }

            var next = NextMarkdownMarker(text, index);
            block.Inlines.Add(new Run
            {
                Text = text[index..next],
                Foreground = foreground,
                FontWeight = baseWeight
            });
            index = next;
        }
    }

    private static bool TryReadDelimited(string text, int start, string marker, out string value, out int end)
    {
        value = string.Empty;
        end = start;
        if (!text.AsSpan(start).StartsWith(marker, StringComparison.Ordinal))
        {
            return false;
        }

        var close = text.IndexOf(marker, start + marker.Length, StringComparison.Ordinal);
        if (close < 0)
        {
            return false;
        }

        value = text[(start + marker.Length)..close];
        end = close + marker.Length;
        return value.Length > 0;
    }

    private static bool TryReadLink(string text, int start, out string label, out string url, out int end)
    {
        label = string.Empty;
        url = string.Empty;
        end = start;
        if (start >= text.Length || text[start] != '[')
        {
            return false;
        }

        var labelEnd = text.IndexOf("](", start, StringComparison.Ordinal);
        if (labelEnd < 0)
        {
            return false;
        }

        var urlEnd = text.IndexOf(')', labelEnd + 2);
        if (urlEnd < 0)
        {
            return false;
        }

        label = text[(start + 1)..labelEnd];
        url = text[(labelEnd + 2)..urlEnd];
        end = urlEnd + 1;
        return !string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(url);
    }

    private static bool TryReadInlineMath(string text, int start, out string value, out int end)
    {
        value = string.Empty;
        end = start;
        if (text.AsSpan(start).StartsWith("\\(", StringComparison.Ordinal))
        {
            var close = text.IndexOf("\\)", start + 2, StringComparison.Ordinal);
            if (close > start)
            {
                value = text[(start + 2)..close];
                end = close + 2;
                return value.Length > 0;
            }
        }

        if (text[start] == '$' && start + 1 < text.Length && !char.IsWhiteSpace(text[start + 1]))
        {
            var close = text.IndexOf('$', start + 1);
            if (close > start + 1)
            {
                value = text[(start + 1)..close];
                end = close + 1;
                return value.Length > 0;
            }
        }

        return false;
    }

    private static int NextMarkdownMarker(string text, int start)
    {
        var next = text.Length;
        foreach (var marker in new[] { "`", "**", "__", "[", "\\(", "$", "*", "_" })
        {
            var found = text.IndexOf(marker, start + 1, StringComparison.Ordinal);
            if (found >= 0)
            {
                next = Math.Min(next, found);
            }
        }

        return next;
    }

    private bool IsDarkMode
    {
        get
        {
            if (_settings().Appearance.Theme.Equals("Dark", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (ActualTheme == ElementTheme.Dark)
            {
                return true;
            }

            return XamlRoot?.Content is FrameworkElement root && root.ActualTheme == ElementTheme.Dark;
        }
    }

    private Brush PrimaryTextBrush() => IsDarkMode ? UiKit.BrushFromHex("#F8FAFC") : ResourceBrush("TextFillColorPrimaryBrush", "#111827");

    private Brush SecondaryTextBrush() => IsDarkMode ? UiKit.BrushFromHex("#CBD5E1") : ResourceBrush("TextFillColorSecondaryBrush", "#6B7280");

    private static SolidColorBrush ChatBackgroundBrush() => new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private Brush SurfaceBrush() => IsDarkMode ? UiKit.BrushFromHex("#B8252B36") : ResourceBrush("LayerFillColorDefaultBrush", "#B8FFFFFF");

    private Brush ComposerBackgroundBrush() => ResourceBrush("LayerFillColorDefaultBrush", IsDarkMode ? "#10151C" : "#F8FAFC");

    private Brush AssistantBubbleBrush() => ResourceBrush("CardBackgroundFillColorSecondaryBrush", IsDarkMode ? "#252B36" : "#FFFFFF");

    private Brush UserBubbleBrush() => ResourceBrush("AccentFillColorDefaultBrush", UiKit.AccentBrush.Color.ToString());

    private Brush UserBubbleBorderBrush() => ResourceBrush("AccentFillColorDefaultBrush", UiKit.AccentBrush.Color.ToString());

    private Brush ControlSurfaceBrush() => ResourceBrush("ControlFillColorDefaultBrush", IsDarkMode ? "#303746" : "#FFFFFF");

    private Brush StrokeBrush() => ResourceBrush("CardStrokeColorDefaultBrush", IsDarkMode ? "#1AFFFFFF" : "#1A000000");

    private Brush SubtleBrush() => ResourceBrush("SubtleFillColorSecondaryBrush", IsDarkMode ? "#0AFFFFFF" : "#0A000000");

    private Brush CodeBackgroundBrush() => ResourceBrush("SolidBackgroundFillColorBaseBrush", IsDarkMode ? "#151A22" : "#F8FAFC");

    private Brush AccentSubtleBrush() => ResourceBrush("SystemControlTransparentBrush", IsDarkMode ? "#40172554" : "#40EFF6FF");

    private Brush ErrorSurfaceBrush() => ResourceBrush("SystemFillColorCriticalBackgroundBrush", IsDarkMode ? "#3B241C" : "#FEF3C7");

    private static Brush ResourceBrush(string key, string fallback)
    {
        return UiKit.BrushFromHex(fallback);
    }

    private static bool TryAddMarkdownImage(StackPanel panel, string line)
    {
        if (!line.StartsWith("![", StringComparison.Ordinal))
        {
            return false;
        }

        var closeAlt = line.IndexOf("](", StringComparison.Ordinal);
        if (closeAlt < 2 || !line.EndsWith(')'))
        {
            return false;
        }

        var alt = line[2..closeAlt];
        var target = line[(closeAlt + 2)..^1];
        try
        {
            var uri = Uri.TryCreate(target, UriKind.Absolute, out var parsed)
                ? parsed
                : File.Exists(target) ? new Uri(target) : null;
            if (uri is null)
            {
                return false;
            }

            var image = new Image
            {
                Source = new BitmapImage(uri),
                MaxHeight = 260,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(image, string.IsNullOrWhiteSpace(alt) ? "Attached image" : alt);
            panel.Children.Add(image);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private Border Avatar(string? modelName)
    {
        var border = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(19), // Circular avatars look incredibly modern and premium!
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        if (string.IsNullOrWhiteSpace(modelName))
        {
            // Default GhostClaw logo avatar using the actual image icon
            border.Background = new SolidColorBrush(Colors.Transparent);
            border.Child = new Image
            {
                Source = new BitmapImage(new Uri("ms-appx:///Assets/GhostClawUI.Icon.png")),
                Width = 28,
                Height = 28,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            return border;
        }

        var brand = GetBrandInfo(modelName, out var domain, out var glyph, out var color, out var bg);
        border.Background = GetNativeBrandBackground(brand);
        border.Child = GetNativeBrandLogoElement(brand, fontSize: 16);

        // Async load original brand logo for chat bubble avatar only for non-standard default brands
        if (brand == "default")
        {
            var avatarImg = new Image
            {
                Width = 30,
                Height = 30,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var logoUrl = $"https://logo.clearbit.com/{domain}?size=128";
            _ = LoadAvatarLogoAsync(logoUrl, avatarImg, border, modelName);
        }

        return border;
    }

    private static SolidColorBrush GetNativeBrandBackground(string brand)
    {
        switch (brand)
        {
            case "openai":
                return UiKit.BrushFromHex("#E6F6F2");
            case "deepseek":
                return UiKit.BrushFromHex("#EEF1FF");
            case "anthropic":
                return UiKit.BrushFromHex("#FAF6F0");
            case "google":
                return UiKit.BrushFromHex("#E8F0FE");
            case "gemma":
                return UiKit.BrushFromHex("#EDE9FE");
            case "kimi":
                return UiKit.BrushFromHex("#E6FAF6");
            case "meta":
                return UiKit.BrushFromHex("#ECF3FC");
            case "mistralai":
                return UiKit.BrushFromHex("#FFF3EC");
            case "minimax":
                return UiKit.BrushFromHex("#FFEBEB");
            case "qwen":
                return UiKit.BrushFromHex("#CCFBF1");
            case "solar":
                return UiKit.BrushFromHex("#FEF9C3");
            case "nvidia":
                return UiKit.BrushFromHex("#F0FDF4");
            case "zhipu":
                return UiKit.BrushFromHex("#EFF6FF");
            case "xiaomi":
                return UiKit.BrushFromHex("#FFF0E6");
            default:
                return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 241, 245, 249)); // light slate fallback
        }
    }

    private static UIElement GetNativeBrandLogoElement(string brand, double fontSize = 12)
    {
        string? pathData = null;

        switch (brand)
        {
            case "openai":
                pathData = "M19.61 9.29a4.8 4.8 0 0 0-.28-3.79 4.9 4.9 0 0 0-3.07-2.53 4.93 4.93 0 0 0-4.88.75 4.8 4.8 0 0 0-3.32-.4 4.9 4.9 0 0 0-2.91 2.7 4.93 4.93 0 0 0-.82 4.87 4.8 4.8 0 0 0-.28 3.79 4.9 4.9 0 0 0 3.07 2.53 4.93 4.93 0 0 0 4.88-.75 4.8 4.8 0 0 0 3.32.4 4.9 4.9 0 0 0 2.91-2.7 4.93 4.93 0 0 0 .82-4.87zM11.53 4.7a3.46 3.46 0 0 1 2.65-.12 3.44 3.44 0 0 1 2.05 1.9 3.46 3.46 0 0 1-.58 3.51 3.47 3.47 0 0 1-2.65.12c-.52-.19-.9-.57-1.09-1.09a3.4 3.4 0 0 1-.33-2.32c.11-.79.52-1.5 1.1-2zM4.9 9.68a3.46 3.46 0 0 1 .58-3.51 3.44 3.44 0 0 1 2.65-.12c.79.29 1.43.9 1.76 1.69a3.45 3.45 0 0 1-.33 3.41 3.46 3.46 0 0 1-2.65.12c-.79-.29-1.43-.9-1.76-1.69a3.43 3.43 0 0 1 .33-3.41zm2.63 7.82a3.46 3.46 0 0 1-2.65.12 3.44 3.44 0 0 1-2.05-1.9 3.46 3.46 0 0 1 .58-3.51 3.47 3.47 0 0 1 2.65-.12c.52.19.9.57 1.09 1.09a3.4 3.4 0 0 1 .33 2.32 3.43 3.43 0 0 1-1.1 2zM12.47 19.3a3.46 3.46 0 0 1-2.65.12 3.44 3.44 0 0 1-2.05-1.9 3.46 3.46 0 0 1 .58-3.51 3.47 3.47 0 0 1 2.65-.12c.52.19.9.57 1.09 1.09a3.4 3.4 0 0 1 .33 2.32 3.43 3.43 0 0 1-1.1 2zm6.63-4.98a3.46 3.46 0 0 1-.58 3.51 3.44 3.44 0 0 1-2.65.12c-.79-.29-1.43-.9-1.76-1.69a3.45 3.45 0 0 1 .33-3.41 3.46 3.46 0 0 1 2.65-.12c.79.29 1.43.9 1.76 1.69a3.43 3.43 0 0 1-.33 3.41zm-2.63-7.82a3.46 3.46 0 0 1 2.65-.12 3.44 3.44 0 0 1 2.05 1.9 3.46 3.46 0 0 1-.58 3.51 3.47 3.47 0 0 1-2.65.12c-.52-.19-.9-.57-1.09-1.09a3.4 3.4 0 0 1-.33-2.32 3.43 3.43 0 0 1 1.1-2z";
                break;
            case "gemma":
            case "google":
                pathData = "M12 2c0 5.52-4.48 10-10 10 5.52 0 10 4.48 10 10 0-5.52 4.48-10 10-10-5.52 0-10-4.48-10-10z";
                break;
            case "kimi":
                pathData = "M21.846 0a1.923 1.923 0 110 3.846H20.15a.226.226 0 01-.227-.226V1.923C19.923.861 20.784 0 21.846 0z M11.065 11.199l7.257-7.2c.137-.136.06-.41-.116-.41H14.3a.164.164 0 00-.117.051l-7.82 7.756c-.122.12-.302.013-.302-.179V3.82c0-.127-.083-.23-.185-.23H3.186c-.103 0-.186.103-.186.23V19.77c0 .128.083.23.186.23h2.69c.103 0 .186-.102.186-.23v-3.25c0-.069.025-.135.069-.178l2.424-2.406a.158.158 0 01.205-.023l6.484 4.772a7.677 7.677 0 003.453 1.283c.108.012.2-.095.2-.23v-3.06c0-.117-.07-.212-.164-.227a5.028 5.028 0 01-2.027-.807l-5.613-4.064c-.117-.078-.132-.279-.028-.381z";
                break;
            case "meta":
                pathData = "M16.5 6c-1.2 0-2.3.4-3.2 1.3L12 8.5 10.7 7.3c-.9-.9-2-1.3-3.2-1.3C5 6 3 8 3 10.5S5 15 7.5 15c1.2 0 2.3-.4 3.2-1.3l1.3-1.2 1.3 1.2c.9.9 2 1.3 3.2 1.3 2.5 0 4.5-2 4.5-4.5S20 6 16.5 6zm-9 6.8c-1.3 0-2.3-1-2.3-2.3S6.2 8.2 7.5 8.2c.6 0 1.2.3 1.6.7l1.7 1.6-1.7 1.6c-.4.4-1 .7-1.6.7zm9 0c-.6 0-1.2-.3-1.6-.7l-1.7-1.6 1.7-1.6c.4-.4 1-.7 1.6-.7 1.3 0 2.3 1 2.3 2.3s-1 2.3-2.3 2.3z";
                break;
            case "mistralai":
                pathData = "M3 4l9 7 9-7v16l-9-7-9 7z";
                break;
            case "deepseek":
                pathData = "M23.748 4.482c-.254-.124-.364.113-.512.234-.051.039-.094.09-.137.136-.372.397-.806.657-1.373.626-.829-.046-1.537.214-2.163.848-.133-.782-.575-1.248-1.247-1.548-.352-.156-.708-.311-.955-.65-.172-.241-.219-.51-.305-.774-.055-.16-.11-.323-.293-.35-.2-.031-.278.136-.356.276-.313.572-.434 1.202-.422 1.84.027 1.436.633 2.58 1.838 3.393.137.093.172.187.129.323-.082.28-.18.552-.266.833-.055.179-.137.217-.329.14a5.526 5.526 0 01-1.736-1.18c-.857-.828-1.631-1.742-2.597-2.458a11.365 11.365 0 00-.689-.471c-.985-.957.13-1.743.388-1.836.27-.098.093-.432-.779-.428-.872.004-1.67.295-2.687.684a3.055 3.055 0 01-.465.137 9.597 9.597 0 00-2.883-.102c-1.885.21-3.39 1.102-4.497 2.623C.082 8.606-.231 10.684.152 12.85c.403 2.284 1.569 4.175 3.36 5.653 1.858 1.533 3.997 2.284 6.438 2.14 1.482-.085 3.133-.284 4.994-1.86.47.234.962.327 1.78.397.63.059 1.236-.03 1.705-.128.735-.156.684-.837.419-.961-2.155-1.004-1.682-.595-2.113-.926 1.096-1.296 2.746-2.642 3.392-7.003.05-.347.007-.565 0-.845-.004-.17.035-.237.23-.256a4.173 4.173 0 001.545-.475c1.396-.763 1.96-2.015 2.093-3.517.02-.23-.004-.467-.247-.588zM11.581 18c-2.089-1.642-3.102-2.183-3.52-2.16-.392.024-.321.471-.235.763.09.288.207.486.371.739.114.167.192.416-.113.603-.673.416-1.842-.14-1.897-.167-1.361-.802-2.5-1.86-3.301-3.307-.774-1.393-1.224-2.887-1.298-4.482-.02-.386.093-.522.477-.592a4.696 4.696 0 011.529-.039c2.132.312 3.946 1.265 5.468 2.774.868.86 1.525 1.887 2.202 2.891.72 1.066 1.494 2.082 2.48 2.914.348.292.625.514.891.677-.802.09-2.14.11-3.054-.614zm1-6.44a.306.306 0 01.415-.287.302.302 0 01.2.288.306.306 0 01-.31.307.303.303 0 01-.304-.308zm3.11 1.596c-.2.081-.399.151-.59.16a1.245 1.245 0 01-.798-.254c-.274-.23-.47-.358-.552-.758a1.73 1.73 0 01.016-.588c.07-.327-.008-.537-.239-.727-.187-.156-.426-.199-.688-.199a.559.559 0 01-.254-.078c-.11-.054-.2-.19-.114-.358.028-.054.16-.186.192-.21.356-.202.767-.136 1.146.016.352.144.618.408 1.001.782.391.451.462.576.685.914.176.265.336.537.445.848.067.195-.019.354-.25.452z";
                break;
            case "nvidia":
                pathData = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15.5c-3.03 0-5.5-2.47-5.5-5.5S10.47 6.5 13.5 6.5 19 8.97 19 12s-2.47 5.5-5.5 5.5zm0-9c-1.93 0-3.5 1.57-3.5 3.5s1.57 3.5 3.5 3.5 3.5-1.57 3.5-3.5-1.57-3.5-3.5-3.5z";
                break;
            case "solar":
                pathData = "M12 6.5c-3.03 0-5.5 2.47-5.5 5.5s2.47 5.5 5.5 5.5 5.5-2.47 5.5-5.5-2.47-5.5-5.5-5.5z";
                break;
            case "qwen":
                pathData = "M12 2C6.48 2 2 6.48 2 12c0 2.2.71 4.21 1.9 5.85L2.1 21.9l4.05-1.8c1.64 1.19 3.65 1.9 5.85 1.9 5.52 0 10-4.48 10-10S17.52 2 12 2zm0 15c-2.76 0-5-2.24-5-5s2.24-5 5-5 5 2.24 5 5-2.24 5-5 5z";
                break;
        }

        if (pathData != null)
        {
            try
            {
                var geometry = (Microsoft.UI.Xaml.Media.Geometry)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    $"<Geometry xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">{pathData}</Geometry>"
                );

                string brandHex = brand switch
                {
                    "openai" => "#10A37F",
                    "deepseek" => "#005BFF",
                    "anthropic" => "#D97752",
                    "google" => "#4285F4",
                    "gemma" => "#4285F4",
                    "kimi" => "#222222",
                    "meta" => "#0668E1",
                    "mistralai" => "#F97316",
                    "minimax" => "#E11D48",
                    "qwen" => "#6366F1",
                    "solar" => "#EAB308",
                    "nvidia" => "#76B900",
                    "zhipu" => "#3B82F6",
                    "xiaomi" => "#FF6700",
                    _ => "#475569"
                };

                return new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = geometry,
                    Fill = UiKit.BrushFromHex(brandHex),
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                    Width = fontSize,
                    Height = fontSize,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            catch
            {
                // Fallback on parser exception
            }
        }

        // Beautiful default Fluent FontIcon fallback
        string glyph = brand switch
        {
            "openai" => "\uE9F9",
            "deepseek" => "\uE9D2",
            "anthropic" => "\uE9F9",
            "google" => "\uE8D6",
            "gemma" => "\uE8D6",
            "kimi" => "\uE9F9",
            "meta" => "\uE947",
            "mistralai" => "\uE7E7",
            "minimax" => "\uE9E9",
            "qwen" => "\uEA0B",
            "solar" => "\uE706",
            "nvidia" => "\uE781",
            "zhipu" => "\uE7C9",
            _ => "\uE9F9"
        };

        return new FontIcon
        {
            Glyph = glyph,
            FontSize = fontSize - 3 > 6 ? fontSize - 3 : 6,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private async Task LoadBrandLogoAsync(string url, Image logoImage, Border logoContainer, string fallbackGlyph, string fallbackColor, string fallbackBg)
    {
        try
        {
            var bytes = await _logoHttpClient.GetByteArrayAsync(url).ConfigureAwait(false);

            logoContainer.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(bytes);
                        await writer.StoreAsync();
                        await writer.FlushAsync();
                    }

                    stream.Seek(0);

                    var prefix = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 128)).TrimStart();
                    var isSvg = prefix.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
                                 prefix.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
                                 prefix.Contains("<svg", StringComparison.OrdinalIgnoreCase);

                    if (isSvg)
                    {
                        var svgImage = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource();
                        svgImage.RasterizePixelWidth = logoImage.Width > 0 ? logoImage.Width * 2 : 128;
                        svgImage.RasterizePixelHeight = logoImage.Height > 0 ? logoImage.Height * 2 : 128;
                        logoImage.Source = svgImage;
                        logoContainer.Child = logoImage;
                        await svgImage.SetSourceAsync(stream);
                    }
                    else
                    {
                        var bitmapImage = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        bitmapImage.DecodePixelWidth = 128;
                        bitmapImage.DecodePixelHeight = 128;
                        bitmapImage.DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical;
                        logoImage.Source = bitmapImage;
                        logoContainer.Child = logoImage;
                        await bitmapImage.SetSourceAsync(stream);
                    }

                    logoContainer.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    logoContainer.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                }
                catch
                {
                    SetFallbackBrandIcon(logoContainer, fallbackGlyph, fallbackColor, fallbackBg);
                }
            });
        }
        catch
        {
            logoContainer.DispatcherQueue.TryEnqueue(() =>
            {
                SetFallbackBrandIcon(logoContainer, fallbackGlyph, fallbackColor, fallbackBg);
            });
        }
    }

    private static void SetFallbackBrandIcon(Border logoContainer, string glyph, string color, string bg)
    {
        logoContainer.BorderBrush = UiKit.BrushFromHex(color);
        logoContainer.Background = UiKit.BrushFromHex(bg);
        logoContainer.Child = new FontIcon
        {
            Glyph = glyph,
            FontSize = 11,
            Foreground = UiKit.BrushFromHex(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static async Task LoadAvatarLogoAsync(string url, Image logoImage, Border logoContainer, string modelName)
    {
        try
        {
            var bytes = await _logoHttpClient.GetByteArrayAsync(url).ConfigureAwait(false);

            logoContainer.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(bytes);
                        await writer.StoreAsync();
                        await writer.FlushAsync();
                    }

                    stream.Seek(0);

                    var prefix = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 128)).TrimStart();
                    var isSvg = prefix.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
                                 prefix.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
                                 prefix.Contains("<svg", StringComparison.OrdinalIgnoreCase);

                    if (isSvg)
                    {
                        var svgImage = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource();
                        svgImage.RasterizePixelWidth = logoImage.Width > 0 ? logoImage.Width * 2 : 128;
                        svgImage.RasterizePixelHeight = logoImage.Height > 0 ? logoImage.Height * 2 : 128;
                        logoImage.Source = svgImage;
                        logoContainer.Child = logoImage;
                        await svgImage.SetSourceAsync(stream);
                    }
                    else
                    {
                        var bitmapImage = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        bitmapImage.DecodePixelWidth = 128;
                        bitmapImage.DecodePixelHeight = 128;
                        bitmapImage.DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical;
                        logoImage.Source = bitmapImage;
                        logoContainer.Child = logoImage;
                        await bitmapImage.SetSourceAsync(stream);
                    }

                    logoContainer.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    logoContainer.BorderThickness = new Thickness(0);
                }
                catch
                {
                }
            });
        }
        catch
        {
        }
    }

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
    private async void OnComposerPaste(object sender, TextControlPasteEventArgs e)
    {
        var dataPackageView = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();

        if (dataPackageView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            try
            {
                var items = await dataPackageView.GetStorageItemsAsync();
                var files = items.OfType<StorageFile>().ToList();
                if (files.Count > 0)
                {
                    e.Handled = true;
                    await ProcessStorageFilesAsync(files).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _notice("Paste failed", ex.Message, InfoBarSeverity.Error);
            }
        }
        else if (dataPackageView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
        {
            try
            {
                e.Handled = true;
                var bitmapStreamRef = await dataPackageView.GetBitmapAsync();
                using var stream = await bitmapStreamRef.OpenReadAsync();

                var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pasted_image_{Guid.NewGuid():N}.png");
                using (var fileStream = System.IO.File.Create(tempPath))
                {
                    using var classicStream = stream.AsStreamForRead();
                    await classicStream.CopyToAsync(fileStream).ConfigureAwait(false);
                }

                var file = await StorageFile.GetFileFromPathAsync(tempPath);
                await ProcessStorageFilesAsync(new List<StorageFile> { file }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _notice("Image paste failed", ex.Message, InfoBarSeverity.Error);
            }
        }
    }

    private async Task ProcessStorageFilesAsync(IReadOnlyList<StorageFile> files)
    {
        if (files.Count == 0) return;
        var maxCharacters = GetModelMaxCharacterLimit();

        // Synchronously add all file names to uploading list first to avoid race conditions
        foreach (var file in files)
        {
            _uploadingFiles[file.Name] = new CancellationTokenSource();
        }
        RenderAttachmentTray();

        // Process files in parallel
        var tasks = files.Select(async file =>
        {
            ChatAttachment? attachment = null;
            try
            {
                var properties = await file.GetBasicPropertiesAsync();
                var size = (long)properties.Size;

                // Get the cancellation token for this file
                var token = _uploadingFiles.TryGetValue(file.Name, out var cts) ? cts.Token : CancellationToken.None;

                // Read text preview and base64 data URI in parallel on background threads
                var filePath = file.Path ?? file.Name;
                var previewTask = FileTextExtractor.ReadTextPreviewAsync(filePath, size, maxCharacters);
                var contentType = FileTextExtractor.BuildContentType(filePath);
                var processedImageTask = ProcessImageFileAsync(file, size, contentType);

                await Task.WhenAll(previewTask, processedImageTask).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                var processed = await processedImageTask.ConfigureAwait(false);
                var finalPath = string.IsNullOrWhiteSpace(processed.path) ? (file.Path ?? string.Empty) : processed.path;
                var finalContentType = processed.contentType ?? contentType;

                attachment = new ChatAttachment(
                    file.Name,
                    finalPath,
                    finalContentType,
                    size,
                    await previewTask.ConfigureAwait(false),
                    null);
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _notice("File processing failed", $"{file.Name}: {ex.Message}", InfoBarSeverity.Error);
                });
            }
            finally
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _uploadingFiles.Remove(file.Name);
                    if (attachment != null)
                    {
                        _attachments.Add(attachment);
                    }
                    RenderAttachmentTray();
                });
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task AttachFilesAsync()
    {
        try
        {
            var maxCharacters = GetModelMaxCharacterLimit();
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List
            };
            foreach (var filter in new[] { ".txt", ".md", ".json", ".csv", ".log", ".xml", ".yaml", ".yml", ".cs", ".js", ".ts", ".py", ".png", ".jpg", ".jpeg", ".webp", ".pdf", ".docx", ".doc", ".pptx", ".ppt", ".xlsx", ".xls" })
            {
                picker.FileTypeFilter.Add(filter);
            }

            InitializeWithWindow.Initialize(picker, _hwnd);
            var files = await picker.PickMultipleFilesAsync();
            if (files != null && files.Count > 0)
            {
                await ProcessStorageFilesAsync(files).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _notice("Failed to attach files", ex.Message, InfoBarSeverity.Error);
        }
    }

    private static string CleanMarkdownForClipboard(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return string.Empty;

        // Remove code blocks
        var text = System.Text.RegularExpressions.Regex.Replace(markdown, @"```[a-zA-Z0-9]*\n([\s\S]*?)```", "$1");

        // Remove ticks
        text = System.Text.RegularExpressions.Regex.Replace(text, @"`([^`]+)`", "$1");

        // Remove bold/italic markers
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*([^*]+)\*\*", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*([^*]+)\*", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"__([^_]+)__", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"_([^_]+)_", "$1");

        // Remove headers
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^\s*#{1,6}\s+(.+)$", "$1", System.Text.RegularExpressions.RegexOptions.Multiline);

        // Remove LaTeX math
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\\\[([\s\S]*?)\\\]", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\\\(([\s\S]*?)\\\)", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\$\$([\s\S]*?)\$\$", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\$([^$]+)\$", "$1");

        // Remove links
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\(([^)]+)\)", "$1 ($2)");

        // Remove table formatting lines
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^\s*\|.*\|[ \t]*$", "", System.Text.RegularExpressions.RegexOptions.Multiline);

        return text.Trim();
    }

    private static bool IsTextLike(StorageFile file)
    {
        var ext = Path.GetExtension(file.Name);
        return file.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
               new[] { ".txt", ".md", ".json", ".csv", ".log", ".xml", ".yaml", ".yml", ".cs", ".js", ".ts", ".py", ".ini", ".conf", ".sql", ".html", ".css", ".bat", ".ps1", ".sh" }
                   .Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildContentType(StorageFile file)
    {
        var ext = Path.GetExtension(file.Name).ToLowerInvariant();
        var mime = ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".csv" => "text/csv",
            ".xml" => "application/xml",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => null
        };

        if (mime != null) return mime;
        if (!string.IsNullOrWhiteSpace(file.ContentType) && !file.ContentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return file.ContentType;
        }
        return "application/octet-stream";
    }

    private static async Task<(string? path, string? contentType)> ProcessImageFileAsync(StorageFile file, long size, string contentType)
    {
        // Bypass expensive resizing to provide instant attachment feedback.
        return await Task.FromResult((file.Path, contentType)).ConfigureAwait(false);
    }

    private static async Task<string> ResizeImageForProviderAsync(string filePath)
    {
        try
        {
            var fileInfo = new System.IO.FileInfo(filePath);
            var isOversizedFile = fileInfo.Length > 20 * 1024 * 1024; // > 20 MB

            var bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            using var memoryStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var dataWriter = new Windows.Storage.Streams.DataWriter(memoryStream))
            {
                dataWriter.WriteBytes(bytes);
                await dataWriter.StoreAsync();
                await dataWriter.FlushAsync();
                dataWriter.DetachStream();
            }
            memoryStream.Seek(0);

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(memoryStream);

            uint originalWidth = decoder.OrientedPixelWidth;
            uint originalHeight = decoder.OrientedPixelHeight;

            const uint maxDimension = 2048;
            bool needsResize = originalWidth > maxDimension || originalHeight > maxDimension;

            if (!needsResize && !isOversizedFile)
            {
                return filePath;
            }

            double ratio = 1.0;
            if (needsResize)
            {
                ratio = Math.Min((double)maxDimension / originalWidth, (double)maxDimension / originalHeight);
            }

            uint targetWidth = (uint)(originalWidth * ratio);
            uint targetHeight = (uint)(originalHeight * ratio);
            if (targetWidth == 0) targetWidth = 1;
            if (targetHeight == 0) targetHeight = 1;

            var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");

            using (var outputStream = System.IO.File.OpenWrite(tempFile))
            using (var randomAccessStream = outputStream.AsRandomAccessStream())
            {
                var propertySet = new Windows.Graphics.Imaging.BitmapPropertySet
                {
                    { "ImageQuality", new Windows.Graphics.Imaging.BitmapTypedValue(1.0, Windows.Foundation.PropertyType.Single) }
                };

                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, randomAccessStream, propertySet);
                var pixelData = await decoder.GetPixelDataAsync(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    new Windows.Graphics.Imaging.BitmapTransform
                    {
                        ScaledWidth = targetWidth,
                        ScaledHeight = targetHeight,
                        InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant
                    },
                    Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                    Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);

                encoder.SetPixelData(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    targetWidth,
                    targetHeight,
                    decoder.DpiX,
                    decoder.DpiY,
                    pixelData.DetachPixelData());

                await encoder.FlushAsync();
            }
            return tempFile;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Resize failed: {ex}");
            return filePath;
        }
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

    private void RenderAttachmentTray()
    {
        _attachmentTray.Children.Clear();
        var hasAttachments = _attachments.Count > 0 || _uploadingFiles.Count > 0;
        _attachmentTrayScroll.Visibility = hasAttachments ? Visibility.Visible : Visibility.Collapsed;
        _attachmentTray.Visibility = hasAttachments ? Visibility.Visible : Visibility.Collapsed;

        foreach (var name in _uploadingFiles.Keys)
        {
            _attachmentTray.Children.Add(UploadingPreview(name));
        }

        for (var i = 0; i < _attachments.Count; i++)
        {
            var index = i;
            _attachmentTray.Children.Add(AttachmentPreview(_attachments[i], isUser: false, removable: true, () =>
            {
                _attachments.RemoveAt(index);
                RenderAttachmentTray();
            }));
        }

        UpdateSendButtonState();
    }

    private Border UploadingPreview(string filename)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        grid.Children.Add(new ProgressRing
        {
            Width = 14,
            Height = 14,
            IsActive = true,
            VerticalAlignment = VerticalAlignment.Center
        });

        var nameText = UiKit.Text($"Processing {filename}...", 12, FontWeights.SemiBold);
        nameText.Foreground = PrimaryTextBrush();
        nameText.TextTrimming = TextTrimming.CharacterEllipsis;
        nameText.VerticalAlignment = VerticalAlignment.Center;

        Grid.SetColumn(nameText, 1);
        grid.Children.Add(nameText);

        var removeButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
            Width = 24,
            Height = 24,
            MinWidth = 24,
            MinHeight = 24,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = SecondaryTextBrush(),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(removeButton, $"Cancel {filename}");
        removeButton.Click += (_, _) =>
        {
            if (_uploadingFiles.TryGetValue(filename, out var cts))
            {
                cts.Cancel();
            }
        };
        Grid.SetColumn(removeButton, 2);
        grid.Children.Add(removeButton);

        return new Border
        {
            Child = grid,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 0, 6, 0),
            BorderBrush = StrokeBrush(),
            Background = IsDarkMode
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 4, 2)
        };
    }

    private Border AttachmentPreview(ChatAttachment attachment, bool isUser, bool removable, Action? remove = null)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        var isImage = attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        grid.Children.Add(new FontIcon
        {
            Glyph = isImage ? "\uEB9F" : "\uE8A5",
            FontSize = 14,
            Foreground = isUser ? new SolidColorBrush(Colors.White) : UiKit.AccentBrush,
            VerticalAlignment = VerticalAlignment.Center
        });

        var nameText = UiKit.Text(attachment.Name, 12, FontWeights.SemiBold);
        nameText.Foreground = isUser ? new SolidColorBrush(Colors.White) : PrimaryTextBrush();
        nameText.TextTrimming = TextTrimming.CharacterEllipsis;
        nameText.VerticalAlignment = VerticalAlignment.Center;

        var sizeText = UiKit.Text(FormatBytes(attachment.SizeBytes), 11);
        sizeText.Foreground = isUser
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(200, 255, 255, 255))
            : SecondaryTextBrush();
        sizeText.VerticalAlignment = VerticalAlignment.Center;

        var textPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        textPanel.Children.Add(nameText);
        textPanel.Children.Add(sizeText);

        Grid.SetColumn(textPanel, 1);
        grid.Children.Add(textPanel);

        if (removable)
        {
            var removeButton = new Button
            {
                Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
                Width = 24,
                Height = 24,
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = SecondaryTextBrush(),
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(removeButton, $"Remove {attachment.Name}");
            removeButton.Click += (_, _) => remove?.Invoke();
            Grid.SetColumn(removeButton, 2);
            grid.Children.Add(removeButton);
        }
        else if (File.Exists(attachment.Path))
        {
            var downloadBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE896", FontSize = 12 }, // Download/Save icon
                Width = 24,
                Height = 24,
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = isUser ? new SolidColorBrush(Colors.White) : SecondaryTextBrush(),
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(downloadBtn, $"Save {attachment.Name} to Downloads");
            downloadBtn.Click += async (_, _) =>
            {
                try
                {
                    var downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    var destPath = Path.Combine(downloadsPath, attachment.Name);
                    var finalDest = destPath;
                    var counter = 1;
                    while (File.Exists(finalDest))
                    {
                        var ext = Path.GetExtension(destPath);
                        var nameWithoutExt = Path.GetFileNameWithoutExtension(destPath);
                        finalDest = Path.Combine(downloadsPath, $"{nameWithoutExt} ({counter++}){ext}");
                    }
                    File.Copy(attachment.Path, finalDest);
                    _notice("Saved to Downloads", $"File copied to: {Path.GetFileName(finalDest)}", InfoBarSeverity.Success);
                }
                catch (Exception ex)
                {
                    _notice("Download failed", ex.Message, InfoBarSeverity.Error);
                }
            };
            Grid.SetColumn(downloadBtn, 2);
            grid.Children.Add(downloadBtn);
        }

        var border = new Border
        {
            Child = grid,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 0, (removable || File.Exists(attachment.Path)) ? 6 : 12, 0),
            BorderBrush = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(60, 255, 255, 255)) : StrokeBrush(),
            Background = isUser
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255))
                : IsDarkMode
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255))
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 4, 2)
        };

        if (!removable && File.Exists(attachment.Path))
        {
            border.Tapped += (s, e) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(attachment.Path) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    _notice("Could not open file", ex.Message, InfoBarSeverity.Warning);
                }
            };
        }

        if (isImage && !removable && File.Exists(attachment.Path))
        {
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(border);
            panel.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri(attachment.Path)),
                MaxHeight = 160,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left
            });
            return new Border { Child = panel };
        }

        return border;
    }

    private async Task SendAsync()
    {
        if (_isSending)
        {
            try
            {
                _chatCts?.Cancel();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to cancel active generation: {ex.Message}");
            }
            return;
        }
        if (_uploadingFiles.Count > 0)
        {
            _notice("Please wait", "Files are currently uploading/processing. Please wait until they are finished.", InfoBarSeverity.Warning);
            return;
        }

        var text = _composer.Text.Trim();
        var attachments = _attachments.ToList();
        if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
        {
            return;
        }

        var provider = _providers.SelectedItem as ProviderProfile;
        string? model = null;
        if (_models.SelectedItem is ComboBoxItem cbi)
        {
            model = cbi.Tag as string;
        }
        else
        {
            model = _models.SelectedItem as string;
        }

        if (provider is null || string.IsNullOrWhiteSpace(provider.Id))
        {
            _notice("Choose a provider", "Add or select a provider before sending.", InfoBarSeverity.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(model) && _models.Items.Count > 0)
        {
            _models.SelectedIndex = 0;
            if (_models.SelectedItem is ComboBoxItem firstCbi)
            {
                model = firstCbi.Tag as string;
            }
            else
            {
                model = _models.SelectedItem as string;
            }
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            _notice("Choose a model", "Validate the provider or add model names manually.", InfoBarSeverity.Warning);
            return;
        }

        var apiKey = _vault.ReadProviderKey(provider.Id);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _notice("API key missing", $"Add an API key for {provider.Name}, then try again.", InfoBarSeverity.Warning);
            return;
        }

        _composer.Text = "";
        _attachments.Clear();
        RenderAttachmentTray();
        _isSending = true;
        SetBusy(true);
        _chatCts = new CancellationTokenSource();

        var conversationId = _conversationId ?? string.Empty;

        try
        {
            var messageText = string.IsNullOrWhiteSpace(text) ? "Attached file" : text;
            var optimistic = new ChatMessage(Guid.NewGuid().ToString("N"), conversationId, "user", messageText, provider.Id, model, "message", DateTimeOffset.UtcNow, AttachmentMetadata(attachments));
            if (_messages.Children.Count == 1 && _messages.Children[0] is StackPanel)
            {
                _messages.Children.Clear();
            }
            _messages.Children.Add(MessageRow(optimistic));
            await ScrollToEndAsync().ConfigureAwait(false);

            // Build the content to send — prepend skill context if one was injected
            var contentToSend = text;
            if (!string.IsNullOrWhiteSpace(_injectedSkillContext))
            {
                contentToSend = $"<skill_context>\n{_injectedSkillContext}\n</skill_context>\n\n{text}";
                _injectedSkillContext = null;
                _skillBadge.Visibility = Visibility.Collapsed;
            }

            // Pass full resolution attachments as requested by the user
            var processedAttachments = attachments.ToList();

            var request = new ChatSendRequest(conversationId, provider.Id, model, contentToSend, _whisperMode, _settings().Verbosity, processedAttachments, AgentMode: _agentMode);
            StartPollingActiveTraces();
            var result = await _pipe.RequestAsync<ChatSendResult>("chat.send", request, _chatCts.Token).ConfigureAwait(false);
            StopPollingActiveTraces();

            if (result is null)
            {
                throw new InvalidOperationException("No response received from the background agent service.");
            }

            foreach (var trace in result.Trace)
            {
                _messages.Children.Add(Trace(trace));
            }

            _messages.Children.Add(MessageRow(result.AssistantMessage));
            _conversationId = result.AssistantMessage.ConversationId;
            await _conversationChanged(_conversationId).ConfigureAwait(false);
            await ScrollToEndAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                _notice(result.Queued ? "Queued for reconnect" : "Provider error", result.Error, InfoBarSeverity.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            StopPollingActiveTraces();
            _messages.Children.Add(MessageRow(new ChatMessage(Guid.NewGuid().ToString("N"), conversationId, "assistant", "Generation stopped.", provider.Id, model, "error", DateTimeOffset.UtcNow)));
            await ScrollToEndAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StopPollingActiveTraces();
            _messages.Children.Add(MessageRow(new ChatMessage(Guid.NewGuid().ToString("N"), conversationId, "assistant", Explain(ex), provider.Id, model, "error", DateTimeOffset.UtcNow)));
            await ScrollToEndAsync().ConfigureAwait(false);
            _notice("Send failed", Explain(ex), InfoBarSeverity.Error);
        }
        finally
        {
            _isSending = false;
            SetBusy(false);
            _chatCts?.Dispose();
            _chatCts = null;
        }
    }

    private static JsonNode? AttachmentMetadata(IReadOnlyList<ChatAttachment> attachments) =>
        attachments.Count == 0
            ? null
            : new JsonObject { ["attachments"] = JsonSerializer.SerializeToNode(attachments, PipeJson.Options) };

    private static IReadOnlyList<ChatAttachment> ReadAttachments(JsonNode? metadata)
    {
        try
        {
            return metadata?["attachments"]?.Deserialize<IReadOnlyList<ChatAttachment>>(PipeJson.Options) ?? Array.Empty<ChatAttachment>();
        }
        catch
        {
            return Array.Empty<ChatAttachment>();
        }
    }

    private string GetSelectedModelCode()
    {
        try
        {
            string? model = null;
            if (_models.SelectedItem is ComboBoxItem cbi)
            {
                model = cbi.Tag as string;
            }
            else
            {
                model = _models.SelectedItem as string;
            }
            return model ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private int GetModelMaxCharacterLimit()
    {
        try
        {
            var model = GetSelectedModelCode()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(model))
            {
                return 128_000;
            }
            if (model.Contains("gemini-1.5") || model.Contains("gemini-2.0") || model.Contains("gemini-experimental"))
            {
                return 4_000_000; // Gemini: ~1M tokens
            }
            if (model.Contains("claude-3") || model.Contains("claude-3-5"))
            {
                return 800_000; // Claude: ~200k tokens
            }
            if (model.Contains("gpt-4") || model.Contains("gpt-4o") || model.Contains("deepseek") ||
                model.Contains("llama-3.1") || model.Contains("llama-3.2") || model.Contains("mistral-large") || model.Contains("qwen-2.5"))
            {
                return 500_000; // Modern: ~128k tokens
            }
        }
        catch
        {
            // Fallback if UI thread access fails or element is not ready
        }
        return 128_000; // Default/Safe limit
    }

    private void SetBusy(bool busy)
    {
        if (!busy && _isSending)
        {
            return;
        }
        _sending.IsActive = busy;
        _sending.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateSendButtonState();
        _composer.IsEnabled = !busy;
        _providers.IsEnabled = !busy;
        _models.IsEnabled = !busy;
        _toolStatus.Text = busy ? "Thinking" : _whisperMode ? "Whisper" : "Tools ready";
    }


    private static void HookClick(Microsoft.UI.Xaml.Controls.Primitives.ButtonBase btn, RoutedEventHandler handler)
    {
        btn.Click += handler;
        RoutedEventHandler unloaded = null!;
        unloaded = (s, e) =>
        {
            var b = (Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)s;
            b.Click -= handler;
            b.Unloaded -= unloaded;
        };
        btn.Unloaded += unloaded;
    }

    private static void HookPointer(FrameworkElement element, Microsoft.UI.Xaml.Input.PointerEventHandler entered, Microsoft.UI.Xaml.Input.PointerEventHandler exited)
    {
        if (entered != null) element.PointerEntered += entered;
        if (exited != null) element.PointerExited += exited;

        RoutedEventHandler unloaded = null!;
        unloaded = (s, e) =>
        {
            var el = (FrameworkElement)s;
            if (entered != null) el.PointerEntered -= entered;
            if (exited != null) el.PointerExited -= exited;
            el.Unloaded -= unloaded;
        };
        element.Unloaded += unloaded;
    }

    private async Task ScrollToEndAsync()
    {
        await Task.Yield();
        _scroll.ChangeView(null, _scroll.ScrollableHeight, null, disableAnimation: false);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.#} KB";
        }

        return $"{bytes / 1024d / 1024d:0.#} MB";
    }

    private static string Explain(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("Value must be set", StringComparison.OrdinalIgnoreCase))
        {
            return "The provider rejected the request because a required value was missing. Check the API key, base URL, and selected model.";
        }

        if (message.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("operation was cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return "The model request timed out or was cancelled. Test this model in Providers, or choose another chat-capable model.";
        }

        return message;
    }

    private async Task ShowSkillsFlyoutAsync(FrameworkElement anchor)
    {
        try
        {
            var skills = await _pipe.RequestAsync<IReadOnlyList<SkillSummary>>("skills.list").ConfigureAwait(false) ?? Array.Empty<SkillSummary>();
            if (skills.Count == 0)
            {
                _notice("No Skills Found", "No skill files were located. Add *.md skill files to the skills directory.", InfoBarSeverity.Warning);
                return;
            }

            var list = new StackPanel { Spacing = 4, Padding = new Thickness(6) };

            // Header
            var header = UiKit.Text("Inject Skill as Context", 14, FontWeights.SemiBold);
            header.Margin = new Thickness(4, 0, 0, 6);
            list.Children.Add(header);

            var desc = UiKit.Muted("The skill content will be prepended to your next message as guidance context.", 12);
            desc.Margin = new Thickness(4, 0, 0, 8);
            list.Children.Add(desc);

            // Active skill indicator
            if (!string.IsNullOrEmpty(_injectedSkillContext))
            {
                var clearBtn = new Button
                {
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            new FontIcon { Glyph = "\uE711", FontSize = 11 },
                            UiKit.Text("Clear active skill", 12)
                        }
                    },
                    Margin = new Thickness(0, 0, 0, 8),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6, 10, 6),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                clearBtn.Click += (_, _) =>
                {
                    _injectedSkillContext = null;
                    _skillBadge.Visibility = Visibility.Collapsed;
                };
                list.Children.Add(clearBtn);
            }

            // Skill list flyout
            Flyout? flyout = null;
            foreach (var skill in skills)
            {
                var skillName = skill.Name;
                var skillId = skill.Id;
                var row = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto }
                    },
                    ColumnSpacing = 8,
                    Padding = new Thickness(10, 7, 8, 7),
                    CornerRadius = new CornerRadius(6)
                };
                var nameText = UiKit.Text(skillName, 13, FontWeights.SemiBold);
                nameText.TextTrimming = TextTrimming.CharacterEllipsis;
                row.Children.Add(nameText);

                var injectBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE83B", FontSize = 12 }, // Inject icon
                    Width = 28,
                    Height = 28,
                    MinWidth = 28,
                    MinHeight = 28,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(6),
                    Background = UiKit.AccentBrush,
                    Foreground = new SolidColorBrush(Colors.White),
                    BorderThickness = new Thickness(0)
                };
                AutomationProperties.SetName(injectBtn, $"Inject skill {skillName}");
                injectBtn.Click += async (_, _) =>
                {
                    flyout?.Hide();
                    try
                    {
                        var result = await _pipe.RequestAsync<CommandResult>("skills.read", new SimpleIdRequest(skillId)).ConfigureAwait(false);
                        if (result?.Success == true && !string.IsNullOrWhiteSpace(result.Message))
                        {
                            _injectedSkillContext = result.Message;
                            _skillBadge.Text = skillName.Length > 10 ? skillName[..10] + "…" : skillName;
                            _skillBadge.Visibility = Visibility.Visible;
                            _notice("Skill Injected", $"'{skillName}' will guide your next message.", InfoBarSeverity.Success);
                        }
                    }
                    catch (Exception ex)
                    {
                        _notice("Skill load failed", ex.Message, InfoBarSeverity.Error);
                    }
                };
                Grid.SetColumn(injectBtn, 1);
                row.Children.Add(injectBtn);

                // Hover effect
                var rowBorder = new Border
                {
                    Child = row,
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Colors.Transparent)
                };
                rowBorder.PointerEntered += (_, _) => rowBorder.Background = ControlSurfaceBrush();
                rowBorder.PointerExited += (_, _) => rowBorder.Background = new SolidColorBrush(Colors.Transparent);
                ToolTipService.SetToolTip(rowBorder, skill.Description.Length > 120 ? skill.Description[..120] + "…" : skill.Description);
                list.Children.Add(rowBorder);
            }

            var scroll = new ScrollViewer
            {
                Content = list,
                MaxHeight = 380,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            flyout = new Flyout
            {
                Content = scroll,
                Placement = FlyoutPlacementMode.Top
            };
            flyout.ShowAt(anchor);
        }
        catch (Exception ex)
        {
            _notice("Skills unavailable", ex.Message, InfoBarSeverity.Warning);
        }
    }



    private string GetBrandInfo(string modelName, out string domain, out string glyph, out string color, out string bg)
    {
        ModelClassifier.Resolve(modelName, out var brand, out var _);
        var lower = brand.ToLowerInvariant();
        glyph = "\uE9F9"; // default brain/AI

        if (lower.Contains("deepseek"))
        {
            domain = "deepseek.com";
            color = "#4D6BFE";
            bg = "#EEF1FF";
            return "deepseek";
        }
        if (lower.Contains("gpt-") || lower.Contains("o1-") || lower.Contains("o3-") || lower.Contains("openai") || lower.Contains("chatgpt"))
        {
            domain = "openai.com";
            color = "#00A67E";
            bg = "#E6F6F2";
            return "openai";
        }
        if (lower.Contains("claude") || lower.Contains("anthropic"))
        {
            domain = "anthropic.com";
            color = "#CC9966";
            bg = "#FAF6F0";
            return "anthropic";
        }
        if (lower.Contains("gemini") || lower.Contains("google"))
        {
            domain = "google.com";
            color = "#1A73E8";
            bg = "#E8F0FE";
            return "google";
        }
        if (lower.Contains("gemma"))
        {
            domain = "google.com";
            color = "#6366F1";
            bg = "#EDE9FE";
            return "gemma";
        }
        if (lower.Contains("kimi") || lower.Contains("moonshot"))
        {
            domain = "moonshot.cn";
            color = "#00A587";
            bg = "#E6FAF6";
            return "kimi";
        }
        if (lower.Contains("llama") || lower.Contains("meta"))
        {
            domain = "meta.com";
            color = "#044EAB";
            bg = "#ECF3FC";
            return "meta";
        }
        if (lower.Contains("mistral") || lower.Contains("mixtral") || lower.Contains("codestral"))
        {
            domain = "mistral.ai";
            color = "#FD5E08";
            bg = "#FFF3EC";
            return "mistralai";
        }
        if (lower.Contains("minimax"))
        {
            domain = "minimax.com";
            color = "#FF5E5B";
            bg = "#FFEBEB";
            return "minimax";
        }
        if (lower.Contains("qwen"))
        {
            domain = "qwen.ai";
            color = "#0D9488";
            bg = "#CCFBF1";
            return "qwen";
        }
        if (lower.Contains("solar") || lower.Contains("upstage"))
        {
            domain = "upstage.ai";
            color = "#EAB308";
            bg = "#FEF9C3";
            return "solar";
        }
        if (lower.Contains("nvidia"))
        {
            domain = "nvidia.com";
            color = "#76B900";
            bg = "#F0FDF4";
            return "nvidia";
        }
        if (lower.Contains("zhipu") || lower.Contains("glm"))
        {
            domain = "zhipuai.cn";
            color = "#3B82F6";
            bg = "#EFF6FF";
            return "zhipu";
        }
        if (lower.Contains("xiaomi") || lower.Contains("mimo"))
        {
            domain = "xiaomi.com";
            color = "#FF6700";
            bg = "#FFF0E6";
            return "xiaomi";
        }

        domain = "openai.com"; // default fallback for domain logos
        color = "#475569";
        bg = "#F1F5F9";
        return "default";
    }

    private ComboBoxItem CreateModelComboBoxItem(string modelName)
    {
        var parts = modelName.Split(':', 2);
        var code = parts[0].Trim();

        ModelClassifier.Resolve(code, out var brand, out var resolvedFriendlyName);
        var name = parts.Length > 1 ? parts[1].Trim() : resolvedFriendlyName;
        name = ModelClassifier.FormatFriendlyName(name);

        // Build the dropdown list item in a clean 2-column Grid to prevent clipping
        var dropdownGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 450
        };

        var logoContainer = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12), // circular
            Background = GetNativeBrandBackground(brand),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var logoImg = new Image
        {
            Width = 20,
            Height = 20,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        GetBrandInfo(code, out var domain, out var glyph, out var color, out var bg);

        // Pre-fill with themed vector logo
        logoContainer.Child = GetNativeBrandLogoElement(brand, fontSize: 11);

        // Async load original brand logo from web only for non-standard default brands
        if (brand == "default")
        {
            var logoUrl = $"https://logo.clearbit.com/{domain}?size=128";
            _ = LoadBrandLogoAsync(logoUrl, logoImg, logoContainer, glyph, color, bg);
        }

        Grid.SetColumn(logoContainer, 0);
        dropdownGrid.Children.Add(logoContainer);

        var modelText = new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(modelText, 1);
        dropdownGrid.Children.Add(modelText);

        var item = new ComboBoxItem
        {
            Content = dropdownGrid,
            Tag = code,
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MaxWidth = 380
        };
        ToolTipService.SetToolTip(item, parts.Length > 1 ? $"{name} ({code})" : code);
        return item;
    }

    private async Task ExportPdfAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_conversationId))
            {
                _notice("Export Unavailable", "No active conversation to export.", InfoBarSeverity.Warning);
                return;
            }

            var conversation = await _pipe.RequestAsync<ConversationDetail>("conversations.get", new SimpleIdRequest(_conversationId)).ConfigureAwait(false);
            if (conversation is null || conversation.Messages.Count == 0)
            {
                _notice("Export Failed", "Could not retrieve messages for this conversation.", InfoBarSeverity.Error);
                return;
            }

            var savePicker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, _hwnd);
            savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            savePicker.FileTypeChoices.Add("PDF Document", new List<string>() { ".pdf" });
            savePicker.SuggestedFileName = $"{conversation.Summary.Title.Replace(" ", "_")}_Transcript.pdf";

            var file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                try
                {
                    var bubbleImages = new List<(byte[] PngBytes, double Width, double Height)>();
                    foreach (UIElement child in _messages.Children)
                    {
                        if (child.Visibility != Visibility.Visible) continue;

                        var fwE = child as FrameworkElement;
                        if (fwE != null && fwE.ActualHeight == 0) continue;

                        var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                        await rtb.RenderAsync(child);

                        var pixelBuffer = await rtb.GetPixelsAsync();
                        var pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixelBuffer);

                        using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, ms);
                        encoder.SetPixelData(
                            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                            (uint)rtb.PixelWidth,
                            (uint)rtb.PixelHeight,
                            96,
                            96,
                            pixels);
                        await encoder.FlushAsync();

                        using var stream = ms.AsStream();
                        var imageBytes = new byte[stream.Length];
                        stream.Seek(0, System.IO.SeekOrigin.Begin);
                        await stream.ReadExactlyAsync(imageBytes, 0, imageBytes.Length).ConfigureAwait(false);

                        double actualW = fwE != null ? fwE.ActualWidth : rtb.PixelWidth;
                        double actualH = fwE != null ? fwE.ActualHeight : rtb.PixelHeight;

                        bubbleImages.Add((imageBytes, actualW, actualH));
                    }

                    byte[] pdfBytes = PdfExporter.ExportVisualToPdf(conversation.Summary.Title, bubbleImages);
                    await Windows.Storage.FileIO.WriteBytesAsync(file, pdfBytes);
                    _notice("PDF Exported", $"Conversation saved to {file.Name}", InfoBarSeverity.Success);
                }
                catch (Exception)
                {
                    try
                    {
                        await file.DeleteAsync();
                    }
                    catch { /* ignore fallback delete error */ }
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            _notice("Export Failed", ex.Message, InfoBarSeverity.Error);
        }
    }

    private static bool HasGeneratedFiles(JsonNode? metadata)
    {
        if (metadata != null && metadata["attachments"] is JsonArray arr && arr.Count > 0)
        {
            foreach (var node in arr)
            {
                if (node != null && node["Name"]?.ToString() is string name)
                {
                    if (!name.StartsWith("Execution_Error_", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith("Python_Not_Found_", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static string StripFileGenerationCode(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;

        var regex = new System.Text.RegularExpressions.Regex(
            @"`{3}python[ \t]*\r?\n([\s\S]*?)(?:`{3}|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );

        var matches = regex.Matches(content);
        var sb = new StringBuilder(content);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var code = match.Groups[1].Value;
            if (code.Contains(".save(") || code.Contains("open(") || code.Contains("write("))
            {
                sb.Replace(match.Value, "");
            }
        }

        var cleaned = sb.ToString();
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\n{3,}", "\n\n");
        return cleaned.Trim();
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
            _pollingTimerHandler ??= async (_, _) => await PollActiveTracesTickAsync().ConfigureAwait(false);
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
            var active = await _pipe.RequestAsync<ActiveTracesResponse>("chat.activeTraces", new SimpleIdRequest(_conversationId)).ConfigureAwait(false);
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

    private Border? RenderActivePlanCard(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

            var stack = new StackPanel { Spacing = 6 };

            var cardHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 4) };
            cardHeader.Children.Add(new FontIcon { Glyph = "\uE8FD", FontSize = 13, Foreground = UiKit.AccentBrush });
            cardHeader.Children.Add(UiKit.Text("ACTIVE PLAN", 11, FontWeights.Bold));
            stack.Children.Add(cardHeader);

            foreach (var step in root.EnumerateArray())
            {
                var text = step.TryGetProperty("text", out var tProp) ? tProp.GetString() ?? "" : "";
                var state = step.TryGetProperty("state", out var sProp) ? sProp.GetString() ?? "pending" : "pending";

                var stepRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(4, 2, 0, 2) };

                FrameworkElement checkboxIcon;
                if (state == "completed")
                {
                    checkboxIcon = new FontIcon { Glyph = "\uE73E", FontSize = 12, Foreground = UiKit.BrushFromHex("#22C55E") };
                }
                else if (state == "running")
                {
                    checkboxIcon = new ProgressRing { Width = 12, Height = 12, IsActive = true };
                }
                else
                {
                    checkboxIcon = new FontIcon { Glyph = "\uE739", FontSize = 12, Foreground = SecondaryTextBrush() };
                }

                stepRow.Children.Add(checkboxIcon);

                var textBlock = UiKit.Text(text, 12);
                if (state == "completed")
                {
                    textBlock.Foreground = SecondaryTextBrush();
                }
                else if (state == "running")
                {
                    textBlock.FontWeight = FontWeights.SemiBold;
                }
                stepRow.Children.Add(textBlock);

                stack.Children.Add(stepRow);
            }

            return new Border
            {
                Background = ResourceBrush("CardBackgroundFillColorSecondaryBrush", "#F9FAFB"),
                BorderBrush = UiKit.AccentBrush,
                BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(0, 6, 6, 0),
                Margin = new Thickness(0, 0, 0, 4),
                Child = stack
            };
        }
        catch
        {
            return new Border
            {
                Background = ResourceBrush("CardBackgroundFillColorSecondaryBrush", "#F9FAFB"),
                BorderBrush = UiKit.AccentBrush,
                BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(12, 10, 12, 10),
                Child = UiKit.Muted($"Plan: {json}", 11)
            };
        }
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

internal sealed class HandCursorBorder : ContentControl
{
    public HandCursorBorder()
    {
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);
    }
}

internal static class ModelClassifier
{
    public static void Resolve(string code, out string brand, out string friendlyName)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            brand = "default";
            friendlyName = string.Empty;
            return;
        }

        var lower = code.ToLowerInvariant();

        // Default fallbacks
        brand = "default";
        friendlyName = code;

        // 1. Extract prefix provider if present (e.g. "nvidia/...", "together/...", "groq/...")
        string cleanCode = code;
        int slashIdx = lower.IndexOf('/');
        if (slashIdx != -1 && slashIdx + 1 < code.Length)
        {
            cleanCode = code.Substring(slashIdx + 1);
            lower = cleanCode.ToLowerInvariant();
        }

        // 2. Identify the true architectural family and parent brand
        if (lower.Contains("llama"))
        {
            brand = "meta"; // Meta owns Llama!
        }
        else if (lower.Contains("deepseek"))
        {
            brand = "deepseek";
        }
        else if (lower.Contains("gpt-") || lower.Contains("o1-") || lower.Contains("o3-") || lower.Contains("openai") || lower.Contains("chatgpt"))
        {
            brand = "openai";
        }
        else if (lower.Contains("claude") || lower.Contains("anthropic"))
        {
            brand = "anthropic";
        }
        else if (lower.Contains("gemini") || lower.Contains("google"))
        {
            brand = "google";
        }
        else if (lower.Contains("gemma"))
        {
            brand = "gemma";
        }
        else if (lower.Contains("moonshot") || lower.Contains("kimi"))
        {
            brand = "kimi";
        }
        else if (lower.Contains("mistral") || lower.Contains("mixtral") || lower.Contains("codestral"))
        {
            brand = "mistralai";
        }
        else if (lower.Contains("qwen"))
        {
            brand = "qwen";
        }
        else if (lower.Contains("minimax"))
        {
            brand = "minimax";
        }
        else if (lower.Contains("solar") || lower.Contains("upstage"))
        {
            brand = "solar";
        }
        else if (lower.Contains("nvidia") || lower.Contains("nemotron"))
        {
            brand = "nvidia";
        }
        else if (lower.Contains("zhipu") || lower.Contains("glm"))
        {
            brand = "zhipu";
        }
        else if (lower.Contains("xiaomi") || lower.Contains("mimo"))
        {
            brand = "xiaomi";
        }

        // 3. Construct a beautiful friendly name
        friendlyName = FormatFriendlyName(cleanCode);

        // Special mappings for standard models to look stunning
        if (lower.Contains("llama-4-maverick")) friendlyName = "Llama 4 Maverick";
        else if (lower.Contains("llama-3.1-8b") || lower.Contains("llama-3.1-70b") || lower.Contains("llama-3.1-405b")) friendlyName = "Llama 3.1";
        else if (lower.Contains("llama-3.2-1b") || lower.Contains("llama-3.2-3b")) friendlyName = "Llama 3.2";
        else if (lower.Contains("llama-3.3-70b")) friendlyName = "Llama 3.3";
        else if (lower.Contains("deepseek-reasoner") || lower.Contains("deepseek-r1")) friendlyName = "DeepSeek R1";
        else if (lower.Contains("deepseek-chat") || lower.Contains("deepseek-v3")) friendlyName = "DeepSeek V3";
        else if (lower.Contains("gpt-4o-mini")) friendlyName = "GPT-4o Mini";
        else if (lower.Contains("gpt-4o") && !lower.Contains("mini")) friendlyName = "GPT-4o";
        else if (lower.Contains("o1-mini")) friendlyName = "o1 Mini";
        else if (lower.Contains("o1-preview")) friendlyName = "o1 Preview";
        else if (lower.Contains("claude-3-5-sonnet")) friendlyName = "Claude 3.5 Sonnet";
        else if (lower.Contains("claude-3-5-haiku")) friendlyName = "Claude 3.5 Haiku";
        else if (lower.Contains("claude-3-opus")) friendlyName = "Claude 3 Opus";
        else if (lower.Contains("gemini-1.5-pro")) friendlyName = "Gemini 1.5 Pro";
        else if (lower.Contains("gemini-1.5-flash")) friendlyName = "Gemini 1.5 Flash";
        else if (lower.Contains("gemini-2.0-flash")) friendlyName = "Gemini 2.0 Flash";
        else if (lower.Contains("gemini-2.0-pro")) friendlyName = "Gemini 2.0 Pro";
        else if (lower.Contains("qwen-2.5-coder") || lower.Contains("qwen2.5-coder")) friendlyName = "Qwen 2.5-Coder";
        else if (lower.Contains("qwen-2.5-72b") || lower.Contains("qwen2.5-72b")) friendlyName = "Qwen 2.5";
        else if (lower.Contains("qwen-plus")) friendlyName = "Qwen Plus";
        else if (lower.Contains("qwen-max")) friendlyName = "Qwen Max";
        else if (lower.Contains("glm-4-flash")) friendlyName = "GLM-4 Flash";
        else if (lower.Contains("glm-4-plus")) friendlyName = "GLM-4 Plus";
        else if (lower.Contains("glm-4")) friendlyName = "GLM-4";
        else if (lower.Contains("minimax-abab6.5") || lower.Contains("minimax-abab6")) friendlyName = "MiniMax abab6.5";
        else if (lower.Contains("solar-10.7b")) friendlyName = "Solar";
    }

    public static string FormatFriendlyName(string cleanCode)
    {
        if (string.IsNullOrWhiteSpace(cleanCode)) return string.Empty;

        // 1. Remove parameter counts (e.g. 80b, 72b, 8b, 70b, 405b, 1b, 3b, 1.5b, etc.)
        cleanCode = System.Text.RegularExpressions.Regex.Replace(cleanCode, @"\b\d+(\.\d+)?[bB]\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 2. Remove thinking/thought, instruct/instructed, preview/reasoner tags
        cleanCode = System.Text.RegularExpressions.Regex.Replace(cleanCode, @"\b(thinking|thought|instruct|instructed|reasoner|preview|chat|base|web)\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 3. Remove internal/architecture tags (e.g. a3b, a8b, etc.)
        cleanCode = System.Text.RegularExpressions.Regex.Replace(cleanCode, @"\ba\d+[bB]\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 4. Replace multiple hyphens/underscores/spaces with a single space
        cleanCode = System.Text.RegularExpressions.Regex.Replace(cleanCode, @"[-_]+", " ");
        cleanCode = System.Text.RegularExpressions.Regex.Replace(cleanCode, @"\s+", " ");
        cleanCode = cleanCode.Trim();

        // Split by space to format each word
        var parts = cleanCode.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0) continue;

            // Capitalize known acronyms
            if (part.Equals("gpt", StringComparison.OrdinalIgnoreCase)) part = "GPT";
            else if (part.Equals("oss", StringComparison.OrdinalIgnoreCase)) part = "OSS";
            else if (part.Equals("glm", StringComparison.OrdinalIgnoreCase)) part = "GLM";
            else if (part.Equals("llm", StringComparison.OrdinalIgnoreCase)) part = "LLM";
            else if (part.Equals("slm", StringComparison.OrdinalIgnoreCase)) part = "SLM";
            else if (part.Equals("r1", StringComparison.OrdinalIgnoreCase)) part = "R1";
            else if (part.Equals("v3", StringComparison.OrdinalIgnoreCase)) part = "V3";
            else if (part.Equals("v4", StringComparison.OrdinalIgnoreCase)) part = "V4";
            else
            {
                // Capitalize standard words (e.g. qwen3 -> Qwen 3)
                var match = System.Text.RegularExpressions.Regex.Match(part, @"^([a-zA-Z]+)(\d+)$");
                if (match.Success)
                {
                    var word = match.Groups[1].Value;
                    var num = match.Groups[2].Value;

                    word = char.ToUpper(word[0]) + word.Substring(1).ToLowerInvariant();
                    if (word.Equals("Gpt", StringComparison.OrdinalIgnoreCase)) word = "GPT";
                    if (word.Equals("Glm", StringComparison.OrdinalIgnoreCase)) word = "GLM";

                    part = word + " " + num;
                }
                else
                {
                    // Default title casing
                    part = char.ToUpper(part[0]) + part.Substring(1).ToLowerInvariant();
                }
            }

            parts[i] = part;
        }

        string result = string.Join(" ", parts);

        // Standardize specialized hyphenated acronyms
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\bGPT OSS\b", "GPT-OSS", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\bGPT-OSS\b", "GPT-OSS", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Convert "<number> <Word>" to "<number>-<Word>" (e.g. "3 Next" -> "3-Next")
        // result = System.Text.RegularExpressions.Regex.Replace(result, @"\b(\d+(\.\d+)?)\s+([a-zA-Z]+)\b", "$1-$3");

        return result;
    }
}

