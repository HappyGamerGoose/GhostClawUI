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

    public async Task SendQuickPromptAsync(string text)
    {
        _composer.Text = text;
        await SendAsync();
    }


    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Drop to attach files";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
    }


    private async void OnDrop(object sender, DragEventArgs e)
    {
        var maxCharacters = GetModelMaxCharacterLimit();
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var files = items.OfType<StorageFile>().ToList();
                if (files.Count > 0)
                {
                    await ProcessStorageFilesAsync(files);
                }
            }
            catch (Exception ex)
            {
                _notice("Drop handling failed", ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                deferral.Complete();
            }
        }
    }

    private async void OnComposerPaste(object sender, TextControlPasteEventArgs e)
    {
        var dataPackageView = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();

        if (dataPackageView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            try
            {
                var items = await dataPackageView.GetStorageItemsAsync();
                var files = items.OfType<StorageFile>().ToList();
                if (files.Count > 0)
                {
                    e.Handled = true;
                    await ProcessStorageFilesAsync(files);
                }
            }
            catch (Exception ex)
            {
                _notice("Paste failed", ex.Message, InfoBarSeverity.Error);
            }
        }
        else if (dataPackageView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
        {
            try
            {
                e.Handled = true;
                var bitmapStreamRef = await dataPackageView.GetBitmapAsync();
                using var stream = await bitmapStreamRef.OpenReadAsync();

                var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pasted_image_{Guid.NewGuid():N}.png");
                using (var fileStream = System.IO.File.Create(tempPath))
                {
                    using var classicStream = stream.AsStreamForRead();
                    await classicStream.CopyToAsync(fileStream);
                }

                var file = await StorageFile.GetFileFromPathAsync(tempPath);
                await ProcessStorageFilesAsync(new List<StorageFile> { file });
            }
            catch (Exception ex)
            {
                _notice("Image paste failed", ex.Message, InfoBarSeverity.Error);
            }
        }
    }


    private async Task ProcessStorageFilesAsync(IReadOnlyList<StorageFile> files)
    {
        if (files.Count == 0) return;
        var maxCharacters = GetModelMaxCharacterLimit();

        // Synchronously add all file names to uploading list first to avoid race conditions
        foreach (var file in files)
        {
            _uploadingFiles[file.Name] = new CancellationTokenSource();
        }
        RenderAttachmentTray();

        // Process files in parallel
        var tasks = files.Select(async file =>
        {
            ChatAttachment? attachment = null;
            try
            {
                var properties = await file.GetBasicPropertiesAsync();
                var size = (long)properties.Size;

                // Get the cancellation token for this file
                var token = _uploadingFiles.TryGetValue(file.Name, out var cts) ? cts.Token : CancellationToken.None;

                // Read text preview and base64 data URI in parallel on background threads
                var filePath = file.Path ?? file.Name;
                var previewTask = FileTextExtractor.ReadTextPreviewAsync(filePath, size, maxCharacters);
                var contentType = FileTextExtractor.BuildContentType(filePath);
                var processedImageTask = ProcessImageFileAsync(file, size, contentType);

                await Task.WhenAll(previewTask, processedImageTask);
                token.ThrowIfCancellationRequested();

                var processed = await processedImageTask;
                var finalPath = string.IsNullOrWhiteSpace(processed.path) ? (file.Path ?? string.Empty) : processed.path;
                var finalContentType = processed.contentType ?? contentType;

                attachment = new ChatAttachment(
                    file.Name,
                    finalPath,
                    finalContentType,
                    size,
                    await previewTask,
                    null);
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _notice("File processing failed", $"{file.Name}: {ex.Message}", InfoBarSeverity.Error);
                });
            }
            finally
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _uploadingFiles.Remove(file.Name);
                    if (attachment != null)
                    {
                        _attachments.Add(attachment);
                    }
                    RenderAttachmentTray();
                });
            }
        }).ToList();

        await Task.WhenAll(tasks);
    }


    private async Task AttachFilesAsync()
    {
        try
        {
            var maxCharacters = GetModelMaxCharacterLimit();
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List
            };
            foreach (var filter in new[] { ".txt", ".md", ".json", ".csv", ".log", ".xml", ".yaml", ".yml", ".cs", ".js", ".ts", ".py", ".png", ".jpg", ".jpeg", ".webp", ".pdf", ".docx", ".doc", ".pptx", ".ppt", ".xlsx", ".xls" })
            {
                picker.FileTypeFilter.Add(filter);
            }

            InitializeWithWindow.Initialize(picker, _hwnd);
            var files = await picker.PickMultipleFilesAsync();
            if (files != null && files.Count > 0)
            {
                await ProcessStorageFilesAsync(files);
            }
        }
        catch (Exception ex)
        {
            _notice("Failed to attach files", ex.Message, InfoBarSeverity.Error);
        }
    }


    private void RenderAttachmentTray()
    {
        _attachmentTray.Children.Clear();
        var hasAttachments = _attachments.Count > 0 || _uploadingFiles.Count > 0;
        _attachmentTrayScroll.Visibility = hasAttachments ? Visibility.Visible : Visibility.Collapsed;
        _attachmentTray.Visibility = hasAttachments ? Visibility.Visible : Visibility.Collapsed;

        foreach (var name in _uploadingFiles.Keys)
        {
            _attachmentTray.Children.Add(UploadingPreview(name));
        }

        for (var i = 0; i < _attachments.Count; i++)
        {
            var index = i;
            _attachmentTray.Children.Add(AttachmentPreview(_attachments[i], isUser: false, removable: true, () =>
            {
                _attachments.RemoveAt(index);
                RenderAttachmentTray();
            }));
        }

        UpdateSendButtonState();
    }


    private Border UploadingPreview(string filename)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        grid.Children.Add(new ProgressRing
        {
            Width = 14,
            Height = 14,
            IsActive = true,
            VerticalAlignment = VerticalAlignment.Center
        });

        var nameText = UiKit.Text($"Processing {filename}...", 12, FontWeights.SemiBold);
        nameText.Foreground = PrimaryTextBrush();
        nameText.TextTrimming = TextTrimming.CharacterEllipsis;
        nameText.VerticalAlignment = VerticalAlignment.Center;

        Grid.SetColumn(nameText, 1);
        grid.Children.Add(nameText);

        var removeButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
            Width = 24,
            Height = 24,
            MinWidth = 24,
            MinHeight = 24,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = SecondaryTextBrush(),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(removeButton, $"Cancel {filename}");
        removeButton.Click += (_, _) =>
        {
            if (_uploadingFiles.TryGetValue(filename, out var cts))
            {
                cts.Cancel();
            }
        };
        Grid.SetColumn(removeButton, 2);
        grid.Children.Add(removeButton);

        return new Border
        {
            Child = grid,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 0, 6, 0),
            BorderBrush = StrokeBrush(),
            Background = IsDarkMode
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 4, 2)
        };
    }


    private Border AttachmentPreview(ChatAttachment attachment, bool isUser, bool removable, Action? remove = null)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        var isImage = attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        grid.Children.Add(new FontIcon
        {
            Glyph = isImage ? "\uEB9F" : "\uE8A5",
            FontSize = 14,
            Foreground = isUser ? new SolidColorBrush(Colors.White) : UiKit.AccentBrush,
            VerticalAlignment = VerticalAlignment.Center
        });

        var nameText = UiKit.Text(attachment.Name, 12, FontWeights.SemiBold);
        nameText.Foreground = isUser ? new SolidColorBrush(Colors.White) : PrimaryTextBrush();
        nameText.TextTrimming = TextTrimming.CharacterEllipsis;
        nameText.VerticalAlignment = VerticalAlignment.Center;

        var sizeText = UiKit.Text(FormatBytes(attachment.SizeBytes), 11);
        sizeText.Foreground = isUser
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(200, 255, 255, 255))
            : SecondaryTextBrush();
        sizeText.VerticalAlignment = VerticalAlignment.Center;

        var textPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        textPanel.Children.Add(nameText);
        textPanel.Children.Add(sizeText);

        Grid.SetColumn(textPanel, 1);
        grid.Children.Add(textPanel);

        if (removable)
        {
            var removeButton = new Button
            {
                Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
                Width = 24,
                Height = 24,
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = SecondaryTextBrush(),
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(removeButton, $"Remove {attachment.Name}");
            removeButton.Click += (_, _) => remove?.Invoke();
            Grid.SetColumn(removeButton, 2);
            grid.Children.Add(removeButton);
        }
        else if (File.Exists(attachment.Path))
        {
            var downloadBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE896", FontSize = 12 }, // Download/Save icon
                Width = 24,
                Height = 24,
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = isUser ? new SolidColorBrush(Colors.White) : SecondaryTextBrush(),
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(downloadBtn, $"Save {attachment.Name} to Downloads");
            downloadBtn.Click += async (_, _) =>
            {
                try
                {
                    var downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    var destPath = Path.Combine(downloadsPath, attachment.Name);
                    var finalDest = destPath;
                    var counter = 1;
                    while (File.Exists(finalDest))
                    {
                        var ext = Path.GetExtension(destPath);
                        var nameWithoutExt = Path.GetFileNameWithoutExtension(destPath);
                        finalDest = Path.Combine(downloadsPath, $"{nameWithoutExt} ({counter++}){ext}");
                    }
                    File.Copy(attachment.Path, finalDest);
                    _notice("Saved to Downloads", $"File copied to: {Path.GetFileName(finalDest)}", InfoBarSeverity.Success);
                }
                catch (Exception ex)
                {
                    _notice("Download failed", ex.Message, InfoBarSeverity.Error);
                }
            };
            Grid.SetColumn(downloadBtn, 2);
            grid.Children.Add(downloadBtn);
        }

        var border = new Border
        {
            Child = grid,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 0, (removable || File.Exists(attachment.Path)) ? 6 : 12, 0),
            BorderBrush = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(60, 255, 255, 255)) : StrokeBrush(),
            Background = isUser
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255))
                : IsDarkMode
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255))
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 4, 2)
        };

        if (!removable && File.Exists(attachment.Path))
        {
            border.Tapped += (s, e) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(attachment.Path) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    _notice("Could not open file", ex.Message, InfoBarSeverity.Warning);
                }
            };
        }

        if (isImage && !removable && File.Exists(attachment.Path))
        {
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(border);
            panel.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri(attachment.Path)),
                MaxHeight = 160,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left
            });
            return new Border { Child = panel };
        }

        return border;
    }


    private async Task SendAsync()
    {
        if (_isSending)
        {
            try
            {
                _chatCts?.Cancel();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to cancel active generation: {ex.Message}");
            }
            return;
        }
        if (_uploadingFiles.Count > 0)
        {
            _notice("Please wait", "Files are currently uploading/processing. Please wait until they are finished.", InfoBarSeverity.Warning);
            return;
        }

        var text = _composer.Text.Trim();
        var attachments = _attachments.ToList();
        if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
        {
            return;
        }

        var provider = _providers.SelectedItem as ProviderProfile;
        string? model = null;
        if (_models.SelectedItem is ComboBoxItem cbi)
        {
            model = cbi.Tag as string;
        }
        else
        {
            model = _models.SelectedItem as string;
        }

        if (provider is null || string.IsNullOrWhiteSpace(provider.Id))
        {
            _notice("Choose a provider", "Add or select a provider before sending.", InfoBarSeverity.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(model) && _models.Items.Count > 0)
        {
            _models.SelectedIndex = 0;
            if (_models.SelectedItem is ComboBoxItem firstCbi)
            {
                model = firstCbi.Tag as string;
            }
            else
            {
                model = _models.SelectedItem as string;
            }
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            _notice("Choose a model", "Validate the provider or add model names manually.", InfoBarSeverity.Warning);
            return;
        }

        var apiKey = _vault.ReadProviderKey(provider.Id);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _notice("API key missing", $"Add an API key for {provider.Name}, then try again.", InfoBarSeverity.Warning);
            return;
        }

        _composer.Text = "";
        _attachments.Clear();
        RenderAttachmentTray();
        _isSending = true;
        SetBusy(true);
        _chatCts = new CancellationTokenSource();

        var conversationId = _conversationId ?? string.Empty;

        try
        {
            var messageText = string.IsNullOrWhiteSpace(text) ? "Attached file" : text;
            var optimistic = new ChatMessage(Guid.NewGuid().ToString("N"), conversationId, "user", messageText, provider.Id, model, "message", DateTimeOffset.UtcNow, AttachmentMetadata(attachments));
            if (_messages.Children.Count == 1 && _messages.Children[0] is StackPanel)
            {
                _messages.Children.Clear();
            }
            _messages.Children.Add(MessageRow(optimistic));
            await ScrollToEndAsync();

            // Build the content to send — prepend skill context if one was injected
            var contentToSend = text;
            if (!string.IsNullOrWhiteSpace(_injectedSkillContext))
            {
                contentToSend = $"<skill_context>\n{_injectedSkillContext}\n</skill_context>\n\n{text}";
                _injectedSkillContext = null;
                _skillBadge.Visibility = Visibility.Collapsed;
            }

            // Pass full resolution attachments as requested by the user
            var processedAttachments = attachments.ToList();

            var request = new ChatSendRequest(conversationId, provider.Id, model, contentToSend, _whisperMode, _settings().Verbosity, processedAttachments, AgentMode: _agentMode);
            StartPollingActiveTraces();
            var result = await _pipe.RequestAsync<ChatSendResult>("chat.send", request, _chatCts.Token);
            StopPollingActiveTraces();

            if (result is null)
            {
                throw new InvalidOperationException("No response received from the background agent service.");
            }

            if (result.Trace != null)
            {
                foreach (var trace in result.Trace)
                {
                    _messages.Children.Add(Trace(trace));
                }
            }

            if (result.AssistantMessage != null)
            {
                _messages.Children.Add(MessageRow(result.AssistantMessage));
                _conversationId = result.AssistantMessage.ConversationId;
                
                try
                {
                    await _conversationChanged(_conversationId);
                }
                catch (Exception cvEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Error in conversationChanged: {cvEx}");
                }
            }

            try
            {
                await ScrollToEndAsync();
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                _notice(result.Queued ? "Queued for reconnect" : "Provider error", result.Error, InfoBarSeverity.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            StopPollingActiveTraces();
            _messages.Children.Add(MessageRow(new ChatMessage(Guid.NewGuid().ToString("N"), conversationId, "assistant", "Generation stopped.", provider.Id, model, "error", DateTimeOffset.UtcNow)));
            await ScrollToEndAsync();
        }
        catch (Exception ex)
        {
            StopPollingActiveTraces();
            _messages.Children.Add(MessageRow(new ChatMessage(Guid.NewGuid().ToString("N"), conversationId, "assistant", Explain(ex), provider.Id, model, "error", DateTimeOffset.UtcNow)));
            await ScrollToEndAsync();
            _notice("Failed to send message", $"An error occurred: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _isSending = false;
            SetBusy(false);
            _chatCts?.Dispose();
            _chatCts = null;
        }
    }

}
