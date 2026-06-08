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

internal sealed partial class MainWindow : Window, IDisposable
{
    private readonly PipeClient _pipe = new();
    private readonly CredentialVault _vault = new();
    private readonly Border _settingsDot = new() { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = UiKit.BrushFromHex("#64748B") };
    private readonly Border _collapsedSettingsDot = new() { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = UiKit.BrushFromHex("#64748B") };
    private readonly Dictionary<string, Border> _navButtons = new(StringComparer.OrdinalIgnoreCase);
    private ServiceHealthReport? _lastHealth;
    private AppSettings _settings = new(new AppearanceSettings("System", "#2563EB", "Segoe UI Variable", 15, 1.35, "Comfortable", "Split", true), "Normal", new[] { "https://api.smithery.ai/servers", "https://registry.smithery.ai/servers", "https://mcp.higress.ai/" }, true, false, true);
    private string? _currentConversationId;
    private TrayHotkeyService? _tray;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusTimer;
    private Windows.Foundation.TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object>? _statusTimerHandler;
    private bool _sidebarExpanded = true;
    private string _currentPage = "Chat";

    public static MainWindow? Instance { get; private set; }
    public bool IsDarkMode => RootHost.ActualTheme == ElementTheme.Dark;

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();
        _content.ContentTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection
        {
            new Microsoft.UI.Xaml.Media.Animation.EntranceThemeTransition { FromHorizontalOffset = 28, FromVerticalOffset = 0 }
        };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Title = "GhostClawUI";
        SystemBackdrop = null;
        TrySetWindowIcon();
        TrySetInitialWindowSize();
        InitializeSidebar();
        RootHost.ActualThemeChanged += (_, _) => { ApplyShellPalette(); ApplyRootBackground(); };
        ApplyShellPalette();
        ApplyRootBackground();
        _tray = new TrayHotkeyService(this, () => ShowPage("Chat"), () => ShowPage("Settings"), Close, OpenQuickPrompt);
        _ = InitializeAsync();
    }

    private void ApplyRootBackground()
    {
        RootHost.Background = UiKit.BrushFromHex(IsDarkMode ? "#121212" : "#F8FAFC");
    }

    public void Dispose()
    {
        _tray?.Dispose();
        _statusTimer?.Stop();
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

    private static async Task EnsureServiceRunningAsync()
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
        }).ConfigureAwait(false);
    }

    private async Task InitializeAsync()
    {
        // Go directly to a fresh Chat on startup instead of the onboarding screen immediately
        ShowPage("Chat");

        await EnsureServiceRunningAsync().ConfigureAwait(false);
        await LoadSettingsAsync().ConfigureAwait(false);
        await RefreshStatusAsync().ConfigureAwait(false);
        await RefreshConversationsAsync().ConfigureAwait(false);

        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(6);
        _statusTimerHandler = async (_, _) => await RefreshStatusAsync().ConfigureAwait(false);
        _statusTimer.Tick += _statusTimerHandler;
        _statusTimer.Start();
    }

    private void InitializeSidebar()
    {
        var search = UiKit.TextBox("Search", "Search conversations");
        search.Background = UiKit.SidebarControlBrush;
        search.Foreground = UiKit.SidebarTextBrush;
        search.PlaceholderForeground = UiKit.SidebarMutedBrush;
        search.BorderBrush = UiKit.SidebarBorderBrush;
        search.Margin = new Thickness(0, 8, 0, 6);
        search.TextChanged += async (_, _) => await RefreshConversationsAsync(search.Text).ConfigureAwait(false);
        _searchHost.Children.Add(search);

        _newChatHost.Children.Add(UiKit.PrimaryButton("New Chat", Symbol.Add, (_, _) =>
        {
            _currentConversationId = null;
            ShowPage("Chat");
        }));

        _navStack.Children.Add(NavButton("Chat", "\uE8F2")); // ChatBubbles
        _navStack.Children.Add(NavButton("Providers", "\uE82D")); // Server
        _navStack.Children.Add(NavButton("MCPs", "\uEA42")); // Puzzle piece
        _navStack.Children.Add(NavButton("Skills", "\uE829")); // Lightbulb
        _navStack.Children.Add(NavButton("Social", "\uE716")); // People
        _navStack.Children.Add(NavButton("Appearance", "\uE790")); // ColorPalette
        _navStack.Children.Add(NavButton("Settings", "\uE713")); // Settings

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

        _collapsedNavStack.Children.Add(IconNavButton("\uE72A", "Expand sidebar", ToggleSidebar)); // Forward
        _collapsedNavStack.Children.Add(IconNavButton("\uE8F2", "Chat", () => ShowPage("Chat")));
        _collapsedNavStack.Children.Add(IconNavButton("\uE82D", "Providers", () => ShowPage("Providers")));
        _collapsedNavStack.Children.Add(IconNavButton("\uEA42", "MCPs", () => ShowPage("MCPs")));
        _collapsedNavStack.Children.Add(IconNavButton("\uE829", "Skills", () => ShowPage("Skills")));
        _collapsedNavStack.Children.Add(IconNavButton("\uE716", "Social", () => ShowPage("Social")));
        _collapsedNavStack.Children.Add(IconNavButton("\uE790", "Appearance", () => ShowPage("Appearance")));
        _collapsedNavStack.Children.Add(IconNavButton("\uE713", "Settings", () => ShowPage("Settings")));

        _sidebarBorder.Background = UiKit.SidebarBrush;
        _sidebarBorder.BorderBrush = UiKit.SidebarBorderBrush;
        
        _workspaceLabel.Foreground = UiKit.SidebarMutedBrush;
        _chatsLabel.Foreground = UiKit.SidebarMutedBrush;
        _brandName.Foreground = UiKit.SidebarTextBrush;
        
        _collapseButton.PointerEntered += (s, e) =>
        {
            _collapseButton.Background = UiKit.SidebarHoverBrush;
            var isDark = RootHost.ActualTheme == ElementTheme.Dark;
            _collapseIcon.Foreground = isDark ? new SolidColorBrush(Microsoft.UI.Colors.White) : UiKit.AccentBrush;
        };
        _collapseButton.PointerExited += (s, e) =>
        {
            _collapseButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            _collapseIcon.Foreground = UiKit.SidebarMutedBrush;
        };
        _collapseButton.Click += (_, _) => ToggleSidebar();
        
        _brandBtn.PointerEntered += (s, _) => { if (s is Border b) b.Background = UiKit.SidebarHoverBrush; };
        _brandBtn.PointerExited += (s, _) => { if (s is Border b) b.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)); };
        _brandBtn.Tapped += (s, _) => { _currentConversationId = null; ShowPage("Chat"); };
    }
}
