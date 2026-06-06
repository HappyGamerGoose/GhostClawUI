using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Text.RegularExpressions;
using GhostClawUI.Service.Infrastructure;
using GhostClawUI.Service.Storage;
using GhostClawUI.Shared;

namespace GhostClawUI.Service.Agent;

internal sealed partial class McpCatalog
{
    private const string BuiltInTimeServerId = "time";
    private const string BuiltInSearchServerId = "web-search";
    private static readonly object SettingsLock = new();
    private readonly EncryptedStore _store;
    private readonly HttpClient _httpClient;
    private readonly AppPaths _paths;

    public McpCatalog(EncryptedStore store, HttpClient httpClient, AppPaths paths)
    {
        _store = store;
        _httpClient = httpClient;
        _paths = paths;
        SeedEmbeddedCatalog();
    }

    public async Task<IReadOnlyList<McpServerDefinition>> RefreshAsync(CancellationToken cancellationToken)
    {
        return await Task.FromResult(List()).ConfigureAwait(false);
    }

    public async Task<McpSearchResponse> SearchOnlineAsync(string? query, int page, int pageSize, CancellationToken cancellationToken)
    {
        var cleanQuery = query?.Trim() ?? string.Empty;

        var allLocal = List().Where(server =>
            string.IsNullOrWhiteSpace(cleanQuery) ||
            server.Name.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase) ||
            server.Description.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase) ||
            server.RegistryUrl.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase)
        ).ToList();

        var totalCount = allLocal.Count;
        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
        if (totalPages < 1) totalPages = 1;

        var serversList = allLocal.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return await Task.FromResult(new McpSearchResponse(serversList, page, totalPages, totalCount)).ConfigureAwait(false);
    }

    private static string AppendPageParam(string url, int page)
    {
        var separator = url.Contains('?') ? "&" : "?";
        return $"{url}{separator}page={page}";
    }

    public IReadOnlyList<McpServerDefinition> List() => VisibleServers(_store.ListMcpServers());



    private void SeedEmbeddedCatalog()
    {
        foreach (var server in EmbeddedServers())
        {
            var existing = _store.ListMcpServers().FirstOrDefault(item => item.Id == server.Id);
            if (existing is null)
            {
                _store.UpsertMcp(server);
                continue;
            }

            if (IsBuiltInServer(server.Id) && !existing.Installed)
            {
                _store.UpsertMcp(existing with { Installed = true, UpdatedAt = DateTimeOffset.UtcNow });
            }
        }

        WriteGhostClawSettings();
    }

    private McpServerDefinition ResolveRequest(McpServerRequest request, bool installed)
    {
        var existing = _store.ListMcpServers().FirstOrDefault(item => item.Id == request.Id);
        if (existing is not null)
        {
            return existing with { Installed = installed, UpdatedAt = DateTimeOffset.UtcNow };
        }

        var args = request.Args ?? SplitCommand(request.Command ?? string.Empty).Skip(1).ToList();
        var command = request.Command;
        if (!string.IsNullOrWhiteSpace(command) && command.Contains(' '))
        {
            command = SplitCommand(command)[0];
        }

        return new McpServerDefinition(
            request.Id,
            request.Name ?? request.Id,
            "Custom MCP server",
            string.IsNullOrWhiteSpace(command) ? "npx" : command,
            args,
            request.RegistryUrl ?? "manual",
            installed,
            null,
            DateTimeOffset.UtcNow);
    }

    private void WriteGhostClawSettings()
    {
        lock (SettingsLock)
        {
            try
            {
                var settingsPath = Path.Combine(_paths.GhostClawRuntimeRoot, "data", "sessions", "main", ".claude", "settings.json");
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

                var mcpServers = new JsonObject();
                foreach (var server in _store.ListMcpServers().Where(item => item.Installed))
                {
                    if (server.Command.Equals("builtin-time", StringComparison.OrdinalIgnoreCase))
                    {
                        AddBuiltInTimeServer(mcpServers);
                        continue;
                    }

                    if (server.Command.Equals("builtin-web-search", StringComparison.OrdinalIgnoreCase))
                    {
                        AddBuiltInSearchServer(mcpServers);
                        continue;
                    }

                    var serverJson = new JsonObject();
                    if (server.Command.Equals("sse", StringComparison.OrdinalIgnoreCase) && server.Args.Count > 0)
                    {
                        serverJson["type"] = "sse";
                        serverJson["url"] = server.Args[0];
                    }
                    else if ((server.Command.Equals("remote", StringComparison.OrdinalIgnoreCase) ||
                              server.Command.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                              server.Command.Equals("streamable-http", StringComparison.OrdinalIgnoreCase)) && server.Args.Count > 0)
                    {
                        serverJson["type"] = "http";
                        serverJson["url"] = server.Args[0];
                    }
                    else
                    {
                        serverJson["command"] = server.Command;
                        serverJson["args"] = new JsonArray(server.Args.Select(arg => JsonValue.Create(arg)).ToArray());
                    }

                    // Check for serialized environment variables in description
                    if (server.Description.StartsWith("__JSON__:", StringComparison.Ordinal))
                    {
                        try
                        {
                            var node = JsonNode.Parse(server.Description[9..])?.AsObject();
                            var envNode = node?["env"]?.AsObject();
                            if (envNode is not null && envNode.Count > 0)
                            {
                                var envObj = new JsonObject();
                                foreach (var prop in envNode)
                                {
                                    envObj[prop.Key] = prop.Value?.DeepClone();
                                }
                                serverJson["env"] = envObj;
                            }
                        }
                        catch
                        {
                            // Ignore parsing failures
                        }
                    }

                    mcpServers[server.Id] = serverJson;
                }

                if (!mcpServers.ContainsKey(BuiltInTimeServerId))
                {
                    AddBuiltInTimeServer(mcpServers);
                }

                if (!mcpServers.ContainsKey(BuiltInSearchServerId))
                {
                    AddBuiltInSearchServer(mcpServers);
                }

                var settings = File.Exists(settingsPath)
                    ? JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? new JsonObject()
                    : new JsonObject();
                settings["mcpServers"] = mcpServers;
                settings["env"] ??= new JsonObject
                {
                    ["CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS"] = "1",
                    ["CLAUDE_CODE_ADDITIONAL_DIRECTORIES_CLAUDE_MD"] = "1"
                };
                File.WriteAllText(settingsPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to write GhostClaw settings: " + ex.Message);
            }
        }
    }


    private static bool IsBuiltInServer(string id) =>
        id.Equals(BuiltInTimeServerId, StringComparison.OrdinalIgnoreCase) ||
        id.Equals(BuiltInSearchServerId, StringComparison.OrdinalIgnoreCase);



    private static IReadOnlyList<string> TrustedRegistryUrls(IReadOnlyList<string> urls)
    {
        var trusted = urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Where(url => url.Contains("smithery.ai", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return trusted.Count > 0
            ? trusted
            : new[] { "https://registry.smithery.ai/servers" };
    }

    private static List<McpServerDefinition> VisibleServers(IReadOnlyList<McpServerDefinition> servers) =>
        servers
            .Where(server => server.Installed || IsHighQualitySource(server.RegistryUrl))
            .GroupBy(CanonicalServerKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(server => server.Installed)
                .ThenBy(SourceRank)
                .ThenBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(server => server.Command.StartsWith("builtin-", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(server => server.Installed)
            .ThenBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsHighQualitySource(string registryUrl) =>
        registryUrl.Equals("embedded", StringComparison.OrdinalIgnoreCase) ||
        registryUrl.Equals("manual", StringComparison.OrdinalIgnoreCase) ||
        registryUrl.Contains("smithery.ai", StringComparison.OrdinalIgnoreCase);

    private static int SourceRank(McpServerDefinition server)
    {
        if (server.RegistryUrl.Equals("embedded", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (server.RegistryUrl.Contains("smithery.ai", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 2;
    }

    private static string CanonicalServerKey(McpServerDefinition server)
    {
        string? package = null;
        foreach (var arg in server.Args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;

            if (arg.StartsWith("https://server.smithery.ai/", StringComparison.OrdinalIgnoreCase))
            {
                package = arg["https://server.smithery.ai/".Length..];
                break;
            }
            if (arg.StartsWith("http://server.smithery.ai/", StringComparison.OrdinalIgnoreCase))
            {
                package = arg["http://server.smithery.ai/".Length..];
                break;
            }
            if (!arg.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !arg.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                (arg.Contains('/', StringComparison.Ordinal) ||
                 arg.Contains("mcp", StringComparison.OrdinalIgnoreCase) ||
                 arg.StartsWith('@')))
            {
                package = arg;
                break;
            }
        }

        string rawKey;
        if (!string.IsNullOrWhiteSpace(package))
        {
            rawKey = package.Trim().TrimEnd('/');
            if (rawKey.Contains('/', StringComparison.Ordinal))
            {
                rawKey = rawKey.Split('/').Last();
            }
        }
        else
        {
            rawKey = server.Name;
        }

        var normalized = rawKey.ToLowerInvariant();

        if (normalized.StartsWith("mcp-server-", StringComparison.Ordinal)) normalized = normalized["mcp-server-".Length..];
        if (normalized.StartsWith("mcp-", StringComparison.Ordinal)) normalized = normalized["mcp-".Length..];
        if (normalized.StartsWith("server-", StringComparison.Ordinal)) normalized = normalized["server-".Length..];

        if (normalized.EndsWith("-mcp-server", StringComparison.Ordinal)) normalized = normalized[..^"-mcp-server".Length];
        if (normalized.EndsWith("-mcp", StringComparison.Ordinal)) normalized = normalized[..^"-mcp".Length];
        if (normalized.EndsWith("-server", StringComparison.Ordinal)) normalized = normalized[..^"-server".Length];

        normalized = normalized
            .Replace("headless browser", string.Empty, StringComparison.Ordinal)
            .Replace("mcp server", string.Empty, StringComparison.Ordinal)
            .Replace("mcp", string.Empty, StringComparison.Ordinal)
            .Replace("server", string.Empty, StringComparison.Ordinal);

        normalized = Regex.Replace(normalized, @"[^a-z0-9]+", string.Empty);
        return string.IsNullOrWhiteSpace(normalized) ? server.Id.ToLowerInvariant() : normalized;
    }



    private static List<string> ReadArgs(JsonElement item, IReadOnlyList<string> commandParts)
    {
        if (item.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
        {
            return args.EnumerateArray()
                .Where(arg => arg.ValueKind == JsonValueKind.String)
                .Select(arg => arg.GetString()!)
                .ToList();
        }

        return commandParts.Skip(1).ToList();
    }

    private static string? GetString(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static int GetInt(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : 0;

    private static string CleanName(string name)
    {
        if (name.Contains('/'))
        {
            name = name.Split('/').Last();
        }

        return name.Replace('-', ' ').Replace('_', ' ');
    }

    private static string? ReadNextCursor(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            return GetString(metadata, "nextCursor")
                   ?? GetString(metadata, "next_cursor")
                   ?? GetString(metadata, "cursor");
        }

        return GetString(root, "nextCursor") ?? GetString(root, "next_cursor");
    }

    private static string AppendCursor(string url, string cursor)
    {
        var separator = url.Contains('?') ? "&" : "?";
        return $"{url}{separator}cursor={Uri.EscapeDataString(cursor)}";
    }

    private static List<string> SplitCommand(string command)
    {
        var result = new List<string>();
        var current = "";
        var inQuotes = false;
        foreach (var ch in command)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current);
                    current = "";
                }
                continue;
            }

            current += ch;
        }

        if (current.Length > 0)
        {
            result.Add(current);
        }

        return result;
    }
}
