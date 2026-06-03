using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

class Program
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GhostClawUI.SQLite.v1");

    static void Main()
    {
        string dbPath = @"C:\Users\akshi\AppData\Local\GhostClawUI\ghostclawui.db";
        Console.WriteLine($"Reading database at {dbPath}...");
        if (!File.Exists(dbPath))
        {
            Console.WriteLine("Database file not found!");
            return;
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, base_url, default_model, is_enabled FROM providers";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string id = reader.GetString(0);
                string encName = reader.GetString(1);
                string encUrl = reader.GetString(2);
                string? encModel = reader.IsDBNull(3) ? null : reader.GetString(3);
                int isEnabled = reader.GetInt32(4);

                string name = Decrypt(encName);
                string url = Decrypt(encUrl);
                string? model = encModel != null ? Decrypt(encModel) : null;

                Console.WriteLine($"Provider: ID={id}");
                Console.WriteLine($"  Name: {name}");
                Console.WriteLine($"  URL: {url}");
                Console.WriteLine($"  Default Model: {model}");
                Console.WriteLine($"  IsEnabled: {isEnabled}");
                Console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex);
        }
    }

    private static string Decrypt(string value)
    {
        try
        {
            byte[] decrypted = ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex)
        {
            return $"[Decryption Failed: {ex.Message}]";
        }
    }
}
