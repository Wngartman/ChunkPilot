namespace ChunkPilot.App.WebUi;

internal sealed record WebUiLocalPluginToken(string Token, string FileName, DateTimeOffset ExpiresAt);

/// <summary>
/// Keeps native file paths outside the renderer contract. Tokens are server-bound, short-lived, and
/// single use; issuing or consuming a token never serializes the path.
/// </summary>
internal sealed class WebUiLocalPluginTokenStore
{
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> utcNow;

    public WebUiLocalPluginTokenStore(Func<DateTimeOffset>? utcNow = null) =>
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    public WebUiLocalPluginToken Issue(Guid serverId, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RemoveExpired();
        var token = Guid.NewGuid().ToString("N");
        var expiresAt = utcNow().AddMinutes(5);
        entries[token] = new(serverId, path, expiresAt);
        return new(token, Path.GetFileName(path), expiresAt);
    }

    public string Consume(Guid serverId, string token)
    {
        RemoveExpired();
        if (!entries.Remove(token, out var entry) || entry.ServerId != serverId)
            throw new ArgumentException("The local plugin selection expired. Choose the file again.");
        return entry.Path;
    }

    public void Clear() => entries.Clear();

    private void RemoveExpired()
    {
        var now = utcNow();
        foreach (var token in entries.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
            entries.Remove(token);
    }

    private sealed record Entry(Guid ServerId, string Path, DateTimeOffset ExpiresAt);
}
