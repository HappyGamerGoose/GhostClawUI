using GhostClawUI.App.Services;
using GhostClawUI.App.Ui;
using GhostClawUI.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhostClawUI.App.Views;

internal sealed class ProvidersView : UserControl
{
    private readonly PipeClient _pipe;
    private readonly CredentialVault _vault;
    private readonly Action<string, string, InfoBarSeverity> _notice;
    private readonly StackPanel _cards = new() { Spacing = 12 };
    private readonly TextBox _name = UiKit.TextBox("Provider name", "Provider name");
    private readonly TextBox _baseUrl = UiKit.TextBox("https://api.openai.com/v1", "Provider base URL");
    private readonly PasswordBox _apiKey = new() { PlaceholderText = "API key", MinHeight = 36 };
    private readonly TextBox _models = UiKit.TextBox("code:name, code2:name2", "Manual model codes and friendly names");
    private readonly ComboBox _defaultModel = UiKit.Combo("Default model");
    private readonly StackPanel _modelRows = new() { Spacing = 6 };
    private string? _editingId;
    private IReadOnlyList<string> _validatedModels = Array.Empty<string>();
    private bool _updatingModelsText;
    private readonly System.Collections.Generic.Dictionary<string, Border> _modelStatusIndicators = new();
    private readonly ComboBox _globalDefaultProvider = UiKit.Combo("Default Provider");
    private readonly ComboBox _globalDefaultModel = UiKit.Combo("Default Model");
    private bool _loadingDefaults;
    private IReadOnlyList<ProviderProfile> _providersList = Array.Empty<ProviderProfile>();

    private Grid? _rootGrid;
    private Grid? _bodyGrid;
    private ScrollViewer? _bodyScrollViewer;
    private Border? _formCard;
    private Grid? _listView;
    private ScrollViewer? _listScroll;

    public ProvidersView(PipeClient pipe, CredentialVault vault, Action<string, string, InfoBarSeverity> notice)
    {
        _pipe = pipe;
        _vault = vault;
        _notice = notice;
        Content = Build();
        SizeChanged += OnSizeChanged;
        Unloaded += (s, e) => SizeChanged -= OnSizeChanged;
        _models.TextChanged += (_, _) =>
        {
            if (!_updatingModelsText)
            {
                RefreshModelPreview(ParseModels(_models.Text));
            }
        };

        _globalDefaultProvider.SelectionChanged += (s, e) =>
        {
            if (_loadingDefaults) return;
            UpdateGlobalDefaultModels();
            _ = SaveGlobalDefaultsAsync();
        };

        _globalDefaultModel.SelectionChanged += (s, e) =>
        {
            if (_loadingDefaults) return;
            _ = SaveGlobalDefaultsAsync();
        };

        _ = LoadAsync();
    }

    private UIElement Build()
    {
        var root = UiKit.Page();
        root.MaxWidth = 1200;
        root.HorizontalAlignment = HorizontalAlignment.Center;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowSpacing = 20;
        _rootGrid = root;

        var header = new StackPanel { Spacing = 6 };
        header.Children.Add(UiKit.Text("Providers", 24, Microsoft.UI.Text.FontWeights.SemiBold));
        header.Children.Add(UiKit.Muted("Manage your LLM providers, API keys, and model availability.", 14));
        root.Children.Add(header);

        _bodyGrid = new Grid
        {
            ColumnSpacing = 24
        };
        _bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) });

        var form = new StackPanel { Spacing = 12 };

        var defaultsPanel = new StackPanel { Spacing = 12 };
        defaultsPanel.Children.Add(UiKit.Text("Global Defaults", 18, Microsoft.UI.Text.FontWeights.SemiBold));
        defaultsPanel.Children.Add(Labeled("Default Provider", _globalDefaultProvider));
        defaultsPanel.Children.Add(Labeled("Default Model", _globalDefaultModel));
        var defaultsCard = UiKit.Card(defaultsPanel);
        form.Children.Add(defaultsCard);

        var separator = new Border
        {
            Height = 1,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Margin = new Thickness(0, 8, 0, 8)
        };
        form.Children.Add(separator);

        form.Children.Add(UiKit.Text("Add New Provider", 20, Microsoft.UI.Text.FontWeights.SemiBold));
        form.Children.Add(_name);
        form.Children.Add(_baseUrl);
        form.Children.Add(_apiKey);
        form.Children.Add(_models);
        form.Children.Add(Labeled("Default model", _defaultModel));
        form.Children.Add(UiKit.Text("Models", 12, Microsoft.UI.Text.FontWeights.SemiBold));

        var modelScroll = new ScrollViewer
        {
            Content = _modelRows,
            MaxHeight = 240,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(1),
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6)
        };
        form.Children.Add(modelScroll);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(UiKit.Button("Fetch", Symbol.Sync, async (_, _) => await ValidateAsync().ConfigureAwait(false)));
        buttons.Children.Add(UiKit.PrimaryButton("Save", Symbol.Save, async (_, _) => await SaveAsync().ConfigureAwait(false)));
        buttons.Children.Add(UiKit.Button("New", Symbol.Add, (_, _) => ClearForm()));
        form.Children.Add(buttons);

        var formScroll = new ScrollViewer
        {
            Content = form,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        _formCard = UiKit.Card(formScroll);
        _formCard.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetColumn(_formCard, 1);
        _bodyGrid.Children.Add(_formCard);

        _listView = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
            },
            RowSpacing = 14
        };
        var addBtn = UiKit.PrimaryButton("Add Provider", Symbol.Add, (_, _) => ClearForm());
        _listView.Children.Add(addBtn);

        _listScroll = new ScrollViewer
        {
            Content = _cards,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(_listScroll, 1);
        _listView.Children.Add(_listScroll);

        _bodyGrid.Children.Add(_listView);

        _bodyScrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled
        };

        Grid.SetRow(_bodyGrid, 1);
        root.Children.Add(_bodyGrid);
        return root;
    }

    private async Task LoadAsync()
    {
        try
        {
            _loadingDefaults = true;

            var providers = await _pipe.RequestAsync<IReadOnlyList<ProviderProfile>>("providers.list").ConfigureAwait(false) ?? Array.Empty<ProviderProfile>();
            _providersList = providers;

            _cards.Children.Clear();
            _modelStatusIndicators.Clear();
            foreach (var provider in providers)
            {
                _cards.Children.Add(Card(provider));
            }

            _globalDefaultProvider.ItemsSource = providers;
            _globalDefaultProvider.DisplayMemberPath = nameof(ProviderProfile.Name);

            var settings = await _pipe.RequestAsync<AppSettings>("settings.get").ConfigureAwait(false);
            if (settings != null)
            {
                var defaultProv = providers.FirstOrDefault(p => p.Id == settings.DefaultProviderId);
                if (defaultProv != null)
                {
                    _globalDefaultProvider.SelectedItem = defaultProv;
                    _globalDefaultModel.ItemsSource = defaultProv.Models;

                    var defaultModel = defaultProv.Models.FirstOrDefault(m => m == settings.DefaultModelId);
                    if (defaultModel != null)
                    {
                        _globalDefaultModel.SelectedItem = defaultModel;
                    }
                    else if (defaultProv.Models.Count > 0)
                    {
                        _globalDefaultModel.SelectedItem = defaultProv.DefaultModel ?? defaultProv.Models.FirstOrDefault();
                    }
                }
                else if (providers.Count > 0)
                {
                    _globalDefaultProvider.SelectedItem = providers[0];
                    _globalDefaultModel.ItemsSource = providers[0].Models;
                    if (providers[0].Models.Count > 0)
                    {
                        _globalDefaultModel.SelectedItem = providers[0].DefaultModel ?? providers[0].Models.FirstOrDefault();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _notice("Providers unavailable", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _loadingDefaults = false;
        }
    }

    private void UpdateGlobalDefaultModels()
    {
        var selectedProvider = _globalDefaultProvider.SelectedItem as ProviderProfile;
        if (selectedProvider == null)
        {
            _globalDefaultModel.ItemsSource = null;
            _globalDefaultModel.PlaceholderText = "Select provider first";
            return;
        }

        _globalDefaultModel.ItemsSource = selectedProvider.Models;
        _globalDefaultModel.PlaceholderText = selectedProvider.Models.Count == 0 ? "No models" : "Select model";
        if (selectedProvider.Models.Count > 0)
        {
            _globalDefaultModel.SelectedItem = selectedProvider.DefaultModel ?? selectedProvider.Models.FirstOrDefault();
        }
        else
        {
            _globalDefaultModel.SelectedItem = null;
        }
    }

    private async Task SaveGlobalDefaultsAsync()
    {
        if (_loadingDefaults) return;
        try
        {
            var settings = await _pipe.RequestAsync<AppSettings>("settings.get").ConfigureAwait(false);
            if (settings != null)
            {
                var selectedProvider = _globalDefaultProvider.SelectedItem as ProviderProfile;
                var selectedModel = _globalDefaultModel.SelectedItem as string;

                settings = settings with
                {
                    DefaultProviderId = selectedProvider?.Id,
                    DefaultModelId = selectedModel
                };
                await _pipe.RequestAsync<CommandResult>("settings.update", settings).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _notice("Failed to save defaults", ex.Message, InfoBarSeverity.Warning);
        }
    }

    private Border Card(ProviderProfile provider)
    {
        var panel = new StackPanel { Spacing = 12 };
        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 10
        };
        var title = new StackPanel { Spacing = 3 };
        title.Children.Add(UiKit.Text(provider.Name, 18, Microsoft.UI.Text.FontWeights.SemiBold));
        title.Children.Add(UiKit.Muted($"{provider.Models.Count} model(s) · default {provider.DefaultModel ?? provider.Models.FirstOrDefault() ?? "none"} · key {(_vault.ReadProviderKey(provider.Id) is null ? "missing" : "stored")}", 12));
        header.Children.Add(title);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var heartbeatBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "\uEC92", FontSize = 14 },
                    new TextBlock { Text = "Test All", VerticalAlignment = VerticalAlignment.Center }
                }
            },
            MinHeight = 36,
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(8)
        };
        AutomationProperties.SetName(heartbeatBtn, $"Test all models for {provider.Name}");
        heartbeatBtn.Click += async (_, _) => await TestAllProviderModelsAsync(provider).ConfigureAwait(false);
        buttons.Children.Add(heartbeatBtn);

        buttons.Children.Add(UiKit.Button("Edit", Symbol.Edit, (_, _) => LoadIntoForm(provider)));
        // Remove button always visible
        var removeBtn = UiKit.Button("Remove", Symbol.Delete, async (_, _) =>
        {
            var dialog = new ContentDialog
            {
                Title = "Remove Provider?",
                Content = $"Are you sure you want to remove the provider: \"{provider.Name}\"? This will also delete its API key from the Credential Vault.",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            var res = await dialog.ShowAsync();
            if (res == ContentDialogResult.Primary)
            {
                await _pipe.RequestAsync<CommandResult>("providers.remove", new SimpleIdRequest(provider.Id)).ConfigureAwait(false);
                _vault.DeleteProviderKey(provider.Id);
                await LoadAsync().ConfigureAwait(false);
                _notice("Provider removed", provider.Name, InfoBarSeverity.Success);
            }
        });
        removeBtn.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        buttons.Children.Add(removeBtn);
        Grid.SetColumn(buttons, 1);
        header.Children.Add(buttons);
        panel.Children.Add(header);

        panel.Children.Add(UiKit.Muted(provider.BaseUrl, 12));

        var rows = new StackPanel { Spacing = 6 };
        foreach (var model in provider.Models)
        {
            var modelCode = model.Split(':', 2)[0].Trim();
            var statusKey = $"{provider.Id}_{modelCode}";
            rows.Children.Add(ModelRow(
                model,
                statusKey,
                async (_, _) => await TestSavedModelAsync(provider, model).ConfigureAwait(false),
                async (_, _) => await RemoveSavedModelAsync(provider, model).ConfigureAwait(false)));
        }

        panel.Children.Add(new ScrollViewer
        {
            Content = rows,
            MaxHeight = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        var card = UiKit.Card(panel);
        return card;
    }

    private void LoadIntoForm(ProviderProfile provider)
    {
        _editingId = provider.Id;
        _name.Text = provider.Name;
        _baseUrl.Text = provider.BaseUrl;
        _apiKey.Password = _vault.ReadProviderKey(provider.Id) ?? string.Empty;
        SetModelText(provider.Models, provider.DefaultModel);
    }

    private async Task ValidateAsync()
    {
        try
        {
            var result = await _pipe.RequestAsync<ProviderValidationResult>(
                "providers.validate",
                new ProviderValidationRequest(_name.Text, _baseUrl.Text, _apiKey.Password, ParseModels(_models.Text))).ConfigureAwait(false);
            _validatedModels = result?.Models ?? Array.Empty<string>();
            if (_validatedModels.Count > 0)
            {
                SetModelText(_validatedModels);
            }
            else
            {
                RefreshModelPreview(ParseModels(_models.Text));
            }

            _notice(result?.Success == true ? "Models fetched" : "Fetch failed", result?.Message ?? "No response.", result?.Success == true ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            _notice("Fetch failed", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            var models = _validatedModels.Count > 0 ? _validatedModels : ParseModels(_models.Text);
            if (models.Count == 0)
            {
                _notice("Models required", "Fetch /models or enter model names manually.", InfoBarSeverity.Warning);
                return;
            }

            var defaultModel = _defaultModel.SelectedItem as string ?? models.FirstOrDefault();
            var provider = await _pipe.RequestAsync<ProviderProfile>(
                "providers.upsert",
                new ProviderUpsertRequest(_editingId, _name.Text, _baseUrl.Text, models, defaultModel, true)).ConfigureAwait(false);
            if (provider is not null)
            {
                _vault.SaveProviderKey(provider.Id, _apiKey.Password);
            }

            ClearForm();
            await LoadAsync().ConfigureAwait(false);
            _notice("Provider saved", "API key stored in Windows Credential Manager.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _notice("Provider save failed", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ClearForm()
    {
        _editingId = null;
        _validatedModels = Array.Empty<string>();
        _name.Text = "";
        _baseUrl.Text = "";
        _apiKey.Password = "";
        SetModelText(Array.Empty<string>());
    }

    private void RefreshModelPreview(IReadOnlyList<string> models, string? defaultModel = null)
    {
        _validatedModels = models;
        _defaultModel.ItemsSource = models;
        _defaultModel.PlaceholderText = models.Count == 0 ? "No models yet" : "Choose default";
        _defaultModel.SelectedItem = string.IsNullOrWhiteSpace(defaultModel) ? models.FirstOrDefault() : defaultModel;
        if (_defaultModel.SelectedItem is null && models.Count > 0)
        {
            _defaultModel.SelectedIndex = 0;
        }

        _modelRows.Children.Clear();
        var keysToRemove = _modelStatusIndicators.Keys.Where(k => k.StartsWith("draft_")).ToList();
        foreach (var k in keysToRemove) _modelStatusIndicators.Remove(k);

        if (models.Count == 0)
        {
            var empty = UiKit.Text("No models loaded.", 12);
            empty.Foreground = UiKit.QuietTextBrush;
            _modelRows.Children.Add(empty);
            return;
        }

        foreach (var model in models)
        {
            var modelCode = model.Split(':', 2)[0].Trim();
            var statusKey = $"draft_{modelCode}";
            _modelRows.Children.Add(ModelRow(
                model,
                statusKey,
                async (_, _) => await TestDraftModelAsync(model).ConfigureAwait(false),
                (_, _) => RemoveDraftModel(model)));
        }
    }

    private async Task TestDraftModelAsync(string model)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl.Text) || string.IsNullOrWhiteSpace(model))
        {
            _notice("Model test skipped", "Base URL and model are required.", InfoBarSeverity.Warning);
            return;
        }

        var code = model.Split(':', 2)[0].Trim();
        var statusKey = $"draft_{code}";

        if (_modelStatusIndicators.TryGetValue(statusKey, out var container))
        {
            container.Child = new ProgressRing { Width = 14, Height = 14, IsActive = true };
        }

        try
        {
            var result = await _pipe.RequestAsync<CommandResult>(
                "providers.testModel",
                new ProviderModelTestRequest(string.IsNullOrWhiteSpace(_name.Text) ? "Draft provider" : _name.Text, _baseUrl.Text, _apiKey.Password, code)).ConfigureAwait(false);

            var success = result?.Success == true;
            if (_modelStatusIndicators.TryGetValue(statusKey, out container))
            {
                container.Child = success ? CreateSuccessIndicator() : CreateFailureIndicator();
            }

            _notice(success ? "Model works" : "Model failed", result?.Message ?? "No response.", success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            if (_modelStatusIndicators.TryGetValue(statusKey, out container))
            {
                container.Child = CreateFailureIndicator();
            }
            _notice("Fetch failed", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task TestSavedModelAsync(ProviderProfile provider, string model)
    {
        var code = model.Split(':', 2)[0].Trim();
        var statusKey = $"{provider.Id}_{code}";

        if (_modelStatusIndicators.TryGetValue(statusKey, out var container))
        {
            container.Child = new ProgressRing { Width = 14, Height = 14, IsActive = true };
        }

        try
        {
            var result = await _pipe.RequestAsync<CommandResult>(
                "providers.testModel",
                new ProviderModelTestRequest(provider.Name, provider.BaseUrl, _vault.ReadProviderKey(provider.Id), code)).ConfigureAwait(false);

            var success = result?.Success == true;
            if (_modelStatusIndicators.TryGetValue(statusKey, out container))
            {
                container.Child = success ? CreateSuccessIndicator() : CreateFailureIndicator();
            }

            _notice(success ? "Model works" : "Model failed", $"{provider.Name} / {code}: {result?.Message ?? "No response."}", success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            if (_modelStatusIndicators.TryGetValue(statusKey, out container))
            {
                container.Child = CreateFailureIndicator();
            }
            _notice("Test failed", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task TestAllProviderModelsAsync(ProviderProfile provider)
    {
        _notice("Testing started", $"Testing all {provider.Models.Count} models for {provider.Name}...", InfoBarSeverity.Informational);

        var tasks = new System.Collections.Generic.List<Task>();
        foreach (var model in provider.Models)
        {
            var code = model.Split(':', 2)[0].Trim();
            var statusKey = $"{provider.Id}_{code}";

            if (_modelStatusIndicators.TryGetValue(statusKey, out var container))
            {
                container.Child = new ProgressRing { Width = 14, Height = 14, IsActive = true };
            }

            tasks.Add(Task.Run(async () =>
            {
                bool success = false;
                try
                {
                    var result = await _pipe.RequestAsync<CommandResult>(
                        "providers.testModel",
                        new ProviderModelTestRequest(provider.Name, provider.BaseUrl, _vault.ReadProviderKey(provider.Id), code)).ConfigureAwait(false);
                    success = result?.Success == true;
                }
                catch
                {
                    success = false;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_modelStatusIndicators.TryGetValue(statusKey, out var c))
                    {
                        c.Child = success ? CreateSuccessIndicator() : CreateFailureIndicator();
                    }
                });
            }));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        _notice("Testing complete", $"Concurrently tested all models for {provider.Name}.", InfoBarSeverity.Success);
    }

    private async Task RemoveSavedModelAsync(ProviderProfile provider, string model)
    {
        var remaining = provider.Models.Where(item => !item.Equals(model, StringComparison.OrdinalIgnoreCase)).ToList();
        if (remaining.Count == 0)
        {
            _notice("Keep one model", "A provider needs at least one model.", InfoBarSeverity.Warning);
            return;
        }

        var defaultModel = provider.DefaultModel?.Equals(model, StringComparison.OrdinalIgnoreCase) == true
            ? remaining.FirstOrDefault()
            : provider.DefaultModel;
        await _pipe.RequestAsync<ProviderProfile>(
            "providers.upsert",
            new ProviderUpsertRequest(provider.Id, provider.Name, provider.BaseUrl, remaining, defaultModel, provider.IsEnabled)).ConfigureAwait(false);
        await LoadAsync().ConfigureAwait(false);
        _notice("Model removed", model, InfoBarSeverity.Success);
    }

    private void RemoveDraftModel(string model)
    {
        var remaining = ParseModels(_models.Text)
            .Where(item => !item.Equals(model, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SetModelText(remaining);
    }

    private void SetModelText(IReadOnlyList<string> models, string? defaultModel = null)
    {
        _updatingModelsText = true;
        _models.Text = string.Join(", ", models);
        _updatingModelsText = false;
        RefreshModelPreview(models, defaultModel);
    }

    private FrameworkElement ModelRow(string model, string statusKey, RoutedEventHandler test, RoutedEventHandler remove)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8,
            Padding = new Thickness(10, 6, 8, 6)
        };

        var nameGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        var parts = model.Split(':', 2);
        var code = parts[0].Trim();
        var friendlyName = parts.Length > 1 ? parts[1].Trim() : code;
        var displayText = parts.Length > 1 ? $"{friendlyName} ({code})" : code;

        var name = new TextBlock
        {
            Text = displayText,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        nameGrid.Children.Add(name);

        var statusContainer = new Border
        {
            Width = 20,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(statusContainer, 1);
        nameGrid.Children.Add(statusContainer);

        _modelStatusIndicators[statusKey] = statusContainer;

        row.Children.Add(nameGrid);

        var testButton = UiKit.Button("Test", Symbol.Play, test);
        Grid.SetColumn(testButton, 1);
        row.Children.Add(testButton);

        var removeButton = new Button
        {
            Content = new SymbolIcon(Symbol.Delete),
            Width = 34,
            Height = 34,
            MinWidth = 34,
            MinHeight = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            // Hidden by default – shown on row hover
            Visibility = Microsoft.UI.Xaml.Visibility.Visible
        };
        AutomationProperties.SetName(removeButton, $"Remove model {model}");
        removeButton.Click += remove;
        Grid.SetColumn(removeButton, 2);
        row.Children.Add(removeButton);

        var border = new Border
        {
            Child = row,
            BorderThickness = new Thickness(1),
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(6)
        };
        return border;
    }


    private static UIElement CreateSuccessIndicator()
    {
        return new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = UiKit.BrushFromHex("#10B981"),
            BorderBrush = UiKit.BrushFromHex("#10B981"),
            BorderThickness = new Thickness(1),
            Child = new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 10,
                Foreground = UiKit.BrushFromHex("#FFFFFF"),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold
            }
        };
    }

    private static UIElement CreateFailureIndicator()
    {
        return new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = UiKit.BrushFromHex("#EF4444"),
            BorderBrush = UiKit.BrushFromHex("#EF4444"),
            BorderThickness = new Thickness(1),
            Child = new FontIcon
            {
                Glyph = "\uE711",
                FontSize = 10,
                Foreground = UiKit.BrushFromHex("#FFFFFF"),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold
            }
        };
    }

    private static StackPanel Labeled(string label, FrameworkElement element)
    {
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                UiKit.Text(label, 12, Microsoft.UI.Text.FontWeights.SemiBold),
                element
            }
        };
    }

    private static IReadOnlyList<string> ParseModels(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLayoutForWidth(e.NewSize.Width);
    }

    private void UpdateLayoutForWidth(double width)
    {
        if (_bodyGrid == null || _formCard == null || _listView == null || _listScroll == null || _bodyScrollViewer == null || _rootGrid == null)
            return;

        if (width < 850)
        {
            // Transition list scrolling: let outer ScrollViewer handle it
            _listScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _listScroll.VerticalScrollMode = ScrollMode.Disabled;

            // Configure single column with 2 rows for stacking
            if (_bodyGrid.ColumnDefinitions.Count > 1)
            {
                _bodyGrid.ColumnDefinitions.Clear();
                _bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            if (_bodyGrid.RowDefinitions.Count != 2)
            {
                _bodyGrid.RowDefinitions.Clear();
                _bodyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // List
                _bodyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Form Card
                _bodyGrid.RowSpacing = 24;
            }

            // Set positions: List in Row 0, Form Card in Row 1
            Grid.SetRow(_listView, 0);
            Grid.SetColumn(_listView, 0);
            Grid.SetRow(_formCard, 1);
            Grid.SetColumn(_formCard, 0);

            // Wrap _bodyGrid in _bodyScrollViewer if it's not already
            if (_bodyGrid.Parent != _bodyScrollViewer)
            {
                if (_rootGrid.Children.Contains(_bodyGrid))
                {
                    _rootGrid.Children.Remove(_bodyGrid);
                }

                _bodyScrollViewer.Content = _bodyGrid;

                if (!_rootGrid.Children.Contains(_bodyScrollViewer))
                {
                    Grid.SetRow(_bodyScrollViewer, 1);
                    _rootGrid.Children.Add(_bodyScrollViewer);
                }
            }
        }
        else
        {
            // Transition list scrolling: enable list-specific scrollbar
            _listScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _listScroll.VerticalScrollMode = ScrollMode.Enabled;

            // Configure two columns, clear rows
            if (_bodyGrid.RowDefinitions.Count > 0)
            {
                _bodyGrid.RowDefinitions.Clear();
            }

            if (_bodyGrid.ColumnDefinitions.Count != 2)
            {
                _bodyGrid.ColumnDefinitions.Clear();
                _bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                _bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) });
            }

            // Set positions: List in Column 0, Form Card in Column 1
            Grid.SetRow(_listView, 0);
            Grid.SetColumn(_listView, 0);
            Grid.SetRow(_formCard, 0);
            Grid.SetColumn(_formCard, 1);

            // Restore _bodyGrid directly under _rootGrid if it is in _bodyScrollViewer
            if (_bodyGrid.Parent == _bodyScrollViewer)
            {
                _bodyScrollViewer.Content = null;

                if (_rootGrid.Children.Contains(_bodyScrollViewer))
                {
                    _rootGrid.Children.Remove(_bodyScrollViewer);
                }

                if (!_rootGrid.Children.Contains(_bodyGrid))
                {
                    Grid.SetRow(_bodyGrid, 1);
                    _rootGrid.Children.Add(_bodyGrid);
                }
            }
        }
    }
}
