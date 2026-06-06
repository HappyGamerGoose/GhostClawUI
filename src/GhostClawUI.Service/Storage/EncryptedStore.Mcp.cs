using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GhostClawUI.Service.Infrastructure;
using GhostClawUI.Shared;
using Microsoft.Data.Sqlite;

namespace GhostClawUI.Service.Storage;

internal sealed partial class EncryptedStore
{
    public IReadOnlyList<McpServerDefinition> ListMcpServers()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM mcp_servers ORDER BY installed DESC, name";
            using var reader = command.ExecuteReader();
            var rows = new List<McpServerDefinition>();
            while (reader.Read())
            {
                rows.Add(ReadMcp(reader));
            }

            return rows;
        }
    }

    public void UpsertMcp(McpServerDefinition server)
    {
        Execute(
            """
            INSERT INTO mcp_servers (id, name, description, command, args_json, registry_url, installed, version, updated_at, logo_url)
            VALUES ($id, $name, $description, $command, $args, $registry, $installed, $version, $updated, $logoUrl)
            ON CONFLICT(id) DO UPDATE SET
              name = excluded.name,
              description = excluded.description,
              command = excluded.command,
              args_json = excluded.args_json,
              registry_url = excluded.registry_url,
              installed = excluded.installed,
              version = excluded.version,
              updated_at = excluded.updated_at,
              logo_url = excluded.logo_url
            """,
            ("$id", server.Id),
            ("$name", Protect(server.Name)),
            ("$description", Protect(server.Description)),
            ("$command", Protect(server.Command)),
            ("$args", Protect(JsonSerializer.Serialize(server.Args, PipeJson.Options))),
            ("$registry", Protect(server.RegistryUrl)),
            ("$installed", server.Installed ? 1 : 0),
            ("$version", ProtectNullable(server.Version)),
            ("$updated", server.UpdatedAt.ToString("O")),
            ("$logoUrl", ProtectNullable(server.IconUrl) ?? (object)DBNull.Value));
    }

    private McpServerDefinition ReadMcp(SqliteDataReader reader)
    {
        var argsJson = Unprotect(reader.GetString(reader.GetOrdinal("args_json")));
        string? iconUrl = null;
        try
        {
            var logoCol = reader.GetOrdinal("logo_url");
            if (!reader.IsDBNull(logoCol))
            {
                iconUrl = UnprotectNullable(reader.GetString(logoCol));
            }
        }
        catch
        {
            // Fallback if column doesn't exist
        }

        return new McpServerDefinition(
            reader.GetString(reader.GetOrdinal("id")),
            Unprotect(reader.GetString(reader.GetOrdinal("name"))),
            Unprotect(reader.GetString(reader.GetOrdinal("description"))),
            Unprotect(reader.GetString(reader.GetOrdinal("command"))),
            JsonSerializer.Deserialize<IReadOnlyList<string>>(argsJson, PipeJson.Options) ?? Array.Empty<string>(),
            Unprotect(reader.GetString(reader.GetOrdinal("registry_url"))),
            reader.GetInt32(reader.GetOrdinal("installed")) == 1,
            UnprotectNullable(reader["version"] as string),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
            iconUrl);
    }
}
