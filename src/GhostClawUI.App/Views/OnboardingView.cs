using GhostClawUI.App.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GhostClawUI.App.Views;

internal sealed class OnboardingView : UserControl
{
    public OnboardingView(Action providers, Action store, Action chat)
    {
        var root = new Grid
        {
            Padding = new Thickness(32),
            MaxWidth = 1200,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var card = new StackPanel
        {
            Spacing = 24,
            Width = 660,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        card.Children.Add(Progress());
        var title = UiKit.Text("Set Up GhostClawUI", 28, Microsoft.UI.Text.FontWeights.SemiBold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        card.Children.Add(title);
        card.Children.Add(Step("1", "Add your first provider", "Connect an OpenAI-compatible endpoint and store its key securely.", "Open Providers", providers, primary: true));
        card.Children.Add(Step("2", "Explore MCPs", "Install agent tools from online registries without touching a terminal.", "Open MCPs", store));
        card.Children.Add(Step("3", "Start a conversation", "Pick a tested model and send your first prompt.", "Open Chat", chat));
        root.Children.Add(new Border
        {
            Child = card,
            Padding = new Thickness(56, 48, 56, 48),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        Content = root;
    }

    private static FrameworkElement Progress()
    {
        var steps = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        steps.Children.Add(Marker("1", true));
        steps.Children.Add(Rule());
        steps.Children.Add(Marker("2", false));
        steps.Children.Add(Rule());
        steps.Children.Add(Marker("3", false));
        return steps;
    }

    private static Border Marker(string text, bool active)
    {
        return new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Background = active ? UiKit.AccentBrush : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = active ? UiKit.AccentBrush : UiKit.QuietTextBrush,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = text,
                Foreground = active ? new SolidColorBrush(Microsoft.UI.Colors.White) : UiKit.QuietTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private static Border Rule() =>
        new()
        {
            Width = 42,
            Height = 1,
            Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            VerticalAlignment = VerticalAlignment.Center
        };

    private static Border Step(string number, string title, string subtitle, string buttonText, Action action, bool primary = false)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 14
        };
        grid.Children.Add(Marker(number, primary));
        var copy = new StackPanel { Spacing = 2 };
        copy.Children.Add(UiKit.Text(title, 17, Microsoft.UI.Text.FontWeights.SemiBold));
        copy.Children.Add(UiKit.Muted(subtitle, 13));
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);
        var button = primary
            ? UiKit.PrimaryButton(buttonText, Symbol.OpenFile, (_, _) => action())
            : UiKit.Button(buttonText, Symbol.OpenFile, (_, _) => action());
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);
        return UiKit.Surface(grid);
    }
}
