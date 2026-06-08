using GhostClawUI.App.Services;
using GhostClawUI.App.Ui;
using GhostClawUI.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GhostClawUI.App.Views;

internal sealed class AppearanceView : UserControl
{
    private readonly PipeClient _pipe;
    private readonly Action<AppSettings> _apply;
    private readonly Action<string, string, InfoBarSeverity> _notice;
    
    private string _themeValue = "System";
    private readonly StackPanel _themeControl = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly ToggleButton _themeSystemBtn = new() { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = "\uE7A6", FontSize = 14 }, new TextBlock { Text = "System" } } } };
    private readonly ToggleButton _themeLightBtn = new() { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = "\uE706", FontSize = 14 }, new TextBlock { Text = "Light" } } } };
    private readonly ToggleButton _themeDarkBtn = new() { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = "\uE708", FontSize = 14 }, new TextBlock { Text = "Dark" } } } };

    private readonly ComboBox _font = UiKit.Combo("Font family");
    private readonly Slider _size = new() { Minimum = 12, Maximum = 22, StepFrequency = 1, Width = 150 };
    private readonly TextBlock _sizeReadout = UiKit.Text("15px", 12);
    private readonly Slider _lineHeight = new() { Minimum = 1.1, Maximum = 1.8, StepFrequency = 0.05, Width = 150 };
    private readonly TextBlock _lineHeightReadout = UiKit.Text("1.35", 12);

    private readonly ComboBox _density = UiKit.Combo("Chat density");
    private readonly ComboBox _alignment = UiKit.Combo("Message alignment");
    
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly Windows.Foundation.TypedEventHandler<DispatcherQueueTimer, object> _saveTimerHandler;
    private string _accent;
    private AppSettings _settings;
    private Border? _chatPreviewContainer;
    private StackPanel? _swatches;

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
                new ColumnDefinition { Width = new GridLength(380) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            ColumnSpacing = 24
        };

        var form = new StackPanel { Spacing = 24 };

        // Theme & Accent Group
        var themeGroup = new StackPanel { Spacing = 12 };
        themeGroup.Children.Add(UiKit.Text("Theme & Accent", 16, Microsoft.UI.Text.FontWeights.SemiBold));
        
        _themeSystemBtn.Click += (_, _) => SetTheme("System");
        _themeLightBtn.Click += (_, _) => SetTheme("Light");
        _themeDarkBtn.Click += (_, _) => SetTheme("Dark");
        _themeControl.Children.Add(_themeSystemBtn);
        _themeControl.Children.Add(_themeLightBtn);
        _themeControl.Children.Add(_themeDarkBtn);
        themeGroup.Children.Add(Labeled("Theme", _themeControl));

        _swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var color in new[] { "#2563EB", "#16A34A", "#DC2626", "#9333EA", "#0891B2" })
        {
            _swatches.Children.Add(CreateSwatch(color));
        }
        var customColorBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE710", FontSize = 14 },
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(0)
        };
        ToolTipService.SetToolTip(customColorBtn, "Custom Color");
        customColorBtn.Click += (_, _) => { _accent = "#F59E0B"; UpdateSwatches(); Preview(); QueueSave(); };
        _swatches.Children.Add(customColorBtn);

        themeGroup.Children.Add(Labeled("Accent Color", _swatches));
        form.Children.Add(themeGroup);

        var divider1 = new Border { Height = 1, Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Margin = new Thickness(0, 16, 0, 16) };
        form.Children.Add(divider1);

        // Type & Spacing Group
        var typeGroup = new StackPanel { Spacing = 12 };
        typeGroup.Children.Add(UiKit.Text("Typography & Spacing", 16, Microsoft.UI.Text.FontWeights.SemiBold));
        _font.ItemsSource = new[] { "Segoe UI Variable", "Segoe UI", "Cascadia Code", "Aptos" };
        typeGroup.Children.Add(Labeled("Font", _font));

        _size.ValueChanged += (_, e) => { _sizeReadout.Text = $"{e.NewValue}px"; Preview(); QueueSave(); };
        var sizeRow = new Grid { ColumnSpacing = 8 };
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        
        var smallA = new FontIcon { Glyph = "\uE8D3", FontSize = 10 }; smallA.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(smallA, 0); sizeRow.Children.Add(smallA);
        
        _size.VerticalAlignment = VerticalAlignment.Center; _size.Width = 150;
        Grid.SetColumn(_size, 1); sizeRow.Children.Add(_size);
        
        var largeA = new FontIcon { Glyph = "\uE8D3", FontSize = 18 }; largeA.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(largeA, 2); sizeRow.Children.Add(largeA);
        
        _sizeReadout.VerticalAlignment = VerticalAlignment.Center; _sizeReadout.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_sizeReadout, 3); sizeRow.Children.Add(_sizeReadout);
        typeGroup.Children.Add(Labeled("Font size", sizeRow));

        _lineHeight.ValueChanged += (_, e) => { _lineHeightReadout.Text = $"{e.NewValue:0.00}"; Preview(); QueueSave(); };
        var lhRow = new Grid { ColumnSpacing = 8 };
        lhRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lhRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lhRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lhRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        
        var smallLh = new FontIcon { Glyph = "\uE8D3", FontSize = 10 }; smallLh.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(smallLh, 0); lhRow.Children.Add(smallLh);
        
        _lineHeight.VerticalAlignment = VerticalAlignment.Center; _lineHeight.Width = 150;
        Grid.SetColumn(_lineHeight, 1); lhRow.Children.Add(_lineHeight);
        
        var largeLh = new FontIcon { Glyph = "\uE8D3", FontSize = 18 }; largeLh.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(largeLh, 2); lhRow.Children.Add(largeLh);
        
        _lineHeightReadout.VerticalAlignment = VerticalAlignment.Center; _lineHeightReadout.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_lineHeightReadout, 3); lhRow.Children.Add(_lineHeightReadout);
        typeGroup.Children.Add(Labeled("Line height", lhRow));

        _density.ItemsSource = new[] { "Comfortable", "Compact" };
        typeGroup.Children.Add(Labeled("Density", _density));
        form.Children.Add(typeGroup);

        var divider2 = new Border { Height = 1, Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Margin = new Thickness(0, 16, 0, 16) };
        form.Children.Add(divider2);

        // Layout Group
        var layoutGroup = new StackPanel { Spacing = 12 };
        layoutGroup.Children.Add(UiKit.Text("Layout", 16, Microsoft.UI.Text.FontWeights.SemiBold));
        _alignment.ItemsSource = new[] { "Split", "Left" };
        layoutGroup.Children.Add(Labeled("Alignment", _alignment));
        form.Children.Add(layoutGroup);

        // Footer / Reset
        var resetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 16, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var resetBtn = new Button { Content = "Reset to Default" };
        resetBtn.Click += async (_, _) =>
        {
            var dialog = new ContentDialog
            {
                Title = "Reset Appearance",
                Content = "Are you sure you want to revert to the default appearance settings?",
                PrimaryButtonText = "Reset",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _accent = "#2563EB";
                SetTheme("System");
                _font.SelectedItem = "Segoe UI Variable";
                _size.Value = 15;
                _lineHeight.Value = 1.35;
                _density.SelectedItem = "Comfortable";
                _alignment.SelectedItem = "Split";
                UpdateSwatches();
                Preview();
                QueueSave();
            }
        };
        resetRow.Children.Add(resetBtn);
        var autosave = UiKit.Text("Autosaves as you change it", 12);
        autosave.Foreground = UiKit.QuietTextBrush;
        autosave.VerticalAlignment = VerticalAlignment.Center;
        resetRow.Children.Add(autosave);
        form.Children.Add(resetRow);

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

    private SwatchBorder CreateSwatch(string color)
    {
        var checkIcon = new FontIcon
        {
            Glyph = "\uE73E",
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            Visibility = _accent == color ? Visibility.Visible : Visibility.Collapsed
        };

        var swatch = new Border
        {
            Width = 32,
            Height = 32,
            Background = UiKit.BrushFromHex(color),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(2),
            BorderBrush = _accent == color ? UiKit.AccentBrush : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Tag = color,
            Child = checkIcon
        };

        var swatchWrapper = new SwatchBorder { Content = swatch };
        ToolTipService.SetToolTip(swatchWrapper, color);

        swatchWrapper.PointerEntered += (s, _) =>
        {
            if (s is SwatchBorder wrapper && wrapper.Content is Border b)
                b.BorderBrush = UiKit.AccentBrush;
        };
        swatchWrapper.PointerExited += (s, _) =>
        {
            if (s is SwatchBorder wrapper && wrapper.Content is Border b)
                b.BorderBrush = _accent == (b.Tag as string) ? UiKit.AccentBrush : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        };
        swatchWrapper.PointerPressed += (s, _) =>
        {
            _accent = color;
            UpdateSwatches();
            Preview();
            QueueSave();
        };

        return swatchWrapper;
    }

    private void UpdateSwatches()
    {
        if (_swatches == null) return;
        foreach (var child in _swatches.Children)
        {
            if (child is SwatchBorder wrapper && wrapper.Content is Border b)
            {
                var tagHex = b.Tag as string;
                if (tagHex != null)
                {
                    b.BorderBrush = _accent == tagHex ? UiKit.AccentBrush : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    if (b.Child is FontIcon icon)
                    {
                        icon.Visibility = _accent == tagHex ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            }
        }
    }

    private void SetTheme(string theme)
    {
        _themeValue = theme;
        _themeSystemBtn.IsChecked = theme == "System";
        _themeLightBtn.IsChecked = theme == "Light";
        _themeDarkBtn.IsChecked = theme == "Dark";
        Preview();
        QueueSave();
    }

    private void LoadControls(AppearanceSettings appearance)
    {
        SetTheme(appearance.Theme);
        _font.SelectedItem = appearance.FontFamily;
        _size.Value = appearance.FontSize;
        _lineHeight.Value = appearance.LineHeight;
        _density.SelectedItem = appearance.Density;
        _alignment.SelectedItem = appearance.MessageAlignment;

        _font.SelectionChanged += (_, _) => { Preview(); QueueSave(); };
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
            _themeValue,
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
            Spacing = 6,
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
        
        bool isSplit = (_alignment.SelectedItem as string) == "Split";
        border.HorizontalAlignment = (user && isSplit) ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        
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
