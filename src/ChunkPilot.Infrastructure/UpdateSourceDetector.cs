using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed class UpdateSourceDetector
{
    public UpdateSourceDetectionResult Detect(ServerDefinition server)
    {
        var evidence = new List<string>();
        var explicitSource = Path.Combine(server.RootPath, ".chunkpilot", "update-source.json");
        if (TryReadSource(explicitSource, server.Id, out var source))
            return Trusted(source!, $"ChunkPilot source manifest: {explicitSource}");

        var prism = Path.Combine(server.RootPath, "instance.cfg");
        if (File.Exists(prism))
        {
            var values = File.ReadLines(prism)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
            if (values.TryGetValue("ManagedPackType", out var type) &&
                values.TryGetValue("ManagedPackID", out var project) &&
                values.TryGetValue("ManagedPackVersionID", out var version))
            {
                var provider = type.Equals("modrinth", StringComparison.OrdinalIgnoreCase)
                    ? UpdateProvider.Modrinth
                    : type.Equals("curseforge", StringComparison.OrdinalIgnoreCase)
                        ? UpdateProvider.CurseForge : UpdateProvider.None;
                if (provider != UpdateProvider.None)
                    return Trusted(new UpdateSource
                    {
                        ServerId = server.Id,
                        Provider = provider,
                        ProjectId = project,
                        InstalledVersionId = version,
                        InstalledVersionName = version,
                        MinecraftVersion = server.MinecraftVersion,
                        Loader = server.Ecosystem.ToString(),
                        LoaderVersion = server.LoaderVersion,
                        DetectionEvidence = prism
                    }, $"Prism managed-pack identity: {prism}");
            }
            evidence.Add("Prism metadata exists but does not provide a complete managed pack identity.");
        }

        var modrinth = Path.Combine(server.RootPath, "modrinth.index.json");
        if (File.Exists(modrinth))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(modrinth));
                var root = document.RootElement;
                var version = root.TryGetProperty("versionId", out var versionId) ? versionId.GetString() ?? "" : "";
                evidence.Add(!string.IsNullOrWhiteSpace(version)
                    ? "modrinth.index.json identifies a pack version, but the standard format contains no Modrinth project or API version identity. Recorded catalog provenance or an exact outer-file hash match is required."
                    : "modrinth.index.json exists but does not identify a pack version or trustworthy provider origin.");
            }
            catch (JsonException exception)
            {
                evidence.Add($"modrinth.index.json could not be parsed: {exception.Message}");
            }
        }

        var atLauncher = Path.Combine(server.RootPath, "instance.json");
        if (File.Exists(atLauncher))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(atLauncher));
                var root = document.RootElement;
                var launcherName = root.TryGetProperty("launcher", out var launcher) &&
                                   launcher.ValueKind == JsonValueKind.Object &&
                                   launcher.TryGetProperty("name", out var name)
                    ? name.GetString() ?? "" : "";
                if (launcherName.Equals("ATLauncher", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("pack", out var pack) &&
                    pack.ValueKind == JsonValueKind.Object)
                {
                    var project = pack.TryGetProperty("id", out var id) ? id.ToString() : "";
                    var version = pack.TryGetProperty("version", out var installed) ? installed.ToString() : "";
                    var providerName = pack.TryGetProperty("provider", out var providerValue)
                        ? providerValue.GetString() ?? "" : "";
                    var provider = providerName.ToLowerInvariant() switch
                    {
                        "modrinth" => UpdateProvider.Modrinth,
                        "curseforge" => UpdateProvider.CurseForge,
                        "github" => UpdateProvider.GitHubReleases,
                        _ => UpdateProvider.None
                    };
                    if (provider != UpdateProvider.None &&
                        !string.IsNullOrWhiteSpace(project) &&
                        !string.IsNullOrWhiteSpace(version))
                        return Trusted(new UpdateSource
                        {
                            ServerId = server.Id,
                            Provider = provider,
                            ProjectName = pack.TryGetProperty("name", out var packName)
                                ? packName.GetString() ?? "" : "",
                            ProjectId = project,
                            InstalledVersionId = version,
                            InstalledVersionName = version,
                            MinecraftVersion = server.MinecraftVersion,
                            Loader = server.Ecosystem.ToString(),
                            LoaderVersion = server.LoaderVersion,
                            DetectionEvidence = atLauncher
                        }, $"ATLauncher provider identity: {atLauncher}");
                    evidence.Add("ATLauncher metadata exists but lacks a supported provider, project ID, or version.");
                }
            }
            catch (JsonException exception)
            {
                evidence.Add($"ATLauncher instance.json could not be parsed: {exception.Message}");
            }
        }

        var curse = Path.Combine(server.RootPath, "manifest.json");
        if (File.Exists(curse))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(curse));
                var root = document.RootElement;
                if (root.TryGetProperty("manifestType", out var type) &&
                    type.GetString()?.Contains("minecraftModpack", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var project = root.TryGetProperty("projectID", out var projectId) ? projectId.ToString() : "";
                    var version = root.TryGetProperty("fileID", out var fileId) ? fileId.ToString() :
                        root.TryGetProperty("version", out var versionName) ? versionName.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(project) && !string.IsNullOrWhiteSpace(version))
                        return Trusted(new UpdateSource
                        {
                            ServerId = server.Id,
                            Provider = UpdateProvider.CurseForge,
                            ProjectName = root.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                            ProjectId = project,
                            InstalledVersionId = version,
                            InstalledVersionName = version,
                            MinecraftVersion = server.MinecraftVersion,
                            Loader = server.Ecosystem.ToString(),
                            LoaderVersion = server.LoaderVersion,
                            DetectionEvidence = curse
                        }, $"CurseForge manifest identity: {curse}");
                    evidence.Add("CurseForge manifest.json exists but lacks the pack project/file identity.");
                }
            }
            catch (JsonException exception)
            {
                evidence.Add($"manifest.json could not be parsed: {exception.Message}");
            }
        }

        var github = Path.Combine(server.RootPath, ".chunkpilot-github-release.json");
        if (TryReadSource(github, server.Id, out source) && source!.Provider == UpdateProvider.GitHubReleases)
            return Trusted(source, $"Installer-recorded GitHub release identity: {github}");

        return new UpdateSourceDetectionResult
        {
            IsTrustworthy = false,
            RequiresBaseline = true,
            Message = "No trustworthy server-pack source was proven. Link an update source and identify the installed baseline before checking.",
            Evidence = evidence
        };
    }

    public static void ValidateLink(UpdateSource source)
    {
        if (source.ServerId == Guid.Empty)
            throw new ArgumentException("A server is required.", nameof(source));
        if (source.Provider == UpdateProvider.None)
            throw new ArgumentException("Choose an update provider.", nameof(source));
        if (source.Provider is UpdateProvider.Modrinth or UpdateProvider.CurseForge &&
            string.IsNullOrWhiteSpace(source.ProjectId))
            throw new ArgumentException("The provider project ID or slug is required.", nameof(source));
        if (source.Provider == UpdateProvider.GitHubReleases)
            _ = GitHubReleasesUpdateProvider.NormalizeRepository(source.ProjectId, source.SourceUrl);
        if (source.Provider == UpdateProvider.DirectManifest)
        {
            var uri = new Uri(source.SourceUrl, UriKind.Absolute);
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("Direct update manifests require HTTPS.", nameof(source));
        }
        if (source.Provider == UpdateProvider.LocalPackageHistory &&
            string.IsNullOrWhiteSpace(source.SourceUrl))
            throw new ArgumentException("Select a local update package.", nameof(source));
    }

    private static bool TryReadSource(string path, Guid serverId, out UpdateSource? source)
    {
        source = null;
        if (!File.Exists(path))
            return false;
        try
        {
            source = JsonSerializer.Deserialize<UpdateSource>(File.ReadAllText(path), ProtocolJson.Options);
            if (source is null || source.Provider == UpdateProvider.None)
                return false;
            source = source with { ServerId = serverId, DetectionEvidence = path };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static UpdateSourceDetectionResult Trusted(UpdateSource source, string evidence) =>
        new()
        {
            Source = source,
            IsTrustworthy = true,
            RequiresBaseline = !source.HasIdentifiedBaseline,
            Message = source.HasIdentifiedBaseline
                ? "A trustworthy update source and installed baseline were detected."
                : "The provider was detected, but the installed baseline must be identified before updates are offered.",
            Evidence = [evidence]
        };
}
