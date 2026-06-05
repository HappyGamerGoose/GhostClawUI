import os
import re

file_path = r"c:\Users\akshi\Documents\GhostClawUI\src\GhostClawUI.App\Views\ChatView.cs"
with open(file_path, "r", encoding="utf-8") as f:
    content = f.read()

# Make methods static
methods_to_static = [
    r"private string\? ResolveLocalFilePath",
    r"private SolidColorBrush UserBubbleBrush",
    r"private SolidColorBrush UserBubbleBorderBrush",
    r"private void SetFallbackBrandIcon",
    r"private Grid Trace\(",
    r"private UIElement GetNativeBrandLogoElement",
    r"private async Task LoadAvatarLogoAsync",
    r"private \(string Name, string Url, string Description, string Author, bool Supported, string Fallback\) GetBrandInfo",
    r"private Border RenderReasoningCard",
    r"private Grid TraceRow",
    r"private Border RenderAgentExecutionCard"
]

for pattern in methods_to_static:
    content = re.sub(pattern, lambda m: m.group(0).replace("private", "private static"), content)

# Change IReadOnlyList to List in AttachmentMetadata
content = re.sub(r"private (System\.Text\.Json\.Nodes\.)?JsonNode\? AttachmentMetadata\(IReadOnlyList<ChatAttachment> attachments\)", r"private static System.Text.Json.Nodes.JsonObject? AttachmentMetadata(List<ChatAttachment> attachments)", content)
content = re.sub(r"private JsonObject\? AttachmentMetadata\(IReadOnlyList<ChatAttachment> attachments\)", r"private static System.Text.Json.Nodes.JsonObject? AttachmentMetadata(List<ChatAttachment> attachments)", content)

# Change Brush to SolidColorBrush in ResourceBrush
content = re.sub(r"private Brush ResourceBrush", r"private static SolidColorBrush ResourceBrush", content)

# Remove HandCursorBorder or make it public
content = re.sub(r"internal sealed class HandCursorBorder", r"public sealed class HandCursorBorder", content)

# Fix CA1307 in ChatView.cs - string.Contains without StringComparison
content = re.sub(r"if \(title\.Contains\(\"Search\"\)\)", r'if (title.Contains("Search", StringComparison.Ordinal))', content)
content = re.sub(r"if \(title\.Contains\(\"Memory\"\)\)", r'if (title.Contains("Memory", StringComparison.Ordinal))', content)
content = re.sub(r"if \(title\.Contains\(\"Context\"\)\)", r'if (title.Contains("Context", StringComparison.Ordinal))', content)
content = re.sub(r"if \(title\.Contains\(\"Files\"\)\)", r'if (title.Contains("Files", StringComparison.Ordinal))', content)
content = re.sub(r"if \(title\.Contains\(\"Workspace\"\)\)", r'if (title.Contains("Workspace", StringComparison.Ordinal))', content)

# Or generally in RenderAgentExecutionCard (which might have title.Contains("Reasoning"))
content = re.sub(r'title\.Contains\("([^"]+)"\)', r'title.Contains("\1", StringComparison.OrdinalIgnoreCase)', content)


with open(file_path, "w", encoding="utf-8") as f:
    f.write(content)

print("Done phase 2")
