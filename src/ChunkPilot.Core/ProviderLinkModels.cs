namespace ChunkPilot.Core;

public enum ProviderLinkKind
{
    Project,
    ExactRelease
}

/// <summary>
/// A validated, allowlisted content-provider URL reduced to non-secret provider identity. The original
/// URL is retained only for review/provenance and is never fetched directly.
/// </summary>
public sealed record ProviderLinkReference(
    CatalogProvider Provider,
    ProviderLinkKind Kind,
    string ProjectReference,
    string? ReleaseReference,
    string CanonicalUrl);

public static class ProviderLinkParser
{
    public static bool TryParse(string value, out ProviderLinkReference? reference, out string error)
    {
        reference = null;
        error = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "Paste a complete HTTPS Modrinth or CurseForge project link.";
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();
        if (host is "modrinth.com" or "www.modrinth.com")
            return TryParseModrinth(uri, segments, out reference, out error);
        if (host is "curseforge.com" or "www.curseforge.com")
            return TryParseCurseForge(uri, segments, out reference, out error);

        error = "Only official Modrinth and CurseForge project links are supported.";
        return false;
    }

    public static ProviderLinkReference Parse(string value) =>
        TryParse(value, out var reference, out var error)
            ? reference!
            : throw new ArgumentException(error, nameof(value));

    private static bool TryParseModrinth(Uri uri, string[] segments,
        out ProviderLinkReference? reference, out string error)
    {
        reference = null;
        error = "";
        if (segments.Length < 2 || segments[0] is not ("modpack" or "project") || !SafeId(segments[1]))
        {
            error = "Use a Modrinth modpack project or exact-version link.";
            return false;
        }

        string? release = null;
        if (segments.Length > 2)
        {
            if (segments.Length != 4 || segments[2] != "version" || !SafeId(segments[3]))
            {
                error = "The Modrinth link shape is not supported.";
                return false;
            }
            release = segments[3];
        }

        reference = new ProviderLinkReference(CatalogProvider.Modrinth,
            release is null ? ProviderLinkKind.Project : ProviderLinkKind.ExactRelease,
            segments[1], release,
            $"https://modrinth.com/modpack/{Uri.EscapeDataString(segments[1])}" +
            (release is null ? "" : $"/version/{Uri.EscapeDataString(release)}"));
        return true;
    }

    private static bool TryParseCurseForge(Uri uri, string[] segments,
        out ProviderLinkReference? reference, out string error)
    {
        reference = null;
        error = "";
        if (segments.Length < 3 || segments[0] != "minecraft" || segments[1] != "modpacks" ||
            !SafeId(segments[2]))
        {
            error = "Use a CurseForge Minecraft modpack project or exact-file link.";
            return false;
        }

        string? file = null;
        if (segments.Length > 3)
        {
            if (segments.Length != 5 || segments[3] != "files" ||
                !int.TryParse(segments[4], out var fileId) || fileId <= 0)
            {
                error = "The CurseForge link shape is not supported.";
                return false;
            }
            file = fileId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        reference = new ProviderLinkReference(CatalogProvider.CurseForge,
            file is null ? ProviderLinkKind.Project : ProviderLinkKind.ExactRelease,
            segments[2], file,
            $"https://www.curseforge.com/minecraft/modpacks/{Uri.EscapeDataString(segments[2])}" +
            (file is null ? "" : $"/files/{file}"));
        return true;
    }

    private static bool SafeId(string value) => value.Length is > 0 and <= 120 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
