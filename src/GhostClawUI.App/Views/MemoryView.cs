using GhostClawUI.App.Services;
using GhostClawUI.App.Ui;
using GhostClawUI.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GhostClawUI.App.Views;

internal sealed class MemoryView : UserControl
{
    private readonly PipeClient _pipe;
    private readonly Action<string, string, InfoBarSeverity> _notice;
    private readonly StackPanel _facts = new() { Spacing = 10 };
    private readonly TextBlock _subtitle = UiKit.Text("Loading remembered facts...", 12);

    public MemoryView(PipeClient pipe, Action<string, string, InfoBarSeverity> notice)
    {
        _pipe = pipe;
        _notice = notice;
        Content = Build();
        _ = LoadAsync();
    }

    private UIElement Build()
    {
        var root = UiKit.Page();
        root.MaxWidth = 1200;
        root.HorizontalAlignment = HorizontalAlignment.Center;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowSpacing = 18;

        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 12
        };
        var title = new StackPanel { Spacing = 4 };
        title.Children.Add(UiKit.Text("Memory", 24, Microsoft.UI.Text.FontWeights.SemiBold));
        _subtitle.Foreground = UiKit.QuietTextBrush;
        title.Children.Add(_subtitle);
        header.Children.Add(title);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(UiKit.Button("Refresh", Symbol.Sync, async (_, _) => await LoadAsync()));
        actions.Children.Add(UiKit.Button("Purge", Symbol.Delete, async (_, _) =>
        {
            var dialog = new ContentDialog
            {
                Title = "Purge Memory?",
                Content = "This will permanently delete all remembered facts. This action cannot be undone.",
                PrimaryButtonText = "Purge",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            var res = await dialog.ShowAsync();
            if (res == ContentDialogResult.Primary)
            {
                await _pipe.RequestAsync<CommandResult>("memory.purge");
                await LoadAsync();
                _notice("Memory purged", "All persistent facts were deleted.", InfoBarSeverity.Success);
            }
        }));
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        root.Children.Add(header);

        var scroll = new ScrollViewer { Content = _facts };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        return root;
    }

    private async Task LoadAsync()
    {
        try
        {
            var facts = await _pipe.RequestAsync<IReadOnlyList<MemoryFact>>("memory.list") ?? Array.Empty<MemoryFact>();
            _facts.Children.Clear();
            _subtitle.Text = facts.Count == 0
                ? "GhostClaw remembers durable facts from normal conversation."
                : $"{facts.Count} remembered fact(s), stored locally.";

            if (facts.Count == 0)
            {
                _facts.Children.Add(UiKit.Surface(UiKit.Text("No saved memory yet.", 16, Microsoft.UI.Text.FontWeights.SemiBold)));
                return;
            }

            foreach (var fact in facts)
            {
                _facts.Children.Add(Card(fact));
            }
        }
        catch (Exception ex)
        {
            _notice("Memory unavailable", ex.Message, InfoBarSeverity.Error);
        }
    }

    private Border Card(MemoryFact fact)
    {
        var root = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 12
        };

        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(RichTextFormatter.Markdown(
            ResponseTextSanitizer.CleanForDisplay(fact.Summary),
            ResourceBrush("TextFillColorPrimaryBrush", "#111827"),
            17,
            "Segoe UI Variable",
            1.25));
        content.Children.Add(RichTextFormatter.Markdown(
            ResponseTextSanitizer.CleanForDisplay(fact.Content),
            ResourceBrush("TextFillColorPrimaryBrush", "#111827"),
            13,
            "Segoe UI Variable",
            1.35));
        var source = UiKit.Text($"Source {fact.Source} · updated {fact.UpdatedAt.ToLocalTime():g}", 12);
        source.Foreground = UiKit.QuietTextBrush;
        content.Children.Add(source);
        root.Children.Add(content);

        var delete = new Button
        {
            Content = new SymbolIcon(Symbol.Delete),
            Width = 34,
            Height = 34,
            MinWidth = 34,
            MinHeight = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Top,
            // Invisible by default; shown only on parent card hover
            Visibility = Visibility.Collapsed,
            Opacity = 0
        };
        AutomationProperties.SetName(delete, $"Delete memory {fact.Summary}");
        delete.Click += async (_, _) =>
        {
            var dialog = new ContentDialog
            {
                Title = "Delete Fact?",
                Content = $"Are you sure you want to delete the fact: \"{fact.Summary}\"?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            var res = await dialog.ShowAsync();
            if (res == ContentDialogResult.Primary)
            {
                await _pipe.RequestAsync<CommandResult>("memory.delete", new SimpleIdRequest(fact.Id));
                await LoadAsync();
                _notice("Memory deleted", fact.Summary, InfoBarSeverity.Success);
            }
        };
        Grid.SetColumn(delete, 1);
        root.Children.Add(delete);

        var card = UiKit.Card(root);
        card.PointerEntered += (_, _) =>
        {
            delete.Visibility = Visibility.Visible;
            delete.Opacity = 1;
        };
        card.PointerExited += (_, _) =>
        {
            delete.Visibility = Visibility.Collapsed;
            delete.Opacity = 0;
        };
        return card;
    }

    private static Brush ResourceBrush(string key, string fallback)
    {
        return UiKit.BrushFromHex(fallback);
    }
}
