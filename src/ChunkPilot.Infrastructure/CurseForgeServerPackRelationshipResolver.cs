using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Resolves only a link supplied by the selected client file. CurseForge authors also attach
/// server packs as ordinary additional files: those have alternateFileId/parentProjectFileId,
/// but may have isServerPack=false and no serverPackFileId. Never search by nearby ID or name.
/// </summary>
internal sealed partial class CurseForgeServerPackRelationshipResolver(CurseForgeApiClient api)
{
    public async Task<string> ResolveAsync(string projectId, JsonElement client,
        CancellationToken cancellationToken = default)
    {
        var clientId = CurseForgeCatalogProvider.Text(client, "id");
        var direct = PositiveId(client, "serverPackFileId");
        var alternate = PositiveId(client, "alternateFileId");
        var selected = direct.Length > 0 ? direct : alternate;
        if (selected.Length == 0) return "";
        if (selected.Equals(clientId, StringComparison.Ordinal))
            throw new InvalidDataException("The CurseForge server-pack relationship points to its own client file.");
        // Every consumer resolves and verifies this dedicated target itself. Do not duplicate
        // its request, and let preflight reject changed IDs before contacting a new target.
        if (direct.Length > 0) return selected;

        using var document = await api.GetJsonAsync(
            $"/v1/mods/{Uri.EscapeDataString(projectId)}/files/{selected}", cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var file) || file.ValueKind != JsonValueKind.Object ||
            !CurseForgeCatalogProvider.Text(file, "id").Equals(selected, StringComparison.Ordinal) ||
            !CurseForgeCatalogProvider.Text(file, "modId").Equals(projectId, StringComparison.Ordinal))
            throw new InvalidDataException("CurseForge returned a contradictory exact additional-file identity.");
        var parent = PositiveId(file, "parentProjectFileId");
        if (parent.Length > 0 && !parent.Equals(clientId, StringComparison.Ordinal))
            throw new InvalidDataException("The CurseForge server-pack file is attached to a different client release.");

        // The forward link is authoritative even if the optional reverse field is absent.
        // Without the dedicated server flag require an explicit server-pack label on this
        // already-linked file, then let normal archive/loader preflight verify its contents.
        var labelledServer = file.TryGetProperty("isServerPack", out var flag) && flag.ValueKind == JsonValueKind.True ||
            ServerPackLabel().IsMatch(CurseForgeCatalogProvider.Text(file, "fileName")) ||
            ServerPackLabel().IsMatch(CurseForgeCatalogProvider.Text(file, "displayName"));
        return labelledServer ? selected : "";
    }

    private static string PositiveId(JsonElement value, string property) =>
        value.TryGetProperty(property, out var number) && number.ValueKind == JsonValueKind.Number &&
        number.TryGetInt64(out var id) && id > 0 ? id.ToString(CultureInfo.InvariantCulture) : "";

    [GeneratedRegex(@"(?:^|[^a-z])server[\s._-]*(?:pack|files)(?:[^a-z]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServerPackLabel();
}
