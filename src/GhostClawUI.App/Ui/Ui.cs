using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GhostClawUI.App.Ui;

internal static class UiKit
{
    public static bool IsDarkMode => Application.Current.RequestedTheme == ApplicationTheme.Dark;

    public static readonly Thickness PagePadding = new(34, 30, 34, 28);
    public static readonly SolidColorBrush AccentBrush = BrushFromHex("#0B63F6");
    public static readonly SolidColorBrush QuietTextBrush = BrushFromHex("#64748B");
    public static readonly SolidColorBrush SidebarBrush = new SolidColorBrush(Colors.Transparent);
    public static readonly SolidColorBrush SidebarHoverBrush = BrushFromHex("#19000000");
    public static readonly SolidColorBrush SidebarActiveBrush = BrushFromHex("#0B63F6");
    public static readonly SolidColorBrush SidebarTextBrush = BrushFromHex("#111827");
    public static readonly SolidColorBrush SidebarMutedBrush = BrushFromHex("#64748B");
    public static readonly SolidColorBrush SidebarBorderBrush = BrushFromHex("#15000000");
    public static readonly SolidColorBrush SidebarDividerBrush = BrushFromHex("#E5EAF2");
    public static readonly SolidColorBrush SidebarControlBrush = BrushFromHex("#F1F5F9");

    public static TextBlock Text(string text, double size = 14, Windows.UI.Text.FontWeight? weight = null)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    public static Button Button(string label, Symbol symbol, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new SymbolIcon(symbol),
                    new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }
                }
            },
            MinHeight = 36,
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = new CornerRadius(8)
        };
        AutomationProperties.SetName(button, label);
        button.Click += click;
        return button;
    }

    public static Button PrimaryButton(string label, Symbol symbol, RoutedEventHandler click)
    {
        var button = Button(label, symbol, click);
        button.Background = GetPrimaryGradient();
        button.Foreground = new SolidColorBrush(Colors.White);
        button.BorderBrush = new SolidColorBrush(Colors.Transparent);
        button.Padding = new Thickness(16, 8, 16, 8);
        button.MinHeight = 44;
        AddHoverScale(button);
        return button;
    }

    public static Button IconButton(Symbol symbol, string automationName, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = new SymbolIcon(symbol),
            Width = 40,
            Height = 40,
            MinWidth = 40,
            MinHeight = 40,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8)
        };
        AutomationProperties.SetName(button, automationName);
        button.Click += click;
        return button;
    }

    public static Button NavButton(string label, Symbol symbol, RoutedEventHandler click)
    {
        var button = Button(label, symbol, click);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.MinHeight = 36;
        button.Padding = new Thickness(10, 7, 10, 7);
        button.BorderThickness = new Thickness(0);
        button.Background = new SolidColorBrush(Colors.Transparent);
        return button;
    }

    public static Button SidebarButton(string label, Symbol symbol, RoutedEventHandler click, bool active = false)
    {
        var icon = new SymbolIcon(symbol)
        {
            Foreground = SidebarTextBrush
        };
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SidebarTextBrush,
            FontSize = 15
        };
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children = { icon, text }
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            MinHeight = 42,
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0),
            Background = active ? SidebarActiveBrush : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
        };
        AutomationProperties.SetName(button, label);
        button.Click += click;
        return button;
    }

    public static TextBlock Muted(string text, double size = 13)
    {
        var block = Text(text, size);
        block.Foreground = ThemeBrush("TextFillColorSecondaryBrush", QuietTextBrush);
        return block;
    }

    public static Border Pill(string text, SolidColorBrush brush)
    {
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(24, brush.Color.R, brush.Color.G, brush.Color.B)),
            BorderBrush = brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = brush,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    public static Border Card(UIElement child)
    {
        var card = new Border
        {
            Child = child,
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = BrushFromHex(IsDarkMode ? "#1AFFFFFF" : "#1A000000"),
            Background = BrushFromHex(IsDarkMode ? "#7710151C" : "#99F8FAFC")
        };
        AddHoverScale(card);
        return card;
    }

    public static Border Surface(UIElement child)
    {
        var surface = new Border
        {
            Child = child,
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = BrushFromHex(IsDarkMode ? "#1AFFFFFF" : "#1A000000"),
            Background = BrushFromHex(IsDarkMode ? "#99151A22" : "#BBE2E8F0")
        };
        AddHoverScale(surface);
        return surface;
    }

    public static Grid Page()
    {
        return new Grid
        {
            Padding = PagePadding,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
        };
    }

    public static TextBox TextBox(string placeholder, string automationName)
    {
        var box = new TextBox
        {
            PlaceholderText = placeholder,
            MinHeight = 36
        };
        AutomationProperties.SetName(box, automationName);
        return box;
    }

    public static ComboBox Combo(string automationName)
    {
        var box = new ComboBox { MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(box, automationName);
        return box;
    }

    public static InfoBar Info(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        return new InfoBar
        {
            Title = title,
            Message = message,
            Severity = severity,
            IsOpen = true,
            IsClosable = true
        };
    }

    public static SolidColorBrush BrushFromHex(string value)
    {
        value = value.TrimStart('#');
        if (value.Length == 8)
        {
            var a = Convert.ToByte(value[..2], 16);
            var r = Convert.ToByte(value.Substring(2, 2), 16);
            var g = Convert.ToByte(value.Substring(4, 2), 16);
            var b = Convert.ToByte(value.Substring(6, 2), 16);
            return new SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));
        }
        else if (value.Length == 6)
        {
            var r = Convert.ToByte(value[..2], 16);
            var g = Convert.ToByte(value.Substring(2, 2), 16);
            var b = Convert.ToByte(value.Substring(4, 2), 16);
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
        }
        return new SolidColorBrush(Colors.DodgerBlue);
    }

    public static void SetShellPalette(bool dark)
    {
        SetBrush(SidebarBrush, Colors.Transparent);
        SetBrush(SidebarHoverBrush, dark ? "#15FFFFFF" : "#10000000");
        SetBrush(SidebarTextBrush, dark ? "#F8FAFC" : "#111827");
        SetBrush(SidebarMutedBrush, dark ? "#9CA3AF" : "#64748B");
        SetBrush(SidebarBorderBrush, dark ? "#15FFFFFF" : "#15000000");
        SetBrush(SidebarDividerBrush, dark ? "#20FFFFFF" : "#15000000");
        SetBrush(SidebarControlBrush, dark ? "#10FFFFFF" : "#0A000000");
    }

    private static void SetBrush(SolidColorBrush target, Windows.UI.Color color)
    {
        target.Color = color;
    }

    private static void SetBrush(SolidColorBrush target, string value)
    {
        target.Color = BrushFromHex(value).Color;
    }

    private static Brush ThemeBrush(string key, Brush fallback)
    {
        return fallback;
    }

    public static void AddHoverScale(UIElement element)
    {
        element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        var scale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        element.RenderTransform = scale;

        var enterStoryboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var enterX = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = 1.015, Duration = new Duration(TimeSpan.FromMilliseconds(150)) };
        var enterY = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = 1.015, Duration = new Duration(TimeSpan.FromMilliseconds(150)) };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(enterX, scale);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(enterX, "ScaleX");
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(enterY, scale);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(enterY, "ScaleY");
        enterStoryboard.Children.Add(enterX);
        enterStoryboard.Children.Add(enterY);

        var exitStoryboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var exitX = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = 1.0, Duration = new Duration(TimeSpan.FromMilliseconds(250)) };
        var exitY = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = 1.0, Duration = new Duration(TimeSpan.FromMilliseconds(250)) };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(exitX, scale);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(exitX, "ScaleX");
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(exitY, scale);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(exitY, "ScaleY");
        exitStoryboard.Children.Add(exitX);
        exitStoryboard.Children.Add(exitY);

        element.PointerEntered += (s, e) => enterStoryboard.Begin();
        element.PointerExited += (s, e) => exitStoryboard.Begin();
    }

    public static LinearGradientBrush GetPrimaryGradient()
    {
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1)
        };
        gradient.GradientStops.Add(new GradientStop { Color = AccentBrush.Color, Offset = 0 });
        gradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, (byte)Math.Max(0, AccentBrush.Color.R - 40), (byte)Math.Max(0, AccentBrush.Color.G - 40), (byte)Math.Max(0, AccentBrush.Color.B - 20)), Offset = 1 });
        return gradient;
    }
}



