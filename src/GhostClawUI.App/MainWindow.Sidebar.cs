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
        border.PointerExited += (s, _) =>
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
}
