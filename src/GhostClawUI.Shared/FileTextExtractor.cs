using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GhostClawUI.Shared;

public static class FileTextExtractor
{
    public static bool IsTextLike(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return new[] { ".txt", ".md", ".json", ".csv", ".log", ".xml", ".yaml", ".yml", ".cs", ".js", ".ts", ".py", ".ini", ".conf", ".sql", ".html", ".css", ".bat", ".ps1", ".sh" }
            .Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    public static string BuildContentType(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".csv" => "text/csv",
            ".xml" => "application/xml",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => "application/octet-stream"
        };
    }

    public static async Task<string?> ReadTextPreviewAsync(string filePath, long size, int maxCharacters)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        // PDF, Word, PowerPoint, Excel — extract text natively (CPU-intensive, run on background thread)
        if (new[] { ".pdf", ".docx", ".doc", ".pptx", ".ppt", ".xlsx", ".xls" }.Contains(ext))
        {
            return await Task.Run(() => ExtractTextNativeAsync(filePath, maxCharacters)).ConfigureAwait(false);
        }

        // Images: skip expensive OCR
        if (new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tiff", ".tif", ".gif" }.Contains(ext))
        {
            return null;
        }

        // Plain text files (support large files by reading incrementally)
        if (!IsTextLike(filePath))
            return null;

        try
        {
            return await Task.Run(async () =>
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                var charBuffer = new char[Math.Min(maxCharacters, 4096)];
                var sb = new StringBuilder();
                int totalCharsRead = 0;
                while (totalCharsRead < maxCharacters)
                {
                    int toRead = Math.Min(charBuffer.Length, maxCharacters - totalCharsRead);
                    int read = await reader.ReadAsync(charBuffer, 0, toRead).ConfigureAwait(false);
                    if (read == 0) break;
                    sb.Append(charBuffer, 0, read);
                    totalCharsRead += read;
                }
                
                var result = sb.ToString();
                return (totalCharsRead >= maxCharacters && reader.Peek() >= 0)
                    ? result + "\n..."
                    : result;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to read text preview: {ex}");
            return null;
        }
    }

    private static async Task<string?> ExtractTextNativeAsync(string filePath, int maxCharacters)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        try
        {
            if (ext == ".pdf")
                return await ExtractPdfTextAsync(filePath, maxCharacters).ConfigureAwait(false);
            if (ext == ".docx")
                return await ExtractDocxTextAsync(filePath, maxCharacters).ConfigureAwait(false);
            if (ext == ".pptx")
                return await ExtractPptxTextAsync(filePath, maxCharacters).ConfigureAwait(false);
            if (ext == ".xlsx")
                return await ExtractXlsxTextAsync(filePath, maxCharacters).ConfigureAwait(false);
            if (new[] { ".doc", ".ppt", ".xls" }.Contains(ext))
                return $"[Older legacy format {ext} is not supported directly for native text extraction. Please convert this file to a modern OpenXML format (like .docx, .pptx, or .xlsx) or copy/paste its text content.]";

            return null;
        }
        catch (Exception ex)
        {
            return $"[Extraction Error: {ex.Message}]";
        }
    }

    private static async Task<string?> ExtractPdfTextAsync(string filePath, int maxCharacters)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = UglyToad.PdfPig.PdfDocument.Open(stream);
            var sb = new StringBuilder();
            int pagesWithText = 0;

            foreach (var page in doc.GetPages())
            {
                if (sb.Length >= maxCharacters)
                {
                    break;
                }

                var pageText = page.Text;
                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    pagesWithText++;
                    sb.AppendLine($"--- Page {page.Number} ---");
                    sb.AppendLine(pageText);
                    sb.AppendLine();
                }
            }

            if (pagesWithText == 0)
            {
                return $"[This PDF appears to be a scanned image ({doc.NumberOfPages} page(s)). No embedded text layer was found.]";
            }

            var result = sb.ToString();
            return result.Length <= maxCharacters ? result : result[..maxCharacters] + "\n...";
        }
        catch (Exception ex)
        {
            return $"[PDF Read Error: {ex.Message}]";
        }
    }

    private static async Task<string?> ExtractDocxTextAsync(string filePath, int maxCharacters)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = archive.GetEntry("word/document.xml");
            if (entry == null) return "[Empty document or missing word/document.xml]";

            using var entryStream = entry.Open();
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, Async = true };
            using var reader = System.Xml.XmlReader.Create(entryStream, settings);
            
            var sb = new StringBuilder();
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.LocalName == "t")
                {
                    var text = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sb.Append(text).Append(" ");
                        if (sb.Length >= maxCharacters) break;
                    }
                }
            }
            var textResult = System.Net.WebUtility.HtmlDecode(sb.ToString().Trim());
            return textResult.Length <= maxCharacters ? textResult : textResult[..maxCharacters] + "\n...";
        }
        catch (Exception ex)
        {
            return $"[Word Read Error: {ex.Message}]";
        }
    }

    private static async Task<string?> ExtractPptxTextAsync(string filePath, int maxCharacters)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var sb = new StringBuilder();
            
            int slideNum = 1;
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, Async = true };
            while (true)
            {
                if (sb.Length >= maxCharacters)
                {
                    break;
                }

                var entry = archive.GetEntry($"ppt/slides/slide{slideNum}.xml");
                if (entry == null) break;

                using var entryStream = entry.Open();
                using var reader = System.Xml.XmlReader.Create(entryStream, settings);
                
                bool hasText = false;
                var slideSb = new StringBuilder();
                
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.LocalName == "t")
                    {
                        var text = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            hasText = true;
                            slideSb.Append(text).Append(" ");
                            if (sb.Length + slideSb.Length >= maxCharacters) break;
                        }
                    }
                }
                
                if (hasText)
                {
                    sb.AppendLine($"--- Slide {slideNum} ---");
                    sb.AppendLine(slideSb.ToString());
                    sb.AppendLine();
                }
                
                slideNum++;
            }

            var textResult = System.Net.WebUtility.HtmlDecode(sb.ToString().Trim());
            if (string.IsNullOrWhiteSpace(textResult)) return "[Empty presentation]";
            return textResult.Length <= maxCharacters ? textResult : textResult[..maxCharacters] + "\n...";
        }
        catch (Exception ex)
        {
            return $"[PowerPoint Read Error: {ex.Message}]";
        }
    }

    private static async Task<string?> ExtractXlsxTextAsync(string filePath, int maxCharacters)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, Async = true };
            
            var sharedStrings = new List<string>();
            var sharedStringsEntry = archive.GetEntry("xl/sharedStrings.xml");
            if (sharedStringsEntry != null)
            {
                using var entryStream = sharedStringsEntry.Open();
                using var reader = System.Xml.XmlReader.Create(entryStream, settings);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.LocalName == "t")
                    {
                        var valText = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        sharedStrings.Add(System.Net.WebUtility.HtmlDecode(valText));
                    }
                }
            }

            var sb = new StringBuilder();
            int sheetNum = 1;
            while (true)
            {
                if (sb.Length >= maxCharacters)
                {
                    break;
                }

                var entry = archive.GetEntry($"xl/worksheets/sheet{sheetNum}.xml");
                if (entry == null) break;

                using var entryStream = entry.Open();
                using var reader = System.Xml.XmlReader.Create(entryStream, settings);
                
                sb.AppendLine($"--- Sheet {sheetNum} ---");
                
                bool inRow = false;
                List<string> cellValues = null;
                string currentCellType = "";
                
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (sb.Length >= maxCharacters) break;
                    
                    if (reader.NodeType == System.Xml.XmlNodeType.Element)
                    {
                        if (reader.LocalName == "row")
                        {
                            inRow = true;
                            cellValues = new List<string>();
                        }
                        else if (inRow && reader.LocalName == "c")
                        {
                            currentCellType = reader.GetAttribute("t") ?? "";
                        }
                        else if (inRow && reader.LocalName == "v")
                        {
                            var currentCellValue = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                            if (!string.IsNullOrEmpty(currentCellValue))
                            {
                                if (currentCellType == "s" && int.TryParse(currentCellValue, out var idx) && idx >= 0 && idx < sharedStrings.Count)
                                {
                                    cellValues.Add(sharedStrings[idx]);
                                }
                                else
                                {
                                    cellValues.Add(currentCellValue);
                                }
                            }
                        }
                    }
                    else if (reader.NodeType == System.Xml.XmlNodeType.EndElement)
                    {
                        if (reader.LocalName == "row")
                        {
                            inRow = false;
                            if (cellValues != null && cellValues.Count > 0)
                            {
                                sb.AppendLine(string.Join("\t", cellValues));
                            }
                        }
                    }
                }
                
                sb.AppendLine();
                sheetNum++;
            }

            var text = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(text)) return "[Empty spreadsheet]";
            return text.Length <= maxCharacters ? text : text[..maxCharacters] + "\n...";
        }
        catch (Exception ex)
        {
            return $"[Excel Read Error: {ex.Message}]";
        }
    }
}
