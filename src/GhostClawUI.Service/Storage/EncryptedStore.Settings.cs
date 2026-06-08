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
    public AppSettings GetSettings()
    {
        var json = GetSetting("app-settings");
        if (!string.IsNullOrWhiteSpace(json))
        {
            var parsed = JsonSerializer.Deserialize<AppSettings>(json, PipeJson.Options);
            if (parsed is not null)
            {
                return parsed with { RegistryUrls = MergeRegistryUrls(parsed.RegistryUrls) };
            }
        }

        return DefaultSettings();
    }

    public void SaveSettings(AppSettings settings) =>
        SetSetting("app-settings", JsonSerializer.Serialize(settings with { RegistryUrls = MergeRegistryUrls(settings.RegistryUrls) }, PipeJson.Options));

    public TelegramSettings GetTelegramSettings()
    {
        var json = GetSetting("telegram-settings");
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<TelegramSettings>(json, PipeJson.Options);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse telegram settings: {ex}");
            }
        }

        return new TelegramSettings("", "", false);
    }

    public void SaveTelegramSettings(TelegramSettings settings) =>
        SetSetting("telegram-settings", JsonSerializer.Serialize(settings, PipeJson.Options));

    public WhatsAppSettings GetWhatsAppSettings()
    {
        var json = GetSetting("whatsapp-settings");
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<WhatsAppSettings>(json, PipeJson.Options);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse whatsapp settings: {ex}");
            }
        }

        return new WhatsAppSettings("", "", "", "5000", false);
    }

    public void SaveWhatsAppSettings(WhatsAppSettings settings) =>
        SetSetting("whatsapp-settings", JsonSerializer.Serialize(settings, PipeJson.Options));

    public bool ProbeWritable()
    {
        var key = "health-probe";
        var value = DateTimeOffset.UtcNow.ToString("O");
        SetSetting(key, value);
        return GetSetting(key) == value;
    }

    public ExportResult ExportAllData()
    {
        var payload = new
        {
            providers = ListProviders(),
            conversations = ListConversations().Select(item => GetConversation(item.Id)),
            memory = ListMemory(),
            tools = ListMcpServers(),
            settings = GetSettings(),
            exportedAt = DateTimeOffset.UtcNow
        };
        return new ExportResult("ghostclawui-export.json", JsonSerializer.Serialize(payload, PipeJson.Options));
    }

    public void PurgeAllData()
    {
        Execute("DELETE FROM messages");
        Execute("DELETE FROM conversations");
        Execute("DELETE FROM providers");
        Execute("DELETE FROM memory");
        Execute("DELETE FROM mcp_servers");
        Execute("DELETE FROM settings");
        Execute("DELETE FROM queued_tasks");
    }

    public void RecordBackup(string path, string backupPath) =>
        Execute(
            "INSERT INTO backups (id, path, backup_path, created_at) VALUES ($id, $path, $backup, $now)",
            ("$id", Guid.NewGuid().ToString("N")),
            ("$path", Protect(path)),
            ("$backup", Protect(backupPath)),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));

    public (string Path, string BackupPath)? GetLastBackup()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path, backup_path FROM backups ORDER BY created_at DESC LIMIT 1";
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return (Unprotect(reader.GetString(0)), Unprotect(reader.GetString(1)));
        }
    }

    public string? GetProviderKey(string providerId) => GetSetting($"provider-key-{providerId}");

    public void SaveProviderKey(string providerId, string apiKey) => SetSetting($"provider-key-{providerId}", apiKey);

    public void DeleteProviderKey(string providerId) => Execute("DELETE FROM settings WHERE key = $key", ("$key", $"provider-key-{providerId}"));

    private string? GetSetting(string key)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            var value = command.ExecuteScalar() as string;
            return value is null ? null : Unprotect(value);
        }
    }

    private void SetSetting(string key, string value) =>
        Execute("INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$key", key), ("$value", Protect(value)));

    private static AppSettings DefaultSettings() =>
        new(
            new AppearanceSettings("System", "#2563EB", "Segoe UI Variable", 15, 1.35, "Comfortable", "Split", true),
            "Normal",
            DefaultRegistryUrls(),
            true,
            false,
            true,
            null,
            null,
            null,
            null,
            false);

    private static IReadOnlyList<string> DefaultRegistryUrls() =>
        new[]
        {
            "https://registry.smithery.ai/servers"
        };

    private static IReadOnlyList<string> MergeRegistryUrls(IReadOnlyList<string> current) =>
        current.Concat(DefaultRegistryUrls())
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Where(IsTrustedRegistryUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsTrustedRegistryUrl(string url) =>
        url.Contains("smithery.ai", StringComparison.OrdinalIgnoreCase);
}
