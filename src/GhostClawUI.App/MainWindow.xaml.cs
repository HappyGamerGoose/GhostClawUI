using System.Text;
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

internal sealed partial class MainWindow : Window
{
    private readonly PipeClient _pipe = new();
    private readonly CredentialVault _vault = new();
    private readonly ContentControl _content = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };
    private readonly ListView _conversationList = new();
    private readonly StackPanel _noticeHost = new();
    private readonly ColumnDefinition _sidebarColumn = new() { Width = GridLength.Auto };
    private readonly TextBlock _statusText = UiKit.Text("Service unknown", 12);
    private readonly Border _settingsDot = new() { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = UiKit.BrushFromHex("#64748B") };
    private readonly Border _collapsedSettingsDot = new() { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = UiKit.BrushFromHex("#64748B") };
    private readonly Dictionary<string, Border> _navButtons = new(StringComparer.OrdinalIgnoreCase);
    private ServiceHealthReport? _lastHealth;
    private AppSettings _settings = new(new AppearanceSettings("System", "#2563EB", "Segoe UI Variable", 15, 1.35, "Comfortable", "Split", true), "Normal", new[] { "https://api.smithery.ai/servers", "https://registry.smithery.ai/servers", "https://mcp.higress.ai/" }, true, false, true);
    private string? _currentConversationId;
    private TrayHotkeyService? _tray;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusTimer;
    private Windows.Foundation.TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object>? _statusTimerHandler;
    private FrameworkElement? _expandedSidebar;
    private FrameworkElement? _collapsedSidebar;
    private Border? _sidebarBorder;
    private bool _sidebarExpanded = true;
    private string _currentPage = "Chat";

    public static MainWindow? Instance { get; private set; }

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();
        _content.ContentTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection
        {
            new Microsoft.UI.Xaml.Media.Animation.EntranceThemeTransition { FromHorizontalOffset = 28, FromVerticalOffset = 0 }
        };
        Title = "GhostClawUI";
        SystemBackdrop = new MicaBackdrop();
        TrySetWindowIcon();
        TrySetInitialWindowSize();
        BuildShell();
        RootHost.ActualThemeChanged += (_, _) => ApplyShellPalette();
        ApplyShellPalette();
        _tray = new TrayHotkeyService(this, () => ShowPage("Chat"), () => ShowPage("Settings"), Close, OpenQuickPrompt);
        _ = InitializeAsync();
    }

    public nint Hwnd => WindowNative.GetWindowHandle(this);

    private void TrySetWindowIcon()
    {
        try
        {
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        }
        catch
        {
            // The packaged tile icon still applies if AppWindow rejects the icon path.
        }
    }

    private void TrySetInitialWindowSize()
    {
        try
        {
            var size = new SizeInt32(1440, 940);
            AppWindow.Resize(size);
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var work = display.WorkArea;
            AppWindow.Move(new PointInt32(
                Math.Max(work.X, work.X + (work.Width - size.Width) / 2),
                Math.Max(work.Y, work.Y + (work.Height - size.Height) / 2)));
        }
        catch
        {
            // Let Windows choose the default size if AppWindow sizing is unavailable.
        }
    }

    private async Task EnsureServiceRunningAsync()
    {
        await Task.Run(() =>
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GhostClawUI");
            try
            {
                Directory.CreateDirectory(logDir);
                var debugLog = new StringBuilder();
                debugLog.AppendLine($"Launcher started at {DateTimeOffset.UtcNow}");
                
                var processes = System.Diagnostics.Process.GetProcessesByName("GhostClawUI.Service");
                debugLog.AppendLine($"GetProcessesByName('GhostClawUI.Service') count: {processes.Length}");
                
                if (processes.Length == 0)
                {
                    var baseDir = AppContext.BaseDirectory;
                    var serviceExe = Path.Combine(baseDir, "GhostClawUI.Service.exe");
                    if (!File.Exists(serviceExe))
                    {
                        serviceExe = Path.Combine(baseDir, "Service", "GhostClawUI.Service.exe");
                    }
                    debugLog.AppendLine($"Service exe target path: {serviceExe}");
                    
                    var exists = File.Exists(serviceExe);
                    debugLog.AppendLine($"Service exe exists: {exists}");
                    
                    if (exists)
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = serviceExe,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        var proc = System.Diagnostics.Process.Start(psi);
                        debugLog.AppendLine($"Process.Start returned process: {proc != null} (Id: {proc?.Id})");
                    }
                }
                File.WriteAllText(Path.Combine(logDir, "launcher_debug.txt"), debugLog.ToString());
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(logDir, "launcher_error.txt"), ex.ToString());
                }
                catch
                {
                    // Ignore nested logging errors
                }
            }
        });
    }

    private async Task InitializeAsync()
    {
        // Go directly to a fresh Chat on startup instead of the onboarding screen immediately
        ShowPage("Chat");

        await EnsureServiceRunningAsync();
        await LoadSettingsAsync();
        await RefreshStatusAsync();
        await RefreshConversationsAsync();

        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(6);
        _statusTimerHandler = async (_, _) => await RefreshStatusAsync();
        _statusTimer.Tick += _statusTimerHandler;
        _statusTimer.Start();
    }

    private void BuildShell()
    {
        var root = new Grid
        {
            ColumnDefinitions =
            {
                _sidebarColumn,
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
        };

        var sidebarGrid = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
            }
        };
        var sidebarPanel = new StackPanel { Spacing = 16 };
        var brandRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        brand.Children.Add(new Border
        {
            Width = 38,
            Height = 38,
            Child = new Image
            {
                Source = new BitmapImage(new Uri("ms-appx:///Assets/GhostClawUI.Icon.png")),
                Width = 38,
                Height = 38,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        var brandCopy = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var name = UiKit.Text("GhostClaw", 16, Microsoft.UI.Text.FontWeights.SemiBold);
        name.Foreground = UiKit.SidebarTextBrush;
        brandCopy.Children.Add(name);
        _statusText.Foreground = UiKit.SidebarMutedBrush;
        brandCopy.Children.Add(_statusText);
        brand.Children.Add(brandCopy);
        brandRow.Children.Add(brand);
        var collapse = new Button
        {
            Content = new FontIcon
            {
                Glyph = "\uE72B", // ChevronLeft
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = UiKit.SidebarMutedBrush
            },
            Width = 32,
            Height = 32,
            MinWidth = 32,
            MinHeight = 32,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
        };
        collapse.PointerEntered += (s, e) =>
        {
            collapse.Background = UiKit.SidebarHoverBrush;
            if (collapse.Content is FontIcon fi)
            {
                var isDark = RootHost.ActualTheme == ElementTheme.Dark;
                fi.Foreground = isDark ? new SolidColorBrush(Microsoft.UI.Colors.White) : UiKit.AccentBrush;
            }
        };
        collapse.PointerExited += (s, e) =>
        {
            collapse.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            if (collapse.Content is FontIcon fi)
            {
                fi.Foreground = UiKit.SidebarMutedBrush;
            }
        };
        AutomationProperties.SetName(collapse, "Collapse sidebar");
        collapse.Click += (_, _) => ToggleSidebar();
        Grid.SetColumn(collapse, 1);
        brandRow.Children.Add(collapse);
        sidebarPanel.Children.Add(brandRow);

        var conversations = new StackPanel { Spacing = 10 };
        var search = UiKit.TextBox("Search", "Search conversations");
        search.Background = UiKit.SidebarControlBrush;
        search.Foreground = UiKit.SidebarTextBrush;
        search.PlaceholderForeground = UiKit.SidebarMutedBrush;
        search.BorderBrush = UiKit.SidebarBorderBrush;
        search.Margin = new Thickness(0, 8, 0, 6);
        search.TextChanged += async (_, _) => await RefreshConversationsAsync(search.Text);
        conversations.Children.Add(search);

        sidebarPanel.Children.Add(UiKit.PrimaryButton("New Chat", Symbol.Add, (_, _) =>
        {
            _currentConversationId = null;
            ShowPage("Chat");
        }));

        sidebarPanel.Children.Add(SectionLabel("Workspace"));
        var nav = new StackPanel { Spacing = 4 };
        nav.Children.Add(NavButton("Chat", "\uE8F2")); // ChatBubbles
        nav.Children.Add(NavButton("Providers", "\uE82D")); // Server
        nav.Children.Add(NavButton("MCPs", "\uEA42")); // Puzzle piece
        nav.Children.Add(NavButton("Skills", "\uE829")); // Lightbulb
        nav.Children.Add(NavButton("Social", "\uE716")); // People
        nav.Children.Add(NavButton("Appearance", "\uE790")); // ColorPalette
        nav.Children.Add(NavButton("Settings", "\uE713")); // Settings
        sidebarPanel.Children.Add(nav);

        conversations.Children.Add(SectionLabel("Chats"));
        _conversationList.MinHeight = 240;
        _conversationList.BorderThickness = new Thickness(0);
        _conversationList.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        _conversationList.SelectionChanged += (_, _) =>
        {
            var summary = (_conversationList.SelectedItem as ListViewItem)?.Tag as ConversationSummary
                          ?? _conversationList.SelectedItem as ConversationSummary;
            if (summary is not null)
            {
                _currentConversationId = summary.Id;
                ShowPage("Chat");
            }
        };
        conversations.Children.Add(_conversationList);
        sidebarPanel.Children.Add(conversations);

        Grid.SetRow(sidebarPanel, 0);
        sidebarGrid.Children.Add(sidebarPanel);

        _expandedSidebar = sidebarGrid;
        _collapsedSidebar = BuildCollapsedSidebar();
        _collapsedSidebar.Visibility = Visibility.Collapsed;

        var sidebarHost = new Grid();
        sidebarHost.Children.Add(_expandedSidebar);
        sidebarHost.Children.Add(_collapsedSidebar);

        var sidebar = new Border
        {
            Child = sidebarHost,
            Width = 306,
            Padding = new Thickness(18, 18, 18, 18),
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = UiKit.SidebarBorderBrush,
            Background = UiKit.SidebarBrush
        };
        _sidebarBorder = sidebar;

        var main = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
            }
        };
        _noticeHost.Margin = new Thickness(20, 18, 20, 0);
        Grid.SetRow(_noticeHost, 0);
        main.Children.Add(_noticeHost);
        Grid.SetRow(_content, 1);
        main.Children.Add(_content);

        Grid.SetColumn(sidebar, 0);
        Grid.SetColumn(main, 1);
        root.Children.Add(sidebar);
        root.Children.Add(main);
        RootHost.Children.Add(root);
    }

    private FrameworkElement BuildCollapsedSidebar()
    {
        var grid = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
            }
        };

        var top = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var brandBtn = new Border
        {
            Child = new Image
            {
                Source = new BitmapImage(new Uri("ms-appx:///Assets/GhostClawUI.Icon.png")),
                Width = 24, Height = 24, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            },
            Width = 40, Height = 40, CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Margin = new Thickness(0, 0, 0, 8)
        };
        brandBtn.PointerEntered += (s, _) => { if (s is Border b) b.Background = UiKit.SidebarHoverBrush; };
        brandBtn.PointerExited += (s, _) => { if (s is Border b) b.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)); };
        brandBtn.Tapped += (s, _) => { _currentConversationId = null; ShowPage("Chat"); };
        ToolTipService.SetToolTip(brandBtn, "New chat");

        top.Children.Add(brandBtn);
        top.Children.Add(IconNavButton("\uE72A", "Expand sidebar", ToggleSidebar)); // Forward
        top.Children.Add(IconNavButton("\uE8F2", "Chat", () => ShowPage("Chat")));
        top.Children.Add(IconNavButton("\uE82D", "Providers", () => ShowPage("Providers")));
        top.Children.Add(IconNavButton("\uEA42", "MCPs", () => ShowPage("MCPs")));
        top.Children.Add(IconNavButton("\uE829", "Skills", () => ShowPage("Skills")));
        top.Children.Add(IconNavButton("\uE716", "Social", () => ShowPage("Social")));
        top.Children.Add(IconNavButton("\uE790", "Appearance", () => ShowPage("Appearance")));
        top.Children.Add(IconNavButton("\uE713", "Settings", () => ShowPage("Settings")));
        Grid.SetRow(top, 0);
        grid.Children.Add(top);

        return grid;
    }

    private Border IconNavButton(string glyph, string name, Action action)
    {
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = UiKit.SidebarTextBrush
        };
        var border = new Border
        {
            Child = icon,
            Width = 40,
            Height = 40,
            MinWidth = 40,
            MinHeight = 40,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
        };
        border.PointerEntered += (s, _) =>
        {
            if (s is Border b)
            {
                b.Background = UiKit.SidebarHoverBrush;
            }
            var isDark = RootHost.ActualTheme == ElementTheme.Dark;
            icon.Foreground = isDark ? new SolidColorBrush(Microsoft.UI.Colors.White) : UiKit.AccentBrush;
        };
        border.PointerExited  += (s, _) =>
        {
            if (s is Border b)
            {
                b.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            }
            icon.Foreground = UiKit.SidebarTextBrush;
        };
        border.IsTabStop = true;
        border.UseSystemFocusVisuals = true;
        border.Tapped += (s, _) => action();
        border.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter || e.Key == Windows.System.VirtualKey.Space)
            {
                action();
                e.Handled = true;
            }
        };
        AutomationProperties.SetName(border, name);
        return border;
    }

    private void ToggleSidebar()
    {
        _sidebarExpanded = !_sidebarExpanded;
        if (_sidebarBorder is not null)
        {
            double targetWidth = _sidebarExpanded ? 306 : 64;
            var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = targetWidth,
                Duration = new Duration(TimeSpan.FromMilliseconds(250)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuadraticEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseInOut },
                EnableDependentAnimation = true
            };
            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            storyboard.Children.Add(animation);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, _sidebarBorder);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Width");
            
            _sidebarBorder.Padding = _sidebarExpanded
                ? new Thickness(18, 18, 18, 18)
                : new Thickness(6, 18, 6, 18);
                
            storyboard.Begin();
        }

        if (_expandedSidebar is not null)
        {
            _expandedSidebar.Visibility = _sidebarExpanded ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_collapsedSidebar is not null)
        {
            _collapsedSidebar.Visibility = _sidebarExpanded ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private Border NavButton(string label, string glyph)
    {
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = UiKit.SidebarTextBrush
        };
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = UiKit.SidebarTextBrush,
            FontSize = 15
        };

        var indicator = new Border
        {
            Width = 3,
            Height = 16,
            CornerRadius = new CornerRadius(1.5),
            Background = UiKit.AccentBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };

        var contentStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(12, 0, 0, 0),
            Children = { icon, text }
        };

        var buttonGrid = new Grid();
        buttonGrid.Children.Add(indicator);
        buttonGrid.Children.Add(contentStack);

        var border = new Border
        {
            Child = buttonGrid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 40,
            Padding = new Thickness(0, 8, 12, 8),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
        };

        _navButtons[label] = border;

        border.PointerEntered += (s, e) =>
        {
            if (s is Border b)
            {
                var isCurrent = label.Equals(_currentPage, StringComparison.OrdinalIgnoreCase) ||
                                (_currentPage.Equals("Onboarding", StringComparison.OrdinalIgnoreCase) && label.Equals("Chat", StringComparison.OrdinalIgnoreCase));
                if (!isCurrent)
                {
                    b.Background = UiKit.SidebarHoverBrush;
                    icon.Foreground = UiKit.AccentBrush;
                    text.Foreground = UiKit.AccentBrush;
                }
            }
        };

        border.PointerExited += (s, e) =>
        {
            if (s is Border b)
            {
                var isCurrent = label.Equals(_currentPage, StringComparison.OrdinalIgnoreCase) ||
                                (_currentPage.Equals("Onboarding", StringComparison.OrdinalIgnoreCase) && label.Equals("Chat", StringComparison.OrdinalIgnoreCase));
                if (!isCurrent)
                {
                    b.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                    icon.Foreground = UiKit.SidebarTextBrush;
                    text.Foreground = UiKit.SidebarTextBrush;
                }
            }
        };

        border.IsTabStop = true;
        border.UseSystemFocusVisuals = true;
        border.Tapped += (s, e) => ShowPage(label);
        border.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter || e.Key == Windows.System.VirtualKey.Space)
            {
                ShowPage(label);
                e.Handled = true;
            }
        };

        AutomationProperties.SetName(border, label);
        return border;
    }

    private static TextBlock SectionLabel(string text)
    {
        var label = UiKit.Text(text, 12, Microsoft.UI.Text.FontWeights.SemiBold);
        label.Foreground = UiKit.SidebarMutedBrush;
        label.Margin = new Thickness(4, 14, 0, 0);
        return label;
    }

    public void ShowPage(string page)
    {
        _currentPage = page;
        UpdateNavSelection();
        _content.Content = page switch
        {
            "Onboarding" => new OnboardingView(() => ShowPage("Providers"), () => ShowPage("MCPs"), () => ShowPage("Chat")),
            "Providers" => new ProvidersView(_pipe, _vault, ShowNotice),
            "MCPs" => new McpStoreView(_pipe, ShowNotice),
            "MCP Store" => new McpStoreView(_pipe, ShowNotice),
            "Memory" => new MemoryView(_pipe, ShowNotice),
            "Skills" => new SkillsView(_pipe, Hwnd, ShowNotice),
            "Appearance" => new AppearanceView(_pipe, _settings, settings =>
            {
                _settings = settings;
                ApplyAppearance(settings.Appearance);
            }, ShowNotice),
            "Social" => new SocialView(_pipe, ShowNotice),
            "Settings" => new SettingsView(_pipe, _settings, Hwnd, SaveExportAsync, ShowNotice),
            _ => new ChatView(_pipe, _vault, () => _settings, Hwnd, _currentConversationId, async id =>
            {
                _currentConversationId = id;
                await RefreshConversationsAsync();
            }, ShowNotice)
        };
    }

    private void UpdateNavSelection()
    {
        foreach (var (page, border) in _navButtons)
        {
            var active = page.Equals(_currentPage, StringComparison.OrdinalIgnoreCase) ||
                         (_currentPage.Equals("Onboarding", StringComparison.OrdinalIgnoreCase) && page.Equals("Chat", StringComparison.OrdinalIgnoreCase));

            if (border.Child is Grid grid)
            {
                var indicator = grid.Children[0] as Border;
                if (indicator is not null)
                {
                    indicator.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                }

                border.Background = active
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(12, UiKit.AccentBrush.Color.R, UiKit.AccentBrush.Color.G, UiKit.AccentBrush.Color.B))
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

                if (grid.Children[1] is StackPanel stack)
                {
                    foreach (var child in stack.Children)
                    {
                        switch (child)
                        {
                            case SymbolIcon icon:
                                icon.Foreground = active ? UiKit.AccentBrush : UiKit.SidebarTextBrush;
                                break;
                            case TextBlock text:
                                text.Foreground = active ? UiKit.AccentBrush : UiKit.SidebarTextBrush;
                                text.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
                                break;
                        }
                    }
                }
            }
        }
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            _settings = await _pipe.RequestAsync<AppSettings>("settings.get") ?? _settings;
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

    private void OverrideSystemAccentColor(Windows.UI.Color color)
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
            _lastHealth = await _pipe.RequestAsync<ServiceHealthReport>("health.check");
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

    private async Task RefreshConversationsAsync(string? query = null)
    {
        try
        {
            var conversations = await _pipe.RequestAsync<IReadOnlyList<ConversationSummary>>("conversations.list", new SimpleTextRequest(query ?? string.Empty)) ?? Array.Empty<ConversationSummary>();
            _conversationList.ItemsSource = null;
            _conversationList.DisplayMemberPath = null;
            _conversationList.Items.Clear();
            foreach (var conversation in conversations)
            {
                _conversationList.Items.Add(ConversationItem(conversation));
            }
        }
        catch
        {
            _conversationList.ItemsSource = null;
            _conversationList.Items.Clear();
        }
    }

    private ListViewItem ConversationItem(ConversationSummary summary)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8,
            Padding = new Thickness(4, 4, 2, 4)
        };
        var title = new TextBlock
        {
            Text = summary.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = UiKit.SidebarTextBrush
        };
        row.Children.Add(title);

        var time = new TextBlock
        {
            Text = summary.UpdatedAt.ToLocalTime().ToString("t"),
            FontSize = 11,
            Foreground = UiKit.SidebarMutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(time, 1);
        row.Children.Add(time);

        var rename = new Button
        {
            Content = new SymbolIcon(Symbol.Edit),
            Foreground = UiKit.SidebarMutedBrush,
            Width = 30,
            Height = 30,
            MinWidth = 30,
            MinHeight = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(rename, $"Rename conversation {summary.Title}");

        var renameInput = new TextBox { Text = summary.Title, Width = 200, Margin = new Thickness(0, 0, 0, 8) };
        var saveBtn = new Button
        {
            Content = "Save",
            Background = UiKit.AccentBrush,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var flyoutPanel = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(10)
        };
        flyoutPanel.Children.Add(new TextBlock { Text = "Rename Conversation", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = UiKit.SidebarTextBrush });
        flyoutPanel.Children.Add(renameInput);
        flyoutPanel.Children.Add(saveBtn);
        var flyout = new Flyout { Content = flyoutPanel };
        rename.Flyout = flyout;

        saveBtn.Click += async (_, _) =>
        {
            var newTitle = renameInput.Text.Trim();
            if (string.IsNullOrEmpty(newTitle)) return;
            
            try
            {
                await _pipe.RequestAsync<CommandResult>("conversations.rename", new RenameConversationRequest(summary.Id, newTitle));
                flyout.Hide();
                await RefreshConversationsAsync();
                ShowNotice("Conversation renamed", newTitle, InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowNotice("Rename failed", ex.Message, InfoBarSeverity.Error);
            }
        };

        Grid.SetColumn(rename, 2);
        row.Children.Add(rename);

        var delete = new Button
        {
            Content = new SymbolIcon(Symbol.Delete),
            Foreground = UiKit.SidebarMutedBrush,
            Width = 30,
            Height = 30,
            MinWidth = 30,
            MinHeight = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            // Hidden by default; shown only when the user hovers over the conversation item
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(delete, $"Delete conversation {summary.Title}");
        delete.Click += async (_, _) =>
        {
            try
            {
                await _pipe.RequestAsync<CommandResult>("conversations.delete", new SimpleIdRequest(summary.Id));
                if (_currentConversationId == summary.Id)
                {
                    _currentConversationId = null;
                    ShowPage("Chat");
                }

                await RefreshConversationsAsync();
                ShowNotice("Conversation deleted", summary.Title, InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowNotice("Delete failed", ex.Message, InfoBarSeverity.Error);
            }
        };
        Grid.SetColumn(delete, 3);
        row.Children.Add(delete);

        var item = new ListViewItem
        {
            Tag = summary,
            Content = row,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(4, 2, 4, 2),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Foreground = UiKit.SidebarTextBrush
        };

        // Show rename and delete buttons only when the row is hovered
        item.PointerEntered += (_, _) => { rename.Visibility = Visibility.Visible; delete.Visibility = Visibility.Visible; };
        item.PointerExited  += (_, _) => { rename.Visibility = Visibility.Collapsed; delete.Visibility = Visibility.Collapsed; };

        var contextMenu = new MenuFlyout();
        var exportItem = new MenuFlyoutItem
        {
            Text = "Export chat as PDF",
            Icon = new SymbolIcon(Symbol.Document)
        };
        exportItem.Click += async (_, _) =>
        {
            try
            {
                var conversation = await _pipe.RequestAsync<ConversationDetail>("conversations.get", new SimpleIdRequest(summary.Id));
                if (conversation is null || conversation.Messages.Count == 0)
                {
                    ShowNotice("Export Failed", "Could not retrieve messages for this conversation.", InfoBarSeverity.Error);
                    return;
                }

                var savePicker = new Windows.Storage.Pickers.FileSavePicker();
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, Hwnd);
                savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                savePicker.FileTypeChoices.Add("PDF Document", new List<string>() { ".pdf" });
                savePicker.SuggestedFileName = $"{conversation.Summary.Title.Replace(" ", "_")}_Transcript.pdf";

                var file = await savePicker.PickSaveFileAsync();
                if (file != null)
                {
                    try
                    {
                        byte[] pdfBytes = PdfExporter.ExportToPdf(conversation.Summary.Title, conversation.Messages);
                        await Windows.Storage.FileIO.WriteBytesAsync(file, pdfBytes);
                        ShowNotice("PDF Exported", $"Conversation saved to {file.Name}", InfoBarSeverity.Success);
                    }
                    catch
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
                ShowNotice("Export Failed", ex.Message, InfoBarSeverity.Error);
            }
        };
        contextMenu.Items.Add(exportItem);
        item.ContextFlyout = contextMenu;

        return item;
    }

    private async Task SaveExportAsync(ExportResult export)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GhostClawUI Exports");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, export.FileName);
        await File.WriteAllTextAsync(path, export.Content);
        ShowNotice("Export saved", path, InfoBarSeverity.Success);
    }

    private void ShowNotice(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        _noticeHost.Children.Add(UiKit.Info(title, message, severity));
    }

    private void OpenQuickPrompt()
    {
        var quick = new QuickPromptWindow(async text =>
        {
            ShowPage("Chat");
            if (_content.Content is ChatView chat)
            {
                await chat.SendQuickPromptAsync(text);
            }
        });
        quick.Activate();
    }
}



