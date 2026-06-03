using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GhostClawUI.Service.Infrastructure;
using GhostClawUI.Shared;

namespace GhostClawUI.Service.Agent;

internal sealed record McpToolSearchResult(
    string ServerId,
    string ServerName,
    string ToolName,
    string Query,
    string Content);

internal sealed class McpToolRunner
{
    private readonly AppPaths _paths;

    public McpToolRunner(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<McpToolSearchResult?> TrySearchAsync(
        string prompt,
        IReadOnlyList<McpServerDefinition> servers,
        CancellationToken cancellationToken)
    {
        var query = BuildSearchQuery(prompt);
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var candidates = servers
            .Where(server => server.Installed && IsSearchCandidate(server))
            .OrderBy(server => server.Id.Equals("web-search", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(server => server.Name)
            .Take(4)
            .ToList();

        foreach (var server in candidates)
        {
            try
            {
                var result = await TryRunSearchServerAsync(server, query, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(result?.Content))
                {
                    return result;
                }
            }
            catch
            {
                // Tool servers are optional capabilities; one failing server should not break chat.
            }
        }

        return null;
    }

    private async Task<McpToolSearchResult?> TryRunSearchServerAsync(
        McpServerDefinition server,
        string query,
        CancellationToken cancellationToken)
    {
        var launch = ResolveLaunch(server);
        if (launch is null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = launch.Value.FileName,
                WorkingDirectory = _paths.GhostClawRuntimeRoot,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        foreach (var arg in launch.Value.Args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        if (Directory.Exists(_paths.NodeRuntimeRoot))
        {
            process.StartInfo.Environment["PATH"] = _paths.NodeRuntimeRoot + ";" + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        }

        if (!process.Start())
        {
            return null;
        }

        try
        {
            await SendAsync(process, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "GhostClawUI",
                        ["version"] = "1.0.0"
                    }
                }
            }, timeout.Token).ConfigureAwait(false);
            _ = await ReadAsync(process, timeout.Token).ConfigureAwait(false);

            await SendAsync(process, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/initialized"
            }, timeout.Token).ConfigureAwait(false);

            await SendAsync(process, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/list"
            }, timeout.Token).ConfigureAwait(false);
            var toolsResponse = await ReadAsync(process, timeout.Token).ConfigureAwait(false);
            var tool = SelectSearchTool(toolsResponse);
            if (tool is null)
            {
                return null;
            }

            var toolName = tool["name"]?.GetValue<string>() ?? "search";
            await SendAsync(process, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 3,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = toolName,
                    ["arguments"] = BuildToolArguments(tool, query)
                }
            }, timeout.Token).ConfigureAwait(false);
            var callResponse = await ReadAsync(process, timeout.Token).ConfigureAwait(false);
            var content = ExtractToolText(callResponse);
            return string.IsNullOrWhiteSpace(content)
                ? null
                : new McpToolSearchResult(server.Id, server.Name, toolName, query, content);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private (string FileName, IReadOnlyList<string> Args)? ResolveLaunch(McpServerDefinition server)
    {
        if (server.Command.Equals("remote", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (server.Command.Equals("builtin-web-search", StringComparison.OrdinalIgnoreCase))
        {
            var scriptPath = Path.Combine(_paths.RuntimeRoot, "builtins", "ghostclaw-web-search-mcp.js");
            return File.Exists(scriptPath)
                ? (_paths.ResolveNodeExe(), new[] { scriptPath })
                : null;
        }

        if (server.Command.Equals("npx", StringComparison.OrdinalIgnoreCase))
        {
            var cli = Path.Combine(_paths.NodeRuntimeRoot, "node_modules", "npm", "bin", "npx-cli.js");
            return File.Exists(cli)
                ? (_paths.ResolveNodeExe(), new[] { cli }.Concat(server.Args).ToList())
                : ("npx", server.Args);
        }

        if (server.Command.Equals("npm", StringComparison.OrdinalIgnoreCase))
        {
            var cli = Path.Combine(_paths.NodeRuntimeRoot, "node_modules", "npm", "bin", "npm-cli.js");
            return File.Exists(cli)
                ? (_paths.ResolveNodeExe(), new[] { cli }.Concat(server.Args).ToList())
                : ("npm", server.Args);
        }

        if (server.Command.Equals("node", StringComparison.OrdinalIgnoreCase))
        {
            return (_paths.ResolveNodeExe(), server.Args);
        }

        return (server.Command, server.Args);
    }

    private static async Task SendAsync(Process process, JsonObject message, CancellationToken cancellationToken)
    {
        var json = message.ToJsonString();
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await process.StandardInput.BaseStream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await process.StandardInput.BaseStream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonObject> ReadAsync(Process process, CancellationToken cancellationToken)
    {
        var reader = process.StandardOutput;
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null)
            {
                throw new InvalidOperationException("MCP server closed stdout before sending a response.");
            }
            line = line.Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(line, @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    throw new InvalidOperationException("Invalid Content-Length header.");
                }
                int length = int.Parse(match.Groups[1].Value);
                
                // Read the empty separator line if it hasn't been consumed yet
                // Standard stream next has "\r\n" which ReadLineAsync might not have consumed
                // ReadLineAsync reads the Content-Length line. If the server sent Content-Length:\r\n\r\n
                // then next in stream is the empty separator line. Let's read it.
                await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                var charBuffer = new char[length];
                int offset = 0;
                while (offset < length)
                {
                    int read = await reader.ReadAsync(charBuffer, offset, length - offset).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new InvalidOperationException("MCP server closed stdout mid-response.");
                    }
                    offset += read;
                }
                var bodyText = new string(charBuffer);
                return JsonNode.Parse(bodyText)?.AsObject()
                    ?? throw new InvalidOperationException("MCP response was not a JSON object.");
            }
            else if (line.StartsWith("{"))
            {
                // Plain newline-delimited JSON line!
                return JsonNode.Parse(line)?.AsObject()
                    ?? throw new InvalidOperationException("MCP response was not a JSON object.");
            }
        }
    }

    private static bool EndsWith(List<byte> bytes, string suffix)
    {
        var tail = Encoding.ASCII.GetBytes(suffix);
        if (bytes.Count < tail.Length)
        {
            return false;
        }

        for (var i = 0; i < tail.Length; i++)
        {
            if (bytes[bytes.Count - tail.Length + i] != tail[i])
            {
                return false;
            }
        }

        return true;
    }

    private static JsonObject? SelectSearchTool(JsonObject toolsResponse)
    {
        var tools = toolsResponse["result"]?["tools"]?.AsArray();
        if (tools is null)
        {
            return null;
        }

        return tools
            .OfType<JsonObject>()
            .OrderBy(tool =>
            {
                var name = tool["name"]?.GetValue<string>()?.ToLowerInvariant() ?? string.Empty;
                return name.Contains("search") ? 0 : name.Contains("web") ? 1 : 2;
            })
            .FirstOrDefault(tool =>
            {
                var name = tool["name"]?.GetValue<string>()?.ToLowerInvariant() ?? string.Empty;
                var description = tool["description"]?.GetValue<string>()?.ToLowerInvariant() ?? string.Empty;
                return name.Contains("search") || name.Contains("web") || description.Contains("search the web");
            });
    }

    private static JsonObject BuildToolArguments(JsonObject tool, string query)
    {
        var args = new JsonObject();
        var properties = tool["inputSchema"]?["properties"]?.AsObject();
        var queryName = new[] { "query", "q", "search", "searchTerms", "text" }
            .FirstOrDefault(name => properties?.ContainsKey(name) == true)
            ?? "query";

        args[queryName] = query;
        if (properties?.ContainsKey("maxResults") == true)
        {
            args["maxResults"] = 5;
        }
        else if (properties?.ContainsKey("limit") == true)
        {
            args["limit"] = 5;
        }
        else if (properties?.ContainsKey("count") == true)
        {
            args["count"] = 5;
        }

        return args;
    }

    private static string ExtractToolText(JsonObject response)
    {
        if (response["error"] is JsonNode error)
        {
            throw new InvalidOperationException(error.ToJsonString());
        }

        var result = response["result"];
        var content = result?["content"]?.AsArray();
        if (content is not null)
        {
            var builder = new StringBuilder();
            foreach (var part in content.OfType<JsonObject>())
            {
                if (part["text"] is JsonNode text)
                {
                    builder.AppendLine(text.GetValue<string>());
                }
                else if (part["content"] is JsonNode nested)
                {
                    builder.AppendLine(nested.GetValue<string>());
                }
            }

            if (builder.Length > 0)
            {
                return builder.ToString().Trim();
            }
        }

        return result?.ToJsonString() ?? string.Empty;
    }

    private static bool IsSearchCandidate(McpServerDefinition server)
    {
        var text = $"{server.Id} {server.Name} {server.Description} {server.Command}".ToLowerInvariant();
        return text.Contains("search") || text.Contains("exa") || text.Contains("brave") || text.Contains("web");
    }

    private static string BuildSearchQuery(string prompt)
    {
        var cleaned = Regex.Replace(prompt, "<ghostclaw_runtime_context>.*?</ghostclaw_runtime_context>", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        cleaned = cleaned.ReplaceLineEndings(" ").Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        return cleaned.Length <= 240 ? cleaned : cleaned[..240];
    }
}
