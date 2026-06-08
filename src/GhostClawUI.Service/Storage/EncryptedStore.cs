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
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GhostClawUI.SQLite.v1");
    private readonly object _gate = new();
    private readonly string _connectionString;
    private readonly AppPaths _paths;

    public EncryptedStore(AppPaths paths)
    {
        _paths = paths;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        Initialize();
    }

    private SqliteConnection OpenDaemonDb()
    {
        var dbPath = Path.Combine(_paths.GhostClawRuntimeRoot, "store", "messages.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS providers (
                  id TEXT PRIMARY KEY,
                  name TEXT NOT NULL,
                  base_url TEXT NOT NULL,
                  models_json TEXT NOT NULL,
                  default_model TEXT,
                  is_enabled INTEGER NOT NULL,
                  updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS conversations (
                  id TEXT PRIMARY KEY,
                  title TEXT NOT NULL,
                  pinned INTEGER NOT NULL DEFAULT 0,
                  created_at TEXT NOT NULL,
                  updated_at TEXT NOT NULL,
                  context_cleared_at TEXT
                );
                CREATE TABLE IF NOT EXISTS messages (
                  id TEXT PRIMARY KEY,
                  conversation_id TEXT NOT NULL,
                  role TEXT NOT NULL,
                  content TEXT NOT NULL,
                  provider_id TEXT,
                  model TEXT,
                  kind TEXT NOT NULL,
                  created_at TEXT NOT NULL,
                  metadata_json TEXT,
                  FOREIGN KEY(conversation_id) REFERENCES conversations(id)
                );
                CREATE TABLE IF NOT EXISTS memory (
                  id TEXT PRIMARY KEY,
                  summary TEXT NOT NULL,
                  content TEXT NOT NULL,
                  source TEXT NOT NULL,
                  created_at TEXT NOT NULL,
                  updated_at TEXT NOT NULL,
                  deleted INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_messages_conversation_id ON messages (conversation_id);
                CREATE INDEX IF NOT EXISTS idx_memory_deleted ON memory (deleted);
                CREATE TABLE IF NOT EXISTS mcp_servers (
                  id TEXT PRIMARY KEY,
                  name TEXT NOT NULL,
                  description TEXT NOT NULL,
                  command TEXT NOT NULL,
                  args_json TEXT NOT NULL,
                  registry_url TEXT NOT NULL,
                  installed INTEGER NOT NULL DEFAULT 0,
                  version TEXT,
                  updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS settings (
                  key TEXT PRIMARY KEY,
                  value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS queued_tasks (
                  id TEXT PRIMARY KEY,
                  command TEXT NOT NULL,
                  payload_json TEXT,
                  status TEXT NOT NULL,
                  created_at TEXT NOT NULL,
                  updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS backups (
                  id TEXT PRIMARY KEY,
                  path TEXT NOT NULL,
                  backup_path TEXT NOT NULL,
                  created_at TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();

            using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.CommandText = "PRAGMA table_info(mcp_servers)";
                using var checkReader = checkCmd.ExecuteReader();
                var hasLogo = false;
                while (checkReader.Read())
                {
                    if (checkReader.GetString(checkReader.GetOrdinal("name")).Equals("logo_url", StringComparison.OrdinalIgnoreCase))
                    {
                        hasLogo = true;
                        break;
                    }
                }
                if (!hasLogo)
                {
                    using var alterCmd = connection.CreateCommand();
                    alterCmd.CommandText = "ALTER TABLE mcp_servers ADD COLUMN logo_url TEXT";
                    alterCmd.ExecuteNonQuery();
                }
            }

            // Purge non-Smithery/non-embedded/non-manual servers from the database on startup
            try
            {
                var toDelete = new List<string>();
                using (var listCmd = connection.CreateCommand())
                {
                    listCmd.CommandText = "SELECT id, registry_url FROM mcp_servers";
                    using var reader = listCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var id = reader.GetString(0);
                        var encryptedRegistry = reader.GetString(1);
                        var registryUrl = Unprotect(encryptedRegistry);
                        if (!string.Equals(registryUrl, "embedded", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(registryUrl, "manual", StringComparison.OrdinalIgnoreCase) &&
                            !registryUrl.Contains("smithery.ai", StringComparison.OrdinalIgnoreCase))
                        {
                            toDelete.Add(id);
                        }
                    }
                }

                foreach (var id in toDelete)
                {
                    using var deleteCmd = connection.CreateCommand();
                    deleteCmd.CommandText = "DELETE FROM mcp_servers WHERE id = $id";
                    deleteCmd.Parameters.AddWithValue("$id", id);
                    deleteCmd.ExecuteNonQuery();
                }
            }
            catch
            {
                // Protect against startup lockups or exceptions
            }
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            command.ExecuteNonQuery();
        }
    }

    private static string Protect(string value) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));

    private static string? ProtectNullable(string? value) => string.IsNullOrEmpty(value) ? null : Protect(value);

    private static string Unprotect(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return value;
        }
    }

    private static string? UnprotectNullable(string? value) => string.IsNullOrEmpty(value) ? null : Unprotect(value);
}
