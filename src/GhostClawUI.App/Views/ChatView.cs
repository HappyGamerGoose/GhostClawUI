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

internal sealed partial class ChatView : UserControl, IDisposable
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

    private static void AddMarkdownInlines(InlineCollection inlines, double baseSize, string text, Brush foreground, Windows.UI.Text.FontWeight baseWeight)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (TryReadDelimited(text, index, "`", out var code, out var codeEnd))
            {
                inlines.Add(new Run
                {
                    Text = code,
                    Foreground = foreground,
                    FontFamily = new FontFamily("Cascadia Mono"),
                    FontSize = Math.Max(11, baseSize - 1),
                    FontWeight = baseWeight
                });
                index = codeEnd;
                continue;
            }

            if (TryReadDelimited(text, index, "**", out var bold, out var boldEnd) ||
                TryReadDelimited(text, index, "__", out bold, out boldEnd))
            {
                inlines.Add(new Run
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
                    inlines.Add(hyperlink);
                }
                else
                {
                    inlines.Add(new Run { Text = $"{label} ({url})", Foreground = foreground });
                }

                index = linkEnd;
                continue;
            }

            if (TryReadInlineMath(text, index, out var math, out var mathEnd))
            {
                inlines.Add(new Run
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
                inlines.Add(new Run
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
            inlines.Add(new Run
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

    private ComboBoxItem CreateModelComboBoxItem(string modelName)
    {
        var parts = modelName.Split('|', 2);
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
        return cleanCode;
    }
}



