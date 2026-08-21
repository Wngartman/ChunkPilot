using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChunkPilot.Infrastructure;

/// <summary>Persistent proof that a managed instance root belongs to one ChunkPilot server.</summary>
public sealed record ManagedInstanceOwnershipMarker(
    int SchemaVersion,
    Guid ServerId,
    DateTimeOffset CreatedAt,
    string Product,
    string OwnershipSource = "CreationTransaction",
    string EvidenceId = "")
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = ".chunkpilot-managed-instance.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string PathIn(string root) => Path.Combine(root, FileName);

    public static async Task WriteAsync(string root, Guid serverId, CancellationToken cancellationToken,
        string ownershipSource = "CreationTransaction", string evidenceId = "")
    {
        Directory.CreateDirectory(root);
        var path = PathIn(root);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary,
                JsonSerializer.Serialize(new ManagedInstanceOwnershipMarker(
                    CurrentSchemaVersion, serverId, DateTimeOffset.UtcNow, "ChunkPilot", ownershipSource, evidenceId), Json),
                new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static bool Proves(string root, Guid serverId)
        => Inspect(root, serverId).Proven;

    public static ManagedInstanceOwnershipInspection Inspect(string root, Guid serverId)
    {
        try
        {
            var path = PathIn(root);
            if (!File.Exists(path))
                return new(false, false, "No persistent managed-instance marker is present.", null);
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                return new(true, false, "The managed-instance marker is a reparse point.", null);
            var marker = JsonSerializer.Deserialize<ManagedInstanceOwnershipMarker>(File.ReadAllText(path), Json);
            if (marker is null)
                return new(true, false, "The managed-instance marker is empty.", null);
            if (marker.SchemaVersion != CurrentSchemaVersion)
                return new(true, false, $"Managed-instance marker schema {marker.SchemaVersion} is not supported.", marker);
            if (marker.ServerId != serverId)
                return new(true, false, "The managed-instance marker belongs to another server.", marker);
            if (!marker.Product.Equals("ChunkPilot", StringComparison.Ordinal))
                return new(true, false, "The managed-instance marker was not written by ChunkPilot.", marker);
            return new(true, true, $"Persistent marker proven ({marker.OwnershipSource}).", marker);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(true, false, $"The managed-instance marker could not be verified: {exception.Message}", null);
        }
    }
}

public sealed record ManagedInstanceOwnershipInspection(
    bool MarkerPresent,
    bool Proven,
    string Detail,
    ManagedInstanceOwnershipMarker? Marker);

public static class ManagedOwnershipReconciliationPolicy
{
    public static bool CanRestoreMissingMarker(
        bool managedRegistration,
        bool rootInExactConfiguredManagedRoot,
        bool uniqueRegisteredRoot,
        bool closedPathBoundary,
        bool markerPresent,
        bool exactSuccessfulCreationEvidence) =>
        managedRegistration && rootInExactConfiguredManagedRoot && uniqueRegisteredRoot &&
        closedPathBoundary && !markerPresent && exactSuccessfulCreationEvidence;
}
