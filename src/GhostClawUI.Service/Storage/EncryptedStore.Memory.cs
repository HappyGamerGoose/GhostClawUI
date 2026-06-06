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

    private MemoryFact ReadMemory(SqliteDataReader reader) =>
        new(
            reader.GetString(reader.GetOrdinal("id")),
            Unprotect(reader.GetString(reader.GetOrdinal("summary"))),
            Unprotect(reader.GetString(reader.GetOrdinal("content"))),
            Unprotect(reader.GetString(reader.GetOrdinal("source"))),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))));
}
