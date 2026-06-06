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

    private void DetectAndAddLocalFiles(StackPanel panel, string messageContent)
    {
        if (string.IsNullOrWhiteSpace(messageContent)) return;

        // Run detection and file existence checks in the background
        _ = Task.Run(() =>
        {
            try
            {
                var regex = new System.Text.RegularExpressions.Regex(
                    @"(?i)(?:""([^""]+?\.(?:pptx|docx|xlsx|pdf|txt|csv|png|jpg|jpeg|zip))""|'([^']+?\.(?:pptx|docx|xlsx|pdf|txt|csv|png|jpg|jpeg|zip))'|\`([^\`]+?\.(?:pptx|docx|xlsx|pdf|txt|csv|png|jpg|jpeg|zip))\`|\b([a-zA-Z]:[\\/][^:\*\?""<>\|\s]+?\.(?:pptx|docx|xlsx|pdf|txt|csv|png|jpg|jpeg|zip))\b|\b([^:\*\?""<>\|\s\u201c\u201d\u2018\u2019]+?\.(?:pptx|docx|xlsx|pdf|txt|csv|png|jpg|jpeg|zip))\b)",
                    System.Text.RegularExpressions.RegexOptions.Compiled);

                var matches = regex.Matches(messageContent);
                var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var attachmentsToAdd = new List<ChatAttachment>();

                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    // Extract the path from the matched group
                    string potPath = string.Empty;
                    for (int i = 1; i <= 5; i++)
                    {
                        if (match.Groups[i].Success)
                        {
                            potPath = match.Groups[i].Value;
                            break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(potPath)) continue;

                    // Trim leading/trailing punctuation and markdown
                    potPath = potPath.Trim(' ', '\t', '\r', '\n', '.', ',', '!', '?', ':', '*', '(', ')', '[', ']', '{', '}');

                    string? resolvedPath = ResolveLocalFilePath(potPath);
                    if (resolvedPath != null && !addedPaths.Contains(resolvedPath))
                    {
                        addedPaths.Add(resolvedPath);
                        try
                        {
                            var fileInfo = new System.IO.FileInfo(resolvedPath);
                            var name = fileInfo.Name;
                            var ext = fileInfo.Extension.ToLowerInvariant();
                            var contentType = ext switch
                            {
                                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                                ".pdf" => "application/vnd.openxmlformats-officedocument.pdf",
                                ".png" => "image/png",
                                ".jpg" => "image/jpeg",
                                ".jpeg" => "image/jpeg",
                                ".txt" => "text/plain",
                                ".csv" => "text/csv",
                                ".zip" => "application/zip",
                                _ => "application/octet-stream"
                            };

                            attachmentsToAdd.Add(new ChatAttachment(name, resolvedPath, contentType, fileInfo.Length, null));
                        }
                        catch
                        {
                            // Ignore errors building preview
                        }
                    }
                }

                if (attachmentsToAdd.Count > 0)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        foreach (var attachment in attachmentsToAdd)
                        {
                            var card = AttachmentPreview(attachment, isUser: false, removable: false);
                            panel.Children.Add(card);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error detecting local files: {ex}");
            }
        });
    }


    private async Task ExportPdfAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_conversationId))
            {
                _notice("Export Unavailable", "No active conversation to export.", InfoBarSeverity.Warning);
                return;
            }

            var conversation = await _pipe.RequestAsync<ConversationDetail>("conversations.get", new SimpleIdRequest(_conversationId)).ConfigureAwait(false);
            if (conversation is null || conversation.Messages.Count == 0)
            {
                _notice("Export Failed", "Could not retrieve messages for this conversation.", InfoBarSeverity.Error);
                return;
            }

            var savePicker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, _hwnd);
            savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            savePicker.FileTypeChoices.Add("PDF Document", new List<string>() { ".pdf" });
            savePicker.SuggestedFileName = $"{conversation.Summary.Title.Replace(" ", "_")}_Transcript.pdf";

            var file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                try
                {
                    var bubbleImages = new List<(byte[] PngBytes, double Width, double Height)>();
                    foreach (UIElement child in _messages.Children)
                    {
                        if (child.Visibility != Visibility.Visible) continue;

                        var fwE = child as FrameworkElement;
                        if (fwE != null && fwE.ActualHeight == 0) continue;

                        var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                        await rtb.RenderAsync(child);

                        var pixelBuffer = await rtb.GetPixelsAsync();
                        var pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixelBuffer);

                        using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, ms);
                        encoder.SetPixelData(
                            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                            (uint)rtb.PixelWidth,
                            (uint)rtb.PixelHeight,
                            96,
                            96,
                            pixels);
                        await encoder.FlushAsync();

                        using var stream = ms.AsStream();
                        var imageBytes = new byte[stream.Length];
                        stream.Seek(0, System.IO.SeekOrigin.Begin);
                        await stream.ReadExactlyAsync(imageBytes, 0, imageBytes.Length).ConfigureAwait(false);

                        double actualW = fwE != null ? fwE.ActualWidth : rtb.PixelWidth;
                        double actualH = fwE != null ? fwE.ActualHeight : rtb.PixelHeight;

                        bubbleImages.Add((imageBytes, actualW, actualH));
                    }

                    byte[] pdfBytes = PdfExporter.ExportVisualToPdf(conversation.Summary.Title, bubbleImages);
                    await Windows.Storage.FileIO.WriteBytesAsync(file, pdfBytes);
                    _notice("PDF Exported", $"Conversation saved to {file.Name}", InfoBarSeverity.Success);
                }
                catch (Exception)
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
            _notice("Export Failed", ex.Message, InfoBarSeverity.Error);
        }
    }


    private static bool HasGeneratedFiles(JsonNode? metadata)
    {
        if (metadata != null && metadata["attachments"] is JsonArray arr && arr.Count > 0)
        {
            foreach (var node in arr)
            {
                if (node != null && node["Name"]?.ToString() is string name)
                {
                    if (!name.StartsWith("Execution_Error_", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith("Python_Not_Found_", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }


    private static string StripFileGenerationCode(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;

        var regex = new System.Text.RegularExpressions.Regex(
            @"`{3}python[ \t]*\r?\n([\s\S]*?)(?:`{3}|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );

        var matches = regex.Matches(content);
        var sb = new StringBuilder(content);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var code = match.Groups[1].Value;
            if (code.Contains(".save(") || code.Contains("open(") || code.Contains("write("))
            {
                sb.Replace(match.Value, "");
            }
        }

        var cleaned = sb.ToString();
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\n{3,}", "\n\n");
        return cleaned.Trim();
    }

}
