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
}
