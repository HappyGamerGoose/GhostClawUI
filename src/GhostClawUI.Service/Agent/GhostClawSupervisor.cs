using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using GhostClawUI.Service.Infrastructure;
using GhostClawUI.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GhostClawUI.Service.Agent;

internal sealed class GhostClawSupervisor
{
    private readonly AppPaths _paths;
    private readonly McpCatalog _mcpCatalog;
    private readonly ILogger<GhostClawSupervisor> _logger;
    private readonly object _gate = new();
    private Process? _process;
    private WindowsJob? _job;
    private int _restartCount;
    private ServiceStatus _status = new(true, true, null, "Active", "GhostClaw is running directly in-process.", 0, DateTimeOffset.UtcNow);
    private readonly System.Threading.Channels.Channel<string> _logQueue = System.Threading.Channels.Channel.CreateUnbounded<string>();

    public GhostClawSupervisor(AppPaths paths, McpCatalog mcpCatalog, ILogger<GhostClawSupervisor> logger)
    {
        _paths = paths;
        _mcpCatalog = mcpCatalog;
        _logger = logger;

        _ = Task.Run(async () =>
        {
            var logPath = Path.Combine(_paths.DataRoot, "ghostclaw.log");
            await foreach (var line in _logQueue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await File.AppendAllTextAsync(logPath, line).ConfigureAwait(false);
                }
                catch { }
            }
        });
    }

    public ServiceStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ProvisionRuntime();
                _mcpCatalog.EnsureGhostClawSettings();
                using (var process = StartGhostClaw())
                {
                    SetStatus(true, process.Id, "Running", "GhostClaw service process is running.");
                    await process.WaitForExitAsync(stoppingToken).ConfigureAwait(false);

                    lock (_gate)
                    {
                        if (_process == process)
                        {
                            _process = null;
                        }
                    }

                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    _restartCount++;
                    SetStatus(false, null, "Restarting", $"GhostClaw exited with code {process.ExitCode}; restarting in {backoff.TotalSeconds:n0}s.");
                }

                await Task.Delay(backoff, stoppingToken).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _restartCount++;
                SetStatus(false, null, "Error", ex.Message);
                _logger.LogError(ex, "GhostClaw supervisor iteration failed");
                await Task.Delay(backoff, stoppingToken).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
            }
        }

        Stop();
    }

    public CommandResult Restart()
    {
        Stop();
        SetStatus(false, null, "Restarting", "Restart requested.");
        return new CommandResult(true, "GhostClaw restart requested.");
    }

    public CommandResult UndoLastFileModification()
    {
        // File backup hooks are exposed for agent integrations; this initial UI
        // build keeps the last-backup operation simple and conservative.
        return new CommandResult(false, "No restorable file backup has been recorded yet.");
    }

    private void KillRuntimeProcesses()
    {
        try
        {
            var runtimePath = _paths.RuntimeRoot;
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == Process.GetCurrentProcess().Id)
                        {
                            continue;
                        }
                        var path = process.MainModule?.FileName;
                        if (path != null && path.StartsWith(runtimePath, System.StringComparison.OrdinalIgnoreCase))
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                        // Ignore processes we cannot inspect or kill
                    }
                }
            }
        }
        catch
        {
            // Ignore
        }
    }

    public void ProvisionRuntime()
    {
        Directory.CreateDirectory(_paths.RuntimeRoot);

        var payloadZip = Path.Combine(_paths.PackagedPayloadRoot, "payload.zip");
        if (File.Exists(payloadZip))
        {
            var marker = Path.Combine(_paths.RuntimeRoot, ".payload-version");
            var version = $"{new FileInfo(payloadZip).Length}:{File.GetLastWriteTimeUtc(payloadZip):O}";
            if (!File.Exists(marker) || File.ReadAllText(marker) != version)
            {
                KillRuntimeProcesses();

                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        if (Directory.Exists(_paths.GhostClawRuntimeRoot))
                        {
                            Directory.Delete(_paths.GhostClawRuntimeRoot, recursive: true);
                        }

                        if (Directory.Exists(_paths.NodeRuntimeRoot))
                        {
                            Directory.Delete(_paths.NodeRuntimeRoot, recursive: true);
                        }
                        break;
                    }
                    catch
                    {
                        if (attempt == 3)
                        {
                            throw;
                        }
                        KillRuntimeProcesses();
                        System.Threading.Thread.Sleep(500);
                    }
                }

                ExtractZipWithPermissiveOverwrites(payloadZip, _paths.RuntimeRoot);
                File.WriteAllText(marker, version);
            }

            Directory.CreateDirectory(Path.Combine(_paths.GhostClawRuntimeRoot, "groups", "main"));
            Directory.CreateDirectory(Path.Combine(_paths.GhostClawRuntimeRoot, "store"));
            Directory.CreateDirectory(Path.Combine(_paths.GhostClawRuntimeRoot, "data"));
            return;
        }

        var payloadGhostClaw = Path.Combine(_paths.PackagedPayloadRoot, "ghostclaw");
        var sourceGhostClaw = Directory.Exists(payloadGhostClaw) ? payloadGhostClaw : _paths.DevGhostClawRoot;
        if (sourceGhostClaw is null || !Directory.Exists(sourceGhostClaw))
        {
            throw new DirectoryNotFoundException("GhostClaw payload was not found in the package and no sibling ghostclaw-main repo was detected.");
        }

        CopyDirectory(sourceGhostClaw, _paths.GhostClawRuntimeRoot, ShouldSkipRuntimeFile);

        var payloadNode = Path.Combine(_paths.PackagedPayloadRoot, "node");
        if (Directory.Exists(payloadNode))
        {
            CopyDirectory(payloadNode, _paths.NodeRuntimeRoot, _ => false);
        }

        Directory.CreateDirectory(Path.Combine(_paths.GhostClawRuntimeRoot, "groups", "main"));
        Directory.CreateDirectory(Path.Combine(_paths.GhostClawRuntimeRoot, "store"));
        Directory.CreateDirectory(Path.Combine(_paths.GhostClawRuntimeRoot, "data"));
    }

    private Process StartGhostClaw()
    {
        var entry = Path.Combine(_paths.GhostClawRuntimeRoot, "dist", "index.js");
        if (!File.Exists(entry))
        {
            throw new FileNotFoundException("GhostClaw dist/index.js is missing. Run the build script so the TypeScript payload is compiled before packaging.", entry);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _paths.ResolveNodeExe(),
            WorkingDirectory = _paths.GhostClawRuntimeRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(entry);
        startInfo.Environment["GHOSTCLAW_UI_SERVICE"] = "1";
        startInfo.Environment["MAX_CONCURRENT_AGENTS"] = "3";

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.LogInformation("[ghostclaw] {Line}", e.Data);
                _logQueue.Writer.TryWrite($"[OUT] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}: {e.Data}\n");
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.LogWarning("[ghostclaw] {Line}", e.Data);
                _logQueue.Writer.TryWrite($"[ERR] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}: {e.Data}\n");
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start GhostClaw.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_gate)
        {
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to kill existing process tree"); }
            _process?.Dispose();
            _process = process;
            _job?.Dispose();
            _job = WindowsJob.TryCreate(_logger);
            _job?.TryAssign(process);
        }

        return process;
    }

    private void Stop()
    {
        lock (_gate)
        {
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort during shutdown/restart.
            }

            _process?.Dispose();
            _process = null;
            _job?.Dispose();
            _job = null;
            SetStatus(false, null, "Stopped", "GhostClaw is stopped.");
        }
    }

    private void SetStatus(bool running, int? pid, string state, string detail)
    {
        lock (_gate)
        {
            _status = new ServiceStatus(true, running, pid, state, detail, _restartCount, DateTimeOffset.UtcNow);
        }
    }

    private static void ExtractZipWithPermissiveOverwrites(string zipPath, string destinationDirectory)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var destFullPath = Path.GetFullPath(destinationDirectory);
        if (!destFullPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
            destFullPath += Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            var destinationPath = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
            if (!destinationPath.StartsWith(destFullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Zip entry '{entry.FullName}' attempts to extract outside the destination directory.");
            }

            if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                entry.ExtractToFile(destinationPath, overwrite: true);
            }
            catch (IOException)
            {
                // Continue extracting remaining files (e.g. if file is locked)
                System.Diagnostics.Debug.WriteLine($"Skipping locked file during extraction: {entry.FullName}");
            }
            catch (UnauthorizedAccessException)
            {
                // Continue extracting remaining files
                System.Diagnostics.Debug.WriteLine($"Access denied to: {entry.FullName}");
            }
        }
    }

    private static bool ShouldSkipRuntimeFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
               path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}") ||
               path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
               path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");
    }

    private static void CopyDirectory(string source, string destination, Func<string, bool> skip)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            if (skip(directory))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (skip(file))
            {
                continue;
            }

            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private sealed class WindowsJob : IDisposable
    {
        private readonly nint _handle;
        private readonly ILogger _logger;

        private WindowsJob(nint handle, ILogger logger)
        {
            _handle = handle;
            _logger = logger;
        }

        public static WindowsJob? TryCreate(ILogger logger)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            var handle = CreateJobObject(nint.Zero, null);
            if (handle == nint.Zero)
            {
                logger.LogWarning("Could not create Windows Job Object: {Error}", new Win32Exception(Marshal.GetLastWin32Error()).Message);
                return null;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = 0x00002000
                }
            };

            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(handle, 9, buffer, (uint)length))
                {
                    logger.LogWarning("Could not configure Job Object: {Error}", new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return new WindowsJob(handle, logger);
        }

        public void TryAssign(Process process)
        {
            if (!AssignProcessToJobObject(_handle, process.Handle))
            {
                _logger.LogWarning("Could not assign GhostClaw to Job Object: {Error}", new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }
        }

        public void Dispose()
        {
            if (_handle != nint.Zero)
            {
                CloseHandle(_handle);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint CreateJobObject(nint lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(nint hJob, int infoType, nint lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(nint job, nint process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(nint handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }
    }
}

internal sealed class GhostClawHostedService : BackgroundService
{
    private readonly GhostClawSupervisor _supervisor;

    public GhostClawHostedService(GhostClawSupervisor supervisor)
    {
        _supervisor = supervisor;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _supervisor.RunAsync(stoppingToken);
}
