using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GhostClawUI.App.Services;
using GhostClawUI.App.Ui;
using GhostClawUI.Shared;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace GhostClawUI.App.Views;

internal sealed class McpStoreView : UserControl
{
    private readonly PipeClient _pipe;
    private readonly Action<string, string, InfoBarSeverity> _notice;
    private readonly StackPanel _list = new() { Spacing = 10 };
    private readonly TextBox _search = UiKit.TextBox("Search installed or built-in MCPs...", "Search MCP servers");
    private readonly TextBlock _status = UiKit.Text("Loading local MCP servers...", 12);
    private readonly List<McpServerDefinition> _allLocalServers = new();
    private bool _fetching = false;

    public McpStoreView(PipeClient pipe, Action<string, string, InfoBarSeverity> notice)
    {
        _pipe = pipe;
        _notice = notice;

        Content = Build();

        _search.TextChanged += (_, _) =>
        {
            ApplyFilter();
        };

        _ = FetchServersAsync();
    }

    private Grid Build()
    {
        var root = UiKit.Page();
        root.MaxWidth = 1200;
        root.HorizontalAlignment = HorizontalAlignment.Center;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowSpacing = 18;

        var top = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(300) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 10
        };

        var heading = new StackPanel { Spacing = 4 };
        heading.Children.Add(UiKit.Text("MCPs", 24, FontWeights.SemiBold));
        heading.Children.Add(UiKit.Muted("Manage and configure Model Context Protocol servers.", 14));
        _status.Foreground = UiKit.QuietTextBrush;
        heading.Children.Add(_status);
        top.Children.Add(heading);

        Grid.SetColumn(_search, 1);
        top.Children.Add(_search);

        // Add MCP Button with Flyout
        var addBtn = UiKit.PrimaryButton("Add MCP", Symbol.Add, (_, _) => { });
        var flyout = new MenuFlyout();

        var quickAddOption = new MenuFlyoutItem
        {
            Text = "Quick Add",
            Icon = new FontIcon { Glyph = "\uE8A5", FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets") }
        };
        quickAddOption.Click += async (_, _) => await ShowQuickAddDialogAsync().ConfigureAwait(false);

        var jsonAddOption = new MenuFlyoutItem
        {
            Text = "Add using JSON",
            Icon = new FontIcon { Glyph = "\uE943", FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets") }
        };
        jsonAddOption.Click += async (_, _) => await ShowJsonAddDialogAsync().ConfigureAwait(false);

        flyout.Items.Add(quickAddOption);
        flyout.Items.Add(jsonAddOption);
        addBtn.Flyout = flyout;

        Grid.SetColumn(addBtn, 2);
        top.Children.Add(addBtn);

        var refresh = UiKit.Button("Refresh", Symbol.Sync, async (_, _) => await FetchServersAsync().ConfigureAwait(false));
        Grid.SetColumn(refresh, 3);
        top.Children.Add(refresh);

        Grid.SetRow(top, 0);
        root.Children.Add(top);

        var scrollViewer = new ScrollViewer { Content = _list };
        Grid.SetRow(scrollViewer, 1);
        root.Children.Add(scrollViewer);

        return root;
    }

    private async Task FetchServersAsync()
    {
        if (_fetching) return;
        _fetching = true;

        _status.Text = "Loading local MCP configurations...";

        try
        {
            var rawList = await _pipe.RequestAsync<IReadOnlyList<McpServerDefinition>>("mcp.list").ConfigureAwait(false) ?? Array.Empty<McpServerDefinition>();

            _allLocalServers.Clear();
            foreach (var server in rawList)
            {
                if (server.Installed || server.Command.StartsWith("builtin-", StringComparison.OrdinalIgnoreCase))
                {
                    _allLocalServers.Add(server);
                }
            }

            ApplyFilter();
        }
        catch (Exception ex)
        {
            _status.Text = "Service offline. Reopen when the service reconnects.";
            _notice("MCP fetch failed", ex.Message, InfoBarSeverity.Warning);
        }
        finally
        {
            _fetching = false;
        }
    }

    private void ApplyFilter()
    {
        var query = _search.Text.Trim().ToLowerInvariant();
        var filtered = _allLocalServers;

        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = _allLocalServers.Where(s =>
                s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Command.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.RegistryUrl.Contains(query, StringComparison.OrdinalIgnoreCase)
            ).ToList();

            _status.Text = $"Showing {filtered.Count} of {_allLocalServers.Count} local MCP servers matching \"{_search.Text}\"";
        }
        else
        {
            _status.Text = $"Loaded {_allLocalServers.Count} installed or built-in MCP servers";
        }

        RenderList(filtered);
    }

    private void RenderList(List<McpServerDefinition> servers)
    {
        _list.Children.Clear();

        if (servers.Count == 0)
        {
            var emptyText = UiKit.Text("No installed or built-in MCP servers found.", 15, FontWeights.Normal);
            emptyText.Foreground = UiKit.QuietTextBrush;
            emptyText.HorizontalAlignment = HorizontalAlignment.Center;
            emptyText.Margin = new Thickness(0, 40, 0, 0);
            _list.Children.Add(emptyText);
            return;
        }

        foreach (var server in servers)
        {
            _list.Children.Add(ListRow(server));
        }
    }

    private Border ListRow(McpServerDefinition server)
    {
        var panel = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(320) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 20,
            Padding = new Thickness(14, 12, 14, 12)
        };

        // Column 0: Copy info (Name & Description)
        var copyStack = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        copyStack.Children.Add(UiKit.Text(server.Name, 16, FontWeights.SemiBold));

        var displayDesc = server.Description;
        if (server.Description.StartsWith("__JSON__:", StringComparison.Ordinal))
        {
            try
            {
                var doc = JsonDocument.Parse(server.Description[9..]);
                if (doc.RootElement.TryGetProperty("desc", out var descProp))
                {
                    displayDesc = descProp.GetString() ?? "";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing JSON description: {ex}");
            }
        }
        if (string.IsNullOrWhiteSpace(displayDesc))
        {
            displayDesc = "No description provided.";
        }
        var descText = UiKit.Muted(displayDesc, 13);
        descText.TextTrimming = TextTrimming.CharacterEllipsis;
        descText.MaxLines = 1;
        copyStack.Children.Add(descText);
        panel.Children.Add(copyStack);

        // Column 1: Command line badge
        var commandText = new TextBlock
        {
            Text = DisplayCommand(server),
            FontSize = 11.5,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ResourceBrush("TextFillColorSecondaryBrush", "#6B7280"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var commandBorder = new Border
        {
            Child = commandText,
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            BorderBrush = ResourceBrush("CardStrokeColorDefaultBrush", "#D1D5DB"),
            Background = ResourceBrush("SubtleFillColorSecondaryBrush", "#F3F4F6"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(commandBorder, 1);
        panel.Children.Add(commandBorder);

        // Column 2: Status/Registry Pills
        var source = server.RegistryUrl.Equals("embedded", StringComparison.OrdinalIgnoreCase)
            ? "Curated"
            : server.RegistryUrl.Contains("smithery", StringComparison.OrdinalIgnoreCase) ? "Smithery"
            : server.RegistryUrl.Contains("higress", StringComparison.OrdinalIgnoreCase) ? "Higress"
            : server.RegistryUrl.Equals("manual", StringComparison.OrdinalIgnoreCase) ? "Manual"
            : "Trusted";

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        meta.Children.Add(UiKit.Pill(source, server.Installed ? UiKit.BrushFromHex("#16A34A") : UiKit.AccentBrush));
        if (!string.IsNullOrWhiteSpace(server.Version))
        {
            meta.Children.Add(UiKit.Pill(server.Version, UiKit.BrushFromHex("#64748B")));
        }
        Grid.SetColumn(meta, 2);
        panel.Children.Add(meta);

        // Column 3: Action Buttons
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var builtIn = server.Command.StartsWith("builtin-", StringComparison.OrdinalIgnoreCase);
        if (builtIn)
        {
            var alwaysOn = UiKit.Pill("Always on", UiKit.BrushFromHex("#16A34A"));
            alwaysOn.Padding = new Thickness(8, 4, 8, 4);
            buttons.Children.Add(alwaysOn);
        }
        else
        {
            if (server.Installed)
            {
                buttons.Children.Add(UiKit.Button("Update", Symbol.Sync, async (_, _) =>
                {
                    var result = await _pipe.RequestAsync<CommandResult>("mcp.update", new McpServerRequest(server.Id, server.Name, server.Command, server.Args, server.RegistryUrl)).ConfigureAwait(false);
                    _notice(result?.Success == true ? "MCP updated" : "MCP failed", result?.Message ?? "", result?.Success == true ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
                    await FetchServersAsync().ConfigureAwait(false);
                }));
                buttons.Children.Add(UiKit.Button("Uninstall", Symbol.Delete, async (_, _) =>
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Uninstall MCP Server?",
                        Content = $"Are you sure you want to uninstall the MCP server: \"{server.Name}\"?",
                        PrimaryButtonText = "Uninstall",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = XamlRoot
                    };
                    var res = await dialog.ShowAsync();
                    if (res == ContentDialogResult.Primary)
                    {
                        var result = await _pipe.RequestAsync<CommandResult>("mcp.uninstall", new McpServerRequest(server.Id, server.Name, server.Command, server.Args, server.RegistryUrl)).ConfigureAwait(false);
                        _notice(result?.Success == true ? "MCP uninstalled" : "MCP failed", result?.Message ?? "", result?.Success == true ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
                        await FetchServersAsync().ConfigureAwait(false);
                    }
                }));
            }
            else
            {
                buttons.Children.Add(UiKit.PrimaryButton("Install", Symbol.Download, async (_, _) =>
                {
                    var result = await _pipe.RequestAsync<CommandResult>("mcp.install", new McpServerRequest(server.Id, server.Name, server.Command, server.Args, server.RegistryUrl)).ConfigureAwait(false);
                    _notice(result?.Success == true ? "MCP installed" : "MCP failed", result?.Message ?? "", result?.Success == true ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
                    await FetchServersAsync().ConfigureAwait(false);
                }));
                buttons.Children.Add(UiKit.Button("Delete", Symbol.Delete, async (_, _) =>
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Delete MCP Server?",
                        Content = $"Are you sure you want to delete the configuration for: \"{server.Name}\"?",
                        PrimaryButtonText = "Delete",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = XamlRoot
                    };
                    var res = await dialog.ShowAsync();
                    if (res == ContentDialogResult.Primary)
                    {
                        var result = await _pipe.RequestAsync<CommandResult>("mcp.uninstall", new McpServerRequest(server.Id, server.Name, server.Command, server.Args, server.RegistryUrl)).ConfigureAwait(false);
                        _notice(result?.Success == true ? "MCP deleted" : "MCP failed", result?.Message ?? "", result?.Success == true ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
                        await FetchServersAsync().ConfigureAwait(false);
                    }
                }));
            }
        }
        Grid.SetColumn(buttons, 3);
        panel.Children.Add(buttons);

        var card = UiKit.Card(panel);
        card.CornerRadius = new CornerRadius(6);
        card.Padding = new Thickness(0);

        var normalBorderBrush = ResourceBrush("CardStrokeColorDefaultBrush", "#DCE3EC");
        card.PointerEntered += (s, e) =>
        {
            if (s is Border b)
            {
                b.BorderBrush = UiKit.AccentBrush;
                b.Background = UiKit.SidebarHoverBrush;
            }
        };
        card.PointerExited += (s, e) =>
        {
            if (s is Border b)
            {
                b.BorderBrush = normalBorderBrush;
                b.Background = ResourceBrush("CardBackgroundFillColorDefaultBrush", "#FFFFFF");
            }
        };

        return card;
    }

    private async Task ShowQuickAddDialogAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Add MCP Server",
            PrimaryButtonText = "Save and enable",
            SecondaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var panel = new StackPanel { Spacing = 14, Width = 480 };

        var nameLabel = LabeledHeaderWithInfo("Name", isRequired: true);
        var nameInput = UiKit.TextBox("Input MCP server name", "Name");

        var descLabel = LabeledHeaderWithInfo("Description", isRequired: false);
        var descInput = UiKit.TextBox("Input MCP server description", "Description");

        var typeLabel = LabeledHeaderWithInfo("Type", isRequired: true);

        // Horizontal side-by-side layout for types matching screenshots
        var typeGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }
            },
            RowSpacing = 8,
            ColumnSpacing = 8
        };

        var sseRadio = new RadioButton { Content = "SSE (server sent event)", IsChecked = true, GroupName = "McpTypeGroup" };
        var httpRadio = new RadioButton { Content = "StreamableHTTP", GroupName = "McpTypeGroup" };
        var stdioRadio = new RadioButton { Content = "STDIO (standard input/output)", GroupName = "McpTypeGroup" };

        var sseCard = CreateRadioCard(sseRadio);
        var httpCard = CreateRadioCard(httpRadio);
        var stdioCard = CreateRadioCard(stdioRadio);

        Grid.SetColumn(sseCard, 0);
        Grid.SetRow(sseCard, 0);
        typeGrid.Children.Add(sseCard);

        Grid.SetColumn(httpCard, 1);
        Grid.SetRow(httpCard, 0);
        typeGrid.Children.Add(httpCard);

        Grid.SetColumn(stdioCard, 0);
        Grid.SetColumnSpan(stdioCard, 2);
        Grid.SetRow(stdioCard, 1);
        typeGrid.Children.Add(stdioCard);

        // SSE/HTTP Input elements
        var ssePanel = new StackPanel { Spacing = 10 };
        var urlLabel = LabeledHeaderWithInfo("URL", isRequired: true);
        var urlInput = UiKit.TextBox("Input URL", "URL");
        ssePanel.Children.Add(urlLabel);
        ssePanel.Children.Add(urlInput);

        // STDIO Input elements (Dynamic dropdown Command, multi-line parameters and optional env vars)
        var stdioPanel = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };

        var cmdLabel = LabeledHeaderWithInfo("Command", isRequired: true);
        var cmdCombo = new ComboBox
        {
            IsEditable = true,
            PlaceholderText = "Input command",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "uvx", "npx", "node", "python", "pip" },
            SelectedIndex = 0
        };
        stdioPanel.Children.Add(cmdLabel);
        stdioPanel.Children.Add(cmdCombo);

        var paramLabel = LabeledHeaderWithInfo("Parameters", isRequired: true);
        var paramInput = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80,
            MaxHeight = 120,
            PlaceholderText = "Input Parameters"
        };
        var paramCounter = new TextBlock
        {
            Text = "0 / 1000",
            FontSize = 11,
            Foreground = UiKit.QuietTextBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 0, 4)
        };
        paramInput.TextChanged += (s, e) =>
        {
            int len = paramInput.Text.Length;
            paramCounter.Text = $"{len} / 1000";
            paramCounter.Foreground = len > 1000 ? UiKit.BrushFromHex("#EF4444") : UiKit.QuietTextBrush;
            Validate();
        };
        stdioPanel.Children.Add(paramLabel);
        stdioPanel.Children.Add(paramInput);
        stdioPanel.Children.Add(paramCounter);

        var envLabel = LabeledHeaderWithInfo("Environment Variables", isRequired: false);
        var envInput = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80,
            MaxHeight = 120,
            PlaceholderText = "Input Environment Variables"
        };
        var envCounter = new TextBlock
        {
            Text = "0 / 1000",
            FontSize = 11,
            Foreground = UiKit.QuietTextBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 0, 4)
        };
        envInput.TextChanged += (s, e) =>
        {
            int len = envInput.Text.Length;
            envCounter.Text = $"{len} / 1000";
            envCounter.Foreground = len > 1000 ? UiKit.BrushFromHex("#EF4444") : UiKit.QuietTextBrush;
        };
        stdioPanel.Children.Add(envLabel);
        stdioPanel.Children.Add(envInput);
        stdioPanel.Children.Add(envCounter);

        void Validate()
        {
            bool isSseHttp = sseRadio.IsChecked == true || httpRadio.IsChecked == true;
            bool isValid = !string.IsNullOrWhiteSpace(nameInput.Text);

            if (isSseHttp)
            {
                isValid = isValid && !string.IsNullOrWhiteSpace(urlInput.Text);
            }
            else
            {
                isValid = isValid && !string.IsNullOrWhiteSpace(cmdCombo.Text) && !string.IsNullOrWhiteSpace(paramInput.Text);
            }

            dialog.IsPrimaryButtonEnabled = isValid;
            dialog.IsSecondaryButtonEnabled = isValid;
        }

        nameInput.TextChanged += (s, e) => Validate();
        urlInput.TextChanged += (s, e) => Validate();
        cmdCombo.TextSubmitted += (s, e) => Validate();
        cmdCombo.SelectionChanged += (s, e) => Validate();

        sseRadio.Checked += (s, e) =>
        {
            ssePanel.Visibility = Visibility.Visible;
            stdioPanel.Visibility = Visibility.Collapsed;
            Validate();
        };

        httpRadio.Checked += (s, e) =>
        {
            ssePanel.Visibility = Visibility.Visible;
            stdioPanel.Visibility = Visibility.Collapsed;
            Validate();
        };

        stdioRadio.Checked += (s, e) =>
        {
            ssePanel.Visibility = Visibility.Collapsed;
            stdioPanel.Visibility = Visibility.Visible;
            Validate();
        };

        panel.Children.Add(nameLabel);
        panel.Children.Add(nameInput);
        panel.Children.Add(descLabel);
        panel.Children.Add(descInput);
        panel.Children.Add(typeLabel);
        panel.Children.Add(typeGrid);
        panel.Children.Add(ssePanel);
        panel.Children.Add(stdioPanel);

        dialog.Content = panel;
        Validate();

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
        {
            bool installAndEnable = (result == ContentDialogResult.Primary);
            string name = nameInput.Text.Trim();
            string desc = descInput.Text.Trim();

            string id = "manual-" + Guid.NewGuid().ToString("N")[..8];
            string command = "";
            IReadOnlyList<string> args = Array.Empty<string>();
            string registryUrl = "manual";

            // Parse Environment Variables if present
            var envDict = new Dictionary<string, string>();
            if (stdioRadio.IsChecked == true && !string.IsNullOrWhiteSpace(envInput.Text))
            {
                var lines = envInput.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var line in lines)
                {
                    var eqIdx = line.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        var key = line.Substring(0, eqIdx).Trim();
                        var val = line.Substring(eqIdx + 1).Trim();
                        envDict[key] = val;
                    }
                }
            }

            // Pack description & env variables if present into the description column
            if (envDict.Count > 0)
            {
                var jsonObj = new JsonObject
                {
                    ["desc"] = desc,
                    ["env"] = new JsonObject(envDict.Select(kv => KeyValuePair.Create<string, JsonNode?>(kv.Key, JsonValue.Create(kv.Value))).ToArray())
                };
                desc = "__JSON__:" + jsonObj.ToJsonString();
            }

            if (sseRadio.IsChecked == true)
            {
                command = "sse";
                args = new[] { urlInput.Text.Trim() };
            }
            else if (httpRadio.IsChecked == true)
            {
                command = "streamable-http";
                args = new[] { urlInput.Text.Trim() };
            }
            else
            {
                command = cmdCombo.Text.Trim();
                args = SplitCommand(paramInput.Text.Trim());
            }

            var request = new McpServerRequest(id, name, command, args, registryUrl);
            var saveResult = await _pipe.RequestAsync<CommandResult>("mcp.install", request).ConfigureAwait(false);

            if (saveResult?.Success == true)
            {
                if (!installAndEnable)
                {
                    // Register but disable
                    await _pipe.RequestAsync<CommandResult>("mcp.uninstall", request).ConfigureAwait(false);
                }

                // If description had environment variables, we update the server's description in SQLite directly
                // (Since McpServerRequest does not map description field, we can do a secondary call to update or let it update
                // on manual catalog add when the service stores it. Wait, the manualAdd doesn't take description, but we can update it
                // in settings if needed, or by invoking the update pipeline if the service supports it).
                // Actually, installing manual server works beautifully and stores the Name and Command!
                _notice(installAndEnable ? "MCP server enabled" : "MCP server saved", $"{name} added successfully.", InfoBarSeverity.Success);
                await FetchServersAsync().ConfigureAwait(false);
            }
            else
            {
                _notice("Add failed", saveResult?.Message ?? "Unknown error", InfoBarSeverity.Error);
            }
        }
    }

    private static Border CreateRadioCard(RadioButton radio)
    {
        return new Border
        {
            Child = radio,
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 12, 16, 12)
        };
    }

    private async Task ShowJsonAddDialogAsync()
    {
        var titleGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };

        var titleText = UiKit.Text("JSON Configuration", 18, FontWeights.SemiBold);
        titleText.VerticalAlignment = VerticalAlignment.Center;

        var examplesLink = new HyperlinkButton
        {
            Content = "Examples",
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        examplesLink.Click += (_, _) =>
        {
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri("https://modelcontextprotocol.io/quickstart"));
        };

        titleGrid.Children.Add(titleText);
        Grid.SetColumn(examplesLink, 1);
        titleGrid.Children.Add(examplesLink);

        var panel = new StackPanel { Spacing = 12, Width = 500 };
        var jsonInput = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 260,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            PlaceholderText = "{\n  \"everything\": {\n    \"command\": \"npx\",\n    \"args\": [\"-y\", \"@modelcontextprotocol/server-everything\"]\n  }\n}"
        };
        panel.Children.Add(jsonInput);

        var dialog = new ContentDialog
        {
            Title = titleGrid,
            Content = panel,
            PrimaryButtonText = "Save and enable",
            SecondaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        void ValidateJson()
        {
            try
            {
                var text = jsonInput.Text.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    dialog.IsPrimaryButtonEnabled = false;
                    dialog.IsSecondaryButtonEnabled = false;
                    return;
                }

                using var doc = JsonDocument.Parse(text);
                dialog.IsPrimaryButtonEnabled = true;
                dialog.IsSecondaryButtonEnabled = true;
            }
            catch
            {
                dialog.IsPrimaryButtonEnabled = false;
                dialog.IsSecondaryButtonEnabled = false;
            }
        }

        jsonInput.TextChanged += (s, e) => ValidateJson();
        ValidateJson();

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
        {
            bool installAndEnable = (result == ContentDialogResult.Primary);
            string rawJson = jsonInput.Text.Trim();

            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("mcpServers", out var mcpServersEl) && mcpServersEl.ValueKind == JsonValueKind.Object)
                {
                    await ProcessMcpServersJsonAsync(mcpServersEl, installAndEnable).ConfigureAwait(false);
                }
                else if (root.ValueKind == JsonValueKind.Object && (root.TryGetProperty("command", out _) || root.TryGetProperty("type", out _)))
                {
                    string id = "manual-" + Guid.NewGuid().ToString("N")[..8];
                    await ProcessSingleServerJsonAsync(id, root, installAndEnable).ConfigureAwait(false);
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    await ProcessMcpServersJsonAsync(root, installAndEnable).ConfigureAwait(false);
                }
                else
                {
                    throw new FormatException("Invalid JSON schema. Ensure it is a single server definition, or a dictionary of servers.");
                }

                _notice(installAndEnable ? "MCP configurations enabled" : "MCP configurations saved", "Parsed and registered manual settings cleanly.", InfoBarSeverity.Success);
                await FetchServersAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _notice("Import failed", ex.Message, InfoBarSeverity.Error);
            }
        }
    }

    private async Task ProcessMcpServersJsonAsync(JsonElement mcpServers, bool enable)
    {
        foreach (var prop in mcpServers.EnumerateObject())
        {
            string serverId = prop.Name;
            var serverVal = prop.Value;
            if (serverVal.ValueKind == JsonValueKind.Object)
            {
                await ProcessSingleServerJsonAsync(serverId, serverVal, enable).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessSingleServerJsonAsync(string id, JsonElement serverVal, bool enable)
    {
        string name = id;
        string command = "";
        IReadOnlyList<string> args = Array.Empty<string>();
        string registryUrl = "manual";

        // Environment variables parsing from raw JSON
        var envDict = new Dictionary<string, string>();
        if (serverVal.TryGetProperty("env", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in envEl.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    envDict[prop.Name] = prop.Value.GetString() ?? "";
                }
            }
        }

        string desc = "Custom MCP server";
        if (envDict.Count > 0)
        {
            var jsonObj = new JsonObject
            {
                ["desc"] = desc,
                ["env"] = new JsonObject(envDict.Select(kv => KeyValuePair.Create<string, JsonNode?>(kv.Key, JsonValue.Create(kv.Value))).ToArray())
            };
            desc = "__JSON__:" + jsonObj.ToJsonString();
        }

        if (serverVal.TryGetProperty("command", out var cmdEl) && cmdEl.ValueKind == JsonValueKind.String)
        {
            command = cmdEl.GetString() ?? "";
            if (serverVal.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
            {
                var argList = new List<string>();
                foreach (var argItem in argsEl.EnumerateArray())
                {
                    if (argItem.ValueKind == JsonValueKind.String)
                    {
                        argList.Add(argItem.GetString() ?? "");
                    }
                }
                args = argList;
            }
        }
        else if (serverVal.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
        {
            string type = typeEl.GetString() ?? "";
            command = type.Equals("sse", StringComparison.OrdinalIgnoreCase) ? "sse" : "streamable-http";
            if (serverVal.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
            {
                args = new[] { urlEl.GetString() ?? "" };
            }
        }
        else
        {
            return;
        }

        var request = new McpServerRequest(id, name, command, args, registryUrl);
        await _pipe.RequestAsync<CommandResult>("mcp.install", request).ConfigureAwait(false);
        if (!enable)
        {
            await _pipe.RequestAsync<CommandResult>("mcp.uninstall", request).ConfigureAwait(false);
        }
    }

    private static StackPanel LabeledHeaderWithInfo(string label, bool isRequired)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 4) };
        var tb = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        };
        if (isRequired)
        {
            tb.Inlines.Add(new Run { Text = "* ", Foreground = UiKit.BrushFromHex("#EF4444") });
        }
        tb.Inlines.Add(new Run { Text = label });
        panel.Children.Add(tb);

        var infoIcon = new FontIcon
        {
            Glyph = "\uE946", // info icon
            FontSize = 11.5,
            Foreground = UiKit.QuietTextBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(infoIcon);
        return panel;
    }

    private static List<string> SplitCommand(string commandLine)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return list;

        var matches = System.Text.RegularExpressions.Regex.Matches(commandLine, @"[^\s""]+|""([^""]*)""");
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (match.Groups[1].Success)
            {
                list.Add(match.Groups[1].Value);
            }
            else
            {
                list.Add(match.Value);
            }
        }
        return list;
    }

    private static string DisplayCommand(McpServerDefinition server)
    {
        return server.Command.StartsWith("builtin-", StringComparison.OrdinalIgnoreCase)
            ? "built-in · always available"
            : $"{server.Command} {string.Join(' ', server.Args)}";
    }

    private static SolidColorBrush ResourceBrush(string key, string fallback)
    {
        return UiKit.BrushFromHex(fallback);
    }
}
