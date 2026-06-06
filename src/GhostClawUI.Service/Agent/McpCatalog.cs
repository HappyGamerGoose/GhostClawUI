using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Text.RegularExpressions;
using GhostClawUI.Service.Infrastructure;
using GhostClawUI.Service.Storage;
using GhostClawUI.Shared;

namespace GhostClawUI.Service.Agent;

internal sealed class McpCatalog
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

    public CommandResult Install(McpServerRequest request)
    {
        var server = ResolveRequest(request, installed: true);
        _store.UpsertMcp(server);
        WriteGhostClawSettings();
        return new CommandResult(true, $"{server.Name} installed.");
    }

    public CommandResult Update(McpServerRequest request)
    {
        if (IsBuiltInServer(request.Id))
        {
            WriteGhostClawSettings();
            return new CommandResult(true, "Built-in MCP server is always current.");
        }

        var server = ResolveRequest(request, installed: true) with { UpdatedAt = DateTimeOffset.UtcNow };
        _store.UpsertMcp(server);
        WriteGhostClawSettings();
        return new CommandResult(true, $"{server.Name} updated.");
    }

    public CommandResult Uninstall(McpServerRequest request)
    {
        if (IsBuiltInServer(request.Id))
        {
            WriteGhostClawSettings();
            return new CommandResult(true, "Built-in MCP server stays enabled.");
        }

        var server = ResolveRequest(request, installed: false);
        _store.UpsertMcp(server);
        WriteGhostClawSettings();
        return new CommandResult(true, $"{server.Name} uninstalled.");
    }

    public CommandResult AddManual(SimpleTextRequest request)
    {
        var parts = SplitCommand(request.Text);
        if (parts.Count == 0)
        {
            return new CommandResult(false, "Command is empty.");
        }

        var id = "manual-" + Guid.NewGuid().ToString("N")[..8];
        var server = new McpServerDefinition(
            id,
            id,
            "Manual MCP server",
            parts[0],
            parts.Skip(1).ToList(),
            "manual",
            true,
            null,
            DateTimeOffset.UtcNow);
        _store.UpsertMcp(server);
        WriteGhostClawSettings();
        return new CommandResult(true, "Manual server added.");
    }

    public void EnsureGhostClawSettings() => WriteGhostClawSettings();

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

    private void AddBuiltInTimeServer(JsonObject mcpServers)
    {
        var scriptPath = WriteBuiltInTimeServer();
        mcpServers[BuiltInTimeServerId] = new JsonObject
        {
            ["command"] = _paths.ResolveNodeExe(),
            ["args"] = new JsonArray(JsonValue.Create(scriptPath))
        };
    }

    private void AddBuiltInSearchServer(JsonObject mcpServers)
    {
        var scriptPath = WriteBuiltInSearchServer();
        mcpServers[BuiltInSearchServerId] = new JsonObject
        {
            ["command"] = _paths.ResolveNodeExe(),
            ["args"] = new JsonArray(JsonValue.Create(scriptPath))
        };
    }

    private string WriteBuiltInTimeServer()
    {
        var serverRoot = Path.Combine(_paths.RuntimeRoot, "builtins");
        Directory.CreateDirectory(serverRoot);
        var scriptPath = Path.Combine(serverRoot, "ghostclaw-time-mcp.js");
        const string script = """
const serverInfo = { name: 'ghostclaw-time', version: '1.0.0' };
let buffer = Buffer.alloc(0);
let useContentLength = false;

process.stdin.on('data', chunk => {
  buffer = Buffer.concat([buffer, chunk]);
  drain();
});

function drain() {
  while (true) {
    let firstChar = '';
    for (let i = 0; i < buffer.length; i++) {
      const charCode = buffer[i];
      if (charCode !== 32 && charCode !== 9 && charCode !== 10 && charCode !== 13) {
        firstChar = String.fromCharCode(charCode);
        break;
      }
    }
    if (!firstChar) {
      buffer = Buffer.alloc(0);
      return;
    }

    if (firstChar === '{') {
      const newlineIndex = buffer.indexOf('\n');
      if (newlineIndex < 0) return;
      const line = buffer.slice(0, newlineIndex).toString('utf8').trim();
      buffer = buffer.slice(newlineIndex + 1);
      if (line) {
        useContentLength = false;
        try {
          handle(JSON.parse(line));
        } catch (err) {
        }
      }
    } else {
      const headerEnd = buffer.indexOf('\r\n\r\n');
      const fallbackHeaderEnd = headerEnd < 0 ? buffer.indexOf('\n\n') : headerEnd;
      if (fallbackHeaderEnd < 0) return;
      const separatorLength = headerEnd >= 0 ? 4 : 2;
      const header = buffer.slice(0, fallbackHeaderEnd).toString('utf8');
      const match = /Content-Length:\s*(\d+)/i.exec(header);
      if (!match) {
        buffer = buffer.slice(fallbackHeaderEnd + separatorLength);
        continue;
      }
      const length = Number(match[1]);
      const messageStart = fallbackHeaderEnd + separatorLength;
      const messageEnd = messageStart + length;
      if (buffer.length < messageEnd) return;
      const body = buffer.slice(messageStart, messageEnd).toString('utf8');
      buffer = buffer.slice(messageEnd);
      useContentLength = true;
      try {
        handle(JSON.parse(body));
      } catch (err) {
      }
    }
  }
}

function send(message) {
  const json = JSON.stringify(message);
  if (useContentLength) {
    const body = Buffer.from(json, 'utf8');
    process.stdout.write(`Content-Length: ${body.length}\r\n\r\n`);
    process.stdout.write(body);
  } else {
    process.stdout.write(json + '\n');
  }
}

function success(id, result) {
  send({ jsonrpc: '2.0', id, result });
}

function error(id, code, message) {
  send({ jsonrpc: '2.0', id, error: { code, message } });
}

function handle(message) {
  if (!Object.prototype.hasOwnProperty.call(message, 'id')) return;
  switch (message.method) {
    case 'initialize':
      success(message.id, {
        protocolVersion: message.params?.protocolVersion || '2024-11-05',
        capabilities: { tools: {} },
        serverInfo
      });
      return;
    case 'tools/list':
      success(message.id, {
        tools: [{
          name: 'get_current_time',
          description: 'Return the current date and time. Use this whenever relative dates, schedules, deadlines, or “now/today/tomorrow” appear.',
          inputSchema: {
            type: 'object',
            properties: {
              timeZone: { type: 'string', description: 'Optional IANA time zone, for example Asia/Kolkata or America/New_York.' }
            }
          }
        }]
      });
      return;
    case 'tools/call':
      if (message.params?.name !== 'get_current_time') {
        error(message.id, -32601, `Unknown tool: ${message.params?.name || ''}`);
        return;
      }
      success(message.id, {
        content: [{ type: 'text', text: JSON.stringify(currentTime(message.params?.arguments?.timeZone), null, 2) }]
      });
      return;
    default:
      error(message.id, -32601, `Unknown method: ${message.method}`);
  }
}

function currentTime(timeZone) {
  const now = new Date();
  const zone = typeof timeZone === 'string' && timeZone.trim() ? timeZone.trim() : Intl.DateTimeFormat().resolvedOptions().timeZone;
  const formatter = new Intl.DateTimeFormat('en-US', {
    timeZone: zone,
    dateStyle: 'full',
    timeStyle: 'long'
  });
  return {
    isoUtc: now.toISOString(),
    timeZone: zone,
    local: formatter.format(now),
    unixMilliseconds: now.getTime()
  };
}
""";
        try
        {
            File.WriteAllText(scriptPath, script);
        }
        catch (UnauthorizedAccessException)
        {
            if (!File.Exists(scriptPath)) throw;
        }
        return scriptPath;
    }

    private string WriteBuiltInSearchServer()
    {
        var serverRoot = Path.Combine(_paths.RuntimeRoot, "builtins");
        Directory.CreateDirectory(serverRoot);
        var scriptPath = Path.Combine(serverRoot, "ghostclaw-web-search-mcp.js");
        const string script = """
const serverInfo = { name: 'ghostclaw-web-search', version: '1.0.0' };
let buffer = Buffer.alloc(0);
let useContentLength = false;

process.stdin.on('data', chunk => {
  buffer = Buffer.concat([buffer, chunk]);
  drain();
});

function drain() {
  while (true) {
    let firstChar = '';
    for (let i = 0; i < buffer.length; i++) {
      const charCode = buffer[i];
      if (charCode !== 32 && charCode !== 9 && charCode !== 10 && charCode !== 13) {
        firstChar = String.fromCharCode(charCode);
        break;
      }
    }
    if (!firstChar) {
      buffer = Buffer.alloc(0);
      return;
    }

    if (firstChar === '{') {
      const newlineIndex = buffer.indexOf('\n');
      if (newlineIndex < 0) return;
      const line = buffer.slice(0, newlineIndex).toString('utf8').trim();
      buffer = buffer.slice(newlineIndex + 1);
      if (line) {
        useContentLength = false;
        try {
          const parsed = JSON.parse(line);
          handle(parsed).catch(err => error(parsed.id, -32000, err.message || String(err)));
        } catch (err) {
        }
      }
    } else {
      const headerEnd = buffer.indexOf('\r\n\r\n');
      const fallbackHeaderEnd = headerEnd < 0 ? buffer.indexOf('\n\n') : headerEnd;
      if (fallbackHeaderEnd < 0) return;
      const separatorLength = headerEnd >= 0 ? 4 : 2;
      const header = buffer.slice(0, fallbackHeaderEnd).toString('utf8');
      const match = /Content-Length:\s*(\d+)/i.exec(header);
      if (!match) {
        buffer = buffer.slice(fallbackHeaderEnd + separatorLength);
        continue;
      }
      const length = Number(match[1]);
      const messageStart = fallbackHeaderEnd + separatorLength;
      const messageEnd = messageStart + length;
      if (buffer.length < messageEnd) return;
      const body = buffer.slice(messageStart, messageEnd).toString('utf8');
      buffer = buffer.slice(messageEnd);
      useContentLength = true;
      try {
        const parsed = JSON.parse(body);
        handle(parsed).catch(err => error(parsed.id, -32000, err.message || String(err)));
      } catch (err) {
      }
    }
  }
}

function send(message) {
  const json = JSON.stringify(message);
  if (useContentLength) {
    const body = Buffer.from(json, 'utf8');
    process.stdout.write(`Content-Length: ${body.length}\r\n\r\n`);
    process.stdout.write(body);
  } else {
    process.stdout.write(json + '\n');
  }
}

function success(id, result) {
  send({ jsonrpc: '2.0', id, result });
}

function error(id, code, message) {
  send({ jsonrpc: '2.0', id, error: { code, message } });
}

async function handle(message) {
  if (!Object.prototype.hasOwnProperty.call(message, 'id')) return;
  switch (message.method) {
    case 'initialize':
      success(message.id, {
        protocolVersion: message.params?.protocolVersion || '2024-11-05',
        capabilities: { tools: {} },
        serverInfo
      });
      return;
    case 'tools/list':
      success(message.id, {
        tools: [{
          name: 'web_search',
          description: 'Search the web for current information and return concise source snippets.',
          inputSchema: {
            type: 'object',
            required: ['query'],
            properties: {
              query: { type: 'string', description: 'Search query.' },
              maxResults: { type: 'integer', minimum: 1, maximum: 8, description: 'Maximum result count.' }
            }
          }
        }]
      });
      return;
    case 'tools/call':
      if (message.params?.name !== 'web_search') {
        error(message.id, -32601, `Unknown tool: ${message.params?.name || ''}`);
        return;
      }
      success(message.id, {
        content: [{ type: 'text', text: await searchWeb(message.params?.arguments || {}) }]
      });
      return;
    default:
      error(message.id, -32601, `Unknown method: ${message.method}`);
  }
}

async function searchWeb(args) {
  const query = String(args.query || '').trim();
  if (!query) throw new Error('Search query is required.');
  const maxResults = Math.max(1, Math.min(Number(args.maxResults || 5), 8));
  const url = `https://www.bing.com/search?q=${encodeURIComponent(query)}&format=rss`;
  const response = await fetch(url, { headers: { accept: 'application/rss+xml, application/xml, text/xml' } });
  if (!response.ok) throw new Error(`Search failed with HTTP ${response.status}`);
  const xml = await response.text();
  const results = parseRss(xml).slice(0, maxResults);
  if (results.length === 0) {
    return JSON.stringify({ query, results: [], note: 'No concise search snippets were returned. Try a more specific query or install Exa/Brave for deeper search.' }, null, 2);
  }
  return JSON.stringify({ query, results }, null, 2);
}

function parseRss(xml) {
  const rows = [];
  const itemRegex = /<item>([\s\S]*?)<\/item>/gi;
  let match;
  while ((match = itemRegex.exec(xml)) !== null) {
    const item = match[1];
    const title = readTag(item, 'title');
    const link = readTag(item, 'link');
    const description = readTag(item, 'description');
    if (title || description || link) {
      rows.push({ title, snippet: description, url: link });
    }
  }
  return rows;
}

function readTag(xml, tag) {
  const match = new RegExp(`<${tag}>([\\s\\S]*?)<\\/${tag}>`, 'i').exec(xml);
  return match ? decodeXml(match[1].replace(/<!\\[CDATA\\[|\\]\\]>/g, '').trim()) : '';
}

function decodeXml(value) {
  return value
    .replace(/&amp;/g, '&')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'");
}
""";
        try
        {
            File.WriteAllText(scriptPath, script);
        }
        catch (UnauthorizedAccessException)
        {
            if (!File.Exists(scriptPath)) throw;
        }
        return scriptPath;
    }

    private static bool IsBuiltInServer(string id) =>
        id.Equals(BuiltInTimeServerId, StringComparison.OrdinalIgnoreCase) ||
        id.Equals(BuiltInSearchServerId, StringComparison.OrdinalIgnoreCase);

    private static McpServerDefinition[] EmbeddedServers()
    {
        var now = DateTimeOffset.UtcNow;
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return new[]
        {
            new McpServerDefinition("time", "Time", "Built-in current time, date, timezone, and scheduling context for every agent run.", "builtin-time", Array.Empty<string>(), "embedded", true, "built-in", now),
            new McpServerDefinition("web-search", "Web Search", "Built-in lightweight web search for current information, source snippets, and search-grounded answers.", "builtin-web-search", Array.Empty<string>(), "embedded", true, "built-in", now),
            new McpServerDefinition("context7", "Context7", "Documentation and library context lookup.", "npx", new[] { "-y", "@upstash/context7-mcp" }, "embedded", false, null, now),
            new McpServerDefinition("exa", "Exa", "Neural web search and research.", "npx", new[] { "-y", "exa-mcp-server" }, "embedded", false, null, now),
            new McpServerDefinition("playwright", "Playwright headless browser", "Browser automation, screenshots, and page inspection.", "npx", new[] { "-y", "@playwright/mcp", "--headless" }, "embedded", true, null, now),
            new McpServerDefinition("code-sandbox", "Code sandbox", "Isolated command and code execution server.", "npx", new[] { "-y", "@modelcontextprotocol/server-everything" }, "embedded", true, null, now),
            new McpServerDefinition("sequential-thinking", "Sequential thinking", "Structured multi-step reasoning and planning.", "npx", new[] { "-y", "@modelcontextprotocol/server-sequential-thinking" }, "embedded", true, null, now),
            new McpServerDefinition("memory-server", "Memory server", "Local graph-style memory for agents that need durable context.", "npx", new[] { "-y", "@modelcontextprotocol/server-memory" }, "embedded", true, null, now),
            new McpServerDefinition("github", "GitHub", "Repository, pull request, issue, and code search operations.", "npx", new[] { "-y", "@modelcontextprotocol/server-github" }, "embedded", false, null, now),
            new McpServerDefinition("filesystem", "Filesystem", "Read and write files inside explicitly allowed local folders.", "npx", new[] { "-y", "@modelcontextprotocol/server-filesystem", documents }, "embedded", true, null, now),
            new McpServerDefinition("brave-search", "Brave Search", "Web and local search through the Brave Search API.", "npx", new[] { "-y", "@modelcontextprotocol/server-brave-search" }, "embedded", false, null, now),
            new McpServerDefinition("slack", "Slack", "Read and post Slack messages when workspace tokens are configured.", "npx", new[] { "-y", "@modelcontextprotocol/server-slack" }, "embedded", false, null, now),
            new McpServerDefinition("postgres", "Postgres", "Inspect schemas and run database queries against configured PostgreSQL instances.", "npx", new[] { "-y", "@modelcontextprotocol/server-postgres" }, "embedded", false, null, now),
            new McpServerDefinition("google-maps", "Google Maps", "Geocoding, places, directions, and map context via Google Maps APIs.", "npx", new[] { "-y", "@modelcontextprotocol/server-google-maps" }, "embedded", false, null, now)
        };
    }

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
