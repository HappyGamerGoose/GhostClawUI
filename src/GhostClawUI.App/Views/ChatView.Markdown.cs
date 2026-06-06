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

    private void RenderContent(StackPanel panel, string content, bool isUser)
    {
        var foreground = isUser ? new SolidColorBrush(Colors.White) : PrimaryTextBrush();
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        var paragraph = new StringBuilder();
        var code = new StringBuilder();
        var math = new StringBuilder();
        var think = new StringBuilder();
        var inCode = false;
        var inMath = false;
        var inThink = false;

        void FlushParagraph()
        {
            if (paragraph.Length == 0)
            {
                return;
            }

            var block = MarkdownText(paragraph.ToString().TrimEnd(), foreground, _settings().Appearance.FontSize, FontWeights.Normal, isUser);
            block.Margin = new Thickness(0, 4, 0, 4); // Premium airy spacing
            panel.Children.Add(block);
            paragraph.Clear();
        }

        void FlushCode()
        {
            var block = CodeBlock(code.ToString().TrimEnd(), isUser);
            block.Margin = new Thickness(0, 8, 0, 8);
            panel.Children.Add(block);
            code.Clear();
        }

        void FlushMath()
        {
            var block = MathBlock(math.ToString().TrimEnd(), isUser);
            block.Margin = new Thickness(0, 8, 0, 8);
            panel.Children.Add(block);
            math.Clear();
        }

        void FlushThink()
        {
            if (think.Length == 0)
            {
                return;
            }

            var block = ThinkBlock(think.ToString().TrimEnd(), isUser);
            block.Margin = new Thickness(0, 8, 0, 8);
            panel.Children.Add(block);
            think.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var line = rawLine.TrimEnd();
            var trimmed = line.TrimStart();

            if (inThink)
            {
                var closeTags = new[] { "</thinking>", "</thought>", "</think>" };
                var matchedClose = closeTags.FirstOrDefault(t => line.Contains(t, StringComparison.OrdinalIgnoreCase));
                if (matchedClose != null)
                {
                    var closeIdx = line.IndexOf(matchedClose, StringComparison.OrdinalIgnoreCase);
                    think.AppendLine(line[..closeIdx]);
                    FlushThink();
                    inThink = false;
                    var remaining = line[(closeIdx + matchedClose.Length)..];
                    if (!string.IsNullOrWhiteSpace(remaining))
                    {
                        paragraph.Append(remaining);
                    }
                }
                else
                {
                    think.AppendLine(line);
                }
                continue;
            }

            if (inCode)
            {
                if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    FlushCode();
                    inCode = false;
                }
                else
                {
                    code.AppendLine(line);
                }
                continue;
            }

            if (inMath)
            {
                if (trimmed.Equals("$$", StringComparison.Ordinal) || trimmed.Equals("\\]", StringComparison.Ordinal))
                {
                    FlushMath();
                    inMath = false;
                }
                else
                {
                    math.AppendLine(line);
                }
                continue;
            }

            var openTags = new[] { "<thinking>", "<thought>", "<think>" };
            var matchedOpen = openTags.FirstOrDefault(t => line.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (matchedOpen != null)
            {
                var openIdx = line.IndexOf(matchedOpen, StringComparison.OrdinalIgnoreCase);
                var before = line[..openIdx];
                if (!string.IsNullOrEmpty(before))
                {
                    paragraph.Append(before);
                }
                FlushParagraph();
                inThink = true;

                var closeTag = matchedOpen.Insert(1, "/");
                var closeIdx = line.IndexOf(closeTag, openIdx, StringComparison.OrdinalIgnoreCase);
                if (closeIdx >= 0)
                {
                    think.Append(line[(openIdx + matchedOpen.Length)..closeIdx]);
                    FlushThink();
                    inThink = false;
                    var remaining = line[(closeIdx + closeTag.Length)..];
                    if (!string.IsNullOrWhiteSpace(remaining))
                    {
                        paragraph.Append(remaining);
                    }
                }
                else
                {
                    think.AppendLine(line[(openIdx + matchedOpen.Length)..]);
                }
                continue;
            }

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                inCode = true;
                continue;
            }

            if (trimmed.Equals("$$", StringComparison.Ordinal) || trimmed.Equals("\\[", StringComparison.Ordinal))
            {
                FlushParagraph();
                inMath = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            if (TryAddMarkdownImage(panel, line))
            {
                FlushParagraph();
                continue;
            }

            if (IsTableLine(line) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                FlushParagraph();
                var table = new List<string> { line };
                i += 2;
                while (i < lines.Length && IsTableLine(lines[i]))
                {
                    table.Add(lines[i]);
                    i++;
                }

                i--;
                var tableBlock = TableBlock(table, isUser);
                tableBlock.Margin = new Thickness(0, 10, 0, 10);
                panel.Children.Add(tableBlock);
                continue;
            }

            if (trimmed == "---" || trimmed == "***" || trimmed == "___")
            {
                FlushParagraph();
                var hr = new Border
                {
                    Height = 1,
                    Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    Margin = new Thickness(0, 16, 0, 16)
                };
                panel.Children.Add(hr);
                continue;
            }

            if (TryHeading(trimmed, out var headingText, out var headingLevel))
            {
                FlushParagraph();
                var bump = headingLevel == 1 ? 5 : headingLevel == 2 ? 3 : headingLevel == 3 ? 2 : 1;
                var headingBlock = MarkdownText(headingText, foreground, _settings().Appearance.FontSize + bump, FontWeights.SemiBold, isUser);
                headingBlock.Margin = new Thickness(0, 12, 0, 6);
                panel.Children.Add(headingBlock);
                continue;
            }

            if (TryBullet(trimmed, out var bulletText))
            {
                FlushParagraph();
                var listItemBlock = ListItem("\u2022", bulletText, foreground, isUser);
                listItemBlock.Margin = new Thickness(12, 3, 0, 3);
                panel.Children.Add(listItemBlock);
                continue;
            }

            if (TryNumbered(trimmed, out var number, out var numbered))
            {
                FlushParagraph();
                var listItemBlock = ListItem(number, numbered, foreground, isUser);
                listItemBlock.Margin = new Thickness(12, 3, 0, 3);
                panel.Children.Add(listItemBlock);
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph();
                var quoteBlock = QuoteBlock(trimmed[2..], isUser);
                quoteBlock.Margin = new Thickness(0, 8, 0, 8);
                panel.Children.Add(quoteBlock);
                continue;
            }

            if (trimmed.StartsWith("\\[", StringComparison.Ordinal) && trimmed.EndsWith("\\]", StringComparison.Ordinal) && trimmed.Length > 4)
            {
                FlushParagraph();
                var mathBlock = MathBlock(trimmed[2..^2], isUser);
                mathBlock.Margin = new Thickness(0, 8, 0, 8);
                panel.Children.Add(mathBlock);
                continue;
            }

            if (trimmed.StartsWith("$$", StringComparison.Ordinal) && trimmed.EndsWith("$$", StringComparison.Ordinal) && trimmed.Length > 4)
            {
                FlushParagraph();
                var mathBlock = MathBlock(trimmed[2..^2], isUser);
                mathBlock.Margin = new Thickness(0, 8, 0, 8);
                panel.Children.Add(mathBlock);
                continue;
            }

            if (trimmed.StartsWith('$') && trimmed.EndsWith('$') && trimmed.Length > 2)
            {
                FlushParagraph();
                var mathBlock = MathBlock(trimmed[1..^1], isUser);
                mathBlock.Margin = new Thickness(0, 8, 0, 8);
                panel.Children.Add(mathBlock);
                continue;
            }

            if (paragraph.Length > 0)
            {
                paragraph.AppendLine();
            }

            paragraph.Append(line);
        }

        if (inCode)
        {
            FlushCode();
        }

        if (inMath)
        {
            FlushMath();
        }

        if (inThink)
        {
            FlushThink();
        }

        FlushParagraph();
    }


    private TextBlock MarkdownText(string text, Brush foreground, double size, Windows.UI.Text.FontWeight weight, bool isUser = false)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            FontFamily = new FontFamily(_settings().Appearance.FontFamily),
            FontWeight = weight,
            LineHeight = Math.Max(size * _settings().Appearance.LineHeight, size + 4),
            Foreground = foreground,
            IsTextSelectionEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AddMarkdownInlines(block, text, foreground, weight);
        return block;
    }


    private Grid ListItem(string marker, string text, Brush foreground, bool isUser = false)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            ColumnSpacing = 10
        };
        row.Children.Add(new TextBlock
        {
            Text = marker,
            FontSize = _settings().Appearance.FontSize,
            FontFamily = new FontFamily(_settings().Appearance.FontFamily),
            Foreground = foreground,
            Margin = new Thickness(0, 1, 0, 0),
            IsTextSelectionEnabled = true
        });
        var body = MarkdownText(text, foreground, _settings().Appearance.FontSize, FontWeights.Normal, isUser);
        Grid.SetColumn(body, 1);
        row.Children.Add(body);
        return row;
    }


    private Expander ThinkBlock(string text, bool isUser)
    {
        var expander = new Expander
        {
            IsExpanded = _settings().Verbosity == "Expanded",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 4)
        };

        var headerLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        headerLeft.Children.Add(new FontIcon
        {
            Glyph = "\uEA80", // Lightbulb icon
            FontSize = 14,
            Foreground = UiKit.BrushFromHex("#F97316")
        });
        var headerText = UiKit.Text("Thinking Process", 12, FontWeights.SemiBold);
        headerText.Foreground = UiKit.BrushFromHex("#F97316");
        headerLeft.Children.Add(headerText);

        expander.Header = headerLeft;

        var bodyBlock = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = Math.Max(12, _settings().Appearance.FontSize - 1),
            FontFamily = new FontFamily(_settings().Appearance.FontFamily),
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            Foreground = SecondaryTextBrush(),
            IsTextSelectionEnabled = true
        };

        expander.Content = new Border { Padding = new Thickness(0, 8, 0, 0), Child = bodyBlock };
        return expander;
    }


    private Border CodeBlock(string text, bool isUser)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.NoWrap,
            FontSize = 13,
            FontFamily = new FontFamily("Cascadia Mono"),
            Foreground = isUser ? new SolidColorBrush(Colors.White) : PrimaryTextBrush(),
            IsTextSelectionEnabled = true
        };
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = block
        };
        return new Border
        {
            Child = scroll,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(80, 255, 255, 255)) : StrokeBrush(),
            Background = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(35, 255, 255, 255)) : CodeBackgroundBrush()
        };
    }


    private Border MathBlock(string text, bool isUser)
    {
        return new Border
        {
            Child = new TextBlock
            {
                Text = text.Trim(),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                FontFamily = new FontFamily("Cambria Math"),
                Foreground = isUser ? new SolidColorBrush(Colors.White) : PrimaryTextBrush(),
                IsTextSelectionEnabled = true
            },
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(80, 255, 255, 255)) : UiKit.AccentBrush,
            Background = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255)) : AccentSubtleBrush()
        };
    }


    private Border QuoteBlock(string text, bool isUser)
    {
        var block = MarkdownText(text, isUser ? new SolidColorBrush(Colors.White) : PrimaryTextBrush(), _settings().Appearance.FontSize, FontWeights.Normal, isUser);
        return new Border
        {
            Child = block,
            Padding = new Thickness(12, 8, 12, 8),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(120, 255, 255, 255)) : UiKit.AccentBrush,
            Background = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(24, 255, 255, 255)) : SubtleBrush()
        };
    }


    private Border TableBlock(IReadOnlyList<string> tableLines, bool isUser)
    {
        var rows = tableLines.Select(SplitTableRow).Where(row => row.Count > 0).ToList();
        var columnCount = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
        var grid = new Grid();
        for (var column = 0; column < columnCount; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = rows[rowIndex];
            for (var column = 0; column < columnCount; column++)
            {
                var cellText = column < row.Count ? row[column] : string.Empty;
                var block = MarkdownText(cellText, isUser ? new SolidColorBrush(Colors.White) : PrimaryTextBrush(), Math.Max(12, _settings().Appearance.FontSize - 1), rowIndex == 0 ? FontWeights.SemiBold : FontWeights.Normal, isUser);
                block.MaxWidth = 300;

                var cell = new Border
                {
                    Padding = new Thickness(10, 8, 10, 8),
                    BorderThickness = new Thickness(column == 0 ? 0 : 1, rowIndex == 0 ? 0 : 1, 0, 0),
                    BorderBrush = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(70, 255, 255, 255)) : StrokeBrush(),
                    Background = rowIndex == 0
                        ? isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(32, 255, 255, 255)) : SubtleBrush()
                        : new SolidColorBrush(Colors.Transparent),
                    Child = block
                };
                Grid.SetRow(cell, rowIndex);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = grid
        };

        return new Border
        {
            Child = scroll,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(80, 255, 255, 255)) : StrokeBrush(),
            Background = isUser ? new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255)) : SurfaceBrush()
        };
    }

}
