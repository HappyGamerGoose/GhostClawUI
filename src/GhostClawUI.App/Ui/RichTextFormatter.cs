using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace GhostClawUI.App.Ui;

internal static class RichTextFormatter
{
    public static StackPanel Markdown(string text, Brush foreground, double fontSize = 14, string fontFamily = "Segoe UI Variable", double lineHeight = 1.35)
    {
        var panel = new StackPanel { Spacing = 8 };
        var lines = (text ?? string.Empty).ReplaceLineEndings("\n").Split('\n');
        var paragraph = new List<string>();
        var code = new List<string>();
        var inCode = false;

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            panel.Children.Add(InlineText(string.Join('\n', paragraph).TrimEnd(), foreground, fontSize, fontFamily, lineHeight, FontWeights.Normal));
            paragraph.Clear();
        }

        void FlushCode()
        {
            panel.Children.Add(new Border
            {
                Child = new TextBlock
                {
                    Text = string.Join('\n', code).TrimEnd(),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = Math.Max(12, fontSize - 1),
                    FontFamily = new FontFamily("Cascadia Mono"),
                    Foreground = foreground,
                    IsTextSelectionEnabled = true
                },
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = ResourceBrush("CardStrokeColorDefaultBrush", "#D1D5DB"),
                Background = ResourceBrush("SubtleFillColorSecondaryBrush", "#F3F4F6")
            });
            code.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    FlushCode();
                    inCode = false;
                }
                else
                {
                    FlushParagraph();
                    inCode = true;
                }

                continue;
            }

            if (inCode)
            {
                code.Add(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            var trimmed = line.TrimStart();
            if (TryHeading(trimmed, out var heading, out var level))
            {
                FlushParagraph();
                panel.Children.Add(InlineText(heading, foreground, fontSize + Math.Max(1, 5 - level), fontFamily, lineHeight, FontWeights.SemiBold));
                continue;
            }

            if (TryBullet(trimmed, out var bullet))
            {
                FlushParagraph();
                panel.Children.Add(ListItem("-", bullet, foreground, fontSize, fontFamily, lineHeight));
                continue;
            }

            if (TryNumbered(trimmed, out var number, out var numbered))
            {
                FlushParagraph();
                panel.Children.Add(ListItem(number, numbered, foreground, fontSize, fontFamily, lineHeight));
                continue;
            }

            if (IsMathBlock(trimmed, out var math))
            {
                FlushParagraph();
                panel.Children.Add(new TextBlock
                {
                    Text = math,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = fontSize,
                    FontFamily = new FontFamily("Cambria Math"),
                    Foreground = foreground,
                    IsTextSelectionEnabled = true
                });
                continue;
            }

            paragraph.Add(line);
        }

        if (inCode)
        {
            FlushCode();
        }

        FlushParagraph();
        return panel;
    }

    private static TextBlock InlineText(string text, Brush foreground, double fontSize, string fontFamily, double lineHeight, Windows.UI.Text.FontWeight weight)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            FontFamily = new FontFamily(fontFamily),
            FontWeight = weight,
            LineHeight = Math.Max(fontSize * lineHeight, fontSize + 4),
            Foreground = foreground,
            IsTextSelectionEnabled = true
        };
        AddInlines(block, text, foreground, weight);
        return block;
    }

    private static UIElement ListItem(string marker, string text, Brush foreground, double fontSize, string fontFamily, double lineHeight)
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
            FontSize = fontSize,
            FontFamily = new FontFamily(fontFamily),
            Foreground = foreground
        });
        var body = InlineText(text, foreground, fontSize, fontFamily, lineHeight, FontWeights.Normal);
        Grid.SetColumn(body, 1);
        row.Children.Add(body);
        return row;
    }

    private static void AddInlines(TextBlock block, string text, Brush foreground, Windows.UI.Text.FontWeight baseWeight)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (TryDelimited(text, index, "`", out var code, out var end))
            {
                block.Inlines.Add(new Run { Text = code, Foreground = foreground, FontFamily = new FontFamily("Cascadia Mono"), FontSize = Math.Max(11, block.FontSize - 1) });
                index = end;
                continue;
            }

            if (TryDelimited(text, index, "**", out var bold, out end) || TryDelimited(text, index, "__", out bold, out end))
            {
                block.Inlines.Add(new Run { Text = bold, Foreground = foreground, FontWeight = FontWeights.SemiBold });
                index = end;
                continue;
            }

            if (TryInlineMath(text, index, out var math, out end))
            {
                block.Inlines.Add(new Run { Text = math, Foreground = foreground, FontFamily = new FontFamily("Cambria Math"), FontWeight = baseWeight });
                index = end;
                continue;
            }

            if ((text[index] == '*' || text[index] == '_') && TryDelimited(text, index, text[index].ToString(), out var italic, out end))
            {
                block.Inlines.Add(new Run { Text = italic, Foreground = foreground, FontStyle = Windows.UI.Text.FontStyle.Italic, FontWeight = baseWeight });
                index = end;
                continue;
            }

            var next = NextMarker(text, index);
            block.Inlines.Add(new Run { Text = text[index..next], Foreground = foreground, FontWeight = baseWeight });
            index = next;
        }
    }

    private static bool TryHeading(string line, out string text, out int level)
    {
        level = 0;
        while (level < line.Length && level < 6 && line[level] == '#')
        {
            level++;
        }

        text = level == 0 ? string.Empty : line[level..].Trim().TrimEnd('#').Trim();
        return level > 0 && text.Length > 0;
    }

    private static bool TryBullet(string line, out string text)
    {
        foreach (var marker in new[] { "- ", "* ", "\u2022 " })
        {
            if (line.StartsWith(marker, StringComparison.Ordinal))
            {
                text = line[marker.Length..].TrimStart();
                return text.Length > 0;
            }
        }

        text = string.Empty;
        return false;
    }

    private static bool TryNumbered(string line, out string marker, out string text)
    {
        marker = string.Empty;
        text = string.Empty;
        var dot = line.IndexOfAny(new[] { '.', ')' });
        if (dot is <= 0 or > 3 || !line[..dot].All(char.IsDigit) || dot + 1 >= line.Length || line[dot + 1] != ' ')
        {
            return false;
        }

        marker = line[..(dot + 1)];
        text = line[(dot + 2)..];
        return text.Length > 0;
    }

    private static bool IsMathBlock(string line, out string math)
    {
        math = string.Empty;
        if (line.StartsWith("\\[", StringComparison.Ordinal) && line.EndsWith("\\]", StringComparison.Ordinal))
        {
            math = line[2..^2].Trim();
            return math.Length > 0;
        }

        if ((line.StartsWith("$$", StringComparison.Ordinal) && line.EndsWith("$$", StringComparison.Ordinal)) ||
            (line.StartsWith('$') && line.EndsWith('$')))
        {
            math = line.Trim('$').Trim();
            return math.Length > 0;
        }

        return false;
    }

    private static bool TryDelimited(string text, int start, string marker, out string value, out int end)
    {
        value = string.Empty;
        end = start;
        if (!text.AsSpan(start).StartsWith(marker, StringComparison.Ordinal))
        {
            return false;
        }

        var close = text.IndexOf(marker, start + marker.Length, StringComparison.Ordinal);
        if (close < 0)
        {
            return false;
        }

        value = text[(start + marker.Length)..close];
        end = close + marker.Length;
        return value.Length > 0;
    }

    private static bool TryInlineMath(string text, int start, out string value, out int end)
    {
        value = string.Empty;
        end = start;
        if (text[start] != '$' || start + 1 >= text.Length || char.IsWhiteSpace(text[start + 1]))
        {
            return false;
        }

        var close = text.IndexOf('$', start + 1);
        if (close <= start + 1)
        {
            return false;
        }

        value = text[(start + 1)..close];
        end = close + 1;
        return true;
    }

    private static int NextMarker(string text, int start)
    {
        var next = text.Length;
        foreach (var marker in new[] { "`", "**", "__", "$", "*", "_" })
        {
            var found = text.IndexOf(marker, start + 1, StringComparison.Ordinal);
            if (found >= 0)
            {
                next = Math.Min(next, found);
            }
        }

        return next;
    }

    private static Brush ResourceBrush(string key, string fallback)
    {
        try
        {
            return (Brush)Application.Current.Resources[key];
        }
        catch
        {
            return UiKit.BrushFromHex(fallback);
        }
    }
}
