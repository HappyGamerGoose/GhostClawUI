using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GhostClawUI.Service.Infrastructure;
using GhostClawUI.Shared;
using Microsoft.Data.Sqlite;

namespace GhostClawUI.Service.Storage;

internal sealed class EncryptedStore
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

    public IReadOnlyList<ConversationSummary> ListConversations(string? query = null)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.*, COUNT(m.id) AS message_count
                FROM conversations c
                LEFT JOIN messages m ON m.conversation_id = c.id
                GROUP BY c.id
                ORDER BY c.pinned DESC, c.updated_at DESC
                """;
            using var reader = command.ExecuteReader();
            var rows = new List<ConversationSummary>();
            while (reader.Read())
            {
                var title = Unprotect(reader.GetString(reader.GetOrdinal("title")));
                var messageCount = reader.GetInt32(reader.GetOrdinal("message_count"));
                if (messageCount == 0 && title.Equals("New conversation", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(query) && !title.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                rows.Add(new ConversationSummary(
                    reader.GetString(reader.GetOrdinal("id")),
                    title,
                    reader.GetInt32(reader.GetOrdinal("pinned")) == 1,
                    DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at")))));
            }

            return rows;
        }
    }

    public ConversationDetail GetOrCreateConversation(string? id = null)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                var existing = GetConversation(id);
                if (existing is not null)
                {
                    return existing;
                }
            }

            var created = DateTimeOffset.UtcNow;
            var newId = Guid.NewGuid().ToString("N");
            Execute(
                "INSERT INTO conversations (id, title, pinned, created_at, updated_at, context_cleared_at) VALUES ($id, $title, 0, $created, $created, NULL)",
                ("$id", newId),
                ("$title", Protect("New conversation")),
                ("$created", created.ToString("O")));
            return GetConversation(newId)!;
        }
    }

    public ConversationDetail? GetConversation(string id)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var summaryCommand = connection.CreateCommand();
            summaryCommand.CommandText = "SELECT * FROM conversations WHERE id = $id";
            summaryCommand.Parameters.AddWithValue("$id", id);
            using var reader = summaryCommand.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var summary = new ConversationSummary(
                id,
                Unprotect(reader.GetString(reader.GetOrdinal("title"))),
                reader.GetInt32(reader.GetOrdinal("pinned")) == 1,
                DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))));

            var messages = new List<ChatMessage>();
            using var messageCommand = connection.CreateCommand();
            messageCommand.CommandText = "SELECT * FROM messages WHERE conversation_id = $id ORDER BY created_at";
            messageCommand.Parameters.AddWithValue("$id", id);
            using var messageReader = messageCommand.ExecuteReader();
            while (messageReader.Read())
            {
                messages.Add(ReadMessage(messageReader));
            }

            return new ConversationDetail(summary, messages);
        }
    }

    public ChatMessage AddMessage(string conversationId, string role, string content, string? providerId, string? model, string kind, JsonNode? metadata = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var created = DateTimeOffset.UtcNow;
        var message = new ChatMessage(id, conversationId, role, content, providerId, model, kind, created, metadata);
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO messages (id, conversation_id, role, content, provider_id, model, kind, created_at, metadata_json)
                VALUES ($id, $conversationId, $role, $content, $providerId, $model, $kind, $createdAt, $metadata)
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$conversationId", conversationId);
            command.Parameters.AddWithValue("$role", role);
            command.Parameters.AddWithValue("$content", Protect(content));
            command.Parameters.AddWithValue("$providerId", ProtectNullable(providerId) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$model", ProtectNullable(model) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$createdAt", created.ToString("O"));
            command.Parameters.AddWithValue("$metadata", ProtectNullable(metadata?.ToJsonString(PipeJson.Options)) ?? (object)DBNull.Value);
            command.ExecuteNonQuery();

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE conversations
                SET updated_at = $updatedAt,
                    title = CASE
                        WHEN NOT EXISTS (
                            SELECT 1 FROM messages
                            WHERE conversation_id = $conversationId
                              AND id <> $id
                              AND role = 'user'
                        ) THEN $title
                        ELSE title
                    END
                WHERE id = $conversationId
                """;
            update.Parameters.AddWithValue("$updatedAt", created.ToString("O"));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$title", Protect(BuildConversationTitle(content)));
            update.Parameters.AddWithValue("$conversationId", conversationId);
            update.ExecuteNonQuery();
            transaction.Commit();
        }

        return message;
    }

    public void RenameConversation(string id, string title) =>
        Execute("UPDATE conversations SET title = $title, updated_at = $updatedAt WHERE id = $id", ("$title", Protect(title)), ("$updatedAt", DateTimeOffset.UtcNow.ToString("O")), ("$id", id));

    public void PinConversation(string id, bool pinned) =>
        Execute("UPDATE conversations SET pinned = $pinned, updated_at = $updatedAt WHERE id = $id", ("$pinned", pinned ? 1 : 0), ("$updatedAt", DateTimeOffset.UtcNow.ToString("O")), ("$id", id));

    public void DeleteConversation(string id)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM messages WHERE conversation_id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM conversations WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public void UpdateMessageContent(string id, string content)
    {
        Execute("UPDATE messages SET content = $content WHERE id = $id", ("$content", Protect(content)), ("$id", id));
    }

    public void DeleteMessagesAfter(string conversationId, string messageId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();

            string? createdAt = null;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT created_at FROM messages WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", messageId);
                createdAt = cmd.ExecuteScalar() as string;
            }

            if (createdAt != null)
            {
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = "DELETE FROM messages WHERE conversation_id = $conversationId AND created_at >= $createdAt";
                    cmd.Parameters.AddWithValue("$conversationId", conversationId);
                    cmd.Parameters.AddWithValue("$createdAt", createdAt);
                    cmd.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
    }

    public void ClearContext(string conversationId)
    {
        Execute("UPDATE conversations SET context_cleared_at = $ts WHERE id = $id", ("$ts", DateTimeOffset.UtcNow.ToString("O")), ("$id", conversationId));
        AddMessage(conversationId, "system", "Short-term context cleared. History is kept.", null, null, "status");
    }

    public IReadOnlyList<MemoryFact> ListMemory()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM memory WHERE deleted = 0 ORDER BY updated_at DESC";
            using var reader = command.ExecuteReader();
            var rows = new List<MemoryFact>();
            while (reader.Read())
            {
                rows.Add(ReadMemory(reader));
            }

            return rows;
        }
    }

    public IReadOnlyList<MemoryFact> SearchMemory(string text, int limit = 5)
    {
        var queryTerms = Tokenize(text).ToList();
        if (queryTerms.Count == 0)
        {
            return Array.Empty<MemoryFact>();
        }

        var queryVector = BuildSparseVector(queryTerms, text);

        return ListMemory()
            .Select(fact => new
            {
                Fact = fact,
                Score = ScoreMemoryFact(fact, queryTerms, queryVector)
            })
            .Where(item => item.Score > 0.08)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Fact.UpdatedAt)
            .Take(limit)
            .Select(item => item.Fact)
            .ToList();
    }

    private static double ScoreMemoryFact(MemoryFact fact, IReadOnlyList<string> queryTerms, IReadOnlyDictionary<string, double> queryVector)
    {
        var haystack = $"{fact.Summary} {fact.Content}";
        var factTerms = Tokenize(haystack).ToList();
        if (factTerms.Count == 0)
        {
            return 0;
        }

        var factTermSet = factTerms.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overlap = queryTerms.Count(term => factTermSet.Contains(term)) / (double)Math.Max(queryTerms.Count, 1);
        var phrase = queryTerms.Where(term => term.Length > 4).Count(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase)) / (double)Math.Max(queryTerms.Count, 1);
        var cosine = Cosine(queryVector, BuildSparseVector(factTerms, haystack));
        var ageDays = Math.Max(0, (DateTimeOffset.UtcNow - fact.UpdatedAt).TotalDays);
        var recency = 1d / (1d + ageDays / 30d);
        return overlap * 0.45 + phrase * 0.2 + cosine * 0.3 + recency * 0.05;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        return builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length > 2);
    }

    private static IReadOnlyDictionary<string, double> BuildSparseVector(IReadOnlyList<string> terms, string original)
    {
        var vector = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
        {
            vector[term] = vector.GetValueOrDefault(term) + 1;
            if (term.Length >= 5)
            {
                for (var i = 0; i <= term.Length - 3; i++)
                {
                    var gram = "#" + term.Substring(i, 3);
                    vector[gram] = vector.GetValueOrDefault(gram) + 0.35;
                }
            }
        }

        foreach (var phrase in original.ReplaceLineEndings(" ").Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var compact = phrase.Trim().ToLowerInvariant();
            if (compact.Length is > 8 and < 90)
            {
                vector["phrase:" + compact] = vector.GetValueOrDefault("phrase:" + compact) + 0.25;
            }
        }

        return vector;
    }

    private static double Cosine(IReadOnlyDictionary<string, double> left, IReadOnlyDictionary<string, double> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        var dot = 0d;
        foreach (var (key, value) in left)
        {
            if (right.TryGetValue(key, out var other))
            {
                dot += value * other;
            }
        }

        var leftNorm = Math.Sqrt(left.Values.Sum(value => value * value));
        var rightNorm = Math.Sqrt(right.Values.Sum(value => value * value));
        return leftNorm == 0 || rightNorm == 0 ? 0 : dot / (leftNorm * rightNorm);
    }

    public MemoryFact UpsertMemory(MemoryUpdateRequest request)
    {
        var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : request.Id!;
        var now = DateTimeOffset.UtcNow;
        Execute(
            """
            INSERT INTO memory (id, summary, content, source, created_at, updated_at, deleted)
            VALUES ($id, $summary, $content, $source, $now, $now, 0)
            ON CONFLICT(id) DO UPDATE SET
              summary = excluded.summary,
              content = excluded.content,
              source = excluded.source,
              updated_at = excluded.updated_at,
              deleted = 0
            """,
            ("$id", id),
            ("$summary", Protect(request.Summary)),
            ("$content", Protect(request.Content)),
            ("$source", Protect(request.Source)),
            ("$now", now.ToString("O")));

        return new MemoryFact(id, request.Summary, request.Content, request.Source, now);
    }

    public void DeleteMemory(string id) =>
        Execute("UPDATE memory SET deleted = 1, updated_at = $now WHERE id = $id", ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$id", id));

    public void PurgeMemory() => Execute("DELETE FROM memory");

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

    public ExportResult ExportConversation(string id, string format)
    {
        var conversation = GetConversation(id) ?? GetOrCreateConversation(id);
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return new ExportResult($"{SanitizeFileName(conversation.Summary.Title)}.json", JsonSerializer.Serialize(conversation, PipeJson.Options));
        }

        var markdown = new StringBuilder();
        markdown.AppendLine($"# {conversation.Summary.Title}");
        markdown.AppendLine();
        foreach (var message in conversation.Messages)
        {
            markdown.AppendLine($"## {message.Role} - {message.CreatedAt:yyyy-MM-dd HH:mm}");
            markdown.AppendLine();
            markdown.AppendLine(message.Content);
            markdown.AppendLine();
        }

        return new ExportResult($"{SanitizeFileName(conversation.Summary.Title)}.md", markdown.ToString());
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

    public void QueueTask(string command, JsonNode? payload) =>
        Execute(
            "INSERT INTO queued_tasks (id, command, payload_json, status, created_at, updated_at) VALUES ($id, $command, $payload, 'queued', $now, $now)",
            ("$id", Guid.NewGuid().ToString("N")),
            ("$command", command),
            ("$payload", ProtectNullable(payload?.ToJsonString(PipeJson.Options))),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));

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

    public IReadOnlyList<ScheduledTask> ListScheduledTasks()
    {
        lock (_gate)
        {
            var dbPath = Path.Combine(_paths.GhostClawRuntimeRoot, "store", "messages.db");
            if (!File.Exists(dbPath)) return Array.Empty<ScheduledTask>();

            using var connection = OpenDaemonDb();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, group_folder, chat_jid, prompt, pre_check, schedule_type, schedule_value, context_mode, next_run, last_run, last_result, status, created_at FROM scheduled_tasks ORDER BY created_at DESC";
            using var reader = command.ExecuteReader();
            var list = new List<ScheduledTask>();
            while (reader.Read())
            {
                list.Add(new ScheduledTask(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? "isolated" : reader.GetString(7),
                    reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
                    reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.GetString(11),
                    DateTimeOffset.Parse(reader.GetString(12))
                ));
            }
            return list;
        }
    }

    public void UpsertScheduledTask(ScheduledTask task)
    {
        lock (_gate)
        {
            using var connection = OpenDaemonDb();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO scheduled_tasks (id, group_folder, chat_jid, prompt, pre_check, schedule_type, schedule_value, context_mode, next_run, last_run, last_result, status, created_at)
                VALUES ($id, $group, $chatJid, $prompt, $preCheck, $type, $val, $mode, $next, $last, $result, $status, $created)
                ON CONFLICT(id) DO UPDATE SET
                  prompt = excluded.prompt,
                  pre_check = excluded.pre_check,
                  schedule_type = excluded.schedule_type,
                  schedule_value = excluded.schedule_value,
                  context_mode = excluded.context_mode,
                  next_run = excluded.next_run,
                  status = excluded.status
                """;
            command.Parameters.AddWithValue("$id", task.Id);
            command.Parameters.AddWithValue("$group", task.GroupFolder);
            command.Parameters.AddWithValue("$chatJid", task.ChatJid);
            command.Parameters.AddWithValue("$prompt", task.Prompt);
            command.Parameters.AddWithValue("$preCheck", (object?)task.PreCheck ?? DBNull.Value);
            command.Parameters.AddWithValue("$type", task.ScheduleType);
            command.Parameters.AddWithValue("$val", task.ScheduleValue);
            command.Parameters.AddWithValue("$mode", task.ContextMode);
            command.Parameters.AddWithValue("$next", task.NextRun?.ToString("O") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$last", task.LastRun?.ToString("O") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$result", (object?)task.LastResult ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", task.Status);
            command.Parameters.AddWithValue("$created", task.CreatedAt.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public void DeleteScheduledTask(string id)
    {
        lock (_gate)
        {
            using var connection = OpenDaemonDb();
            using var tx = connection.BeginTransaction();
            try
            {
                using (var cmd1 = connection.CreateCommand())
                {
                    cmd1.Transaction = tx;
                    cmd1.CommandText = "DELETE FROM task_run_logs WHERE task_id = $id";
                    cmd1.Parameters.AddWithValue("$id", id);
                    cmd1.ExecuteNonQuery();
                }
                using (var cmd2 = connection.CreateCommand())
                {
                    cmd2.Transaction = tx;
                    cmd2.CommandText = "DELETE FROM scheduled_tasks WHERE id = $id";
                    cmd2.Parameters.AddWithValue("$id", id);
                    cmd2.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    public IReadOnlyList<TaskRunLog> ListTaskRunLogs(string taskId)
    {
        lock (_gate)
        {
            var dbPath = Path.Combine(_paths.GhostClawRuntimeRoot, "store", "messages.db");
            if (!File.Exists(dbPath)) return Array.Empty<TaskRunLog>();

            using var connection = OpenDaemonDb();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, task_id, run_at, duration_ms, status, result, error FROM task_run_logs WHERE task_id = $taskId ORDER BY run_at DESC LIMIT 50";
            command.Parameters.AddWithValue("$taskId", taskId);
            using var reader = command.ExecuteReader();
            var list = new List<TaskRunLog>();
            while (reader.Read())
            {
                list.Add(new TaskRunLog(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)
                ));
            }
            return list;
        }
    }

    public void TriggerScheduledTaskNow(string id)
    {
        lock (_gate)
        {
            using var connection = OpenDaemonDb();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE scheduled_tasks SET next_run = $now, status = 'active' WHERE id = $id";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
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

    private ChatMessage ReadMessage(SqliteDataReader reader)
    {
        var metadataJson = UnprotectNullable(reader["metadata_json"] as string);
        return new ChatMessage(
            reader.GetString(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("conversation_id")),
            reader.GetString(reader.GetOrdinal("role")),
            Unprotect(reader.GetString(reader.GetOrdinal("content"))),
            UnprotectNullable(reader["provider_id"] as string),
            UnprotectNullable(reader["model"] as string),
            reader.GetString(reader.GetOrdinal("kind")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            string.IsNullOrWhiteSpace(metadataJson) ? null : JsonNode.Parse(metadataJson));
    }

    private MemoryFact ReadMemory(SqliteDataReader reader) =>
        new(
            reader.GetString(reader.GetOrdinal("id")),
            Unprotect(reader.GetString(reader.GetOrdinal("summary"))),
            Unprotect(reader.GetString(reader.GetOrdinal("content"))),
            Unprotect(reader.GetString(reader.GetOrdinal("source"))),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))));

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

    private static string Protect(string value) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.LocalMachine));

    private static string? ProtectNullable(string? value) => string.IsNullOrEmpty(value) ? null : Protect(value);

    private static string Unprotect(string value) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.LocalMachine));

    private static string? UnprotectNullable(string? value) => string.IsNullOrEmpty(value) ? null : Unprotect(value);

    private static string BuildConversationTitle(string content)
    {
        var words = content.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries)
            .Take(2)
            .ToList();
        return words.Count > 0 ? string.Join(' ', words) : "New conversation";
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '-');
        }

        return string.IsNullOrWhiteSpace(value) ? "conversation" : value;
    }

    private static AppSettings DefaultSettings() =>
        new(
            new AppearanceSettings("System", "#2563EB", "Segoe UI Variable", 15, 1.35, "Comfortable", "Split", true),
            "Normal",
            DefaultRegistryUrls(),
            true,
            false,
            true,
            null,
            null);

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
