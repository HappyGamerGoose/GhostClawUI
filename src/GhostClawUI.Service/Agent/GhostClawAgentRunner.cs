using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GhostClawUI.Service.Infrastructure;
using GhostClawUI.Shared;
using Microsoft.Extensions.Logging;

namespace GhostClawUI.Service.Agent;

internal sealed class GhostClawAgentRunner
{
    private const string OutputStart = "---GHOSTCLAW_OUTPUT_START---";
    private const string OutputEnd = "---GHOSTCLAW_OUTPUT_END---";
    private readonly AppPaths _paths;
    private readonly McpCatalog _mcpCatalog;
    private readonly ILogger<GhostClawAgentRunner> _logger;

    public GhostClawAgentRunner(AppPaths paths, McpCatalog mcpCatalog, ILogger<GhostClawAgentRunner> logger)
    {
        _paths = paths;
        _mcpCatalog = mcpCatalog;
        _logger = logger;
    }

    public async Task<GhostClawAgentResult> TryRunAsync(
        ProviderProfile provider,
        string apiKey,
        string model,
        string prompt,
        string conversationId,
        Action<AgentTraceCard> onTrace,
        CancellationToken cancellationToken,
        IReadOnlyList<ChatAttachment>? attachments = null)
    {
        var entry = Path.Combine(_paths.GhostClawRuntimeRoot, "agent-runner", "dist", "index.js");
        if (!File.Exists(entry))
        {
            return GhostClawAgentResult.Failed("GhostClaw agent runner is not present in the packaged payload.");
        }

        var node = _paths.ResolveNodeExe();
        var groupDir = Path.Combine(_paths.GhostClawRuntimeRoot, "groups", "main");
        var ipcDir = Path.Combine(_paths.GhostClawRuntimeRoot, "data", "ipc", "main");
        var configDir = Path.Combine(_paths.GhostClawRuntimeRoot, "data", "sessions", "main", ".claude");
        Directory.CreateDirectory(groupDir);
        Directory.CreateDirectory(ipcDir);
        Directory.CreateDirectory(configDir);
        _mcpCatalog.EnsureGhostClawSettings();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(12));

        var start = new ProcessStartInfo
        {
            FileName = node,
            WorkingDirectory = groupDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(entry);
        start.Environment["GHOSTCLAW_GROUP_DIR"] = groupDir;
        start.Environment["GHOSTCLAW_IPC_DIR"] = ipcDir;
        start.Environment["GHOSTCLAW_GLOBAL_DIR"] = Path.Combine(_paths.GhostClawRuntimeRoot, "groups", "global");
        start.Environment["GHOSTCLAW_GLOBAL_SKILLS_DIR"] = Path.Combine(_paths.GhostClawRuntimeRoot, "skills");
        start.Environment["CLAUDE_CONFIG_DIR"] = configDir;
        start.Environment["GHOSTCLAW_MODEL"] = model;
        start.Environment["ANTHROPIC_MODEL"] = model;
        start.Environment["GHOSTCLAW_PROVIDER_TYPE"] = IsAnthropicProvider(provider) ? "anthropic" : "openai";
        start.Environment["TZ"] = TimeZoneInfo.Local.Id;
        if (!IsDefaultAnthropicBaseUrl(provider.BaseUrl))
        {
            start.Environment["ANTHROPIC_BASE_URL"] = IsAnthropicProvider(provider)
                ? SanitizeAnthropicBaseUrl(provider.BaseUrl)
                : provider.BaseUrl.TrimEnd('/');
        }

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var localTraces = new List<AgentTraceCard>();

        var stdoutTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data == null)
            {
                stdoutTcs.TrySetResult(true);
            }
            else
            {
                lock (stdoutBuilder)
                {
                    stdoutBuilder.AppendLine(e.Data);
                }
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data == null)
            {
                stderrTcs.TrySetResult(true);
            }
            else
            {
                lock (stderrBuilder)
                {
                    stderrBuilder.AppendLine(e.Data);
                }
                ParseAndReportStderrLine(e.Data, conversationId, localTraces, onTrace);
            }
        };

        if (!process.Start())
        {
            return GhostClawAgentResult.Failed("GhostClaw agent runner could not be started.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var secrets = new JsonObject
        {
            ["ANTHROPIC_API_KEY"] = apiKey,
            ["OPENAI_API_KEY"] = apiKey,
            ["GHOSTCLAW_MODEL"] = model,
            ["ANTHROPIC_MODEL"] = model
        };
        if (!IsDefaultAnthropicBaseUrl(provider.BaseUrl))
        {
            var baseUrl = provider.BaseUrl.TrimEnd('/');
            secrets["OPENAI_BASE_URL"] = baseUrl;
            secrets["ANTHROPIC_BASE_URL"] = IsAnthropicProvider(provider)
                ? SanitizeAnthropicBaseUrl(provider.BaseUrl)
                : baseUrl;
        }

        var input = new JsonObject
        {
            ["prompt"] = prompt,
            ["groupFolder"] = "main",
            ["chatJid"] = $"ui:{conversationId}",
            ["isMain"] = true,
            ["isScheduledTask"] = true,
            ["assistantName"] = "GhostClaw",
            ["secrets"] = secrets
        };

        if (attachments != null && attachments.Count > 0)
        {
            var mediaArray = new JsonArray();
            foreach (var att in attachments)
            {
                var isImage = att.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
                var isPdf = att.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);

                if ((isImage || isPdf) && !string.IsNullOrWhiteSpace(att.Path) && File.Exists(att.Path))
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(att.Path);
                        var dataUri = $"data:{att.ContentType};base64,{Convert.ToBase64String(bytes)}";
                        mediaArray.Add(new JsonObject
                        {
                            ["dataUri"] = dataUri,
                            ["mediaType"] = att.ContentType
                        });
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to read attachment bytes."); }
                }
            }
            if (mediaArray.Count > 0)
            {
                input["mediaFiles"] = mediaArray;
            }
        }

        try
        {
            await process.StandardInput.WriteAsync(input.ToJsonString(PipeJson.Options)).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (Exception ex)
        {
            TryKill(process);
            var err = "";
            try
            {
                await Task.WhenAny(stderrTcs.Task, Task.Delay(500)).ConfigureAwait(false);
                err = stderrBuilder.ToString();
            }
            catch (Exception taskEx) { _logger.LogWarning(taskEx, "Failed while awaiting stderr timeout."); }
            var msg = $"Failed to write to agent runner input: {ex.Message}";
            if (!string.IsNullOrWhiteSpace(err))
            {
                msg += $"\nNode Error:\n{err}";
            }
            return GhostClawAgentResult.Failed(msg);
        }

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return GhostClawAgentResult.Failed("GhostClaw agent mode timed out before returning a final answer.");
        }

        try
        {
            await Task.WhenAll(stdoutTcs.Task, stderrTcs.Task).WaitAsync(TimeSpan.FromSeconds(5), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception awaitEx) { _logger.LogWarning(awaitEx, "Timeout or error waiting for output streams."); }

        var stdout = stdoutBuilder.ToString();
        var stderr = stderrBuilder.ToString();
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            _logger.LogInformation("[ghostclaw-agent] {Stderr}", TrimForLog(stderr));
        }

        var parsed = ParseOutput(stdout);

        // Transition any lingering "running" trace cards to "done" or "failed" before returning
        lock (localTraces)
        {
            var finalState = (process.ExitCode == 0 && string.IsNullOrWhiteSpace(parsed.Error)) ? "done" : "failed";
            for (int i = 0; i < localTraces.Count; i++)
            {
                if (localTraces[i].State == "running")
                {
                    var updated = localTraces[i] with { State = finalState };
                    localTraces[i] = updated;
                    onTrace(updated);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(parsed.Content))
        {
            return GhostClawAgentResult.FromContent(parsed.Content!, parsed.SessionId, parsed.Attachments, localTraces);
        }

        if (!string.IsNullOrWhiteSpace(parsed.Error))
        {
            return GhostClawAgentResult.Failed(parsed.Error!);
        }

        return process.ExitCode == 0
            ? GhostClawAgentResult.Failed("GhostClaw agent completed without a final answer.")
            : GhostClawAgentResult.Failed($"GhostClaw agent exited with code {process.ExitCode}.");
    }

    private static void ParseAndReportStderrLine(string line, string conversationId, List<AgentTraceCard> localTraces, Action<AgentTraceCard> onTrace)
    {
        var prefix = "[agent-runner] ";
        var idx = line.IndexOf(prefix);
        if (idx < 0) return;
        var msg = line.Substring(idx + prefix.Length).Trim();

        if (msg.StartsWith("Agent thinking..."))
        {
            lock (localTraces)
            {
                var last = localTraces.LastOrDefault();
                if (last != null && last.State == "running")
                {
                    localTraces.Remove(last);
                    var updated = last with { State = "done" };
                    localTraces.Add(updated);
                    onTrace(updated);
                }
                
                var thinking = new AgentTraceCard("Thinking", "Analyzing next steps...", "running");
                localTraces.Add(thinking);
                onTrace(thinking);
            }
        }
        else if (msg.StartsWith("Tool: "))
        {
            var toolPart = msg.Substring(6);
            var pipeIdx = toolPart.IndexOf('|');
            var toolName = pipeIdx >= 0 ? toolPart.Substring(0, pipeIdx).Trim() : toolPart.Trim();
            var inputStr = pipeIdx >= 0 && toolPart.Length > pipeIdx + 8 ? toolPart.Substring(pipeIdx + 8).Trim() : "";

            lock (localTraces)
            {
                var last = localTraces.LastOrDefault();
                if (last != null && last.State == "running")
                {
                    localTraces.Remove(last);
                    var updated = last with { State = "done" };
                    localTraces.Add(updated);
                    onTrace(updated);
                }

                var title = GetNiceToolName(toolName);
                var detail = GetNiceToolDetail(toolName, inputStr);
                var toolTrace = new AgentTraceCard(title, detail, "running");
                localTraces.Add(toolTrace);
                onTrace(toolTrace);
            }
        }
        else if (msg.StartsWith("ToolFinished: "))
        {
            var finishPart = msg.Substring(14);
            var pipeIdx = finishPart.IndexOf('|');
            var toolName = pipeIdx >= 0 ? finishPart.Substring(0, pipeIdx).Trim() : finishPart.Trim();
            var statusStr = pipeIdx >= 0 && finishPart.Length > pipeIdx + 9 ? finishPart.Substring(pipeIdx + 9).Trim() : "";
            
            var state = statusStr.Contains("error") ? "failed" : "done";
            var title = GetNiceToolName(toolName);

            lock (localTraces)
            {
                var last = localTraces.LastOrDefault(t => t.Title == title && t.State == "running") ?? localTraces.LastOrDefault();
                if (last != null)
                {
                    localTraces.Remove(last);
                    var updated = last with { State = state };
                    localTraces.Add(updated);
                    onTrace(updated);
                }
            }
        }
        else if (msg.StartsWith("Calling MCP tool: "))
        {
            var callPart = msg.Substring(18);
            lock (localTraces)
            {
                var last = localTraces.LastOrDefault();
                if (last != null && last.State == "running")
                {
                    localTraces.Remove(last);
                    var updated = last with { State = "done" };
                    localTraces.Add(updated);
                    onTrace(updated);
                }

                var mcpTrace = new AgentTraceCard("MCP Call", $"Calling {callPart}...", "running");
                localTraces.Add(mcpTrace);
                onTrace(mcpTrace);
            }
        }
        else if (msg.StartsWith("Thought: "))
        {
            var thought = msg.Substring(9).Trim();
            lock (localTraces)
            {
                var thinking = localTraces.LastOrDefault(t => t.Title == "Thinking");
                if (thinking != null) localTraces.Remove(thinking);

                var existing = localTraces.FirstOrDefault(t => t.Title == "Reasoning");
                if (existing != null) localTraces.Remove(existing);

                var thoughtTrace = new AgentTraceCard("Reasoning", thought, "done");
                localTraces.Add(thoughtTrace);
                onTrace(thoughtTrace);
            }
        }
        else if (msg.StartsWith("Initializing MCP server: "))
        {
            var serverName = msg.Substring(25).Trim();
            lock (localTraces)
            {
                var mcpInitTrace = new AgentTraceCard("MCP Connect", $"Initializing server: {serverName}", "running");
                localTraces.Add(mcpInitTrace);
                onTrace(mcpInitTrace);
            }
        }
        else if (msg.StartsWith("Connected to MCP server: "))
        {
            var serverName = msg.Substring(25).Trim();
            lock (localTraces)
            {
                var existing = localTraces.FirstOrDefault(t => t.Title == "MCP Connect" && t.State == "running");
                if (existing != null)
                {
                    localTraces.Remove(existing);
                    var updated = existing with { State = "done", Detail = $"Connected to {serverName}" };
                    localTraces.Add(updated);
                    onTrace(updated);
                }
            }
        }
        else if (msg.StartsWith("Failed to connect to MCP server "))
        {
            var parts = msg.Substring(32).Split(':');
            var serverName = parts[0].Trim();
            lock (localTraces)
            {
                var existing = localTraces.FirstOrDefault(t => t.Title == "MCP Connect" && t.State == "running");
                if (existing != null) localTraces.Remove(existing);
                var errTrace = new AgentTraceCard("MCP Connect", $"Failed to connect to {serverName}", "failed");
                localTraces.Add(errTrace);
                onTrace(errTrace);
            }
        }
        else if (msg.StartsWith("Plan: "))
        {
            var planJson = msg.Substring(6).Trim();
            lock (localTraces)
            {
                var existing = localTraces.FirstOrDefault(t => t.Title == "Active Plan");
                if (existing != null)
                {
                    localTraces.Remove(existing);
                }
                var planTrace = new AgentTraceCard("Active Plan", planJson, "done");
                localTraces.Add(planTrace);
                onTrace(planTrace);
            }
        }
    }

    private static string GetNiceToolName(string name)
    {
        if (name == "read_file") return "Data Analysis (Read File)";
        if (name == "write_to_file") return "Data Analysis (Write File)";
        if (name == "execute_command") return "Terminal Agent (Bash)";
        if (name == "attempt_completion") return "Planner Agent (Complete)";
        if (name.StartsWith("mcp__"))
        {
            var parts = name.Split("__");
            if (parts.Length >= 3)
            {
                var serverName = parts[1].ToLowerInvariant();
                if (serverName.Contains("search") || serverName.Contains("browse") || serverName.Contains("web") || serverName.Contains("fetch") || serverName.Contains("chrome"))
                {
                    return $"Web Agent ({parts[1]})";
                }
                return $"Specialist Agent ({parts[1]})";
            }
        }
        return name;
    }

    private static string GetNiceToolDetail(string name, string inputJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            var root = doc.RootElement;
            if (name == "read_file" && root.TryGetProperty("path", out var p))
                return $"Reading file: {p.GetString()}";
            if (name == "write_to_file" && root.TryGetProperty("path", out var wp))
                return $"Writing file: {wp.GetString()}";
            if (name == "execute_command" && root.TryGetProperty("command", out var c))
                return $"Executing: {c.GetString()}";
            if (name == "attempt_completion" && root.TryGetProperty("result", out var r))
                return $"Attempting completion with result: {r.GetString()}";
        }
        catch {}
        return string.IsNullOrWhiteSpace(inputJson) ? $"Running tool {name}" : $"Input: {inputJson}";
    }

    private static (string? Content, string? Error, string? SessionId, IReadOnlyList<ChatAttachment>? Attachments) ParseOutput(string stdout)
    {
        string? lastContent = null;
        string? lastError = null;
        string? sessionId = null;
        List<ChatAttachment>? attachments = null;
        var cursor = 0;
        while (true)
        {
            var start = stdout.IndexOf(OutputStart, cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var jsonStart = start + OutputStart.Length;
            var end = stdout.IndexOf(OutputEnd, jsonStart, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            var json = stdout[jsonStart..end].Trim();
            cursor = end + OutputEnd.Length;
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.TryGetProperty("newSessionId", out var sid) && sid.ValueKind == JsonValueKind.String)
                {
                    sessionId = sid.GetString();
                }

                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                {
                    lastError = error.GetString();
                }

                if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
                {
                    var value = result.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        lastContent = value;
                    }
                }

                if (root.TryGetProperty("attachments", out var atts) && atts.ValueKind == JsonValueKind.Array)
                {
                    attachments ??= new List<ChatAttachment>();
                    foreach (var item in atts.EnumerateArray())
                    {
                        try
                        {
                            var name = item.GetProperty("name").GetString() ?? "";
                            var path = item.GetProperty("path").GetString() ?? "";
                            var contentType = item.GetProperty("contentType").GetString() ?? "";
                            var sizeBytes = item.GetProperty("sizeBytes").GetInt64();
                            var textPreview = item.TryGetProperty("textPreview", out var tp) ? tp.GetString() : null;
                            var dataUri = item.TryGetProperty("dataUri", out var du) ? du.GetString() : null;
                            attachments.Add(new ChatAttachment(name, path, contentType, sizeBytes, textPreview, dataUri));
                        }
                        catch
                        {
                            // ignore malformed attachments
                        }
                    }
                }
            }
            catch
            {
                lastError = "GhostClaw agent returned malformed output.";
            }
        }

        return (lastContent, lastError, sessionId, attachments);
    }

    private static bool IsDefaultAnthropicBaseUrl(string baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl) ||
        baseUrl.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The Anthropic SDK automatically appends /v1/messages to the base URL.
    /// If the provider URL already ends with /v1, strip it so the SDK doesn't
    /// double up (e.g. /api/v1/v1/messages → 404).
    /// </summary>
    private static string SanitizeAnthropicBaseUrl(string baseUrl)
    {
        var url = baseUrl.TrimEnd('/');
        if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^3];
        }
        return url;
    }

    private static void TryKill(Process process)
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
            // Best effort after a timeout.
        }
    }

    private static bool IsAnthropicProvider(ProviderProfile provider)
    {
        var url = provider.BaseUrl ?? "";
        var name = provider.Name ?? "";
        var id = provider.Id ?? "";
        return string.IsNullOrWhiteSpace(url) ||
               url.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("anthropic", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("anthropic", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("anthropic", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("claude", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimForLog(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 1000 ? value : value[..1000] + "...";
    }
}

internal sealed record GhostClawAgentResult(bool Success, string? Content, string? Error, string? SessionId, IReadOnlyList<ChatAttachment>? Attachments = null, IReadOnlyList<AgentTraceCard>? Traces = null)
{
    public static GhostClawAgentResult FromContent(string content, string? sessionId, IReadOnlyList<ChatAttachment>? attachments = null, IReadOnlyList<AgentTraceCard>? traces = null) => new(true, content, null, sessionId, attachments, traces);

    public static GhostClawAgentResult Failed(string error) => new(false, null, error, null, null, null);
}
