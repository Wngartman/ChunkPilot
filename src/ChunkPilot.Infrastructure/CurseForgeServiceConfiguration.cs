using System.Reflection;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Public deployment address only. Application credentials belong to the remote service and must
/// never be carried in this metadata, local settings, environment variables, or an installer.
/// </summary>
public static class CurseForgeServiceConfiguration
{
    public static Uri? Endpoint => ReadEndpoint(typeof(CurseForgeServiceConfiguration).Assembly);

    internal static Uri? ReadEndpoint(Assembly assembly)
    {
        var entries = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => attribute.Key == "CurseForgeServiceEndpoint").ToArray();
        if (entries.Length == 0) return null;
        if (entries.Length != 1)
            throw new InvalidOperationException("The CurseForge service configuration is ambiguous.");
        return ValidateEndpoint(entries[0].Value);
    }

    public static Uri? ValidateEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            uri.HostNameType != UriHostNameType.Dns || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            !uri.IdnHost.Contains('.') || uri.IdnHost.EndsWith('.') ||
            value.Contains('\\') || uri.AbsolutePath.Contains('%') ||
            value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
            throw new InvalidOperationException("The CurseForge service requires a fixed public HTTPS endpoint without credentials or query parameters.");

        // A build may select a dedicated service origin or a fixed, non-encoded path prefix.
        // Normalization is performed once; callers append known relative routes only.
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }
}

public enum CurseForgeAccessMode
{
    Unavailable,
    PersonalKey,
    ApplicationService
}
