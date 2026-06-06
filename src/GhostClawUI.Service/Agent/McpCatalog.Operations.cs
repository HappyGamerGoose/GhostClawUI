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
}
