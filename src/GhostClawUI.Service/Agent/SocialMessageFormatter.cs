using System;
using System.Text.RegularExpressions;

namespace GhostClawUI.Service.Agent;

internal static class SocialMessageFormatter
{
    public static string ToTelegramHtml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";

        var html = markdown
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");

        // Convert code blocks (pre)
        html = Regex.Replace(html, @"```\w*\n([\s\S]*?)```", "<pre><code>$1</code></pre>");
        
        // Convert inline code
        html = Regex.Replace(html, @"`([^`\n]+)`", "<code>$1</code>");
        
        // Convert bold
        html = Regex.Replace(html, @"\*\*(.+?)\*\*", "<b>$1</b>");
        
        // Convert italic
        html = Regex.Replace(html, @"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", "<i>$1</i>");

        return html;
    }

    public static string ToWhatsAppMarkdown(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";

        var wa = markdown;

        // Convert Headers to Bold
        wa = Regex.Replace(wa, @"^#+\s+(.+)$", "*$1*", RegexOptions.Multiline);

        // Convert standard Markdown bold (**bold**) to WhatsApp bold (*bold*)
        wa = Regex.Replace(wa, @"\*\*(.+?)\*\*", "*$1*");

        // Convert standard Markdown italic (*italic*) to WhatsApp italic (_italic_)
        wa = Regex.Replace(wa, @"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", "_$1_");

        // Convert Markdown strikethrough (~~strike~~) to WhatsApp strikethrough (~strike~)
        wa = Regex.Replace(wa, @"\~\~(.+?)\~\~", "~$1~");

        return wa;
    }
}
