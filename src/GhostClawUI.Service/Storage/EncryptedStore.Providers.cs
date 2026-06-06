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
    public IReadOnlyList<ProviderProfile> ListProviders()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM providers ORDER BY name";
            using var reader = command.ExecuteReader();
            var rows = new List<ProviderProfile>();
            while (reader.Read())
            {
                rows.Add(ReadProvider(reader));
            }

            return rows;
        }
    }

    public ProviderProfile? GetProvider(string id)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM providers WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return ReadProvider(reader);
            }

            return null;
        }
    }

    public ProviderProfile UpsertProvider(ProviderUpsertRequest request)
    {
        var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : request.Id!;
        var updatedAt = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO providers (id, name, base_url, models_json, default_model, is_enabled, updated_at)
                VALUES ($id, $name, $baseUrl, $models, $defaultModel, $isEnabled, $updatedAt)
                ON CONFLICT(id) DO UPDATE SET
                  name = excluded.name,
                  base_url = excluded.base_url,
                  models_json = excluded.models_json,
                  default_model = excluded.default_model,
                  is_enabled = excluded.is_enabled,
                  updated_at = excluded.updated_at
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", Protect(request.Name));
            command.Parameters.AddWithValue("$baseUrl", Protect(request.BaseUrl));
            command.Parameters.AddWithValue("$models", Protect(JsonSerializer.Serialize(request.Models, PipeJson.Options)));
            command.Parameters.AddWithValue("$defaultModel", ProtectNullable(request.DefaultModel) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$isEnabled", request.IsEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O"));
            command.ExecuteNonQuery();
        }

        return new ProviderProfile(id, request.Name, request.BaseUrl, request.Models, request.DefaultModel, request.IsEnabled, updatedAt);
    }

    public void RemoveProvider(string id)
    {
        Execute("DELETE FROM providers WHERE id = $id", ("$id", id));
    }

    private ProviderProfile ReadProvider(SqliteDataReader reader)
    {
        var modelsJson = Unprotect(reader.GetString(reader.GetOrdinal("models_json")));
        return new ProviderProfile(
            reader.GetString(reader.GetOrdinal("id")),
            Unprotect(reader.GetString(reader.GetOrdinal("name"))),
            Unprotect(reader.GetString(reader.GetOrdinal("base_url"))),
            JsonSerializer.Deserialize<IReadOnlyList<string>>(modelsJson, PipeJson.Options) ?? Array.Empty<string>(),
            UnprotectNullable(reader["default_model"] as string),
            reader.GetInt32(reader.GetOrdinal("is_enabled")) == 1,
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))));
    }
}
