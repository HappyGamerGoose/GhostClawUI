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
    public void QueueTask(string command, JsonNode? payload) =>
        Execute(
            "INSERT INTO queued_tasks (id, command, payload_json, status, created_at, updated_at) VALUES ($id, $command, $payload, 'queued', $now, $now)",
            ("$id", Guid.NewGuid().ToString("N")),
            ("$command", command),
            ("$payload", ProtectNullable(payload?.ToJsonString(PipeJson.Options))),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));

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
}
