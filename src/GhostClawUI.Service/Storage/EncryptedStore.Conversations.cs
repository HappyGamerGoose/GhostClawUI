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
}
