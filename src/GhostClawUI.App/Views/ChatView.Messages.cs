using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
using System.IO;
using System.IO.Compression;
using Microsoft.UI.Xaml.Controls.Primitives;
using GhostClawUI.App.Services;
using GhostClawUI.App.Ui;
using GhostClawUI.Shared;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace GhostClawUI.App.Views;

internal sealed partial class ChatView
{

    private void DrawMessages(IReadOnlyList<ChatMessage> messages)
    {
        _messages.Children.Clear();
        var visible = messages.Where(message => message.Kind != "status").ToList();
        if (visible.Count == 0)
        {
            DrawEmptyState("Ask GhostClaw anything.");
            return;
        }

        foreach (var message in visible)
        {
            _messages.Children.Add(MessageRow(message));
        }

        _ = ScrollToEndAsync();
    }


    private Grid MessageRow(ChatMessage message)
    {
        var isUser = message.Role.Equals("user", StringComparison.OrdinalIgnoreCase);
        var isError = message.Kind.Equals("error", StringComparison.OrdinalIgnoreCase);
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            ColumnSpacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var bubble = Bubble(message, isUser, isError);
        if (isUser)
        {
            bubble.HorizontalAlignment = HorizontalAlignment.Right;
            bubble.MaxWidth = 960;
            Grid.SetColumn(bubble, 1);
            row.Children.Add(bubble);
            return row;
        }

        row.Children.Add(Avatar(message.Model));
        Grid.SetColumn(bubble, 1);
        bubble.HorizontalAlignment = HorizontalAlignment.Left;
        bubble.MaxWidth = 1100;
        row.Children.Add(bubble);
        return row;
    }


    private StackPanel Bubble(ChatMessage message, bool isUser, bool isError)
    {
        Border? bubbleBorder = null;
        var panel = new StackPanel { Spacing = 8 };
        var visibleContent = isUser ? message.Content : ResponseTextSanitizer.CleanForDisplay(message.Content);
        if (!isUser && HasGeneratedFiles(message.Metadata))
        {
            visibleContent = StripFileGenerationCode(visibleContent);
        }

        if (!isUser && message.Metadata != null && message.Metadata["traces"] is JsonNode tracesNode)
        {
            try
            {
                var traces = JsonSerializer.Deserialize<List<AgentTraceCard>>(tracesNode.ToJsonString(), PipeJson.Options);
                if (traces != null && traces.Count > 0)
                {
                    // Clean up any lingering "running" states for completed/static database messages
                    for (int i = 0; i < traces.Count; i++)
                    {
                        if (traces[i].State == "running")
                        {
                            traces[i] = traces[i] with { State = "done" };
                        }
                    }

                    var filteredTraces = traces.Where(t =>
                        !(t.Title == "Reasoning" && string.Equals(t.Detail?.Trim(), visibleContent?.Trim(), StringComparison.OrdinalIgnoreCase)) &&
                        !(t.Title == "Thinking" && string.Equals(t.Detail?.Trim(), visibleContent?.Trim(), StringComparison.OrdinalIgnoreCase))
                    ).ToList();

                    if (filteredTraces.Count > 0)
                    {
                        panel.Children.Add(TracesExpander(filteredTraces));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error deserializing traces: {ex}");
            }
        }

        RenderContent(panel, visibleContent, isUser);
        if (!isUser)
        {
            DetectAndAddLocalFiles(panel, message.Content);
        }
        foreach (var attachment in ReadAttachments(message.Metadata))
        {
            panel.Children.Add(AttachmentPreview(attachment, isUser, removable: false));
        }

        var actionPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
            Opacity = 0,
            IsHitTestVisible = false
        };

        if (isUser)
        {
            var editBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE70F", FontSize = 10 },
                Width = 24,
                Height = 24,
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255)),
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Colors.White)
            };
            AutomationProperties.SetName(editBtn, "Edit prompt");
            HookClick(editBtn, (_, _) =>
            {
                if (bubbleBorder != null)
                {
                    ShowInlineEditor(bubbleBorder, message, isUser: true);
                }
            });
            actionPanel.Children.Add(editBtn);
        }
        else if (isError)
        {
            var retryBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE72C", FontSize = 10 },
                Width = 24,
                Height = 24,
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(15, 255, 255, 255)),
                BorderThickness = new Thickness(0),
                Foreground = SecondaryTextBrush()
            };
            AutomationProperties.SetName(retryBtn, "Retry request");
            HookClick(retryBtn, async (_, _) =>
            {
                string? lastUserPrompt = null;
                string? lastUserId = null;
                try
                {
                    var conversation = await _pipe.RequestAsync<ConversationDetail>("conversations.get", new SimpleIdRequest(_conversationId ?? string.Empty)).ConfigureAwait(false);
                    if (conversation is not null)
                    {
                        var lastUser = conversation.Messages.LastOrDefault(m => m.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
                        if (lastUser is not null)
                        {
                            lastUserPrompt = lastUser.Content;
                            lastUserId = lastUser.Id;
                        }
                    }
                }
                catch
                {
                }

                if (!string.IsNullOrEmpty(lastUserPrompt) && !string.IsNullOrEmpty(lastUserId))
                {
                    try
                    {
                        // Roll back conversation from this user prompt (inclusive)
                        await _pipe.RequestAsync<CommandResult>("conversations.deleteMessagesAfter", new DeleteMessagesAfterRequest(_conversationId ?? string.Empty, lastUserId)).ConfigureAwait(false);

                        // Immediately reload in the UI to wipe off the old response!
                        await LoadAsync().ConfigureAwait(false);

                        // Resubmit the prompt
                        await SendQuickPromptAsync(lastUserPrompt).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _notice("Re-generation failed", ex.Message, InfoBarSeverity.Error);
                    }
                }
                else
                {
                    _notice("Re-generation unavailable", "Could not locate the previous user message.", InfoBarSeverity.Warning);
                }
            });
            actionPanel.Children.Add(retryBtn);
        }
        else
        {
            var copyBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uF0E3", FontSize = 10 },
                Width = 24,
                Height = 24,
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(15, 255, 255, 255)),
                BorderThickness = new Thickness(0),
                Foreground = SecondaryTextBrush()
            };
            AutomationProperties.SetName(copyBtn, "Copy plain text");
            HookClick(copyBtn, (_, _) =>
            {
                var cleanText = CleanMarkdownForClipboard(message.Content);
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(cleanText);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                _notice("Copied to Clipboard", "Message text copied in plain text format.", InfoBarSeverity.Success);
            });
            actionPanel.Children.Add(copyBtn);

            var editBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE70F", FontSize = 10 },
                Width = 24,
                Height = 24,
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(15, 255, 255, 255)),
                BorderThickness = new Thickness(0),
                Foreground = SecondaryTextBrush()
            };
            AutomationProperties.SetName(editBtn, "Edit response");
            HookClick(editBtn, (_, _) =>
            {
                if (bubbleBorder != null)
                {
                    ShowInlineEditor(bubbleBorder, message, isUser: false);
                }
            });
            actionPanel.Children.Add(editBtn);

            var retryBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE72C", FontSize = 10 },
                Width = 24,
                Height = 24,
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(15, 255, 255, 255)),
                BorderThickness = new Thickness(0),
                Foreground = SecondaryTextBrush()
            };
            AutomationProperties.SetName(retryBtn, "Re-generate response");
            HookClick(retryBtn, async (_, _) =>
            {
                string? lastUserPrompt = null;
                string? lastUserId = null;
                try
                {
                    var conversation = await _pipe.RequestAsync<ConversationDetail>("conversations.get", new SimpleIdRequest(_conversationId ?? string.Empty)).ConfigureAwait(false);
                    if (conversation is not null)
                    {
                        var lastUser = conversation.Messages.LastOrDefault(m => m.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
                        if (lastUser is not null)
                        {
                            lastUserPrompt = lastUser.Content;
                            lastUserId = lastUser.Id;
                        }
                    }
                }
                catch
                {
                }

                if (!string.IsNullOrEmpty(lastUserPrompt) && !string.IsNullOrEmpty(lastUserId))
                {
                    try
                    {
                        // Roll back conversation from this user prompt (inclusive)
                        await _pipe.RequestAsync<CommandResult>("conversations.deleteMessagesAfter", new DeleteMessagesAfterRequest(_conversationId ?? string.Empty, lastUserId)).ConfigureAwait(false);

                        // Immediately reload in the UI to wipe off the old response!
                        await LoadAsync().ConfigureAwait(false);

                        // Resubmit the prompt
                        await SendQuickPromptAsync(lastUserPrompt).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _notice("Re-generation failed", ex.Message, InfoBarSeverity.Error);
                    }
                }
                else
                {
                    _notice("Re-generation unavailable", "Could not locate the previous user message.", InfoBarSeverity.Warning);
                }
            });
            actionPanel.Children.Add(retryBtn);
        }

        bubbleBorder = new Border
        {
            Child = panel,
            Padding = new Thickness(20, 16, 20, 16),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            BorderBrush = isError
                ? UiKit.BrushFromHex("#FB923C")
                : isUser ? UserBubbleBorderBrush() : StrokeBrush(),
            Background = isError
                ? ErrorSurfaceBrush()
                : isUser ? UserBubbleBrush() : AssistantBubbleBrush()
        };
        UiKit.AddElevation(bubbleBorder, 16);

        var container = new StackPanel
        {
            Spacing = 4,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), // Intercept hover events continuously across gaps
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Transitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection
            {
                new Microsoft.UI.Xaml.Media.Animation.EntranceThemeTransition { FromVerticalOffset = 16, IsStaggeringEnabled = false }
            }
        };
        container.Children.Add(bubbleBorder);

        actionPanel.HorizontalAlignment = HorizontalAlignment.Right;
        actionPanel.Margin = new Thickness(0, 0, 4, 0);
        container.Children.Add(actionPanel);

        System.Threading.CancellationTokenSource? hideCts = null;

        HookPointer(container,
            (s, e) =>
            {
                hideCts?.Cancel();
                hideCts = null;
                actionPanel.Opacity = 1;
                actionPanel.IsHitTestVisible = true;
            },
            (s, e) =>
            {
                hideCts?.Cancel();
                hideCts = new System.Threading.CancellationTokenSource();
                var token = hideCts.Token;
                Task.Delay(350, token).ContinueWith(t =>
                {
                    if (!t.IsCanceled && !token.IsCancellationRequested)
                    {
                        container.DispatcherQueue.TryEnqueue(() =>
                        {
                            if (!token.IsCancellationRequested)
                            {
                                actionPanel.Opacity = 0;
                                actionPanel.IsHitTestVisible = false;
                            }
                        });
                    }
                }, TaskScheduler.Default);
            });

        return container;
    }


    private void ShowInlineEditor(Border bubbleBorder, ChatMessage message, bool isUser)
    {
        var currentText = message.Content;

        var editBox = new TextBox
        {
            Text = currentText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 60,
            MaxHeight = 300,
            Width = isUser ? 520 : 740,
            FontSize = _settings().Appearance.FontSize,
            FontFamily = new FontFamily(_settings().Appearance.FontFamily),
            Background = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255)) : ControlSurfaceBrush(),
            Foreground = isUser ? new SolidColorBrush(Colors.White) : PrimaryTextBrush(),
            BorderThickness = new Thickness(1),
            BorderBrush = StrokeBrush()
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var saveButton = new Button
        {
            Content = isUser ? "Save & Submit" : "Save",
            Background = UiKit.AccentBrush,
            Foreground = new SolidColorBrush(Colors.White),
            CornerRadius = new CornerRadius(6)
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = StrokeBrush(),
            BorderThickness = new Thickness(1),
            Foreground = isUser ? new SolidColorBrush(Colors.White) : PrimaryTextBrush(),
            CornerRadius = new CornerRadius(6)
        };

        buttonPanel.Children.Add(saveButton);
        buttonPanel.Children.Add(cancelButton);

        var editorPanel = new StackPanel { Spacing = 4 };
        editorPanel.Children.Add(editBox);
        editorPanel.Children.Add(buttonPanel);

        var originalContent = bubbleBorder.Child;
        var originalBackground = bubbleBorder.Background;
        var originalBorderBrush = bubbleBorder.BorderBrush;

        bubbleBorder.Child = editorPanel;
        bubbleBorder.Background = isUser ? UiKit.AccentBrush : AssistantBubbleBrush();

        HookClick(cancelButton, (_, _) =>
        {
            bubbleBorder.Child = originalContent;
            bubbleBorder.Background = originalBackground;
            bubbleBorder.BorderBrush = originalBorderBrush;
        });

        HookClick(saveButton, async (_, _) =>
        {
            var newText = editBox.Text.Trim();
            if (string.IsNullOrEmpty(newText)) return;

            if (newText == currentText)
            {
                bubbleBorder.Child = originalContent;
                bubbleBorder.Background = originalBackground;
                bubbleBorder.BorderBrush = originalBorderBrush;
                return;
            }

            saveButton.IsEnabled = false;
            cancelButton.IsEnabled = false;

            try
            {
                if (isUser)
                {
                    // Sync provider and model to the original message's selection
                    SelectProviderAndModel(message.ProviderId, message.Model);

                    // User prompt edit: Delete this message and subsequent ones, and send new prompt
                    await _pipe.RequestAsync<CommandResult>("conversations.deleteMessagesAfter", new DeleteMessagesAfterRequest(message.ConversationId, message.Id)).ConfigureAwait(false);
                    _composer.Text = newText;
                    await SendAsync().ConfigureAwait(false);
                }
                else
                {
                    // Assistant response edit: Update DB and redraw in place
                    await _pipe.RequestAsync<CommandResult>("messages.update", new MessageUpdateRequest(message.Id, newText)).ConfigureAwait(false);
                    _notice("Message Updated", "Assistant response has been updated successfully.", InfoBarSeverity.Success);
                    await LoadAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _notice("Edit failed", ex.Message, InfoBarSeverity.Error);
                saveButton.IsEnabled = true;
                cancelButton.IsEnabled = true;
            }
        });
    }


    private Border Avatar(string? modelName)
    {
        var border = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(19), // Circular avatars look incredibly modern and premium!
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        if (string.IsNullOrWhiteSpace(modelName))
        {
            // Default GhostClaw logo avatar using the actual image icon
            border.Background = new SolidColorBrush(Colors.Transparent);
            border.Child = new Image
            {
                Source = new BitmapImage(new Uri("ms-appx:///Assets/GhostClawUI.Icon.png")),
                Width = 28,
                Height = 28,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            return border;
        }

        var brand = GetBrandInfo(modelName, out var domain, out var glyph, out var color, out var bg);
        border.Background = GetNativeBrandBackground(brand);
        border.Child = GetNativeBrandLogoElement(brand, fontSize: 16);

        // Async load original brand logo for chat bubble avatar only for non-standard default brands
        if (brand == "default")
        {
            var avatarImg = new Image
            {
                Width = 30,
                Height = 30,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var logoUrl = $"https://logo.clearbit.com/{domain}?size=128";
            _ = LoadAvatarLogoAsync(logoUrl, avatarImg, border, modelName);
        }

        return border;
    }


    private async Task ScrollToEndAsync()
    {
        await Task.Yield();
        _scroll.ChangeView(null, _scroll.ScrollableHeight, null, disableAnimation: false);
    }

}
