using System.Text;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>Transactionally materializes the dev-gated official Terraria server.</summary>
public sealed class TerrariaServerInstaller
{
    private readonly AppDataPaths paths;
    private readonly ITerrariaServerProvider provider;
    private readonly ServerCreationTransaction transaction;

    public TerrariaServerInstaller(
        AppDataPaths paths,
        ChunkPilotStore store,
        ITerrariaServerProvider? provider = null,
        ServerCreationTransaction? transaction = null)
    {
        this.paths = paths;
        this.provider = provider ?? new OfficialTerrariaProvider();
        this.transaction = transaction ?? new ServerCreationTransaction(store);
    }

    public async Task<InstallationResult> InstallAsync(
        TerrariaInstallRequest request,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var instanceRoot = CreationPathSafety.Canonical(
            string.IsNullOrWhiteSpace(request.InstanceRoot) ? paths.ManagedServers : request.InstanceRoot);
        Directory.CreateDirectory(instanceRoot);
        var safeName = ManagedServerInstaller.MakeSafeInstanceName(request.ServerName);
        var destination = Path.Combine(instanceRoot, safeName);
        var staging = Path.Combine(instanceRoot, ServerCreationTransaction.StagingFolderName(request.OperationId));
        var log = Path.Combine(paths.Staging, $"terraria-{request.OperationId:N}.log");
        Directory.CreateDirectory(paths.Staging);
        CreationPathSafety.EnsureWithin(instanceRoot, destination);
        CreationPathSafety.EnsureWithin(instanceRoot, staging);
        var serverId = Guid.NewGuid();
        TerrariaMaterializationResult? materialization = null;

        var result = await transaction.RunAsync(new CreationTransactionRequest
            {
                OperationId = request.OperationId,
                ServerId = serverId,
                ServerName = request.ServerName.Trim(),
                CreationKind = "ExperimentalTerraria",
                InstanceRoot = instanceRoot,
                Destination = destination,
                StagingPath = staging,
                LogPath = log
            },
            async (context, token) =>
            {
                materialization = await provider.DownloadAndMaterializeAsync(
                    paths.Cache, context.StagingPath, progress, token).ConfigureAwait(false);
                var finalWorld = Path.Combine(context.DestinationPath, "Worlds", "chunkpilot-world.wld");
                var configuration = request.Configuration with { WorldPath = finalWorld };
                await TerrariaServerConfigurationWriter.WriteAsync(
                    context.StagingPath, context.DestinationPath, configuration, token).ConfigureAwait(false);
                var release = materialization.Release;
                var definition = new ServerDefinition
                {
                    Id = serverId,
                    Name = request.ServerName.Trim(),
                    RootPath = context.DestinationPath,
                    Executable = Path.Combine(context.DestinationPath, release.Artifact.ExecutableName),
                    Arguments = $"{release.LaunchProfile.ConfigurationArgument} " +
                                CommandLineQuoter.QuoteWindowsArgument(Path.Combine(context.DestinationPath, "serverconfig.txt")),
                    WorkingDirectory = context.DestinationPath,
                    GameKind = ServerGameKind.Terraria,
                    GameVersion = release.Version,
                    Ecosystem = ServerEcosystem.Custom,
                    MinecraftVersion = "",
                    LoaderVersion = release.ReleaseId,
                    SaveCommand = release.LaunchProfile.SaveCommand,
                    SaveFallbackCommand = release.LaunchProfile.SaveCommand,
                    SaveConfirmationPattern = release.LaunchProfile.SaveConfirmationPattern,
                    StopCommand = release.LaunchProfile.StopCommand,
                    ReadinessPattern = release.LaunchProfile.ReadinessPattern,
                    StartupTimeoutSeconds = checked((int)release.LaunchProfile.StartupTimeout.TotalSeconds),
                    Port = configuration.Port,
                    IsManaged = true,
                    ManagedInstanceRoot = instanceRoot,
                    RunInBackground = true,
                    MinimumRamMb = 512,
                    MaximumRamMb = 2_048,
                    CreationNetworkingPreference = request.NetworkingPreference
                };
                return new CreationCandidate(
                    definition,
                    release.ArtifactUrl,
                    materialization.LocalSha256,
                    $"Official Terraria {release.Version} ({release.ReleaseId}); SHA-256 locally calculated by ChunkPilot, not an official checksum.");
            },
            VerifyCandidate,
            progress,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            if (result.Failure is not null) throw result.Failure;
            throw new InvalidOperationException(CreationPhasePolicy.Describe(result.Outcome));
        }
        return new InstallationResult
        {
            Definition = result.Definition!,
            SourceUrl = materialization!.Release.ArtifactUrl,
            Sha256 = materialization.LocalSha256,
            StagingLogPath = log,
            Outcome = result.Outcome,
            Warnings = result.Warnings
        };
    }

    private static void VerifyCandidate(string staging, CreationCandidate candidate)
    {
        if (candidate.Definition.GameKind != ServerGameKind.Terraria ||
            !candidate.Definition.GameVersion.Equals(OfficialTerrariaProvider.CurrentVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The staged Terraria identity does not match the official release.");
        var executable = Path.Combine(staging, "TerrariaServer.exe");
        var config = Path.Combine(staging, "serverconfig.txt");
        if (!File.Exists(executable) || !File.Exists(config))
            throw new InvalidDataException("The staged Terraria server is missing its executable or managed configuration.");
        using var stream = File.OpenRead(executable);
        if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
            throw new InvalidDataException("The staged Terraria executable is not a Windows PE file.");
    }
}

public static class TerrariaServerConfigurationWriter
{
    public static async Task<string> WriteAsync(
        string stagingRoot,
        string finalRoot,
        TerrariaServerConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var canonicalStaging = Path.GetFullPath(stagingRoot);
        var canonicalFinal = Path.TrimEndingDirectorySeparator(Path.GetFullPath(finalRoot));
        var world = Path.GetFullPath(configuration.WorldPath);
        EnsureWithin(canonicalFinal, world, "The Terraria world must remain inside its managed server folder.");
        if (configuration.AutoCreateSize is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(configuration), "Terraria world size must be 1, 2, or 3.");
        if (configuration.Difficulty is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(configuration), "Terraria difficulty must be 0 through 3.");
        if (configuration.MaximumPlayers is < 1 or > 255)
            throw new ArgumentOutOfRangeException(nameof(configuration), "Terraria maximum players must be between 1 and 255.");
        if (configuration.Port is < 1_024 or > 65_535)
            throw new ArgumentOutOfRangeException(nameof(configuration), "Terraria's managed port must be between 1024 and 65535.");
        if (!configuration.BindAddress.Equals("127.0.0.1", StringComparison.Ordinal))
            throw new InvalidOperationException("The experimental Terraria foundation is restricted to loopback.");
        if (configuration.EnableUpnp)
            throw new InvalidOperationException("The experimental Terraria foundation never enables UPnP.");
        ValidateText(configuration.WorldName, nameof(configuration.WorldName), 80, required: true);
        ValidateText(configuration.Motd, nameof(configuration.Motd), 240, required: false);
        ValidateText(configuration.Password, nameof(configuration.Password), 64, required: false);
        ValidateText(configuration.Seed, nameof(configuration.Seed), 128, required: false);

        Directory.CreateDirectory(canonicalStaging);
        var target = Path.Combine(canonicalStaging, "serverconfig.txt");
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        var lines = new List<string>
        {
            "# Managed by ChunkPilot's experimental Terraria foundation.",
            "# The server is bound to loopback and UPnP is disabled.",
            $"world={world}",
            $"autocreate={configuration.AutoCreateSize}",
            $"worldname={configuration.WorldName}",
            $"difficulty={configuration.Difficulty}",
            $"maxplayers={configuration.MaximumPlayers}",
            $"port={configuration.Port}",
            $"motd={configuration.Motd}",
            $"secure={(configuration.Secure ? 1 : 0)}",
            "upnp=0",
            "ip=127.0.0.1"
        };
        if (!string.IsNullOrEmpty(configuration.Seed)) lines.Add($"seed={configuration.Seed}");
        if (!string.IsNullOrEmpty(configuration.Password)) lines.Add($"password={configuration.Password}");
        try
        {
            await File.WriteAllLinesAsync(temporary, lines, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            if (File.Exists(target)) File.Replace(temporary, target, null, true);
            else File.Move(temporary, target);
            return target;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ValidateText(string value, string name, int maximum, bool required)
    {
        if (required && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A value is required.", name);
        if (value.Length > maximum || value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
            throw new ArgumentException("The value contains unsupported control text or is too long.", name);
    }

    private static void EnsureWithin(string root, string candidate, string message)
    {
        var prefix = root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(message);
    }
}
