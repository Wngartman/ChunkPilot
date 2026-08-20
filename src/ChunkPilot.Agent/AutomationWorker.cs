using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ChunkPilot.Agent;

public sealed class AutomationWorker
{
    private readonly ServerSupervisor supervisor;
    private readonly ChunkPilotStore store;
    private readonly DiagnosticsService diagnostics;
    private readonly ILogger<AutomationWorker> logger;
    private readonly Dictionary<Guid, ServerSnapshot> previous = [];
    private readonly Dictionary<Guid, long> consoleSequences = [];
    private readonly ConcurrentDictionary<Guid, byte> executing = new();
    private readonly Dictionary<Guid, DateTimeOffset> lastScheduledRuns = [];

    public AutomationWorker(
        ServerSupervisor supervisor,
        ChunkPilotStore store,
        DiagnosticsService diagnostics,
        ILogger<AutomationWorker> logger)
    {
        this.supervisor = supervisor;
        this.store = store;
        this.diagnostics = diagnostics;
        this.logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var recipes = await store.GetAutomationRecipesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var group in recipes.Where(recipe => recipe.Enabled).GroupBy(recipe => recipe.ServerId))
        {
            ManagedServer server;
            try { server = supervisor.Get(group.Key); }
            catch (KeyNotFoundException) { continue; }
            var current = server.Snapshot(1_000);
            previous.TryGetValue(group.Key, out var prior);
            foreach (var recipe in group)
            {
                if (!ShouldRun(recipe, current, prior))
                    continue;
                if (!executing.TryAdd(recipe.Id, 0))
                    continue;
                _ = ExecuteAndReleaseAsync(recipe, server, cancellationToken);
            }
            previous[group.Key] = current;
            consoleSequences[group.Key] = current.Console.Count == 0
                ? 0
                : current.Console[current.Console.Count - 1].Sequence;
        }
    }

    private bool ShouldRun(AutomationRecipe recipe, ServerSnapshot current, ServerSnapshot? prior)
    {
        var priorPlayers = prior?.OnlinePlayers ?? 0;
        var players = current.OnlinePlayers ?? 0;
        return recipe.Trigger switch
        {
            AutomationTriggerKind.ServerStarted =>
                prior?.State is ServerState.Stopped or ServerState.Crashed &&
                current.State is ServerState.Starting or ServerState.Running,
            AutomationTriggerKind.ServerReady => prior?.State != ServerState.Running &&
                                                 current.State == ServerState.Running,
            AutomationTriggerKind.ServerStopped => prior is not null &&
                                                   prior.State != ServerState.Stopped &&
                                                   current.State == ServerState.Stopped,
            AutomationTriggerKind.ServerCrashed => prior?.State != ServerState.Crashed &&
                                                   current.State == ServerState.Crashed,
            AutomationTriggerKind.PlayerJoined => players > priorPlayers,
            AutomationTriggerKind.FirstPlayerJoined => priorPlayers == 0 && players > 0,
            AutomationTriggerKind.LastPlayerLeft => priorPlayers > 0 && players == 0,
            AutomationTriggerKind.PlayerCountThreshold =>
                int.TryParse(recipe.TriggerValue, out var threshold) &&
                players >= threshold && priorPlayers < threshold,
            AutomationTriggerKind.ScheduledTime => ScheduledNow(recipe),
            AutomationTriggerKind.HighRam =>
                long.TryParse(recipe.TriggerValue, out var ramMb) &&
                (current.CurrentStatistics?.WorkingSetBytes ?? 0) >= ramMb * 1024 * 1024 &&
                (prior?.CurrentStatistics?.WorkingSetBytes ?? 0) < ramMb * 1024 * 1024,
            AutomationTriggerKind.LowDiskSpace => IsLowDisk(recipe, current, prior),
            AutomationTriggerKind.ConsolePattern => NewConsoleMatches(recipe, current),
            _ => false
        };
    }

    private bool IsLowDisk(
        AutomationRecipe recipe,
        ServerSnapshot current,
        ServerSnapshot? prior)
    {
        var thresholdGb = long.TryParse(recipe.TriggerValue, out var configured)
            ? Math.Clamp(configured, 1, 1_024) : 10;
        var threshold = thresholdGb * 1024L * 1024 * 1024;
        var drive = new DriveInfo(Path.GetPathRoot(current.Definition.RootPath)!);
        if (drive.AvailableFreeSpace >= threshold)
            return false;
        var now = DateTimeOffset.UtcNow;
        if (lastScheduledRuns.TryGetValue(recipe.Id, out var last) &&
            now - last < TimeSpan.FromHours(1))
            return false;
        lastScheduledRuns[recipe.Id] = now;
        return true;
    }

    private bool ScheduledNow(AutomationRecipe recipe)
    {
        if (!TimeSpan.TryParse(recipe.TriggerValue, out var time))
            return false;
        var now = DateTimeOffset.Now;
        if (now.Hour != time.Hours || now.Minute != time.Minutes)
            return false;
        if (lastScheduledRuns.TryGetValue(recipe.Id, out var last) &&
            now - last < TimeSpan.FromMinutes(1))
            return false;
        lastScheduledRuns[recipe.Id] = now;
        return true;
    }

    private bool NewConsoleMatches(AutomationRecipe recipe, ServerSnapshot current)
    {
        consoleSequences.TryGetValue(recipe.ServerId, out var sequence);
        return !string.IsNullOrWhiteSpace(recipe.TriggerValue) &&
               current.Console.Any(line => line.Sequence > sequence &&
                   line.Text.Contains(recipe.TriggerValue, StringComparison.OrdinalIgnoreCase));
    }

    private async Task ExecuteAndReleaseAsync(
        AutomationRecipe recipe,
        ManagedServer server,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var step in recipe.Actions)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(step.TimeoutSeconds, 1, 3_600)));
                await ExecuteStepAsync(step, server, timeout.Token).ConfigureAwait(false);
            }
            await RecordAsync(recipe, server, "Success", "").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Automation {Recipe} failed", recipe.Name);
            await RecordAsync(recipe, server, "Failed", exception.Message).ConfigureAwait(false);
        }
        finally
        {
            executing.TryRemove(recipe.Id, out _);
        }
    }

    private async Task ExecuteStepAsync(
        AutomationStep step,
        ManagedServer server,
        CancellationToken cancellationToken)
    {
        switch (step.Action)
        {
            case AutomationActionKind.SendCommand:
                Ensure((await server.SendCommandAsync(step.Value, "Automation", cancellationToken)
                    .ConfigureAwait(false)));
                break;
            case AutomationActionKind.SendAnnouncement:
                Ensure((await server.SendCommandAsync($"say {step.Value}", "Automation", cancellationToken)
                    .ConfigureAwait(false)));
                break;
            case AutomationActionKind.Save:
                Ensure(await server.SaveAsync("Automation", cancellationToken).ConfigureAwait(false));
                break;
            case AutomationActionKind.Backup:
                _ = await supervisor.BackupAsync(server.Definition.Id, "Automation", cancellationToken)
                    .ConfigureAwait(false);
                break;
            case AutomationActionKind.SafeRestart:
                Ensure(await server.RestartAsync("Automation", cancellationToken).ConfigureAwait(false));
                break;
            case AutomationActionKind.StopAfterEmpty:
            {
                var minutes = int.TryParse(step.Value, out var value) ? Math.Clamp(value, 1, 1_440) : 30;
                await Task.Delay(TimeSpan.FromMinutes(minutes), cancellationToken).ConfigureAwait(false);
                if ((server.Snapshot().OnlinePlayers ?? 0) == 0)
                    Ensure(await server.StopAsync(true, "Automation", cancellationToken).ConfigureAwait(false));
                break;
            }
            case AutomationActionKind.StartAnotherServer:
                if (!Guid.TryParse(step.Value, out var id))
                    throw new InvalidOperationException("StartAnotherServer requires a server ID.");
                Ensure(await supervisor.Get(id).StartAsync("Automation", cancellationToken).ConfigureAwait(false));
                break;
            case AutomationActionKind.RunDiagnostics:
                _ = await diagnostics.AnalyzeAsync(server.Definition, cancellationToken).ConfigureAwait(false);
                break;
            case AutomationActionKind.ShowNotification:
            case AutomationActionKind.RecordActivity:
                break;
            case AutomationActionKind.Wait:
                if (!int.TryParse(step.Value, out var seconds) || seconds is < 1 or > 3_600)
                    throw new InvalidOperationException("Wait requires a duration from 1 to 3600 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
                break;
            case AutomationActionKind.ExternalProgram:
                await RunExternalProgramAsync(step, server, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException($"{step.Action} is not implemented.");
        }
    }

    private static void Ensure(OperationResult result)
    {
        if (!result.Success)
            throw new InvalidOperationException(result.Message);
    }

    private async Task RunExternalProgramAsync(
        AutomationStep step,
        ManagedServer server,
        CancellationToken cancellationToken)
    {
        var specification = JsonSerializer.Deserialize<ExternalProgramAction>(
            step.Value, ProtocolJson.Options)
            ?? throw new InvalidOperationException("External program details are invalid.");
        if (!Path.IsPathFullyQualified(specification.Executable) ||
            !File.Exists(specification.Executable))
            throw new FileNotFoundException(
                "The approved external executable was not found.", specification.Executable);
        var workingDirectory = string.IsNullOrWhiteSpace(specification.WorkingDirectory)
            ? Path.GetDirectoryName(specification.Executable)!
            : Path.GetFullPath(specification.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException(
                $"External program working directory does not exist: {workingDirectory}");
        var start = new ProcessStartInfo
        {
            FileName = specification.Executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in specification.Arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Windows did not start the approved external program.");
        var outputTask = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        var errorTask = ReadBoundedAsync(process.StandardError, cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        await store.AddActivityAsync(new ActivityEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            ServerId = server.Definition.Id,
            ServerName = server.Definition.Name,
            Action = $"External program: {Path.GetFileName(specification.Executable)}",
            Result = process.ExitCode == 0 ? "Success" : $"Exit {process.ExitCode}",
            Error = SecretRedactor.Redact(
                string.Join(Environment.NewLine,
                    new[] { output, error }.Where(value => !string.IsNullOrWhiteSpace(value)))),
            Source = "Automation"
        }, CancellationToken.None).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Approved external program exited with code {process.ExitCode}.");
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        const int maximumCharacters = 65_536;
        var buffer = new char[4_096];
        var output = new StringBuilder();
        while (output.Length < maximumCharacters)
        {
            var count = await reader.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, maximumCharacters - output.Length)),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }

    private Task RecordAsync(AutomationRecipe recipe, ManagedServer server, string result, string error) =>
        store.AddActivityAsync(new ActivityEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            ServerId = server.Definition.Id,
            ServerName = server.Definition.Name,
            Action = $"Automation: {recipe.Name}",
            Result = result,
            Error = SecretRedactor.Redact(error),
            Source = "Automation"
        }, CancellationToken.None);
}
