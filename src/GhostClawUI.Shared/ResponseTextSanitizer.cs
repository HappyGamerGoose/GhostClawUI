using System.Text;

namespace GhostClawUI.Shared;

public static class ResponseTextSanitizer
{
    public static string CleanForStorage(string content) => Clean(content, normalizeMarkdownEscapes: false, fallback: string.Empty);

    public static string CleanForDisplay(string content) =>
        Clean(content, normalizeMarkdownEscapes: true, fallback: "GhostClaw received tool activity, but no final answer was returned yet.");

    private static string Clean(string content, bool normalizeMarkdownEscapes, string fallback)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var lines = content.ReplaceLineEndings("\n").Split('\n');
        var output = new StringBuilder();
        var fenced = new List<string>();
        var inFence = false;
        var inToolArtifact = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                fenced.Add(rawLine);
                if (!inFence)
                {
                    inFence = true;
                    continue;
                }

                if (!LooksLikeToolBlock(fenced))
                {
                    foreach (var fencedLine in fenced)
                    {
                        output.AppendLine(fencedLine);
                    }
                }

                fenced.Clear();
                inFence = false;
                continue;
            }

            if (inFence)
            {
                fenced.Add(rawLine);
                continue;
            }

            if (StartsToolArtifact(line))
            {
                inToolArtifact = !EndsToolArtifact(line);
                continue;
            }

            if (inToolArtifact)
            {
                if (EndsToolArtifact(line))
                {
                    inToolArtifact = false;
                }

                continue;
            }

            if (LooksLikeToolLine(line))
            {
                continue;
            }

            if (IsHorizontalRule(line))
            {
                continue;
            }

            var cleanedLine = rawLine;
            if (normalizeMarkdownEscapes)
            {
                cleanedLine = StripBold(cleanedLine);
            }

            output.AppendLine(cleanedLine);
        }

        if (inFence && fenced.Count > 0 && !LooksLikeToolBlock(fenced))
        {
            foreach (var fencedLine in fenced)
            {
                output.AppendLine(fencedLine);
            }
        }

        var cleaned = output.ToString().Trim();
        if (normalizeMarkdownEscapes)
        {
            cleaned = NormalizeMarkdownEscapes(cleaned);
        }

        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    private static bool IsHorizontalRule(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        int count = 0;
        char? firstChar = null;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == ' ') continue;
            if (c == '\\') continue;
            if (c == '-' || c == '*' || c == '_' || c == '=')
            {
                if (firstChar == null)
                {
                    firstChar = c;
                }
                else if (firstChar != c)
                {
                    return false;
                }
                count++;
            }
            else
            {
                return false;
            }
        }
        return count >= 3;
    }

    private static string StripBold(string text)
    {
        return System.Text.RegularExpressions.Regex.Replace(text, @"\*\*(.*?)\*\*", "$1");
    }

    private static bool LooksLikeToolBlock(IReadOnlyList<string> lines) =>
        lines.Any(line => StartsToolArtifact(line.Trim()) || LooksLikeToolLine(line.Trim()));

    private static bool StartsToolArtifact(string line) =>
        line.StartsWith("<tool", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("<function", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("<invoke", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("<mcp", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("<ghostclaw_runtime_context", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("assistant to=", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("assistant: to=", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("tool to=", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("tool_use", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("tool_result", StringComparison.OrdinalIgnoreCase);

    private static bool EndsToolArtifact(string line) =>
        line.Contains("</tool", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("</function", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("</invoke", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("</mcp", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("</ghostclaw_runtime_context", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeToolLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (line.Contains("assistant to=", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("\"recipient_name\"", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("\"tool_calls\"", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("\"function_call\"", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("\"tool_use\"", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("\"tool_result\"", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("\"tool_name\"", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("\"mcpServers\"", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var jsonish = line.StartsWith('{') ||
                      line.StartsWith('[') ||
                      line.Contains("\":", StringComparison.Ordinal);
        if (!jsonish)
        {
            return false;
        }

        return line.Contains("\"arguments\"", StringComparison.OrdinalIgnoreCase) &&
               (line.Contains("\"mcp", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("\"tool", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("\"function", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("\"input\"", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("\"name\"", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeMarkdownEscapes(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var value = text;
        if (!value.Contains('\n') && (value.Contains("\\n", StringComparison.Ordinal) || value.Contains("\\r\\n", StringComparison.Ordinal)))
        {
            value = value.Replace("\\r\\n", "\n", StringComparison.Ordinal)
                         .Replace("\\n", "\n", StringComparison.Ordinal);
        }

        foreach (var pair in new (string Escaped, string Plain)[]
        {
            ("\\*\\*", "**"),
            ("\\_\\_", "__"),
            ("\\`", "`"),
            ("\\*", "*"),
            ("\\_", "_"),
            ("\\#", "#"),
            ("\\-", "-"),
            ("\\+", "+"),
            ("\\.", "."),
            ("\\[", "["),
            ("\\]", "]"),
            ("\\(", "("),
            ("\\)", ")"),
            ("\\$", "$"),
            ("\\|", "|"),
            ("\\>", ">")
        })
        {
            value = value.Replace(pair.Escaped, pair.Plain, StringComparison.Ordinal);
        }

        return value.Trim();
    }
}
