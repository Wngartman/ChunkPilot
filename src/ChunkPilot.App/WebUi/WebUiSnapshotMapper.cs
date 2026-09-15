using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.App.WebUi;

internal sealed class WebUiSnapshotMapper
{
    internal const int MaximumConsoleLines = 2_000;
    internal const long MaximumServerIconBytes = 512 * 1024;
    internal const int MaximumMigrationConflicts = 200;
    internal const int MaximumMigrationChanges = 500;
    private readonly Dictionary<Guid, IconCacheEntry> iconCache = [];
    private long revision;

    internal void InvalidateServerIcon(Guid serverId) => iconCache.Remove(serverId);

    public JsonNode Capture(MainViewModel viewModel)
    {
        var selected = viewModel.SelectedServer;
        var selectedId = selected?.Definition.Id;
        var detailsReady = selectedId is not null && viewModel.WebUiDetailsServerId == selectedId;
        var host = viewModel.Dashboard.Host;
        var pendingValidation = viewModel.Versions.FirstOrDefault(version =>
            version.ServerId == selectedId && version.IsActive &&
            version.Health == VersionHealth.PendingValidation);
        var versions = viewModel.Versions
            .Where(version => selectedId is null || version.ServerId == selectedId)
            .Select(version => new
            {
                id = version.Id,
                version = string.IsNullOrWhiteSpace(version.VersionName)
                    ? (string.IsNullOrWhiteSpace(version.VersionId) ? version.MinecraftVersion : version.VersionId)
                    : version.VersionName,
                platform = string.IsNullOrWhiteSpace(version.Loader)
                    ? (version.SourceProvider == UpdateProvider.None ? version.Definition.Ecosystem.ToString() : version.SourceProvider.ToString())
                    : version.Loader,
                installedAt = (DateTimeOffset?)version.InstalledAt,
                active = version.IsActive,
                verified = version.Verified,
                health = version.Health.ToString(),
                snapshotSizeBytes = version.SnapshotSize,
                includesWorldData = version.IncludesWorldData,
                rollbackReady = !version.IsActive && version.Verified && File.Exists(version.SnapshotPath)
            }).ToArray();

        var snapshot = new
        {
            revision = Interlocked.Increment(ref revision),
            capturedAt = DateTimeOffset.UtcNow,
            agentConnected = viewModel.Dashboard.AgentConnected,
            appVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion.Split('+')[0] ?? "1.0.0",
            build = new
            {
                productVersion = BuildIdentity.Current.ProductVersion,
                releaseTag = BuildIdentity.Current.ReleaseTag,
                gitSha = BuildIdentity.Current.GitSha,
                buildTimestampUtc = BuildIdentity.Current.BuildTimestampUtc,
                schemaVersion = BuildIdentity.Current.SchemaVersion,
                architecture = BuildIdentity.Current.Architecture,
                defaultUi = BuildIdentity.Current.DefaultUi,
                curseForgeApplicationService = CurseForgeServiceConfiguration.Endpoint is not null
            },
            selectedServerId = selectedId,
            workspace = selectedId is null ? null : new
            {
                serverId = selectedId,
                state = detailsReady ? "Ready" : "Loading"
            },
            operation = viewModel.IsBusy ? new
            {
                method = "authoritative-operation",
                serverId = selectedId,
                message = viewModel.StatusMessage
            } : null,
            statusMessage = string.IsNullOrWhiteSpace(viewModel.StatusMessage) ? null : viewModel.StatusMessage,
            host = new
            {
                cpuPercent = (double?)host.CpuPercent,
                usedMemoryBytes = (long?)host.UsedMemoryBytes,
                totalMemoryBytes = (long?)host.TotalMemoryBytes,
                freeDiskBytes = (long?)host.FreeDiskBytes,
                totalDiskBytes = (long?)host.TotalDiskBytes,
                cpuModel = string.IsNullOrWhiteSpace(host.CpuModel) ? null : host.CpuModel
            },
            servers = viewModel.Servers.Select(server => MapServer(server, host, viewModel, selectedId, detailsReady)).ToArray(),
            connectivity = detailsReady ? MapConnectivity(viewModel, selectedId) : null,
            playerAccess = detailsReady ? MapPlayerAccess(viewModel, selectedId) : null,
            issues = detailsReady ? MapHealthIssues(viewModel, selected) : [],
            console = viewModel.ConsoleLines.TakeLast(MaximumConsoleLines).Select(line => new
            {
                sequence = line.Sequence,
                timestamp = line.Timestamp,
                stream = line.Stream,
                text = line.Text
            }).ToArray(),
            players = viewModel.PlayerRows.Where(_ => detailsReady).Select(player => new
            {
                name = player.Name,
                uuid = player.Uuid,
                online = player.Online,
                allowlisted = player.Whitelisted,
                @operator = player.Operator,
                banned = player.Banned
            }).ToArray(),
            files = viewModel.FileEntries.Where(_ => detailsReady).Select(file => new
            {
                name = file.Name,
                relativePath = file.RelativePath,
                kind = MapFileKind(file),
                sizeBytes = file.IsDirectory ? (long?)null : file.SizeBytes,
                modifiedAt = file.ModifiedAt == default ? (DateTimeOffset?)null : file.ModifiedAt
            }).ToArray(),
            plugins = viewModel.Inventory.Where(_ => detailsReady).Select(item =>
            {
                var load = PluginLoadEvidence(item, selected?.State, viewModel.ConsoleLines);
                return new
                {
                    name = item.Name,
                    fileName = item.FileName,
                    relativePath = item.RelativePath,
                    version = item.Version,
                    id = item.Id,
                    loader = item.Loader,
                    sizeBytes = item.SizeBytes,
                    modifiedAt = item.ModifiedAt,
                    enabled = item.Enabled,
                    duplicateId = item.DuplicateId,
                    dependencies = item.Dependencies,
                    dependencyDetails = item.DependencyDetails.Select(dependency => new
                    {
                        id = dependency.Id,
                        kind = dependency.Kind.ToString()
                    }).ToArray(),
                    compatibility = item.Compatibility.ToString(),
                    compatibilityReason = item.CompatibilityReason,
                    loadState = load.State,
                    loadEvidence = load.Detail,
                    installSource = item.InstallSource,
                    provider = item.Provider?.ToString(),
                    providerProjectId = item.ProviderProjectId,
                    providerVersionId = item.ProviderVersionId,
                    sha256 = item.Sha256,
                    clientRequirement = item.ClientRequirement
                };
            }).ToArray(),
            currentFolder = detailsReady ? viewModel.CurrentFolder : "",
            schedules = viewModel.Schedules.Where(schedule => selectedId is null || schedule.ServerId == selectedId).Select(schedule => new
            {
                id = schedule.Id,
                serverId = schedule.ServerId,
                name = schedule.Name,
                action = schedule.Action.ToString(),
                kind = schedule.Kind.ToString(),
                intervalMinutes = schedule.IntervalMinutes,
                at = schedule.OneTimeAt?.ToString("O") ?? schedule.TimeOfDay.ToString(@"hh\:mm"),
                cron = schedule.CronExpression,
                command = schedule.Command,
                enabled = schedule.Enabled,
                nextRunAt = schedule.NextRunAt,
                lastRunAt = schedule.LastRunAt,
                backupBeforeRestart = schedule.BackupBeforeRestart,
                restartCountdownSeconds = schedule.RestartCountdownSeconds
            }).ToArray(),
            backups = viewModel.Backups.Where(backup => selectedId is null || backup.ServerId == selectedId).Select(backup => new
            {
                id = backup.Id,
                createdAt = backup.CreatedAt,
                description = backup.Description,
                sizeBytes = backup.SizeBytes,
                verified = backup.Verified,
                source = backup.Source
            }).ToArray(),
            versions,
            update = selected is null || !detailsReady ? null : new
            {
                status = viewModel.UpdateStatusText,
                detail = viewModel.UpdateStatusDetail,
                sourceLinked = viewModel.CurrentUpdateSource is not null,
                provider = viewModel.CurrentUpdateSource?.Provider.ToString(),
                projectId = viewModel.CurrentUpdateSource?.ProjectId,
                projectName = viewModel.CurrentUpdateSource?.ProjectName,
                installedVersionId = viewModel.CurrentUpdateSource?.InstalledVersionId,
                installedVersionName = viewModel.CurrentUpdateSource?.InstalledVersionName,
                releaseChannel = viewModel.CurrentUpdateSource?.ReleaseChannel.ToString(),
                minecraftVersion = viewModel.CurrentUpdateSource?.MinecraftVersion,
                loader = viewModel.CurrentUpdateSource?.Loader,
                loaderVersion = viewModel.CurrentUpdateSource?.LoaderVersion,
                checkedAt = viewModel.CurrentUpdateCheck?.CheckedAt,
                targetVersionId = viewModel.CurrentUpdateCheck?.LatestVersion?.VersionId,
                latestVersionName = viewModel.CurrentUpdateCheck?.LatestVersion?.VersionName,
                targetPublishedAt = viewModel.CurrentUpdateCheck?.LatestVersion?.PublishedAt,
                downloadSizeBytes = viewModel.CurrentUpdateCheck?.LatestVersion?.FileSize,
                compatibilityReasons = viewModel.CurrentUpdateCheck?.CompatibilityReasons ?? [],
                compatibility = viewModel.CurrentUpdateCheck?.Compatibility.ToString(),
                canInstall = IsInstallableUpdate(viewModel.CurrentUpdateCheck),
                operationState = viewModel.CurrentUpdateOperation?.Progress.State.ToString(),
                operationStep = viewModel.CurrentUpdateOperation?.Progress.CurrentStep,
                operationDetail = viewModel.CurrentUpdateOperation?.Progress.Detail,
                operationPercent = viewModel.CurrentUpdateOperation is null ? (double?)null : viewModel.CurrentUpdateOperation.Progress.Percent,
                cancellable = viewModel.CurrentUpdateOperation is { IsTerminal: false },
                pendingValidation = pendingValidation is null ? null : new
                {
                    serverId = pendingValidation.ServerId,
                    versionId = pendingValidation.Id,
                    versionName = pendingValidation.VersionName
                },
                migrationReview = MapMigrationReview(
                    selectedId,
                    viewModel.CurrentUpdateCheck?.LatestVersion?.VersionId,
                    viewModel.CurrentUpdateOperation)
            },
            activity = viewModel.Activity.Select(activity => new
            {
                id = activity.Id,
                timestamp = activity.Timestamp,
                serverId = activity.ServerId,
                serverName = activity.ServerName,
                action = activity.Action,
                result = activity.Result,
                error = string.IsNullOrWhiteSpace(activity.Error) ? null : activity.Error,
                durationMs = activity.DurationMilliseconds
            }).ToArray(),
            settings = new
            {
                minimizeToTray = viewModel.MinimizeToTray,
                startMinimized = viewModel.StartMinimized,
                startWithWindows = viewModel.StartWithWindows,
                reducedMotion = viewModel.ReducedMotion
            },
            serverSettings = selected is null || !detailsReady ? null : new
            {
                serverId = selected.Definition.Id,
                name = selected.Definition.Name,
                motd = viewModel.PropertyMotd,
                port = viewModel.PropertyPort,
                maximumPlayers = viewModel.PropertyMaxPlayers,
                difficulty = viewModel.PropertyDifficulty,
                gameMode = viewModel.PropertyGameMode,
                pvp = viewModel.PropertyPvp,
                allowlist = viewModel.PropertyWhiteList,
                minimumRamMb = selected.Definition.MinimumRamMb,
                maximumRamMb = selected.Definition.MaximumRamMb,
                runInBackground = selected.Definition.RunInBackground
            }
        };

        return JsonSerializer.SerializeToNode(snapshot, WebUiProtocol.Json)!;
    }

    internal static WebUiMigrationReview? MapMigrationReview(
        Guid? selectedServerId,
        string? targetVersionId,
        UpdateOperationSnapshot? operation)
    {
        if (selectedServerId is null || string.IsNullOrWhiteSpace(targetVersionId) ||
            operation is not { IsTerminal: true, Success: false, Result: { } result } ||
            operation.Progress.State != UpdateOperationState.PlanningMigration ||
            result.ServerId != selectedServerId.Value ||
            !result.TargetVersionId.Equals(targetVersionId, StringComparison.Ordinal) ||
            !result.MigrationPlan.RequiresManualReview)
            return null;

        var plan = result.MigrationPlan;
        var conflictPaths = MigrationConflictPaths(plan);
        var conflictPathSet = conflictPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allConflictsMapped = conflictPaths.Count == plan.Conflicts.Count &&
            conflictPaths.Count == conflictPathSet.Count &&
            conflictPaths.All(path => plan.Changes.Any(change =>
                change.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase)));
        var canResolve = allConflictsMapped && conflictPaths.Count <= MaximumMigrationConflicts;
        var changes = plan.Changes
            .OrderByDescending(change => conflictPathSet.Contains(change.RelativePath))
            .ThenBy(change => change.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumMigrationChanges)
            .Select(change => new WebUiMigrationChange(
                change.RelativePath,
                change.Ownership.ToString(),
                change.Change,
                change.Reason,
                change.OldSha256,
                change.NewSha256,
                conflictPathSet.Contains(change.RelativePath)))
            .ToArray();
        var truncated = changes.Length < plan.Changes.Count || plan.Conflicts.Count > MaximumMigrationConflicts;
        var detail = canResolve
            ? "Choose Keep installed copy or Use new pack for every conflict. The verified rollback snapshot keeps the previous state recoverable."
            : "This migration review is too large or internally inconsistent to resolve in the WebUI. No active-version switch was attempted.";
        return new WebUiMigrationReview(
            operation.OperationId,
            selectedServerId.Value,
            targetVersionId,
            plan.Conflicts.Count,
            plan.Changes.Count,
            conflictPaths.Take(MaximumMigrationConflicts).ToArray(),
            changes,
            truncated,
            canResolve,
            detail);
    }

    internal static IReadOnlyList<string> MigrationConflictPaths(MigrationPlan plan) => plan.Conflicts
        .Select(conflict =>
        {
            var separator = conflict.IndexOf(':');
            return separator <= 0 ? "" : conflict[..separator].Replace('\\', '/').TrimStart('/');
        })
        .Where(path => path.Length > 0)
        .ToArray();

    internal static (string State, string Detail) PluginLoadEvidence(
        ModPluginEntry plugin,
        ServerState? state,
        IEnumerable<ConsoleLine> console)
    {
        if (!plugin.Enabled)
            return ("Disabled", "The JAR is in ChunkPilot's disabled plugin storage and cannot load.");
        var relevant = console.TakeLast(MaximumConsoleLines)
            .Select(line => line.Text)
            .Where(text => text.Contains(plugin.Name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (relevant.Any(text => text.Contains("Error occurred while enabling", StringComparison.OrdinalIgnoreCase) ||
                                 text.Contains("Could not load", StringComparison.OrdinalIgnoreCase) ||
                                 text.Contains("Failed to load", StringComparison.OrdinalIgnoreCase) ||
                                 text.Contains("InvalidPluginException", StringComparison.OrdinalIgnoreCase)))
            return ("Failed", "The current server log contains an explicit load or enable failure for this plugin.");
        if (relevant.Any(text => text.Contains($"Enabling {plugin.Name} v", StringComparison.OrdinalIgnoreCase) ||
                                 text.Contains($"[{plugin.Name}] Loading", StringComparison.OrdinalIgnoreCase) ||
                                 text.Contains($"[{plugin.Name}] Enabled", StringComparison.OrdinalIgnoreCase)))
            return ("Loaded", "The current server log contains an explicit load or enable line for this plugin.");
        if (state is ServerState.Starting or ServerState.Stopping)
            return ("Pending", "ChunkPilot is waiting for the current lifecycle operation to finish before evaluating load evidence.");
        return state == ServerState.Running
            ? ("Unknown", "The JAR is active, but the bounded current server log contains no explicit load or failure evidence.")
            : ("Not running", "Start the server to collect plugin load evidence. The active JAR alone is not proof that it loaded.");
    }

    private static string MapFileKind(FileSystemEntry file) =>
        !file.IsDirectory && file.SizeBytes > 160 * 1024
            ? "too-large"
            : ServerFilePolicy.Classify(file.Name, file.IsDirectory, file.SizeBytes) switch
    {
        ServerFileKind.Folder => "folder",
        ServerFileKind.EditableText => "editable",
        ServerFileKind.TooLarge => "too-large",
        _ => "binary"
    };

    private object MapServer(ServerSnapshot server, HostSnapshot host, MainViewModel viewModel, Guid? selectedId,
        bool selectedDetailsReady)
    {
        var isSelected = selectedId == server.Definition.Id;
        var network = viewModel.Dashboard.NetworkConfigurations.FirstOrDefault(item =>
            item.ServerId == server.Definition.Id);
        var router = viewModel.Dashboard.RouterMappings.FirstOrDefault(item =>
            item.ServerId == server.Definition.Id);
        var networkMode = network?.Mode ??
            VanillaNetworkingPreferencePolicy.ToNetworkMode(server.Definition.CreationNetworkingPreference);
        var packSource = isSelected && selectedDetailsReady && viewModel.CurrentUpdateSource is
        {
            HasIdentifiedBaseline: true,
            Provider: UpdateProvider.Modrinth or UpdateProvider.CurseForge or UpdateProvider.LocalPackageHistory
        }
            ? viewModel.CurrentUpdateSource
            : null;
        var content = packSource is not null ? "modpack" : server.Definition.Ecosystem switch
        {
            ServerEcosystem.Vanilla => "datapacks",
            ServerEcosystem.Paper or ServerEcosystem.Purpur or ServerEcosystem.Spigot or ServerEcosystem.Bukkit => "plugins",
            ServerEcosystem.Fabric or ServerEcosystem.Quilt or ServerEcosystem.Forge or ServerEcosystem.NeoForge => "mods",
            _ => "unsupported"
        };
        var versioning = VersioningCapability(server.Definition.Ecosystem);
        var connection = ConnectionSummary(viewModel, server);
        return new
        {
            id = server.Definition.Id,
            name = server.Definition.Name,
            state = server.State.ToString(),
            startupProgress = server.StartupProgress,
            gameKind = server.Definition.GameKind.ToString(),
            ecosystem = server.Definition.Ecosystem.ToString(),
            minecraftVersion = server.Definition.MinecraftVersion,
            loaderVersion = string.IsNullOrWhiteSpace(server.Definition.LoaderVersion) ? null : server.Definition.LoaderVersion,
            port = server.Definition.Port,
            managed = server.Definition.IsManaged,
            playersOnline = server.OnlinePlayers,
            playersMaximum = server.MaxPlayers,
            playerStatus = server.PlayerStatus is null ? null : new
            {
                online = server.PlayerStatus.Online,
                maximum = server.PlayerStatus.Maximum,
                source = server.PlayerStatus.Source.ToString(),
                exact = server.PlayerStatus.Exact,
                checkedAt = server.PlayerStatus.CheckedAt,
                detail = server.PlayerStatus.Detail
            },
            uptimeSeconds = server.StartedAt is null ? (double?)null : server.Uptime.TotalSeconds,
            cpuPercent = server.CurrentStatistics?.CpuPercent,
            memoryBytes = server.CurrentStatistics?.WorkingSetBytes,
            maximumMemoryBytes = (long)server.Definition.MaximumRamMb * 1024 * 1024,
            localAddress = connection.LocalAddress ?? (server.State == ServerState.Stopped ? connection.ConfiguredLocalAddress : null),
            lanAddress = connection.LanAddress ?? (server.State == ServerState.Stopped ? connection.ConfiguredLanAddress : null),
            connectionMode = networkMode.ToString(),
            connection,
            publicAddress = connection.Kind is "public" or "router" or "last" ? connection.Address : null,
            publicAddressKind = connection.Kind == "public" ? "verified" : connection.Kind is "router" or "last" ? connection.Kind : null,
            publicAddressObservedAt = connection.PublicVerifiedAddress is not null ? viewModel.ExternalReachability.CheckedAt : router?.LastCheckedAt,
            publicReachability = connection.PublicVerifiedAddress is not null
                ? "confirmed"
                : networkMode == NetworkMode.PortForwarding
                    ? "not-confirmed"
                    : "unavailable",
            lastBackupAt = server.LastBackupAt,
            lastError = string.IsNullOrWhiteSpace(server.LastError) ? null : server.LastError,
            crashAnalysis = server.LastCrashAnalysis is null ? null : new
            {
                reportId = server.LastCrashAnalysis.ReportId,
                analyzedAt = server.LastCrashAnalysis.AnalyzedAt,
                exitCode = server.LastCrashAnalysis.ExitCode,
                code = server.LastCrashAnalysis.Code,
                title = server.LastCrashAnalysis.Title,
                summary = server.LastCrashAnalysis.Summary,
                confidence = server.LastCrashAnalysis.Confidence.ToString(),
                reachedReadiness = server.LastCrashAnalysis.ReachedReadiness,
                serverIdentity = server.LastCrashAnalysis.ServerIdentity,
                runtimeIdentity = server.LastCrashAnalysis.RuntimeIdentity,
                activeOperation = string.IsNullOrWhiteSpace(server.LastCrashAnalysis.ActiveOperation)
                    ? null
                    : server.LastCrashAnalysis.ActiveOperation,
                evidence = server.LastCrashAnalysis.Evidence.Select(item => new
                {
                    source = item.Source,
                    excerpt = item.Excerpt
                }).ToArray(),
                recommendedSteps = server.LastCrashAnalysis.RecommendedSteps,
                safeActions = server.LastCrashAnalysis.SafeActions.Select(action => new
                {
                    code = action.Code,
                    label = action.Label,
                    detail = action.Detail
                }).ToArray()
            },
            iconUrl = ReadServerIcon(server.Definition),
            modpack = packSource is null ? null : new
            {
                provider = packSource.Provider.ToString(),
                projectId = packSource.ProjectId,
                projectName = packSource.ProjectName,
                versionId = packSource.InstalledVersionId,
                versionName = packSource.InstalledVersionName
            },
            samples = server.RecentStatistics.Select(sample => new
            {
                at = sample.Timestamp,
                cpuPercent = sample.CpuPercent,
                memoryBytes = sample.WorkingSetBytes
            }).ToArray(),
            capabilities = new
            {
                console = true,
                players = HasPlayersWorkspace(server.Definition),
                files = true,
                content,
                versioning,
                backups = true,
                versions = true
            }
        };
    }

    internal static bool IsInstallableUpdate(UpdateCheckResult? check) =>
        check is { Status: ServerUpdateStatus.UpdateAvailable, LatestVersion: not null } &&
        check.Compatibility is not UpdateCompatibility.Incompatible and not UpdateCompatibility.Unknown;

    internal static bool HasPlayersWorkspace(ServerDefinition definition) =>
        definition.GameKind == ServerGameKind.Minecraft;

    private static object? MapPlayerAccess(MainViewModel viewModel, Guid? selectedId)
    {
        if (selectedId is null || viewModel.SelectedServer?.Definition is not { } definition ||
            definition.Id != selectedId || !HasPlayersWorkspace(definition))
            return null;

        var capabilities = viewModel.SelectedCapabilities;
        return new
        {
            serverId = selectedId,
            serverRunning = viewModel.AccessServerRunning,
            canManageWhileStopped = viewModel.PlayerAccessWhileStoppedAvailable,
            accessAvailabilityDetail = viewModel.AccessAvailabilityDetail,
            whitelistEnabled = viewModel.WhitelistEnabled,
            supportsAllowlist = capabilities?.SupportsLiveWhitelistCommands ?? false,
            supportsOperators = capabilities?.SupportsOperators ?? false,
            supportsPlayerBans = capabilities?.SupportsPlayerBans ?? false,
            supportsIpBans = capabilities?.SupportsIpBans ?? false,
            capabilityKnown = capabilities is not null,
            error = string.IsNullOrWhiteSpace(viewModel.AccessErrorMessage) ? null : viewModel.AccessErrorMessage
        };
    }

    internal static string VersioningCapability(ServerEcosystem ecosystem) => ecosystem switch
    {
        ServerEcosystem.Paper => "paper",
        ServerEcosystem.Vanilla => "vanilla",
        ServerEcosystem.Fabric => "fabric",
        ServerEcosystem.Quilt => "quilt",
        ServerEcosystem.Forge => "forge",
        ServerEcosystem.NeoForge => "neoforge",
        _ => "unsupported"
    };

    private static object? MapConnectivity(MainViewModel viewModel, Guid? selectedId)
    {
        if (selectedId is null || viewModel.SelectedServer?.Definition.Id != selectedId)
            return null;

        var server = viewModel.SelectedServer;
        var router = viewModel.RouterMapping;
        var firewall = viewModel.FirewallAccess;
        var external = viewModel.ExternalReachability;
        var mode = viewModel.SelectedNetworkMode;
        var publicVerified = external.ServerId == selectedId && viewModel.PublicAccessVerified;
        var publicEndpoint = publicVerified ? viewModel.PublicAccessVerifiedEndpoint : null;
        var lastKnownPublicEndpoint = external.ServerId == selectedId && external.CheckedAt is not null &&
            external.CheckedEndpoint.PublicAddress.Length > 0 && external.CheckedEndpoint.ExternalPort > 0
                ? $"{external.CheckedEndpoint.PublicAddress}:{external.CheckedEndpoint.ExternalPort}"
                : null;
        var effectiveMode = mode;
        var modeTitle = effectiveMode switch { NetworkMode.PortForwarding => "Internet hosting", NetworkMode.HomeNetwork => "LAN", NetworkMode.ThisComputerOnly => "This computer", _ => "Not selected" };
        var modeSummary = effectiveMode switch
        {
            NetworkMode.PortForwarding => "Internet hosting is requested. Binding, Windows and router setup, and outside-in reachability are separate facts.",
            NetworkMode.HomeNetwork => "LAN hosting is requested. The effective binding and current listener determine which address can be used.",
            NetworkMode.ThisComputerOnly => "Only this computer is requested; no broader audience is inferred.",
            _ => "An audience has not been selected. Effective connection evidence is shown separately."
        };
        var connection = ConnectionSummary(viewModel, server);

        return new
        {
            serverId = selectedId,
            mode = effectiveMode.ToString(),
            modeTitle,
            modeSummary,
            connection,
            status = new { title = connection.Badge, detail = connection.Explanation, tone = connection.Tone },
            addresses = new
            {
                local = connection.LocalAddress ?? (server.State == ServerState.Stopped ? connection.ConfiguredLocalAddress : null),
                lan = connection.LanAddress ?? (server.State == ServerState.Stopped ? connection.ConfiguredLanAddress : null),
                publicVerified = connection.PublicVerifiedAddress,
                routerReported = router.ServerId == selectedId && router.HasRouterReportedAddress
                    ? router.RouterReportedEndpoint
                    : null,
                publicVerifiedAt = publicVerified ? external.CheckedAt : null,
                lastKnownPublic = lastKnownPublicEndpoint,
                lastKnownPublicAt = lastKnownPublicEndpoint is null ? null : external.CheckedAt
            },
            router = new
            {
                phase = router.ServerId == selectedId ? router.Phase.ToString() : RouterMappingPhase.Off.ToString(),
                title = viewModel.DirectInternetTitle,
                summary = viewModel.DirectInternetSummary,
                badge = viewModel.DirectInternetBadge,
                tone = Tone(viewModel.DirectInternetTone),
                busy = viewModel.IsDirectInternetBusy,
                enabled = router.ServerId == selectedId && router.Enabled,
                consentRequired = viewModel.ShowsDirectInternetConsent,
                consentPoints = viewModel.DirectInternetConsentPoints,
                canCheck = viewModel.ShowsDirectInternetPrimaryAction,
                canEnable = router.ServerId == selectedId && router.Phase == RouterMappingPhase.Supported && !router.Enabled,
                canStop = viewModel.ShowsDirectInternetTurnOff,
                canCancel = viewModel.ShowsDirectInternetCancel,
                canRetryCleanup = viewModel.ShowsDirectInternetRetry,
                routerReportedCaveat = viewModel.RouterReportedAddressCaveat,
                upstreamNotice = viewModel.ShowsUpstreamNetworkNotice ? viewModel.UpstreamNetworkNotice : null,
                mechanism = viewModel.DirectInternetMechanismLabel,
                transport = viewModel.DirectInternetTransportLabel,
                gateway = viewModel.DirectInternetGateway,
                internalEndpoint = viewModel.DirectInternetInternalEndpoint,
                externalPort = viewModel.DirectInternetExternalPortLabel,
                lease = viewModel.DirectInternetLeaseLabel,
                lastChecked = viewModel.DirectInternetLastCheckedLabel,
                addressClass = viewModel.DirectInternetAddressClassLabel,
                detail = viewModel.DirectInternetTechnicalDetail
            },
            firewall = new
            {
                phase = firewall.ServerId == selectedId ? firewall.Phase.ToString() : FirewallAccessPhase.NotChecked.ToString(),
                title = viewModel.FirewallTitle,
                summary = viewModel.FirewallSummary,
                badge = viewModel.FirewallBadge,
                tone = Tone(viewModel.FirewallTone),
                busy = firewall.ServerId == selectedId && firewall.Busy,
                configured = firewall.ServerId == selectedId && firewall.Configured,
                consentRequired = viewModel.ShowsFirewallConsent,
                consentTitle = viewModel.FirewallConsentTitle,
                consentMessage = viewModel.FirewallConsentMessage,
                primaryAction = viewModel.ShowsFirewallPrimaryAction ? viewModel.FirewallPrimaryActionText : null,
                secondaryAction = viewModel.ShowsFirewallSecondaryAction ? viewModel.FirewallSecondaryActionText : null,
                canRemove = viewModel.ShowsFirewallRemoveAction,
                canCancel = viewModel.ShowsFirewallCancelAction,
                network = viewModel.FirewallNetworkDisplay,
                port = viewModel.FirewallPortDisplay,
                profile = viewModel.FirewallProfileDisplay,
                enabled = viewModel.FirewallEnabledDisplay,
                lastChecked = viewModel.FirewallLastCheckedDisplay,
                detail = viewModel.FirewallTechnicalDetail
            },
            external = new
            {
                phase = external.ServerId == selectedId ? external.Phase.ToString() : ExternalReachabilityPhase.NotChecked.ToString(),
                blocker = external.ServerId == selectedId ? external.Blocker.ToString() : ExternalReachabilityBlocker.None.ToString(),
                title = viewModel.ExternalReachabilityTitle,
                summary = viewModel.ExternalReachabilitySummary,
                badge = viewModel.ExternalReachabilityBadge,
                tone = Tone(viewModel.ExternalReachabilityTone),
                busy = external.ServerId == selectedId && external.Busy,
                canCheck = external.ServerId == selectedId && viewModel.IsExternalReachabilityCheckEnabled,
                canCancel = external.ServerId == selectedId && viewModel.ShowsExternalReachabilityCancel,
                firstUseNotice = viewModel.ShowsExternalReachabilityFirstUseNotice
                    ? viewModel.ExternalReachabilityFirstUseNotice
                    : null,
                verifiedEndpoint = publicEndpoint,
                verifiedAt = publicVerified ? external.CheckedAt : null,
                checkedAt = viewModel.ExternalReachabilityCheckedAtLabel,
                observedAddress = viewModel.ExternalReachabilityObservedAddress,
                routerAddress = viewModel.ExternalReachabilityRouterAddress,
                port = viewModel.ExternalReachabilityPortLabel,
                connectTime = viewModel.ExternalReachabilityConnectTimeLabel,
                addressComparison = viewModel.ShowsExternalReachabilityAddressComparison
                    ? viewModel.ExternalReachabilityAddressComparison
                    : null,
                upstreamAssessment = viewModel.ShowsExternalReachabilityUpstreamAssessment
                    ? viewModel.ExternalReachabilityUpstreamAssessment
                    : null,
                detail = viewModel.ExternalReachabilityTechnicalDetail
            }
        };
    }

    private static IReadOnlyList<WebUiHealthIssue> MapHealthIssues(MainViewModel viewModel, ServerSnapshot? server)
    {
        if (server is null)
            return [];
        var issues = new List<WebUiHealthIssue>(2);
        if (server.State is ServerState.Crashed or ServerState.Unresponsive && server.LastCrashAnalysis is { } crash)
        {
            var articleId = crash.Code switch
            {
                "java.memory" => "java-heap-out-of-memory",
                "java.version" => "java-runtime-mismatch",
                "port.conflict" or "network.bind" => "port-binding-failed",
                "server.watchdog" => "server-watchdog-timeout",
                _ => "server-stopped-unexpectedly"
            };
            issues.Add(new WebUiHealthIssue(
                $"crash:{articleId}", server.Definition.Id, articleId, "startup", "error",
                crash.Title, crash.Summary,
                crash.Evidence.Count > 0 ? crash.Evidence[0].Excerpt : crash.Summary,
                crash.AnalyzedAt, crash.AnalyzedAt,
                $"{crash.ReportId:N}:{crash.Code}:{crash.AnalyzedAt:O}", "openConsole", true));
        }

        var running = server.State is ServerState.Running or ServerState.Starting or ServerState.Restarting;
        if (running && viewModel.SelectedNetworkMode == NetworkMode.PortForwarding)
        {
            var routerProblem = viewModel.RouterMapping.Phase is RouterMappingPhase.Conflict or
                RouterMappingPhase.NeedsAttention or RouterMappingPhase.Unavailable or RouterMappingPhase.Undetermined;
            var firewallProblem = viewModel.FirewallAccess.Phase is FirewallAccessPhase.NeedsAttention or
                FirewallAccessPhase.Stale or FirewallAccessPhase.BlockedByPolicy or FirewallAccessPhase.OwnershipConflict;
            if (routerProblem || firewallProblem)
            {
                var observed = viewModel.RouterMapping.LastCheckedAt ?? viewModel.FirewallAccess.LastCheckedAt ?? DateTimeOffset.UtcNow;
                var phase = routerProblem ? viewModel.RouterMapping.Phase.ToString() : viewModel.FirewallAccess.Phase.ToString();
                var evidence = routerProblem ? viewModel.DirectInternetSummary : viewModel.FirewallSummary;
                issues.Add(new WebUiHealthIssue(
                    "internet-sharing", server.Definition.Id,
                    routerProblem ? "router-port-forwarding-needs-attention" : "windows-firewall-needs-attention",
                    "networking", "warning", "Internet sharing needs attention", evidence, evidence,
                    observed, observed, $"{server.Definition.Port}:{phase}:{evidence}", "openConnectivity", true));
            }
        }
        return issues;
    }

    internal static ServerConnectionSummary ConnectionSummary(MainViewModel viewModel, ServerSnapshot server)
    {
        var id = server.Definition.Id;
        var network = viewModel.Dashboard.NetworkConfigurations.FirstOrDefault(item => item.ServerId == id);
        var router = viewModel.Dashboard.RouterMappings.FirstOrDefault(item => item.ServerId == id);
        var selected = viewModel.SelectedServer?.Definition.Id == id;
        var mode = network?.Mode ??
            VanillaNetworkingPreferencePolicy.ToNetworkMode(server.Definition.CreationNetworkingPreference);
        return ServerConnectionSummaryPolicy.Build(server, mode, viewModel.Dashboard.Host.LanAddress, DateTimeOffset.UtcNow,
            firewallConfigured: selected && viewModel.FirewallAccess.ServerId == id && viewModel.FirewallAccess.Configured,
            routerConfigured: router?.HasActiveMapping == true,
            routerAddress: router is { RouterReportedExternalAddress.Length: > 0, ExternalPort: > 0 }
                ? $"{router.RouterReportedExternalAddress}:{router.ExternalPort}" : null,
            publicVerifiedAddress: selected && viewModel.ExternalReachability.ServerId == id && viewModel.PublicAccessVerified
                ? viewModel.PublicAccessVerifiedEndpoint : null,
            lastPublicAddress: selected && viewModel.ExternalReachability.ServerId == id &&
                viewModel.ExternalReachability.CheckedAt is not null &&
                viewModel.ExternalReachability.CheckedEndpoint is { PublicAddress.Length: > 0, ExternalPort: > 0 } last
                    ? $"{last.PublicAddress}:{last.ExternalPort}" : null);
    }

    private static string Tone(ChunkPilot.App.DesignSystem.AppTone tone) => tone.ToString().ToLowerInvariant();

    internal string? ReadServerIcon(ServerDefinition server)
    {
        var path = Path.Combine(server.RootPath, "server-icon.png");
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > MaximumServerIconBytes)
            {
                iconCache.Remove(server.Id);
                return null;
            }
            if (iconCache.TryGetValue(server.Id, out var cached) &&
                cached.Length == file.Length && cached.LastWriteTimeUtc == file.LastWriteTimeUtc)
                return cached.DataUrl;

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
            {
                iconCache.Remove(server.Id);
                return null;
            }
            var dataUrl = $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
            iconCache[server.Id] = new IconCacheEntry(file.Length, file.LastWriteTimeUtc, dataUrl);
            return dataUrl;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            iconCache.Remove(server.Id);
            return null;
        }
    }

    private sealed record IconCacheEntry(long Length, DateTime LastWriteTimeUtc, string DataUrl);

    private sealed record WebUiHealthIssue(
        string IssueId,
        Guid ServerId,
        string ArticleId,
        string Category,
        string Severity,
        string Title,
        string Summary,
        string EvidenceSummary,
        DateTimeOffset FirstObservedAt,
        DateTimeOffset LastObservedAt,
        string EvidenceFingerprint,
        string PrimaryAction,
        bool Dismissible);
}

internal sealed record WebUiMigrationChange(
    string RelativePath,
    string Ownership,
    string Change,
    string Reason,
    string OldSha256,
    string NewSha256,
    bool RequiresResolution);

internal sealed record WebUiMigrationReview(
    Guid ReviewOperationId,
    Guid ServerId,
    string TargetVersionId,
    int ConflictCount,
    int ChangeCount,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<WebUiMigrationChange> Changes,
    bool Truncated,
    bool CanResolve,
    string Detail);
