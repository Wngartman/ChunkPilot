using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.App.WebUi;

internal sealed record WebUiWorldSourceToken(
    string Token,
    string DisplayName,
    CreationWorldSourceKind Kind,
    string WorldName,
    long SourceSizeBytes,
    long ExpandedSizeBytes,
    int FileCount,
    bool IncludesNether,
    bool IncludesEnd,
    DateTimeOffset ExpiresAt);

/// <summary>Short-lived, single-use native world authority bound to read-only inspection evidence.</summary>
internal sealed class WebUiWorldSourceTokenStore
{
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly CreationWorldSourceService sources;
    private readonly Func<DateTimeOffset> utcNow;

    public WebUiWorldSourceTokenStore(
        CreationWorldSourceService? sources = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.sources = sources ?? new CreationWorldSourceService();
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public WebUiWorldSourceToken Issue(CreationWorldSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RemoveExpired();
        var token = Guid.NewGuid().ToString("N");
        var expiresAt = utcNow().AddMinutes(10);
        entries[token] = new(source, expiresAt);
        return new WebUiWorldSourceToken(
            token,
            Path.GetFileName(source.NativePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            source.Kind,
            source.WorldName,
            source.SourceSizeBytes,
            source.ExpandedSizeBytes,
            source.FileCount,
            !string.IsNullOrWhiteSpace(source.NetherWorldRelativePath),
            !string.IsNullOrWhiteSpace(source.EndWorldRelativePath),
            expiresAt);
    }

    public async Task<CreationWorldSource> ConsumeAsync(string token, CancellationToken cancellationToken)
    {
        RemoveExpired();
        if (!entries.Remove(token, out var entry))
            throw new ArgumentException("The existing-world selection expired. Choose it again.");
        await sources.VerifyUnchangedAsync(entry.Source, cancellationToken).ConfigureAwait(false);
        return entry.Source;
    }

    public void Clear() => entries.Clear();

    private void RemoveExpired()
    {
        var now = utcNow();
        foreach (var token in entries.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
            entries.Remove(token);
    }

    private sealed record Entry(CreationWorldSource Source, DateTimeOffset ExpiresAt);
}
