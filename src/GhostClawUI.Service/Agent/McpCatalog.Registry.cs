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
    private static IEnumerable<McpServerDefinition> ParseRegistry(string registryUrl, string json)
    {
        var trimmed = json.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            if (registryUrl.Contains("higress", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var server in ParseHigressMarketplace(registryUrl, json))
                {
                    yield return server;
                }
            }

            yield break;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("objects", out var npmObjects) && npmObjects.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in npmObjects.EnumerateArray())
            {
                if (!item.TryGetProperty("package", out var package) || package.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var packageName = GetString(package, "name");
                if (string.IsNullOrWhiteSpace(packageName))
                {
                    continue;
                }

                yield return new McpServerDefinition(
                    "npm-" + packageName.Replace('@', 'a').Replace('/', '-'),
                    CleanName(packageName),
                    GetString(package, "description") ?? "MCP server package from npm.",
                    "npx",
                    new[] { "-y", packageName },
                    registryUrl,
                    false,
                    GetString(package, "version"),
                    DateTimeOffset.UtcNow);
            }

            yield break;
        }

        var items = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array
                ? servers.EnumerateArray()
                : root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                    ? data.EnumerateArray()
                    : Enumerable.Empty<JsonElement>();

        foreach (var item in items)
        {
            var server = item;
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("server", out var wrappedServer) && wrappedServer.ValueKind == JsonValueKind.Object)
            {
                server = wrappedServer;
            }

            var isSmithery = registryUrl.Contains("smithery.ai", StringComparison.OrdinalIgnoreCase);
            if (isSmithery && !PassesSmitheryQualityGate(server))
            {
                continue;
            }

            var id = isSmithery
                ? GetString(server, "qualifiedName") ?? GetString(server, "id") ?? GetString(server, "name")
                : GetString(server, "id") ??
                  GetString(server, "name") ??
                  GetString(server, "serverName") ??
                  GetString(server, "qualifiedName");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var status = GetString(server, "status");
            if (status is not null && status.Equals("deleted", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var install = ResolveInstall(server, registryUrl);
            var commandText = GetString(server, "command") ?? install.Command;
            var parts = SplitCommand(commandText);
            var logoUrl = GetString(server, "iconUrl") ?? GetString(server, "logoUrl") ?? GetString(server, "logo") ?? GetString(server, "icon");
            yield return new McpServerDefinition(
                id,
                CleanName(GetString(server, "title") ?? GetString(server, "displayName") ?? GetString(server, "name") ?? id),
                GetString(server, "description") ?? GetString(server, "summary") ?? "Registry MCP server",
                parts.Count > 0 ? parts[0] : commandText,
                install.Args.Count > 0 ? install.Args : ReadArgs(server, parts),
                registryUrl,
                false,
                GetString(server, "version"),
                DateTimeOffset.UtcNow,
                logoUrl);
        }
    }

    private static IEnumerable<McpServerDefinition> ParseHigressMarketplace(string registryUrl, string html)
    {
        var now = DateTimeOffset.UtcNow;
        var seen = new Dictionary<string, McpServerDefinition>(StringComparer.OrdinalIgnoreCase);
        var baseUri = new Uri("https://mcp.higress.ai/");
        foreach (Match match in Regex.Matches(html, @"/server/server\d+", RegexOptions.IgnoreCase))
        {
            var href = match.Value;
            var page = new Uri(baseUri, href).ToString();
            var id = "higress-" + href.Split('/').Last();
            var snippet = SliceAround(html, match.Index, 1400);
            var text = CleanHtmlText(snippet);
            var known = MatchKnownHigress(text);
            var name = known.Name ?? GuessHigressName(text, id);
            var description = known.Description ?? GuessHigressDescription(text);
            seen[id] = new McpServerDefinition(id, name, description, "remote", new[] { page }, registryUrl, false, "higress", now);
        }

        if (seen.Count == 0)
        {
            foreach (var server in HigressSeedServers(registryUrl, now))
            {
                yield return server;
            }

            yield break;
        }

        foreach (var server in seen.Values.OrderBy(server => server.Name))
        {
            yield return server;
        }
    }

    private static IEnumerable<McpServerDefinition> HigressSeedServers(string registryUrl, DateTimeOffset now)
    {
        yield return new McpServerDefinition("higress-context7", "Context7", "Higress catalog entry for up-to-date library documentation and examples.", "remote", new[] { "https://mcp.higress.ai/" }, registryUrl, false, "higress", now);
        yield return new McpServerDefinition("higress-brave-search", "Brave Search", "Higress catalog entry for web search through Brave Search.", "remote", new[] { "https://mcp.higress.ai/" }, registryUrl, false, "higress", now);
        yield return new McpServerDefinition("higress-wolframalpha", "WolframAlpha", "Higress catalog entry for computational knowledge and math answers.", "remote", new[] { "https://mcp.higress.ai/" }, registryUrl, false, "higress", now);
        yield return new McpServerDefinition("higress-e2b", "E2B", "Higress catalog entry for sandboxed code execution.", "remote", new[] { "https://mcp.higress.ai/" }, registryUrl, false, "higress", now);
        yield return new McpServerDefinition("higress-calendar-holiday", "Calendar Holiday", "Higress catalog entry for holiday and calendar lookups.", "remote", new[] { "https://mcp.higress.ai/" }, registryUrl, false, "higress", now);
        yield return new McpServerDefinition("higress-ip-location", "IP Location", "Higress catalog entry for IP geolocation lookups.", "remote", new[] { "https://mcp.higress.ai/" }, registryUrl, false, "higress", now);
    }

    private static (string? Name, string? Description) MatchKnownHigress(string text)
    {
        var known = new (string Name, string Description)[]
        {
            ("Context7", "Up-to-date library documentation and framework examples."),
            ("Brave Search", "Web search through the Brave Search API."),
            ("WolframAlpha", "Computational knowledge, math, and structured answers."),
            ("E2B", "Secure sandboxed code execution for agent workflows."),
            ("Calendar Holiday", "Holiday and calendar lookup utilities."),
            ("Stock Helper", "Market data and stock helper tools."),
            ("Blockscout", "Blockchain explorer and smart contract context."),
            ("IP Location", "IP geolocation and network location lookups."),
            ("Time", "Current time, timezone, and date utilities.")
        };

        return known.FirstOrDefault(item => text.Contains(item.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static string GuessHigressName(string text, string id)
    {
        var match = Regex.Match(text, @"(?<name>[A-Z][A-Za-z0-9][A-Za-z0-9 .&+-]{2,60})\s+(MCP|Model Context Protocol|server|Server)\b");
        if (match.Success)
        {
            return match.Groups["name"].Value.Trim();
        }

        return id.Replace("higress-", "Higress ", StringComparison.OrdinalIgnoreCase);
    }

    private static string GuessHigressDescription(string text)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length == 0)
        {
            return "MCP server from the Higress marketplace.";
        }

        return text.Length <= 180 ? text : text[..180] + "...";
    }

    private static string SliceAround(string value, int index, int radius)
    {
        var start = Math.Max(0, index - radius);
        var length = Math.Min(value.Length - start, radius * 2);
        return value.Substring(start, length);
    }

    private static string CleanHtmlText(string html)
    {
        var withoutScripts = Regex.Replace(html, "<script.*?</script>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var withoutStyles = Regex.Replace(withoutScripts, "<style.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var text = Regex.Replace(withoutStyles, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static bool PassesSmitheryQualityGate(JsonElement server)
    {
        return GetBool(server, "isDeployed") ||
               GetBool(server, "remote") ||
               !string.IsNullOrWhiteSpace(GetString(server, "qualifiedName")) ||
               (server.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Array && packages.GetArrayLength() > 0);
    }

    private static (string Command, IReadOnlyList<string> Args) ResolveInstall(JsonElement server, string registryUrl)
    {
        if (registryUrl.Contains("smithery.ai", StringComparison.OrdinalIgnoreCase))
        {
            var qualifiedName = GetString(server, "qualifiedName");
            if (!string.IsNullOrWhiteSpace(qualifiedName) && GetBool(server, "remote") && GetBool(server, "isDeployed"))
            {
                return ("remote", new[] { $"https://server.smithery.ai/{qualifiedName.TrimStart('/')}" });
            }

            var name = GetString(server, "qualifiedName") ?? GetString(server, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                return ("npx", new[] { "-y", name });
            }
        }

        if (server.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Array)
        {
            foreach (var package in packages.EnumerateArray())
            {
                var registry = GetString(package, "registry_name") ?? GetString(package, "registry") ?? GetString(package, "type");
                var name = GetString(package, "name") ?? GetString(package, "package");
                if (!string.IsNullOrWhiteSpace(name) &&
                    (registry is null || registry.Contains("npm", StringComparison.OrdinalIgnoreCase)))
                {
                    return ("npx", new[] { "-y", name });
                }

                if (!string.IsNullOrWhiteSpace(name) &&
                    registry is not null &&
                    registry.Contains("pypi", StringComparison.OrdinalIgnoreCase))
                {
                    return ("uvx", new[] { name });
                }
            }
        }

        if (server.TryGetProperty("remotes", out var remotes) && remotes.ValueKind == JsonValueKind.Array)
        {
            foreach (var remote in remotes.EnumerateArray())
            {
                var url = GetString(remote, "url") ?? GetString(remote, "endpoint");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return ("remote", new[] { url });
                }
            }
        }

        return ("npx", Array.Empty<string>());
    }
}
