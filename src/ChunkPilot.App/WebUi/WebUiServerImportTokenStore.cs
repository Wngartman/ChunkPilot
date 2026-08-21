using ChunkPilot.Core;

namespace ChunkPilot.App.WebUi;

internal sealed record WebUiServerImportToken(string Token, string FileName, DateTimeOffset ExpiresAt);

/// <summary>Short-lived, single-use native selections bound to their reviewed source identity.</summary>
internal sealed class WebUiServerImportTokenStore
{
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> utcNow;

    public WebUiServerImportTokenStore(Func<DateTimeOffset>? utcNow = null) =>
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    public WebUiServerImportToken Issue(string path, ServerImportInspection inspection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(inspection);
        RemoveExpired();
        var token = Guid.NewGuid().ToString("N");
        var expiresAt = utcNow().AddMinutes(5);
        entries[token] = new(Path.GetFullPath(path), inspection, expiresAt);
        return new(token, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), expiresAt);
    }

    public (string Path, ServerImportInspection Inspection) Consume(string token)
    {
        RemoveExpired();
        if (!entries.Remove(token, out var entry))
            throw new ArgumentException("The local server selection expired. Choose it again.");
        return (entry.Path, entry.Inspection);
    }

    public void Clear() => entries.Clear();

    private void RemoveExpired()
    {
        var now = utcNow();
        foreach (var token in entries.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
            entries.Remove(token);
    }

    private sealed record Entry(string Path, ServerImportInspection Inspection, DateTimeOffset ExpiresAt);
}
