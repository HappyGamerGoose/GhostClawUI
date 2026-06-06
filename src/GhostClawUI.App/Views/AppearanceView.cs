using GhostClawUI.App.Services;
using GhostClawUI.App.Ui;
using GhostClawUI.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GhostClawUI.App.Views;

internal sealed class AppearanceView : UserControl
{
    private readonly PipeClient _pipe;
    private readonly Action<AppSettings> _apply;
    private readonly Action<string, string, InfoBarSeverity> _notice;
    private readonly ComboBox _theme = UiKit.Combo("Theme");
    private readonly ComboBox _font = UiKit.Combo("Font family");
    private readonly Slider _size = new() { Minimum = 12, Maximum = 22, StepFrequency = 1 };
    private readonly Slider _lineHeight = new() { Minimum = 1.1, Maximum = 1.8, StepFrequency = 0.05 };
    private readonly ComboBox _density = UiKit.Combo("Chat density");
    private readonly ComboBox _alignment = UiKit.Combo("Message alignment");
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly Windows.Foundation.TypedEventHandler<DispatcherQueueTimer, object> _saveTimerHandler;
    private string _accent;
    private AppSettings _settings;
    private Border? _chatPreviewContainer;

    public AppearanceView(PipeClient pipe, AppSettings settings, Action<AppSettings> apply, Action<string, string, InfoBarSeverity> notice)
    {
        _pipe = pipe;
        _settings = settings;
        _accent = settings.Appearance.AccentColor;
        _apply = apply;
        _notice = notice;
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(650);
        _saveTimerHandler = async (_, _) =>
        {
            _saveTimer.Stop();
            try
            {
                await SaveAsync(showNotice: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _notice("Appearance autosave failed", ex.Message, InfoBarSeverity.Warning);
            }
        };
        _saveTimer.Tick += _saveTimerHandler;

        Unloaded += (s, e) =>
        {
            _saveTimer.Stop();
            _saveTimer.Tick -= _saveTimerHandler;
        };

        Content = Build();
        LoadControls(settings.Appearance);
    }

    private UIElement Build()
    {
        var root = UiKit.Page();
        root.MaxWidth = 1200;
        root.HorizontalAlignment = HorizontalAlignment.Center;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowSpacing = 20;

        var header = new StackPanel { Spacing = 6 };
        header.Children.Add(UiKit.Text("Appearance", 24, Microsoft.UI.Text.FontWeights.SemiBold));
        header.Children.Add(UiKit.Muted("Customize how GhostClawUI looks and feels.", 14));
        root.Children.Add(header);

        var body = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(340) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            ColumnSpacing = 24
        };

        var form = new StackPanel { Spacing = 20 };
        _theme.ItemsSource = new[] { "System", "Light", "Dark" };
        _font.ItemsSource = new[] { "Segoe UI Variable", "Segoe UI", "Cascadia Code", "Aptos" };
        _density.ItemsSource = new[] { "Comfortable", "Compact" };
        _alignment.ItemsSource = new[] { "Split", "Left" };

        var displayGroup = new StackPanel { Spacing = 12 };
        displayGroup.Children.Add(Labeled("Theme", _theme));
        displayGroup.Children.Add(Labeled("Font", _font));
        displayGroup.Children.Add(Labeled("Font size", _size));
        form.Children.Add(displayGroup);

        var chatGroup = new StackPanel { Spacing = 12 };
        chatGroup.Children.Add(Labeled("Line height", _lineHeight));
        chatGroup.Children.Add(Labeled("Density", _density));
        chatGroup.Children.Add(Labeled("Alignment", _alignment));
        form.Children.Add(chatGroup);

        var swatchContainer = new StackPanel { Spacing = 8 };
        swatchContainer.Children.Add(UiKit.Text("Accent Color", 12, Microsoft.UI.Text.FontWeights.SemiBold));
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var color in new[] { "#2563EB", "#16A34A", "#DC2626", "#9333EA", "#0891B2" })
        {
            var swatch = new Border
            {
                Width = 28,
                Height = 28,
                Background = UiKit.BrushFromHex(color),
                CornerRadius = new CornerRadius(14), // Circular
                BorderThickness = new Thickness(2),
                BorderBrush = _accent == color ? UiKit.AccentBrush : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Tag = color
            };

            var swatchWrapper = new SwatchBorder
            {
                Content = swatch
            };

            swatchWrapper.PointerEntered += (s, _) =>
            {
                if (s is SwatchBorder wrapper && wrapper.Content is Border b)
                {
                    b.BorderBrush = UiKit.AccentBrush;
                }
            };
            swatchWrapper.PointerExited += (s, _) =>
            {
                if (s is SwatchBorder wrapper && wrapper.Content is Border b)
                {
                    b.BorderBrush = _accent == (b.Tag as string) ? UiKit.AccentBrush : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                }
            };
            swatchWrapper.PointerPressed += (s, _) =>
            {
                _accent = color;
                Preview();
                QueueSave();

                foreach (var child in swatches.Children)
                {
                    if (child is SwatchBorder wrapper && wrapper.Content is Border b)
                    {
                        var tagHex = b.Tag as string;
                        b.BorderBrush = _accent == tagHex ? UiKit.AccentBrush : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    }
                }
            };
            swatches.Children.Add(swatchWrapper);
        }
        swatchContainer.Children.Add(swatches);
        form.Children.Add(swatchContainer);

        var resetBtn = new Button
        {
            Content = "Reset to Default",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        };
        resetBtn.Click += (_, _) =>
        {
            _accent = "#2563EB";
            _theme.SelectedItem = "System";
            _font.SelectedItem = "Segoe UI Variable";
            _size.Value = 15;
            _lineHeight.Value = 1.35;
            _density.SelectedItem = "Comfortable";
            _alignment.SelectedItem = "Split";

            foreach (var child in swatches.Children)
            {
                if (child is SwatchBorder wrapper && wrapper.Content is Border b)
                {
                    var tagHex = b.Tag as string;
                    b.BorderBrush = _accent == tagHex ? UiKit.AccentBrush : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                }
            }

            Preview();
            QueueSave();
        };
        form.Children.Add(resetBtn);

        var autosave = UiKit.Text("Autosaves as you change it", 12);
        autosave.Foreground = UiKit.QuietTextBrush;
        form.Children.Add(autosave);
        body.Children.Add(UiKit.Card(form));

        var preview = new StackPanel { Spacing = 12 };
        preview.Children.Add(UiKit.Text("Preview", 18, Microsoft.UI.Text.FontWeights.SemiBold));
        preview.Children.Add(UiKit.Muted("This is how your chat will look.", 13));
        _chatPreviewContainer = new Border { Child = ChatPreview() };
        preview.Children.Add(_chatPreviewContainer);
        var previewCard = UiKit.Card(preview);
        Grid.SetColumn(previewCard, 1);
        body.Children.Add(previewCard);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        return root;
    }

    private void LoadControls(AppearanceSettings appearance)
    {
        _theme.SelectedItem = appearance.Theme;
        _font.SelectedItem = appearance.FontFamily;
        _size.Value = appearance.FontSize;
        _lineHeight.Value = appearance.LineHeight;
        _density.SelectedItem = appearance.Density;
        _alignment.SelectedItem = appearance.MessageAlignment;
        _theme.SelectionChanged += (_, _) => { Preview(); QueueSave(); };
        _font.SelectionChanged += (_, _) => { Preview(); QueueSave(); };
        _size.ValueChanged += (_, _) => { Preview(); QueueSave(); };
        _lineHeight.ValueChanged += (_, _) => { Preview(); QueueSave(); };
        _density.SelectionChanged += (_, _) => { Preview(); QueueSave(); };
        _alignment.SelectionChanged += (_, _) => { Preview(); QueueSave(); };
    }

    private void Preview()
    {
        var settings = CurrentSettings();
        if (_chatPreviewContainer != null)
        {
            _chatPreviewContainer.Child = ChatPreview();
        }
        _apply(settings);
    }

    private void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveAsync(bool showNotice)
    {
        _settings = CurrentSettings();
        await _pipe.RequestAsync<CommandResult>("settings.update", _settings).ConfigureAwait(false);
        if (showNotice)
        {
            _notice("Appearance saved", "Theme settings updated.", InfoBarSeverity.Success);
        }
    }

    private AppSettings CurrentSettings()
    {
        var appearance = new AppearanceSettings(
            _theme.SelectedItem as string ?? "System",
            _accent,
            _font.SelectedItem as string ?? "Segoe UI Variable",
            _size.Value,
            _lineHeight.Value,
            _density.SelectedItem as string ?? "Comfortable",
            _alignment.SelectedItem as string ?? "Split",
            true);
        return _settings with { Appearance = appearance };
    }

    private static StackPanel Labeled(string label, FrameworkElement element)
    {
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                UiKit.Text(label, 12, Microsoft.UI.Text.FontWeights.SemiBold),
                element
            }
        };
    }

    private Border PreviewBubble(string text, bool user)
    {
        var content = UiKit.Text(text, 14);
        content.Foreground = user
            ? new SolidColorBrush(Microsoft.UI.Colors.White)
            : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        var border = UiKit.Card(content);
        border.HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        border.MaxWidth = 340;
        border.Background = user ? UiKit.BrushFromHex(_accent) : (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        border.BorderBrush = user ? UiKit.BrushFromHex(_accent) : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        return border;
    }

    private Border ChatPreview()
    {
        var frame = new StackPanel { Spacing = 12 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new Border
        {
            Width = 24,
            Height = 24,
            Child = new Image
            {
                Source = new BitmapImage(new Uri("ms-appx:///Assets/GhostClawUI.Icon.png")),
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        title.Children.Add(UiKit.Text("Chat Preview", 13, Microsoft.UI.Text.FontWeights.SemiBold));
        frame.Children.Add(title);
        frame.Children.Add(new Border
        {
            Child = UiKit.Muted("Today", 12),
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"]
        });
        frame.Children.Add(PreviewBubble("Hello! Can you explain what GhostClawUI is?", true));
        frame.Children.Add(PreviewBubble("GhostClawUI is a Windows-native agent client with secure providers, MCP tools, and persistent memory.", false));
        frame.Children.Add(PreviewBubble("What are the key features?", true));
        frame.Children.Add(new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            Child = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                Children =
                {
                    UiKit.Muted("Type a message...", 13),
                    new Button
                    {
                        Content = new SymbolIcon(Symbol.Send),
                        Width = 40,
                        Height = 40,
                        MinWidth = 40,
                        CornerRadius = new CornerRadius(8),
                        Background = UiKit.BrushFromHex(_accent),
                        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
                    }
                }
            }
        });

        if (frame.Children.Last() is Border composer && composer.Child is Grid grid && grid.Children.Count > 1)
        {
            Grid.SetColumn((FrameworkElement)grid.Children[1], 1);
        }

        return new Border
        {
            Child = frame,
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"]
        };
    }
}

internal sealed class SwatchBorder : ContentControl
{
    public SwatchBorder()
    {
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);
    }
}



