using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using GhostClawUI.Shared;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GhostClawUI.App.Services;

internal static class PdfExporter
{
    static PdfExporter()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static byte[] ExportToPdf(string conversationTitle, IReadOnlyList<ChatMessage> messages)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(0);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(10.5f).FontFamily(Fonts.SegoeUI).FontColor(Colors.Grey.Darken3));

                page.Header().Element(c => ComposeHeader(c, conversationTitle));
                page.Content().PaddingHorizontal(50).PaddingVertical(30).Element(c => ComposeContent(c, messages));
                page.Footer().Element(ComposeFooter);
            });
        });

        return document.GeneratePdf();
    }

    private static void ComposeHeader(IContainer container, string title)
    {
        container.PaddingHorizontal(50)
            .PaddingTop(40)
            .PaddingBottom(15)
            .Row(row =>
            {
                row.RelativeItem().Text(title).FontSize(14).SemiBold().FontColor(Colors.Black);
                row.RelativeItem().AlignRight().AlignBottom().Text(DateTime.Now.ToString("g")).FontSize(10).FontColor(Colors.Grey.Medium);
            });
    }

    private static void ComposeFooter(IContainer container)
    {
        container.PaddingHorizontal(50)
            .PaddingVertical(20)
            .AlignCenter()
            .Text(x =>
            {
                x.CurrentPageNumber().FontSize(10).FontColor(Colors.Grey.Medium);
            });
    }

    private static void ComposeContent(IContainer container, IReadOnlyList<ChatMessage> messages)
    {
        container.PaddingVertical(10).Column(column =>
        {
            column.Spacing(25);
            
            var visibleMessages = messages.Where(m => !string.Equals(m.Kind, "status", StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var msg in visibleMessages)
            {
                var attachments = new List<ChatAttachment>();
                if (msg.Metadata != null && msg.Metadata["attachments"] is JsonArray arr)
                {
                    try { attachments = arr.Deserialize<List<ChatAttachment>>(PipeJson.Options) ?? new List<ChatAttachment>(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Error deserializing attachments: {ex}"); }
                }

                string displayContent = string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase) 
                    ? msg.Content 
                    : GhostClawUI.Shared.ResponseTextSanitizer.CleanForDisplay(msg.Content);

                if (!string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase) && HasGeneratedFiles(msg.Metadata))
                {
                    displayContent = StripFileGenerationCode(displayContent);
                }

                column.Item().Element(c => ComposeBubble(c, msg.Role, displayContent, attachments, msg.CreatedAt.ToLocalTime()));
            }
        });
    }

    private static void ComposeBubble(IContainer container, string role, string content, List<ChatAttachment> attachments, DateTimeOffset timestamp)
    {
        bool isUser = role.Equals("user", StringComparison.OrdinalIgnoreCase);

        container.PaddingVertical(10).Row(row =>
        {
            if (isUser) row.RelativeItem(1); // Spacer on left for user messages

            row.RelativeItem(6).Column(msgCol =>
            {
                // Header (Role & Time)
                msgCol.Item().PaddingBottom(4).Text(text =>
                {
                    if (isUser) text.AlignRight();
                    text.Span(isUser ? "You" : "GhostClaw").SemiBold().FontSize(11).FontColor(isUser ? Color.FromHex("#005FB8") : Color.FromHex("#0F172A"));
                    text.Span($"    {timestamp:t}").FontSize(9).FontColor(Colors.Grey.Medium);
                });

                // Body wrapped in a subtle elegant card
                var cardCol = msgCol.Item()
                    .Background(isUser ? Color.FromHex("#005FB8") : Color.FromHex("#F8FAFC")) // Vibrant WinUI Blue for User, Soft Grey for AI
                    .Border(isUser ? 0 : 1).BorderColor(Color.FromHex("#E2E8F0"))
                    .BorderLeft(isUser ? 0 : 4).BorderColor(isUser ? Colors.Transparent : Color.FromHex("#3B82F6")) // Gorgeous accent strip for AI
                    .Padding(20);

                cardCol.Column(contentCol =>
                {
                    contentCol.Spacing(10);
                    var textColor = isUser ? Colors.White : Colors.Grey.Darken3;

                    var blocks = ParseMessageContent(content);

                    foreach (var block in blocks)
                    {
                        if (block.Type == BlockType.Text)
                        {
                            contentCol.Item().Text(textDesc => 
                            {
                                AddMarkdownInlines(textDesc, block.Content, isUser, textColor, 10.5f, false);
                            });
                        }
                        else if (block.Type == BlockType.Header1)
                        {
                            contentCol.Item().Text(textDesc => 
                            {
                                AddMarkdownInlines(textDesc, block.Content, isUser, isUser ? Colors.White : Colors.Black, 16f, true);
                            });
                        }
                        else if (block.Type == BlockType.Header2)
                        {
                            contentCol.Item().Text(textDesc => 
                            {
                                AddMarkdownInlines(textDesc, block.Content, isUser, isUser ? Colors.White : Colors.Black, 14f, true);
                            });
                        }
                        else if (block.Type == BlockType.Header3)
                        {
                            contentCol.Item().Text(textDesc => 
                            {
                                AddMarkdownInlines(textDesc, block.Content, isUser, isUser ? Colors.White : Colors.Grey.Darken4, 12f, true);
                            });
                        }
                        else if (block.Type == BlockType.Math)
                        {
                            contentCol.Item()
                                .PaddingVertical(4)
                                .Text(block.Content)
                                .FontFamily("Cambria Math")
                                .FontSize(12)
                                .FontColor(textColor);
                        }
                        else if (block.Type == BlockType.Code)
                        {
                            contentCol.Item()
                                .Background(isUser ? Color.FromHex("#004A8F") : Color.FromHex("#1E293B")) // Dark theme code blocks
                                .PaddingHorizontal(12)
                                .PaddingVertical(8)
                                .Text(block.Content)
                                .FontFamily(Fonts.Consolas)
                                .FontSize(9.5f)
                                .FontColor(isUser ? Colors.White : Color.FromHex("#F8FAFC")); // Bright text in code blocks
                        }
                    }

                    if (attachments != null && attachments.Count > 0)
                    {
                        foreach (var att in attachments)
                        {
                            contentCol.Item()
                                .Background(isUser ? Color.FromHex("#004A8F") : Colors.Grey.Lighten4)
                                .PaddingHorizontal(10)
                                .PaddingVertical(6)
                                .Row(attRow =>
                                {
                                    attRow.RelativeItem().Text($"\ud83d\udcce {att.Name}").FontSize(9.5f).FontColor(isUser ? Colors.White : Colors.Black).SemiBold();
                                    attRow.AutoItem().Text(FormatBytes(att.SizeBytes)).FontSize(8.5f).FontColor(isUser ? Colors.Grey.Lighten2 : Colors.Grey.Darken1);
                                });
                        }
                    }
                });
            });

            if (!isUser) row.RelativeItem(1); // Spacer on right for GhostClaw messages
        });
    }

    private static void AddMarkdownInlines(TextDescriptor textDesc, string text, bool isUser, string baseColor, float baseSize, bool isHeader)
    {
        int index = 0;
        while (index < text.Length)
        {
            var next = NextMarkdownMarker(text, index);
            if (next.Index < 0)
            {
                var span = textDesc.Span(text.Substring(index)).FontColor(baseColor).FontSize(baseSize).LineHeight(1.5f);
                if (isHeader) span.Bold();
                break;
            }

            if (next.Index > index)
            {
                var span = textDesc.Span(text.Substring(index, next.Index - index)).FontColor(baseColor).FontSize(baseSize).LineHeight(1.5f);
                if (isHeader) span.Bold();
            }

            if (next.Marker == "**")
            {
                int end = text.IndexOf("**", next.Index + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    textDesc.Span("**").FontColor(baseColor).FontSize(baseSize).LineHeight(1.5f);
                    index = next.Index + 2;
                    continue;
                }
                textDesc.Span(text.Substring(next.Index + 2, end - (next.Index + 2))).FontColor(baseColor).FontSize(baseSize).Bold().LineHeight(1.5f);
                index = end + 2;
            }
            else if (next.Marker == "*")
            {
                int end = text.IndexOf('*', next.Index + 1);
                if (end < 0)
                {
                    textDesc.Span("*").FontColor(baseColor).FontSize(baseSize).LineHeight(1.5f);
                    index = next.Index + 1;
                    continue;
                }
                textDesc.Span(text.Substring(next.Index + 1, end - (next.Index + 1))).FontColor(baseColor).FontSize(baseSize).Italic().LineHeight(1.5f);
                index = end + 1;
            }
            else if (next.Marker == "`")
            {
                int end = text.IndexOf('`', next.Index + 1);
                if (end < 0)
                {
                    textDesc.Span("`").FontColor(baseColor).FontSize(baseSize).LineHeight(1.5f);
                    index = next.Index + 1;
                    continue;
                }
                textDesc.Span(" " + text.Substring(next.Index + 1, end - (next.Index + 1)) + " ")
                    .FontFamily(Fonts.Consolas)
                    .FontColor(isUser ? Colors.White : Color.FromHex("#000000"))
                    .BackgroundColor(isUser ? Color.FromHex("#2563EB") : Color.FromHex("#e2e8f0"))
                    .FontSize(baseSize - 1f)
                    .LineHeight(1.5f);
                index = end + 1;
            }
            else if (next.Marker == @"\(")
            {
                int end = text.IndexOf(@"\)", next.Index + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    textDesc.Span(@"\(").FontColor(baseColor).FontSize(baseSize).LineHeight(1.5f);
                    index = next.Index + 2;
                    continue;
                }
                textDesc.Span(text.Substring(next.Index + 2, end - (next.Index + 2)))
                    .FontColor(baseColor)
                    .FontSize(baseSize + 1f)
                    .FontFamily("Cambria Math")
                    .LineHeight(1.5f);
                index = end + 2;
            }
        }
    }

    private static (int Index, string Marker) NextMarkdownMarker(string text, int start)
    {
        var markers = new[] { "**", "*", "`", @"\(" };
        int minIndex = -1;
        string bestMarker = "";
        foreach (var marker in markers)
        {
            int idx = text.IndexOf(marker, start, StringComparison.Ordinal);
            if (idx >= 0 && (minIndex == -1 || idx < minIndex))
            {
                if (idx == minIndex && marker.Length < bestMarker.Length)
                    continue;
                
                minIndex = idx;
                bestMarker = marker;
            }
        }
        return (minIndex, bestMarker);
    }

    internal enum BlockType
    {
        Text,
        Code,
        Math,
        Header1,
        Header2,
        Header3
    }

    internal sealed class ContentBlock
    {
        public BlockType Type { get; set; }
        public string Content { get; set; } = string.Empty;
        public string? Language { get; set; }
    }

    private static List<ContentBlock> ParseMessageContent(string content)
    {
        var blocks = new List<ContentBlock>();
        if (string.IsNullOrEmpty(content)) return blocks;

        var lines = content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        bool inCodeBlock = false;
        var currentBlock = new StringBuilder();
        string codeLang = "";

        void FlushText()
        {
            if (currentBlock.Length > 0)
            {
                blocks.Add(new ContentBlock
                {
                    Type = BlockType.Text,
                    Content = currentBlock.ToString().TrimEnd()
                });
                currentBlock.Clear();
            }
        }

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```"))
            {
                if (inCodeBlock)
                {
                    blocks.Add(new ContentBlock
                    {
                        Type = BlockType.Code,
                        Content = currentBlock.ToString().TrimEnd(),
                        Language = codeLang
                    });
                    currentBlock.Clear();
                    inCodeBlock = false;
                }
                else
                {
                    FlushText();
                    inCodeBlock = true;
                    codeLang = trimmed.Substring(3).Trim();
                }
                continue;
            }

            if (inCodeBlock)
            {
                currentBlock.Append(line).Append('\n');
                continue;
            }

            if (trimmed.StartsWith("$$") && trimmed.EndsWith("$$") && trimmed.Length >= 4)
            {
                FlushText();
                blocks.Add(new ContentBlock { Type = BlockType.Math, Content = trimmed.Substring(2, trimmed.Length - 4).Trim() });
                continue;
            }
            if (trimmed.StartsWith("\\[") && trimmed.EndsWith("\\]") && trimmed.Length >= 4)
            {
                FlushText();
                blocks.Add(new ContentBlock { Type = BlockType.Math, Content = trimmed.Substring(2, trimmed.Length - 4).Trim() });
                continue;
            }
            if (trimmed.StartsWith("# "))
            {
                FlushText();
                blocks.Add(new ContentBlock { Type = BlockType.Header1, Content = trimmed.Substring(2).Trim() });
                continue;
            }
            if (trimmed.StartsWith("## "))
            {
                FlushText();
                blocks.Add(new ContentBlock { Type = BlockType.Header2, Content = trimmed.Substring(3).Trim() });
                continue;
            }
            if (trimmed.StartsWith("### "))
            {
                FlushText();
                blocks.Add(new ContentBlock { Type = BlockType.Header3, Content = trimmed.Substring(4).Trim() });
                continue;
            }
            if (trimmed.StartsWith("#### "))
            {
                FlushText();
                blocks.Add(new ContentBlock { Type = BlockType.Header3, Content = trimmed.Substring(5).Trim() });
                continue;
            }

            currentBlock.Append(line).Append('\n');
        }

        if (currentBlock.Length > 0)
        {
            blocks.Add(new ContentBlock
            {
                Type = inCodeBlock ? BlockType.Code : BlockType.Text,
                Content = currentBlock.ToString().TrimEnd(),
                Language = inCodeBlock ? codeLang : null
            });
        }

        return blocks;
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB" };
        double doubleBytes = bytes;
        int index = 0;
        while (doubleBytes >= 1024 && index < suffixes.Length - 1)
        {
            doubleBytes /= 1024;
            index++;
        }
        return $"{doubleBytes:F1} {suffixes[index]}";
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

    public static byte[] ExportVisualToPdf(string conversationTitle, List<(byte[] imageBytes, double width, double height)> images)
    {
        return Array.Empty<byte>();
    }
}
