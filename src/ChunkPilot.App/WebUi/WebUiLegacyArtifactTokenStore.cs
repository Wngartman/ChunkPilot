using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.App.WebUi;

internal sealed record WebUiLegacyArtifactToken(
    string Token,
    string FileName,
    string MinecraftVersion,
    long SizeBytes,
    string Sha256,
    bool MatchesOfficialHash,
    string IdentityEvidence,
    DateTimeOffset ExpiresAt);

/// <summary>Version-bound, single-use native file authority for a user-owned historical server JAR.</summary>
internal sealed class WebUiLegacyArtifactTokenStore
{
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly LegacyServerArtifactInspector inspector;
    private readonly Func<DateTimeOffset> utcNow;

    public WebUiLegacyArtifactTokenStore(
        LegacyServerArtifactInspector? inspector = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.inspector = inspector ?? new LegacyServerArtifactInspector();
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public WebUiLegacyArtifactToken Issue(UserSuppliedServerArtifact artifact)
    {
        RemoveExpired();
        var token = Guid.NewGuid().ToString("N");
        var expiresAt = utcNow().AddMinutes(5);
        entries[token] = new(artifact, expiresAt);
        return new(token, artifact.FileName, artifact.MinecraftVersion, artifact.SizeBytes, artifact.Sha256,
            artifact.MatchesOfficialHash, artifact.IdentityEvidence, expiresAt);
    }

    public async Task<UserSuppliedServerArtifact> ConsumeAsync(
        string versionId, string token, CancellationToken cancellationToken)
    {
        RemoveExpired();
        if (!entries.Remove(token, out var entry) ||
            !entry.Artifact.MinecraftVersion.Equals(versionId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The historical server-file selection expired or belongs to a different Minecraft version. Choose it again.");
        var refreshed = await inspector.InspectAsync(entry.Artifact.NativePath, versionId,
            entry.Artifact.MatchesOfficialHash ? entry.Artifact.Sha1 : "", cancellationToken).ConfigureAwait(false);
        if (refreshed.SizeBytes != entry.Artifact.SizeBytes ||
            !refreshed.Sha256.Equals(entry.Artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected server JAR changed after review. Choose and review it again.");
        return refreshed;
    }

    public void Clear() => entries.Clear();

    private void RemoveExpired()
    {
        var now = utcNow();
        foreach (var key in entries.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
            entries.Remove(key);
    }

    private sealed record Entry(UserSuppliedServerArtifact Artifact, DateTimeOffset ExpiresAt);
}
