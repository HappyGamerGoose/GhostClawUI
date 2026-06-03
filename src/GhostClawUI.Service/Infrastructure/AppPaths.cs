namespace GhostClawUI.Service.Infrastructure;

internal sealed class AppPaths
{
    public AppPaths()
    {
        DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GhostClawUI");
        RuntimeRoot = Path.Combine(DataRoot, "Runtime");
        GhostClawRuntimeRoot = Path.Combine(RuntimeRoot, "ghostclaw");
        NodeRuntimeRoot = Path.Combine(RuntimeRoot, "node");
        DatabasePath = Path.Combine(DataRoot, "ghostclawui.db");
        ExportRoot = Path.Combine(DataRoot, "Exports");
        BackupRoot = Path.Combine(DataRoot, "Backups");
        PackagedPayloadRoot = ResolvePackagedPayloadRoot();
        DevGhostClawRoot = FindSiblingGhostClawRepo();

        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(RuntimeRoot);
        Directory.CreateDirectory(ExportRoot);
        Directory.CreateDirectory(BackupRoot);

        // Migrate legacy database from CommonApplicationData (C:\ProgramData) to LocalApplicationData
        var legacyDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GhostClawUI");
        var legacyDb = Path.Combine(legacyDataRoot, "ghostclawui.db");
        if (File.Exists(legacyDb) && (!File.Exists(DatabasePath) || new FileInfo(DatabasePath).Length < 20480))
        {
            try
            {
                foreach (var file in Directory.GetFiles(legacyDataRoot, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(legacyDataRoot, file);
                    var dest = Path.Combine(DataRoot, relative);
                    var destDir = Path.GetDirectoryName(dest);
                    if (destDir != null)
                    {
                        Directory.CreateDirectory(destDir);
                    }
                    File.Copy(file, dest, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Database migration full copy failed: {ex}");
                // Fallback: copy database file only if full copy fails
                try
                {
                    File.Copy(legacyDb, DatabasePath, overwrite: true);
                }
                catch (Exception fallbackEx) 
                { 
                    System.Diagnostics.Debug.WriteLine($"Database migration fallback failed: {fallbackEx}");
                }
            }
        }
    }

    public string DataRoot { get; }
    public string RuntimeRoot { get; }
    public string GhostClawRuntimeRoot { get; }
    public string NodeRuntimeRoot { get; }
    public string DatabasePath { get; }
    public string ExportRoot { get; }
    public string BackupRoot { get; }
    public string PackagedPayloadRoot { get; }
    public string? DevGhostClawRoot { get; }

    public string ResolveNodeExe()
    {
        var packaged = Path.Combine(NodeRuntimeRoot, "node.exe");
        if (File.Exists(packaged))
        {
            return packaged;
        }

        var payload = Path.Combine(PackagedPayloadRoot, "node", "node.exe");
        if (File.Exists(payload))
        {
            return payload;
        }

        return "node.exe";
    }

    private static string ResolvePackagedPayloadRoot()
    {
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        foreach (var directory in WalkUp(baseDirectory))
        {
            var candidate = Path.Combine(directory.FullName, "GhostClawPayload");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "GhostClawPayload");
    }

    private static IEnumerable<DirectoryInfo> WalkUp(DirectoryInfo? directory)
    {
        while (directory is not null)
        {
            yield return directory;
            directory = directory.Parent;
        }
    }

    private static string? FindSiblingGhostClawRepo()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var sibling = Path.Combine(current.FullName, "ghostclaw-main");
            if (File.Exists(Path.Combine(sibling, "package.json")))
            {
                return sibling;
            }

            if (current.Parent is not null)
            {
                var besideParent = Path.Combine(current.Parent.FullName, "ghostclaw-main");
                if (File.Exists(Path.Combine(besideParent, "package.json")))
                {
                    return besideParent;
                }
            }

            current = current.Parent;
        }

        return null;
    }
}
