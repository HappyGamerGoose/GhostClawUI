using System.Xml.Linq;
using GhostClawUI.Service.Infrastructure;
using GhostClawUI.Service.Storage;
using GhostClawUI.Service.Providers;
using GhostClawUI.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windows.UI.Notifications;
using System.Xml;

namespace GhostClawUI.Service.Agent;

internal sealed class BackgroundWorkerService : BackgroundService
{
    private readonly EncryptedStore _store;
    private readonly ProviderGateway _providerGateway;
    private readonly GhostClawAgentRunner _agentRunner;
    private readonly ILogger<BackgroundWorkerService> _logger;

    public BackgroundWorkerService(
        EncryptedStore store, 
        ProviderGateway providerGateway,
        GhostClawAgentRunner agentRunner, 
        ILogger<BackgroundWorkerService> logger)
    {
        _store = store;
        _providerGateway = providerGateway;
        _agentRunner = agentRunner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Inject the default proactive task if it doesn't exist.
        EnsureProactiveTaskExists();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var tasks = _store.ListScheduledTasks();
                var now = DateTimeOffset.UtcNow;

                foreach (var task in tasks)
                {
                    if (task.Status != "active") continue;

                    if (!string.IsNullOrWhiteSpace(task.NextRun) && DateTimeOffset.TryParse(task.NextRun, out var nextRun))
                    {
                        if (now >= nextRun)
                        {
                            _ = Task.Run(() => ExecuteTaskAsync(task, stoppingToken), stoppingToken);
                        }
                    }
                    else if (task.ScheduleType == "startup" && string.IsNullOrWhiteSpace(task.LastRun))
                    {
                        _ = Task.Run(() => ExecuteTaskAsync(task, stoppingToken), stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background worker loop failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }

    private void EnsureProactiveTaskExists()
    {
        try
        {
            var existingTasks = _store.ListScheduledTasks();
            if (!existingTasks.Any(t => t.Id == "proactive-agent-poll"))
            {
                _store.UpsertScheduledTask(new ScheduledTask(
                    "proactive-agent-poll",
                    "main",
                    "ui:proactive",
                    "Review recent user interactions and determine if you should proactively text the user. If they have been inactive for hours, ask how they are doing or if they need help.",
                    null,
                    "cron",
                    "0 * * * *", // Every hour
                    "isolated",
                    DateTimeOffset.UtcNow.AddMinutes(60).ToString("O"),
                    null,
                    null,
                    "active",
                    DateTimeOffset.UtcNow.ToString("O")
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure proactive task exists.");
        }
    }

    private async Task ExecuteTaskAsync(ScheduledTask task, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Executing background task: {TaskId}", task.Id);

        // Update last run and schedule next run (simplified parsing)
        var nextRunStr = CalculateNextRun(task);
        var updatedTask = task with { LastRun = DateTimeOffset.UtcNow.ToString("O"), NextRun = nextRunStr };
        _store.UpsertScheduledTask(updatedTask);

        try
        {
            var defaultProvider = _store.ListProviders().FirstOrDefault(p => p.IsEnabled);
            if (defaultProvider == null)
            {
                _logger.LogWarning("No enabled provider for background task {TaskId}", task.Id);
                return;
            }

            var model = defaultProvider.DefaultModel;
            if (string.IsNullOrWhiteSpace(model))
            {
                model = defaultProvider.Models.FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(model)) return;

            var apiKey = PasswordVaultHelper.ReadProviderKey(defaultProvider.Id);
            if (string.IsNullOrWhiteSpace(apiKey)) return;

            var result = await _agentRunner.TryRunAsync(
                defaultProvider,
                apiKey,
                model,
                task.Prompt,
                task.ChatJid,
                _ => { }, // No real-time traces needed in pure background
                stoppingToken
            );

            if (result.Success && !string.IsNullOrWhiteSpace(result.Content))
            {
                _store.AddMessage(task.ChatJid, "assistant", result.Content, defaultProvider.Id, model, "chat");
                ShowToastNotification("GhostClaw Proactive Agent", result.Content);
            }
            
            var finalTask = updatedTask with { LastResult = result.Success ? "success" : "failed" };
            _store.UpsertScheduledTask(finalTask);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute background task {TaskId}", task.Id);
            var errTask = updatedTask with { LastResult = "error" };
            _store.UpsertScheduledTask(errTask);
        }
    }

    private string? CalculateNextRun(ScheduledTask task)
    {
        if (task.ScheduleType == "cron")
        {
            // Simple hour bump for now
            return DateTimeOffset.UtcNow.AddHours(1).ToString("O"); 
        }
        return null;
    }

    private void ShowToastNotification(string title, string content)
    {
        try
        {
            var xmlString = $@"
            <toast>
                <visual>
                    <binding template='ToastGeneric'>
                        <text>{System.Security.SecurityElement.Escape(title)}</text>
                        <text>{System.Security.SecurityElement.Escape(content)}</text>
                    </binding>
                </visual>
            </toast>";

            var xmlDocument = new Windows.Data.Xml.Dom.XmlDocument();
            xmlDocument.LoadXml(xmlString);

            var toast = new ToastNotification(xmlDocument);
            ToastNotificationManager.CreateToastNotifier("GhostClawUI").Show(toast);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show toast notification.");
        }
    }
}
