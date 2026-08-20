using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using ChunkPilot.App.Navigation;
using ChunkPilot.App.Presentation;
using ChunkPilot.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace ChunkPilot.App;

public sealed partial class MainViewModel
{
    private readonly Dictionary<string, MigrationResolution> migrationResolutions =
        new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<VersionSnapshot> Versions { get; } = [];

    /// <summary>
    /// True once this server has at least one recorded installed version.
    /// </summary>
    /// <remarks>
    /// The version table is shown only when it has something in it. An empty grid of headers reads
    /// as a feature that is broken rather than one that has nothing to report yet.
    /// </remarks>
    public bool HasVersionHistory => Versions.Count > 0;
    public ObservableCollection<UpdateCenterItem> UpdateCenterItems { get; } = [];
    public ObservableCollection<UpdateHistoryEntry> UpdateHistory { get; } = [];

    public IReadOnlyList<UpdateProvider> UpdateProviders { get; } =
        Enum.GetValues<UpdateProvider>().Where(item => item != UpdateProvider.None).ToArray();
    public IReadOnlyList<ReleaseChannel> ReleaseChannels { get; } = Enum.GetValues<ReleaseChannel>();
    public IReadOnlyList<MigrationResolutionKind> MigrationResolutionKinds { get; } =
        Enum.GetValues<MigrationResolutionKind>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateStatusText))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusDetail))]
    private UpdateSource? currentUpdateSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateStatusText))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusDetail))]
    [NotifyPropertyChangedFor(nameof(CompatibilityExplanation))]
    private UpdateCheckResult? currentUpdateCheck;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateStatusText))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusDetail))]
    private UpdateOperationSnapshot? currentUpdateOperation;

    [ObservableProperty]
    private VersionSnapshot? selectedVersion;

    [ObservableProperty]
    private UpdatePreferences currentUpdatePreferences = new();

    [ObservableProperty]
    private UpdateProvider linkProvider = UpdateProvider.Modrinth;

    [ObservableProperty]
    private string linkProjectName = "";

    [ObservableProperty]
    private string linkProjectId = "";

    [ObservableProperty]
    private string linkSourceUrl = "";

    [ObservableProperty]
    private string linkInstalledVersionId = "";

    [ObservableProperty]
    private string linkInstalledVersionName = "";

    [ObservableProperty]
    private string linkInstalledFileId = "";

    [ObservableProperty]
    private string linkMinecraftVersion = "";

    [ObservableProperty]
    private string linkLoader = "";

    [ObservableProperty]
    private string linkLoaderVersion = "";

    [ObservableProperty]
    private string linkAssetPattern = "server";

    [ObservableProperty]
    private ReleaseChannel linkReleaseChannel = ReleaseChannel.Stable;

    [ObservableProperty]
    private string curseForgeApiKey = "";

    [ObservableProperty]
    private bool curseForgeKeyConfigured;

    [ObservableProperty]
    private string maintenanceWindowText = "4:00 AM";

    [ObservableProperty]
    private string versionDescription = "";

    [ObservableProperty]
    private bool keepVersionPermanently;

    [ObservableProperty]
    private bool confirmMigrationConflicts;

    [ObservableProperty]
    private PackFileChange? selectedMigrationChange;

    [ObservableProperty]
    private MigrationResolutionKind selectedMigrationResolution = MigrationResolutionKind.NewBaseline;

    [ObservableProperty]
    private string mergedMigrationContent = "";

    public string UpdateStatusText =>
        CurrentUpdateOperation is { Progress.State: UpdateOperationState.ReadyToInstall }
            ? UpdateOperationStatePresentation.ToLabel(UpdateOperationState.ReadyToInstall)
            : CurrentUpdateOperation is { IsTerminal: false } operation
            ? UpdateOperationStatePresentation.ToDetail(
                operation.Progress.State,
                operation.Progress.Percent,
                operation.Progress.CurrentStep)
            : CurrentUpdateCheck is not null
                ? ServerUpdateStatusPresentation.ToDetail(
                    CurrentUpdateCheck.Status,
                    CurrentUpdateCheck.Status.ToString())
                : (CurrentUpdateSource is null
                    ? "Not linked to an update source"
                    : "Not checked");

    public string UpdateStatusDetail =>
        CurrentUpdateOperation is { Progress.State: UpdateOperationState.ReadyToInstall } ready
            ? ready.Progress.Detail
            : CurrentUpdateOperation is { IsTerminal: false } operation
            ? UpdateOperationStatePresentation.ToDetail(
                operation.Progress.State,
                operation.Progress.Percent,
                operation.Progress.CurrentStep)
            : CurrentUpdateCheck?.Message ??
              (CurrentUpdateSource is null
                  ? "Update information is not available for this server. You can link an update source or continue managing it manually."
                  : $"{CurrentUpdateSource.Provider} · {CurrentUpdateSource.ProjectName} · installed {CurrentUpdateSource.InstalledVersionName}");

    public string CompatibilityExplanation => CurrentUpdateCheck?.CompatibilityReasons.Count > 0
        ? string.Join(Environment.NewLine, CurrentUpdateCheck.CompatibilityReasons.Select(item => "• " + item))
        : "No compatibility changes were reported.";

    public string MigrationResolutionSummary => migrationResolutions.Count == 0
        ? "No explicit conflict choices saved."
        : string.Join(" · ", migrationResolutions.Select(pair => $"{pair.Key}: {pair.Value.Kind}"));

    public bool IsPendingUpdateValidation =>
        Versions.Any(item => item.IsActive && item.Health == VersionHealth.PendingValidation);
    public string ActivePackVersion => Versions.FirstOrDefault(item => item.IsActive)?.VersionName ??
                                       CurrentUpdateSource?.InstalledVersionName ?? "Unknown";
    public string ActualPackJava => Versions.FirstOrDefault(item => item.IsActive)?.JavaVersion ?? "Unknown";
    public string RollbackAvailability => Versions.Any(item =>
        !item.IsActive && item.Verified && File.Exists(item.SnapshotPath))
        ? "Verified rollback available" : "No verified rollback snapshot";

    partial void OnSelectedVersionChanged(VersionSnapshot? value)
    {
        VersionDescription = value?.Description ?? "";
        KeepVersionPermanently = value?.KeepPermanently ?? false;
    }

    [RelayCommand]
    private void KeepTestingUpdate()
    {
        StatusMessage = "The active update remains pending validation. Its verified rollback snapshot is still available.";
    }

    [RelayCommand]
    private void ViewUpdateConsole()
    {
        Navigation.NavigateServer(ServerDestination.Console, SelectedServer?.Definition.Id);
    }

    [RelayCommand]
    private void ViewDetectedUpdateWarnings()
    {
        var migrationNotes = CurrentUpdateCheck?.LatestVersion?.MigrationNotes;
        dialogs.ShowInformation("Detected update warnings",
            CompatibilityExplanation +
            (string.IsNullOrWhiteSpace(migrationNotes)
                ? ""
                : Environment.NewLine + Environment.NewLine + "Migration notes:" +
                  Environment.NewLine + migrationNotes));
    }

    [RelayCommand]
    private void CompareUpdateVersions()
    {
        var installed = CurrentUpdateCheck?.InstalledVersion;
        var latest = CurrentUpdateCheck?.LatestVersion;
        if (installed is null || latest is null)
        {
            dialogs.ShowInformation("Compare versions",
                "Run Check for updates after linking an installed baseline to compare exact versions.");
            return;
        }

        dialogs.ShowInformation("Compare versions",
            $"Installed: {installed.VersionName} ({installed.VersionId})" + Environment.NewLine +
            $"Minecraft / loader: {installed.MinecraftVersion} / {installed.Loader} {installed.LoaderVersion}" +
            Environment.NewLine +
            $"Java: {(installed.RequiredJavaMajor > 0 ? installed.RequiredJavaMajor.ToString() : "Unknown")}" +
            Environment.NewLine + Environment.NewLine +
            $"Target: {latest.VersionName} ({latest.VersionId})" + Environment.NewLine +
            $"Minecraft / loader: {latest.MinecraftVersion} / {latest.Loader} {latest.LoaderVersion}" +
            Environment.NewLine +
            $"Java: {(latest.RequiredJavaMajor > 0 ? latest.RequiredJavaMajor.ToString() : "Unknown")}" +
            Environment.NewLine + Environment.NewLine +
            CompatibilityExplanation);
    }

    [RelayCommand]
    private async Task DetectUpdateSourceAsync()
    {
        if (SelectedServer is null)
            return;
        await RunBusyAsync("Reading local pack metadata without changing server files…", async () =>
        {
            var detected = await client.SendAsync<UpdateSourceDetectionResult>("DetectUpdateSource",
                new ServerIdRequest(SelectedServer.Definition.Id)).ConfigureAwait(true);
            CurrentUpdateSource = detected.Source;
            if (detected.Source is not null)
                PopulateLinkFields(detected.Source);
            StatusMessage = detected.Message;
            if (!detected.IsTrustworthy)
                dialogs.ShowInformation("Link update source",
                    detected.Message + Environment.NewLine + Environment.NewLine +
                    string.Join(Environment.NewLine, detected.Evidence));
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenUpdateCenterServer(UpdateCenterItem? item)
    {
        if (item is null)
            return;
        SelectedServer = Servers.FirstOrDefault(server => server.Definition.Id == item.ServerId);
    }

    [RelayCommand]
    private async Task LinkUpdateSourceAsync()
    {
        if (SelectedServer is null)
            return;
        var source = new UpdateSource
        {
            ServerId = SelectedServer.Definition.Id,
            Provider = LinkProvider,
            ProjectName = LinkProjectName.Trim(),
            ProjectId = LinkProjectId.Trim(),
            InstalledVersionId = LinkInstalledVersionId.Trim(),
            InstalledVersionName = LinkInstalledVersionName.Trim(),
            InstalledFileId = LinkInstalledFileId.Trim(),
            MinecraftVersion = LinkMinecraftVersion.Trim(),
            Loader = LinkLoader.Trim(),
            LoaderVersion = LinkLoaderVersion.Trim(),
            ReleaseChannel = LinkReleaseChannel,
            SourceUrl = LinkSourceUrl.Trim(),
            AssetNamePattern = LinkAssetPattern.Trim(),
            InstalledAt = DateTimeOffset.Now,
            IsUserLinked = true
        };
        if (!dialogs.Confirm("Link update source",
                $"Provider: {source.Provider}\nProject: {source.ProjectName}\nProject ID: {source.ProjectId}\n" +
                $"Installed baseline: {source.InstalledVersionName} ({source.InstalledVersionId})\n" +
                $"Minecraft / loader: {source.MinecraftVersion} / {source.Loader} {source.LoaderVersion}\n" +
                $"Source: {source.SourceUrl}\n\nChunkPilot will not modify the server. It will trust this identity when comparing releases."))
            return;
        await RunBusyAsync("Linking the reviewed update source…", async () =>
        {
            var result = await client.SendAsync<OperationResult>("LinkUpdateSource",
                new LinkUpdateSourceRequest(source)).ConfigureAwait(true);
            CurrentUpdateSource = source;
            StatusMessage = result.Message;
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void BrowseLocalUpdatePackage()
    {
        var selected = dialogs.SelectFile("Select local server-pack package",
            "Server packages (*.zip;*.jar)|*.zip;*.jar|All files (*.*)|*.*");
        if (selected is null)
            return;
        LinkProvider = UpdateProvider.LocalPackageHistory;
        LinkSourceUrl = selected;
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (SelectedServer is null)
            return;
        await RunBusyAsync("Checking the official linked source…", async () =>
        {
            CurrentUpdateCheck = await client.SendAsync<UpdateCheckResult>("CheckUpdates",
                new CheckUpdatesRequest(SelectedServer.Definition.Id)).ConfigureAwait(true);
            CurrentUpdateSource = CurrentUpdateCheck.Source ?? CurrentUpdateSource;
            migrationResolutions.Clear();
            OnPropertyChanged(nameof(MigrationResolutionSummary));
            StatusMessage = CurrentUpdateCheck.Message;
            await LoadVersionsAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DownloadAvailableUpdateAsync()
    {
        if (SelectedServer is null || CurrentUpdateCheck?.LatestVersion is not { } target)
            return;
        await RunBusyAsync("Downloading update without changing the active server…", async () =>
        {
            var started = await client.SendAsync<UpdateOperationRequest>("BeginPackUpdate",
                new UpdateInstallRequest
                {
                    ServerId = SelectedServer.Definition.Id,
                    TargetVersion = target,
                    DownloadOnly = true,
                    StartForValidation = false
                }).ConfigureAwait(true);
            await PollUpdateOperationAsync(started.OperationId, refreshDetails: false).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task InstallAvailableUpdateAsync()
    {
        if (SelectedServer is null || CurrentUpdateCheck?.LatestVersion is not { } target)
            return;
        if (CurrentUpdateCheck.Compatibility is UpdateCompatibility.Incompatible or UpdateCompatibility.Unknown)
        {
            dialogs.ShowError("Update cannot be installed",
                string.Join(Environment.NewLine, CurrentUpdateCheck.CompatibilityReasons));
            return;
        }
        var warning = CurrentUpdateCheck.CompatibilityReasons.Count == 0
            ? "No compatibility changes were detected."
            : string.Join(Environment.NewLine, CurrentUpdateCheck.CompatibilityReasons.Select(item => "• " + item));
        if (!dialogs.Confirm("Install server-pack update",
                $"Target: {target.VersionName}\nProvider/source: {CurrentUpdateSource?.Provider} · {target.DownloadUrl}\n" +
                $"Published: {UpdatePolicy.FormatUiTimestamp(target.PublishedAt)}\n" +
                $"Minecraft / loader: {target.MinecraftVersion} / {target.Loader} {target.LoaderVersion}\n" +
                $"Download: {(target.FileSize is { } size ? $"{size / 1024d / 1024d:F1} MB" : "Unknown size")}\n\n" +
                $"{warning}\n\nChunkPilot will warn players, save and stop the full process tree, create and verify a full rollback snapshot including worlds, " +
                "stage and hash-check the package, preview migration decisions, switch atomically, start it, wait for readiness, and run a local status query."))
            return;

        await RunBusyAsync("Starting transactional server-pack update…", async () =>
        {
            var started = await client.SendAsync<UpdateOperationRequest>("BeginPackUpdate",
                new UpdateInstallRequest
                {
                    ServerId = SelectedServer.Definition.Id,
                    TargetVersion = target,
                    PlayerCountdownSeconds = SelectedServer.State == ServerState.Running ? 30 : 0,
                    StartForValidation = true,
                    ConfirmedMigrationWarnings = ConfirmMigrationConflicts,
                    MigrationResolutions = new Dictionary<string, MigrationResolution>(
                        migrationResolutions, StringComparer.OrdinalIgnoreCase)
                }).ConfigureAwait(true);
            await PollUpdateOperationAsync(started.OperationId, refreshDetails: true).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CancelPackUpdateAsync()
    {
        if (CurrentUpdateOperation is null || CurrentUpdateOperation.IsTerminal)
            return;
        _ = await client.SendAsync<OperationResult>("CancelPackUpdate",
            new UpdateOperationRequest(CurrentUpdateOperation.OperationId)).ConfigureAwait(true);
        StatusMessage = "Cancellation requested. ChunkPilot will finish any active switch or rollback safely.";
    }

    [RelayCommand]
    private void SaveMigrationResolution()
    {
        if (SelectedMigrationChange is null)
            return;
        if (SelectedMigrationResolution == MigrationResolutionKind.UseMergedText &&
            string.IsNullOrWhiteSpace(MergedMigrationContent))
        {
            dialogs.ShowError("Merged text is empty",
                "Enter the complete resolved text before saving a merged-text decision.");
            return;
        }
        migrationResolutions[SelectedMigrationChange.RelativePath] = new MigrationResolution
        {
            Kind = SelectedMigrationResolution,
            MergedContent = SelectedMigrationResolution == MigrationResolutionKind.UseMergedText
                ? MergedMigrationContent : ""
        };
        OnPropertyChanged(nameof(MigrationResolutionSummary));
        StatusMessage = $"Saved {SelectedMigrationResolution} for {SelectedMigrationChange.RelativePath}.";
    }

    [RelayCommand]
    private async Task MarkVersionHealthyAsync(string? retentionDaysText = null)
    {
        var retentionDays = int.TryParse(retentionDaysText, out var parsedRetentionDays)
            ? parsedRetentionDays
            : 30;
        if (SelectedServer is null)
            return;
        var active = Versions.FirstOrDefault(item => item.IsActive);
        if (active is null)
            return;
        if (!dialogs.Confirm("Mark updated version healthy",
                $"ChunkPilot updated this server to {active.VersionName}. Is it working correctly?\n\n" +
                $"The previous rollback snapshot will be retained for {(retentionDays <= 0 ? "manual cleanup" : $"{retentionDays} days")}."))
            return;
        await RunBusyAsync("Marking the active version healthy…", async () =>
        {
            _ = await client.SendAsync<OperationResult>("MarkVersionHealthy",
                new MarkVersionHealthyRequest(SelectedServer.Definition.Id, active.Id, retentionDays)).ConfigureAwait(true);
            await LoadVersionsAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RollbackVersionAsync()
    {
        if (SelectedServer is null || SelectedVersion is null)
            return;
        if (!dialogs.Confirm("Roll back server version",
                $"Activate {SelectedVersion.VersionName} from this verified snapshot?\n\n{SelectedVersion.SnapshotPath}\n\n" +
                "ChunkPilot will warn players, save and stop a running server, create a safety snapshot of the current installation, " +
                "restore the verified target, and restart when the server was previously running."))
            return;
        await RunBusyAsync("Restoring the selected version…", async () =>
        {
            _ = await client.SendAsync<OperationResult>("RollbackVersion",
                new VersionSnapshotRequest(SelectedServer.Definition.Id, SelectedVersion.Id)).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            await LoadUpdateDetailsAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task VerifyVersionAsync()
    {
        if (SelectedServer is null || SelectedVersion is null)
            return;
        await RunBusyAsync("Verifying compressed snapshot and manifest hashes…", async () =>
        {
            var result = await client.SendAsync<OperationResult>("VerifyVersion",
                new VersionSnapshotRequest(SelectedServer.Definition.Id, SelectedVersion.Id)).ConfigureAwait(true);
            StatusMessage = result.Message;
            await LoadVersionsAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteVersionAsync()
    {
        if (SelectedServer is null || SelectedVersion is null)
            return;
        var version = SelectedVersion;
        if (!dialogs.Confirm("Remove old version snapshot",
                $"Remove this inactive version record?\n\nVersion: {version.VersionName}\nArchive: {version.SnapshotPath}\nManifest: {version.ManifestPath}\n" +
                $"Snapshot size: {version.SnapshotSize / 1024d / 1024d:F1} MB\nWorld data included: {version.IncludesWorldData}\n\n" +
                "Only these snapshot files and the version record are affected. The active server and all separately managed worlds are excluded. " +
                "The files will be moved into ChunkPilot Recovery rather than erased immediately."))
            return;
        await RunBusyAsync("Moving the selected snapshot into Recovery…", async () =>
        {
            var result = await client.SendAsync<OperationResult>("DeleteVersion",
                new VersionSnapshotRequest(SelectedServer.Definition.Id, version.Id)).ConfigureAwait(true);
            StatusMessage = result.Message;
            await LoadVersionsAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveVersionMetadataAsync()
    {
        if (SelectedServer is null || SelectedVersion is null)
            return;
        await RunBusyAsync("Saving version details…", async () =>
        {
            _ = await client.SendAsync<OperationResult>("UpdateVersionMetadata",
                new VersionMetadataRequest(SelectedServer.Definition.Id, SelectedVersion.Id,
                    KeepVersionPermanently, VersionDescription)).ConfigureAwait(true);
            await LoadVersionsAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenVersionSnapshot()
    {
        if (SelectedVersion is null || string.IsNullOrWhiteSpace(SelectedVersion.SnapshotPath))
            return;
        var argument = File.Exists(SelectedVersion.SnapshotPath)
            ? $"/select,{CommandLineQuoter.QuoteWindowsArgument(SelectedVersion.SnapshotPath)}"
            : CommandLineQuoter.QuoteWindowsArgument(Path.GetDirectoryName(SelectedVersion.SnapshotPath) ?? "");
        Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ExportVersionRecord()
    {
        if (SelectedVersion is null)
            return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export ChunkPilot version record",
            Filter = "JSON files (*.json)|*.json",
            FileName = $"chunkpilot-{SelectedVersion.VersionName}-version.json"
        };
        if (dialog.ShowDialog() != true)
            return;
        File.WriteAllText(dialog.FileName,
            JsonSerializer.Serialize(SelectedVersion,
                new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true }));
        StatusMessage = $"Version record exported to {dialog.FileName}";
    }

    [RelayCommand]
    private async Task SaveUpdatePreferencesAsync()
    {
        if (SelectedServer is null)
            return;
        if (!DateTime.TryParse(MaintenanceWindowText, System.Globalization.CultureInfo.CurrentCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var maintenance))
        {
            dialogs.ShowError("Invalid maintenance window",
                "Enter a local time such as 4:00 AM.");
            return;
        }
        var preferences = CurrentUpdatePreferences with
        {
            ServerId = SelectedServer.Definition.Id,
            CheckIntervalHours = Math.Clamp(CurrentUpdatePreferences.CheckIntervalHours, 1, 24 * 30),
            SnapshotRetentionDays = Math.Clamp(CurrentUpdatePreferences.SnapshotRetentionDays, 1, 3650),
            MaintenanceWindow = maintenance.TimeOfDay
        };
        await RunBusyAsync("Saving update preferences…", async () =>
        {
            _ = await client.SendAsync<OperationResult>("SetUpdatePreferences", preferences).ConfigureAwait(true);
            CurrentUpdatePreferences = preferences;
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void SetUpdateCheckInterval(string? hoursText)
    {
        if (!int.TryParse(hoursText, out var hours))
            return;

        CurrentUpdatePreferences = CurrentUpdatePreferences with
        {
            CheckIntervalHours = Math.Clamp(hours, 1, 24 * 30)
        };
    }

    [RelayCommand]
    private async Task SaveCurseForgeApiKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(CurseForgeApiKey))
            return;
        await RunBusyAsync("Encrypting provider key for the current Windows user…", async () =>
        {
            _ = await client.SendAsync<OperationResult>("SetCurseForgeApiKey",
                new SettingsValueRequest("curseforge-api-key", CurseForgeApiKey)).ConfigureAwait(true);
            CurseForgeApiKey = "";
            CurseForgeKeyConfigured = true;
        }).ConfigureAwait(true);
    }

    internal async Task LoadUpdateDetailsAsync()
    {
        if (SelectedServer is null)
            return;
        var id = SelectedServer.Definition.Id;
        var source = await client.SendAsync<UpdateSourceResponse>("GetUpdateSource", new ServerIdRequest(id))
            .ConfigureAwait(true);
        CurrentUpdateSource = source.Source;
        if (CurrentUpdateSource is null)
        {
            var detected = await client.SendAsync<UpdateSourceDetectionResult>(
                "DetectUpdateSource", new ServerIdRequest(id)).ConfigureAwait(true);
            CurrentUpdateSource = detected.Source;
        }
        if (CurrentUpdateSource is not null)
            PopulateLinkFields(CurrentUpdateSource);
        CurrentUpdatePreferences = await client.SendAsync<UpdatePreferences>("GetUpdatePreferences",
            new ServerIdRequest(id)).ConfigureAwait(true);
        CurrentUpdateCheck = (await client.SendAsync<UpdateCheckResponse>("GetLatestUpdateCheck",
            new ServerIdRequest(id)).ConfigureAwait(true)).Check;
        MaintenanceWindowText = DateTime.Today.Add(CurrentUpdatePreferences.MaintenanceWindow)
            .ToString("h:mm tt", System.Globalization.CultureInfo.CurrentCulture);
        Replace(Versions, await client.SendAsync<IReadOnlyList<VersionSnapshot>>("ListVersions",
            new ServerIdRequest(id)).ConfigureAwait(true));
        OnPropertyChanged(nameof(HasVersionHistory));
        OnPropertyChanged(nameof(IsPendingUpdateValidation));
        OnPropertyChanged(nameof(ActivePackVersion));
        OnPropertyChanged(nameof(ActualPackJava));
        OnPropertyChanged(nameof(RollbackAvailability));
        Replace(UpdateHistory, await client.SendAsync<IReadOnlyList<UpdateHistoryEntry>>("GetUpdateHistory",
            new ServerIdRequest(id)).ConfigureAwait(true));
        SelectedVersion = Versions.FirstOrDefault(item => item.IsActive) ?? Versions.FirstOrDefault();
        var key = await client.SendAsync<TextResponse>("HasCurseForgeApiKey").ConfigureAwait(true);
        CurseForgeKeyConfigured = key.Value == "configured";
    }

    internal async Task LoadUpdateCenterAsync() =>
        Replace(UpdateCenterItems,
            await client.SendAsync<IReadOnlyList<UpdateCenterItem>>("GetUpdateCenter").ConfigureAwait(true));

    private async Task PollUpdateOperationAsync(Guid operationId, bool refreshDetails)
    {
        while (true)
        {
            CurrentUpdateOperation = await client.SendAsync<UpdateOperationSnapshot>("GetPackUpdate",
                new UpdateOperationRequest(operationId)).ConfigureAwait(true);
            OnPropertyChanged(nameof(UpdateStatusText));
            OnPropertyChanged(nameof(UpdateStatusDetail));
            StatusMessage = CurrentUpdateOperation.Progress.Detail.Length > 0
                ? CurrentUpdateOperation.Progress.Detail
                : CurrentUpdateOperation.Progress.CurrentStep;
            if (CurrentUpdateOperation.IsTerminal)
                break;
            await Task.Delay(500).ConfigureAwait(true);
        }
        if (CurrentUpdateOperation.Result?.MigrationPlan.Changes.Count > 0)
            SelectedMigrationChange = CurrentUpdateOperation.Result.MigrationPlan.Changes
                .FirstOrDefault(item => CurrentUpdateOperation.Result.MigrationPlan.Conflicts
                    .Any(conflict => conflict.StartsWith(item.RelativePath + ":", StringComparison.OrdinalIgnoreCase)))
                ?? CurrentUpdateOperation.Result.MigrationPlan.Changes[0];
        if (refreshDetails)
        {
            await RefreshAsync().ConfigureAwait(true);
            await LoadUpdateDetailsAsync().ConfigureAwait(true);
        }
        if (CurrentUpdateOperation.Result is { RolledBack: true } rolledBack)
            dialogs.ShowError("Update failed and was rolled back", rolledBack.Message);
        else if (!CurrentUpdateOperation.Success)
            dialogs.ShowError("Update did not complete", CurrentUpdateOperation.Error);
    }

    private async Task LoadVersionsAsync()
    {
        if (SelectedServer is null)
            return;
        Replace(Versions, await client.SendAsync<IReadOnlyList<VersionSnapshot>>("ListVersions",
            new ServerIdRequest(SelectedServer.Definition.Id)).ConfigureAwait(true));
        OnPropertyChanged(nameof(HasVersionHistory));
        CurrentUpdateCheck = (await client.SendAsync<UpdateCheckResponse>("GetLatestUpdateCheck",
            new ServerIdRequest(SelectedServer.Definition.Id)).ConfigureAwait(true)).Check;
        OnPropertyChanged(nameof(IsPendingUpdateValidation));
        OnPropertyChanged(nameof(ActivePackVersion));
        OnPropertyChanged(nameof(ActualPackJava));
        OnPropertyChanged(nameof(RollbackAvailability));
        Replace(UpdateHistory, await client.SendAsync<IReadOnlyList<UpdateHistoryEntry>>("GetUpdateHistory",
            new ServerIdRequest(SelectedServer.Definition.Id)).ConfigureAwait(true));
        SelectedVersion = Versions.FirstOrDefault(item => item.IsActive) ?? Versions.FirstOrDefault();
    }

    private void PopulateLinkFields(UpdateSource source)
    {
        LinkProvider = source.Provider;
        LinkProjectName = source.ProjectName;
        LinkProjectId = source.ProjectId;
        LinkSourceUrl = source.SourceUrl;
        LinkInstalledVersionId = source.InstalledVersionId;
        LinkInstalledVersionName = source.InstalledVersionName;
        LinkInstalledFileId = source.InstalledFileId;
        LinkMinecraftVersion = source.MinecraftVersion;
        LinkLoader = source.Loader;
        LinkLoaderVersion = source.LoaderVersion;
        LinkAssetPattern = source.AssetNamePattern;
        LinkReleaseChannel = source.ReleaseChannel;
    }
}
