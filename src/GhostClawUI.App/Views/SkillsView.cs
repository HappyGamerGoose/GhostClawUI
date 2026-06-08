using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GhostClawUI.App.Services;
using GhostClawUI.App.Ui;
using GhostClawUI.Shared;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace GhostClawUI.App.Views;

/// <summary>
/// Skills browser — lists all installed skill files with their name, description,
/// and a preview of the skill content. Skills can be copied to clipboard.
/// </summary>
internal sealed class SkillsView : UserControl
{
    private readonly PipeClient _pipe;
    private readonly nint _hwnd;
    private readonly Action<string, string, InfoBarSeverity> _notice;
    private readonly StackPanel _listPanel = new() { Spacing = 10 };
    private readonly TextBox _search;
    private IReadOnlyList<SkillSummary> _allSkills = Array.Empty<SkillSummary>();

    public SkillsView(PipeClient pipe, nint hwnd, Action<string, string, InfoBarSeverity> notice)
    {
        _pipe = pipe;
        _hwnd = hwnd;
        _notice = notice;
        _search = UiKit.TextBox("Search skills…", "Skill search");
        Content = Build();
        _ = LoadSkillsAsync();
    }

    private UIElement Build()
    {
        var root = UiKit.Page();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowSpacing = 16;
        root.MaxWidth = 1100;
        root.HorizontalAlignment = HorizontalAlignment.Center;

        var top = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(300) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };

        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(UiKit.Text("Skills Library", 24, FontWeights.SemiBold));
        top.Children.Add(heading);

        Grid.SetColumn(_search, 1);
        _search.HorizontalAlignment = HorizontalAlignment.Stretch;
        _search.TextChanged += (_, _) => FilterSkills(_search.Text);
        top.Children.Add(_search);

        var addSkillBtn = UiKit.PrimaryButton("Add Skill", Symbol.Add, async (_, _) => await AddSkillDialogAsync());
        Grid.SetColumn(addSkillBtn, 2);
        top.Children.Add(addSkillBtn);

        var refreshBtn = UiKit.Button("Refresh", Symbol.Refresh, async (_, _) => await LoadSkillsAsync());
        Grid.SetColumn(refreshBtn, 3);
        top.Children.Add(refreshBtn);

        Grid.SetRow(top, 0);
        root.Children.Add(top);

        // Skills list
        var scroll = new ScrollViewer
        {
            Content = _listPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 4, 0)
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        return root;
    }

    private async Task LoadSkillsAsync()
    {
        try
        {
            _listPanel.Children.Clear();
            _listPanel.Children.Add(LoadingState());

            _allSkills = await _pipe.RequestAsync<IReadOnlyList<SkillSummary>>("skills.list") ?? Array.Empty<SkillSummary>();
            FilterSkills(_search.Text);
        }
        catch (Exception ex)
        {
            _listPanel.Children.Clear();
            _listPanel.Children.Add(UiKit.Text($"Could not load skills: {ex.Message}", 14));
        }
    }

    private void FilterSkills(string? query)
    {
        _listPanel.Children.Clear();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allSkills
            : _allSkills.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                 || s.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        if (filtered.Count == 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 8, Margin = new Thickness(0, 60, 0, 0) };
            empty.Children.Add(new FontIcon { Glyph = "\uE82D", FontSize = 32, Foreground = UiKit.QuietTextBrush });
            empty.Children.Add(UiKit.Muted(string.IsNullOrWhiteSpace(query) ? "No skills installed." : "No skills match your search.", 14));
            _listPanel.Children.Add(empty);
            return;
        }

        foreach (var skill in filtered)
        {
            _listPanel.Children.Add(SkillCard(skill));
        }
    }

    private FrameworkElement SkillCard(SkillSummary skill)
    {
        var card = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 12
        };

        // Left: info
        var left = new StackPanel { Spacing = 4 };

        // Name row
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        nameRow.Children.Add(UiKit.Text(skill.Name, 15, FontWeights.SemiBold));

        // Skill category badge from filename
        var categoryBadge = InferCategory(skill.Id);
        if (categoryBadge is not null)
        {
            var pill = UiKit.Pill(categoryBadge, UiKit.AccentBrush);
            pill.Padding = new Thickness(6, 2, 6, 2);
            if (pill.Child is TextBlock pillTb) pillTb.FontSize = 11;
            nameRow.Children.Add(pill);
        }
        left.Children.Add(nameRow);

        // Description
        if (!string.IsNullOrWhiteSpace(skill.Description))
        {
            var descText = UiKit.Muted(skill.Description, 13);
            descText.TextWrapping = TextWrapping.Wrap;
            descText.TextTrimming = TextTrimming.CharacterEllipsis;
            descText.MaxLines = 2;
            left.Children.Add(descText);
        }

        // File path with inline copy button
        var pathStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        
        var pathText = UiKit.Muted(Path.GetFileName(skill.FilePath), 11);
        pathText.VerticalAlignment = VerticalAlignment.Center;
        pathStack.Children.Add(pathText);

        var copyBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE8C8", FontSize = 12 },
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = 24,
            MinWidth = 24
        };
        UiKit.AddHoverScale(copyBtn);
        AutomationProperties.SetName(copyBtn, $"Copy skill {skill.Name}");
        copyBtn.Click += async (_, _) => await CopySkillAsync(skill);
        ToolTipService.SetToolTip(copyBtn, "Copy skill content");
        pathStack.Children.Add(copyBtn);

        left.Children.Add(pathStack);
        card.Children.Add(left);

        var container = UiKit.Card(card);
        container.Margin = new Thickness(0, 0, 0, 2);
        return container;
    }

    private async Task PreviewSkillAsync(SkillSummary skill)
    {
        try
        {
            var result = await _pipe.RequestAsync<CommandResult>("skills.read", new SimpleIdRequest(skill.Id));
            if (result?.Success != true)
            {
                _notice("Preview failed", result?.Message ?? "Unknown error", InfoBarSeverity.Error);
                return;
            }

            var content = result.Message ?? string.Empty;
            var preview = content.Length > 3000 ? content[..3000] + "\n…[truncated]" : content;

            var textBox = new TextBox
            {
                Text = preview,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
                FontSize = 12
            };
            var previewScroll = new ScrollViewer
            {
                Content = textBox,
                Height = 400,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var dialog = new ContentDialog
            {
                Title = skill.Name,
                Content = previewScroll,
                CloseButtonText = "Close",
                PrimaryButtonText = "Copy to Clipboard",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            var dialogResult = await dialog.ShowAsync();
            if (dialogResult == ContentDialogResult.Primary)
            {
                await CopyToClipboardAsync(content);
            }
        }
        catch (Exception ex)
        {
            _notice("Preview failed", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task CopySkillAsync(SkillSummary skill)
    {
        try
        {
            var result = await _pipe.RequestAsync<CommandResult>("skills.read", new SimpleIdRequest(skill.Id));
            if (result?.Success == true && !string.IsNullOrWhiteSpace(result.Message))
            {
                await CopyToClipboardAsync(result.Message);
                _notice("Copied", $"'{skill.Name}' content copied to clipboard.", InfoBarSeverity.Success);
            }
            else
            {
                _notice("Copy failed", result?.Message ?? "Unknown error", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            _notice("Copy failed", ex.Message, InfoBarSeverity.Error);
        }
    }

    private static async Task CopyToClipboardAsync(string text)
    {
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        await Task.CompletedTask;
    }

    private async Task AddSkillDialogAsync()
    {
        var panel = new StackPanel { Spacing = 12, Width = 400 };

        var nameLabel = UiKit.Text("Skill Name", 13, FontWeights.SemiBold);
        var nameInput = UiKit.TextBox("e.g. PDF Writer", "Skill Name Input");

        var descLabel = UiKit.Text("Description", 13, FontWeights.SemiBold);
        var descInput = UiKit.TextBox("Briefly describe what this skill does", "Skill Description Input");

        var fileLabel = UiKit.Text("Skill Markdown File (.md)", 13, FontWeights.SemiBold);
        var fileRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8
        };

        var fileStatus = UiKit.Muted("No file selected", 13);
        fileStatus.VerticalAlignment = VerticalAlignment.Center;
        fileRow.Children.Add(fileStatus);

        string? selectedContent = null;

        var browseBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new SymbolIcon(Symbol.Find),
                    UiKit.Text("Browse…", 13)
                }
            },
            CornerRadius = new CornerRadius(6)
        };
        Grid.SetColumn(browseBtn, 1);
        fileRow.Children.Add(browseBtn);

        panel.Children.Add(nameLabel);
        panel.Children.Add(nameInput);
        panel.Children.Add(descLabel);
        panel.Children.Add(descInput);
        panel.Children.Add(fileLabel);
        panel.Children.Add(fileRow);

        browseBtn.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".md");

            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                try
                {
                    selectedContent = await FileIO.ReadTextAsync(file);
                    fileStatus.Text = file.Name;
                    fileStatus.Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

                    if (string.IsNullOrWhiteSpace(nameInput.Text))
                    {
                        var potentialName = file.DisplayName
                            .Replace("_SKILL", "", StringComparison.OrdinalIgnoreCase)
                            .Replace("-", " ")
                            .Replace("_", " ");
                        nameInput.Text = potentialName;
                    }
                }
                catch (Exception ex)
                {
                    _notice("Error reading file", ex.Message, InfoBarSeverity.Error);
                }
            }
        };

        var dialog = new ContentDialog
        {
            Title = "Add New Skill",
            Content = panel,
            CloseButtonText = "Cancel",
            PrimaryButtonText = "Save Skill",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var dialogResult = await dialog.ShowAsync();
        if (dialogResult == ContentDialogResult.Primary)
        {
            var name = nameInput.Text?.Trim();
            var desc = descInput.Text?.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                _notice("Validation Error", "Skill name is required.", InfoBarSeverity.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(selectedContent))
            {
                _notice("Validation Error", "Please select a valid markdown file containing the skill instructions.", InfoBarSeverity.Warning);
                return;
            }

            try
            {
                var result = await _pipe.RequestAsync<CommandResult>("skills.upsert", new SkillUpsertRequest(name, desc ?? string.Empty, selectedContent));
                if (result?.Success == true)
                {
                    _notice("Success", $"Skill '{name}' added successfully.", InfoBarSeverity.Success);
                    _ = LoadSkillsAsync();
                }
                else
                {
                    _notice("Error", result?.Message ?? "Failed to save skill.", InfoBarSeverity.Error);
                }
            }
            catch (Exception ex)
            {
                _notice("Error", $"Request failed: {ex.Message}", InfoBarSeverity.Error);
            }
        }
    }

    private static string? InferCategory(string id) => id switch
    {
        var s when s.Contains("pdf") => "PDF",
        var s when s.Contains("docx") || s.Contains("doc-") => "Documents",
        var s when s.Contains("xlsx") || s.Contains("spreadsheet") => "Spreadsheets",
        var s when s.Contains("pptx") || s.Contains("slide") => "Presentations",
        var s when s.Contains("frontend") || s.Contains("canvas") || s.Contains("web") => "Frontend",
        var s when s.Contains("mcp") => "MCP",
        var s when s.Contains("skill-creator") || s.Contains("algorithmic") => "Meta",
        var s when s.Contains("brand") || s.Contains("theme") || s.Contains("internal") => "Brand",
        var s when s.Contains("slack") || s.Contains("gif") => "Communication",
        _ => null
    };

    private static FrameworkElement LoadingState()
    {
        var ring = new ProgressRing { Width = 24, Height = 24, IsActive = true, HorizontalAlignment = HorizontalAlignment.Center };
        var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) };
        panel.Children.Add(ring);
        panel.Children.Add(UiKit.Muted("Loading skills…", 14));
        return panel;
    }
}
