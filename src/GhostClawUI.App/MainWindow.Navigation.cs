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
    public void ShowPage(string page)
    {
        _currentPage = page;
        UpdateNavSelection();
        _content.Content = page switch
        {
            "Onboarding" => new OnboardingView(() => ShowPage("Providers"), () => ShowPage("MCPs"), () => ShowPage("Chat")),
            "Providers" => new ProvidersView(_pipe, _vault, ShowNotice),
            "MCPs" => new McpStoreView(_pipe, ShowNotice),
            "MCP Store" => new McpStoreView(_pipe, ShowNotice),
            "Memory" => new MemoryView(_pipe, ShowNotice),
            "Skills" => new SkillsView(_pipe, Hwnd, ShowNotice),
            "Appearance" => new AppearanceView(_pipe, _settings, settings =>
            {
                _settings = settings;
                ApplyAppearance(settings.Appearance);
            }, ShowNotice),
            "Social" => new SocialView(_pipe, ShowNotice),
            "Settings" => new SettingsView(_pipe, _settings, Hwnd, SaveExportAsync, ShowNotice),
            _ => new ChatView(_pipe, _vault, () => _settings, Hwnd, _currentConversationId, async id =>
            {
                _currentConversationId = id;
                await RefreshConversationsAsync().ConfigureAwait(false);
            }, ShowNotice)
        };
    }
    private async Task RefreshConversationsAsync(string? query = null)
    {
        try
        {
            var conversations = await _pipe.RequestAsync<IReadOnlyList<ConversationSummary>>("conversations.list", new SimpleTextRequest(query ?? string.Empty)).ConfigureAwait(false) ?? Array.Empty<ConversationSummary>();
            _conversationList.ItemsSource = null;
            _conversationList.DisplayMemberPath = null;
            _conversationList.Items.Clear();
            foreach (var conversation in conversations)
            {
                _conversationList.Items.Add(ConversationItem(conversation));
            }
        }
        catch
        {
            _conversationList.ItemsSource = null;
            _conversationList.Items.Clear();
        }
    }

    private ListViewItem ConversationItem(ConversationSummary summary)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8,
            Padding = new Thickness(4, 4, 2, 4)
        };
        var title = new TextBlock
        {
            Text = summary.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = UiKit.SidebarTextBrush
        };
        row.Children.Add(title);

        var time = new TextBlock
        {
            Text = summary.UpdatedAt.ToLocalTime().ToString("t"),
            FontSize = 11,
            Foreground = UiKit.SidebarMutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(time, 1);
        row.Children.Add(time);

        var rename = new Button
        {
            Content = new SymbolIcon(Symbol.Edit),
            Foreground = UiKit.SidebarMutedBrush,
            Width = 30,
            Height = 30,
            MinWidth = 30,
            MinHeight = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(rename, $"Rename conversation {summary.Title}");

        var renameInput = new TextBox { Text = summary.Title, Width = 200, Margin = new Thickness(0, 0, 0, 8) };
        var saveBtn = new Button
        {
            Content = "Save",
            Background = UiKit.AccentBrush,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var flyoutPanel = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(10)
        };
        flyoutPanel.Children.Add(new TextBlock { Text = "Rename Conversation", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = UiKit.SidebarTextBrush });
        flyoutPanel.Children.Add(renameInput);
        flyoutPanel.Children.Add(saveBtn);
        var flyout = new Flyout { Content = flyoutPanel };
        rename.Flyout = flyout;

        saveBtn.Click += async (_, _) =>
        {
            var newTitle = renameInput.Text.Trim();
            if (string.IsNullOrEmpty(newTitle)) return;

            try
            {
                await _pipe.RequestAsync<CommandResult>("conversations.rename", new RenameConversationRequest(summary.Id, newTitle)).ConfigureAwait(false);
                flyout.Hide();
                await RefreshConversationsAsync().ConfigureAwait(false);
                ShowNotice("Conversation renamed", newTitle, InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowNotice("Rename failed", ex.Message, InfoBarSeverity.Error);
            }
        };

        Grid.SetColumn(rename, 2);
        row.Children.Add(rename);

        var delete = new Button
        {
            Content = new SymbolIcon(Symbol.Delete),
            Foreground = UiKit.SidebarMutedBrush,
            Width = 30,
            Height = 30,
            MinWidth = 30,
            MinHeight = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            // Hidden by default; shown only when the user hovers over the conversation item
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(delete, $"Delete conversation {summary.Title}");
        delete.Click += async (_, _) =>
        {
            try
            {
                await _pipe.RequestAsync<CommandResult>("conversations.delete", new SimpleIdRequest(summary.Id)).ConfigureAwait(false);
                if (_currentConversationId == summary.Id)
                {
                    _currentConversationId = null;
                    ShowPage("Chat");
                }

                await RefreshConversationsAsync().ConfigureAwait(false);
                ShowNotice("Conversation deleted", summary.Title, InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowNotice("Delete failed", ex.Message, InfoBarSeverity.Error);
            }
        };
        Grid.SetColumn(delete, 3);
        row.Children.Add(delete);

        var item = new ListViewItem
        {
            Tag = summary,
            Content = row,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(4, 2, 4, 2),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Foreground = UiKit.SidebarTextBrush
        };

        // Show rename and delete buttons only when the row is hovered
        item.PointerEntered += (_, _) => { rename.Visibility = Visibility.Visible; delete.Visibility = Visibility.Visible; };
        item.PointerExited += (_, _) => { rename.Visibility = Visibility.Collapsed; delete.Visibility = Visibility.Collapsed; };

        var contextMenu = new MenuFlyout();
        var exportItem = new MenuFlyoutItem
        {
            Text = "Export chat as PDF",
            Icon = new SymbolIcon(Symbol.Document)
        };
        exportItem.Click += async (_, _) =>
        {
            try
            {
                var conversation = await _pipe.RequestAsync<ConversationDetail>("conversations.get", new SimpleIdRequest(summary.Id)).ConfigureAwait(false);
                if (conversation is null || conversation.Messages.Count == 0)
                {
                    ShowNotice("Export Failed", "Could not retrieve messages for this conversation.", InfoBarSeverity.Error);
                    return;
                }

                var savePicker = new Windows.Storage.Pickers.FileSavePicker();
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, Hwnd);
                savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                savePicker.FileTypeChoices.Add("PDF Document", new List<string>() { ".pdf" });
                savePicker.SuggestedFileName = $"{conversation.Summary.Title.Replace(" ", "_")}_Transcript.pdf";

                var file = await savePicker.PickSaveFileAsync();
                if (file != null)
                {
                    try
                    {
                        byte[] pdfBytes = PdfExporter.ExportToPdf(conversation.Summary.Title, conversation.Messages);
                        await Windows.Storage.FileIO.WriteBytesAsync(file, pdfBytes);
                        ShowNotice("PDF Exported", $"Conversation saved to {file.Name}", InfoBarSeverity.Success);
                    }
                    catch
                    {
                        try
                        {
                            await file.DeleteAsync();
                        }
                        catch { /* ignore fallback delete error */ }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                ShowNotice("Export Failed", ex.Message, InfoBarSeverity.Error);
            }
        };
        contextMenu.Items.Add(exportItem);
        item.ContextFlyout = contextMenu;

        return item;
    }

    private async Task SaveExportAsync(ExportResult export)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GhostClawUI Exports");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, export.FileName);
        await File.WriteAllTextAsync(path, export.Content).ConfigureAwait(false);
        ShowNotice("Export saved", path, InfoBarSeverity.Success);
    }

    private void ShowNotice(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        _noticeHost.Children.Add(UiKit.Info(title, message, severity));
    }

    private void OpenQuickPrompt()
    {
        var quick = new QuickPromptWindow(async text =>
        {
            ShowPage("Chat");
            if (_content.Content is ChatView chat)
            {
                await chat.SendQuickPromptAsync(text).ConfigureAwait(false);
            }
        });
        quick.Activate();
    }
}
