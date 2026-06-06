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
        search.TextChanged += async (_, _) => await RefreshConversationsAsync(search.Text).ConfigureAwait(false);
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

    private Grid BuildCollapsedSidebar()
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
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(8),
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
}
