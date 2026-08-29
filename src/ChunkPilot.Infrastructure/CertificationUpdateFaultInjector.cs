using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// A deliberately narrow, process-local certification hook used to exercise the real automatic
/// rollback path after a pack has switched but before startup validation. Ordinary App/Agent
/// launches cannot enable it: startup must provide a random capability and a controller-owned,
/// marked, isolated runtime root whose fresh data/server paths match the Agent's actual paths.
/// </summary>
public sealed class CertificationUpdateFaultInjector
{
    public const string TokenEnvironmentVariable = "CHUNKPILOT_CERTIFICATION_UPDATE_FAULT_TOKEN";
    public const string RuntimeRootEnvironmentVariable = "CHUNKPILOT_CERTIFICATION_RUNTIME_ROOT";
    public const string RuntimeMarkerFileName = ".chunkpilot-curseforge-certification-root.json";
    private const string ExpectedRuntimePurpose = "ChunkPilot CurseForge runtime certification";
    private const int MaximumMarkerBytes = 64 * 1024;
    private static readonly TimeSpan DefaultArmLifetime = TimeSpan.FromMinutes(10);

    private readonly object gate = new();
    private readonly byte[]? expectedTokenHash;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan armLifetime;
    private ArmedFailure? armed;

    private CertificationUpdateFaultInjector(
        byte[]? expectedTokenHash,
        TimeProvider? timeProvider = null,
        TimeSpan? armLifetime = null)
    {
        this.expectedTokenHash = expectedTokenHash;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.armLifetime = armLifetime ?? DefaultArmLifetime;
    }

    public bool Enabled => expectedTokenHash is not null;

    public static CertificationUpdateFaultInjector CreateFromEnvironment(AppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var token = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        var runtimeRoot = Environment.GetEnvironmentVariable(RuntimeRootEnvironmentVariable);
        try
        {
            if (string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(runtimeRoot))
                return new CertificationUpdateFaultInjector(null);
            if (!IsCapabilityToken(token) || string.IsNullOrWhiteSpace(runtimeRoot))
                return new CertificationUpdateFaultInjector(null);

            ValidateOwnedIsolatedPaths(paths, runtimeRoot);
            return new CertificationUpdateFaultInjector(HashToken(token!));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
                                           UnauthorizedAccessException or JsonException or
                                           InvalidOperationException or NotSupportedException)
        {
            // A malformed or partially injected environment never makes the production Agent fail
            // open. It simply leaves the test hook unavailable.
            return new CertificationUpdateFaultInjector(null);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(RuntimeRootEnvironmentVariable, null);
        }
    }

    public OperationResult Arm(Guid serverId, Guid operationId, string token)
    {
        if (!Enabled || serverId == Guid.Empty || operationId == Guid.Empty || !TokenMatches(token))
            return OperationResult.Fail("The isolated certification update-failure authority was refused.");

        lock (gate)
        {
            RemoveIfExpired();
            if (armed is not null)
            {
                return armed.ServerId == serverId && armed.OperationId == operationId
                    ? OperationResult.Ok("The exact certification update failure is already armed.")
                    : OperationResult.Fail("A different certification update failure is already armed.");
            }

            armed = new ArmedFailure(serverId, operationId, timeProvider.GetTimestamp());
            return OperationResult.Ok("The exact one-shot certification update failure is armed.");
        }
    }

    /// <summary>Consumes the exact one-shot failure. Mismatched operations cannot consume it.</summary>
    public bool TryConsume(Guid serverId, Guid operationId)
    {
        if (!Enabled || serverId == Guid.Empty || operationId == Guid.Empty)
            return false;
        lock (gate)
        {
            RemoveIfExpired();
            if (armed is not { } candidate || candidate.ServerId != serverId ||
                candidate.OperationId != operationId)
                return false;
            armed = null;
            return true;
        }
    }

    internal static CertificationUpdateFaultInjector CreateForTesting(
        string token,
        TimeProvider? timeProvider = null,
        TimeSpan? armLifetime = null)
    {
        if (!IsCapabilityToken(token))
            throw new ArgumentException("Use a 64-character hexadecimal test capability.", nameof(token));
        return new CertificationUpdateFaultInjector(HashToken(token), timeProvider, armLifetime);
    }

    private bool TokenMatches(string token)
    {
        if (expectedTokenHash is null || !IsCapabilityToken(token))
            return false;
        var actual = HashToken(token);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expectedTokenHash, actual);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private void RemoveIfExpired()
    {
        if (armed is { } candidate &&
            timeProvider.GetElapsedTime(candidate.ArmedTimestamp, timeProvider.GetTimestamp()) >= armLifetime)
            armed = null;
    }

    private static void ValidateOwnedIsolatedPaths(AppDataPaths paths, string runtimeRootValue)
    {
        if (!Path.IsPathFullyQualified(runtimeRootValue))
            throw new ArgumentException("The certification runtime root must be absolute.");
        var runtimeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeRootValue));
        var dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.Root));
        var serversRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.ManagedServers));
        var runRoot = Path.GetDirectoryName(dataRoot);
        if (string.IsNullOrWhiteSpace(runRoot) ||
            !runRoot.Equals(Path.GetDirectoryName(serversRoot), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(dataRoot).Equals("data", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(serversRoot).Equals("servers", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(runRoot).StartsWith("run-", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(Path.GetDirectoryName(runRoot))!.Equals("runs", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetDirectoryName(Path.GetDirectoryName(runRoot)!)!.Equals(
                runtimeRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Agent paths are not one fresh certification run.");

        ValidateNoReparseTraversal(runtimeRoot, dataRoot);
        ValidateNoReparseTraversal(runtimeRoot, serversRoot);
        var markerPath = Path.Combine(runtimeRoot, RuntimeMarkerFileName);
        var markerInfo = new FileInfo(markerPath);
        if (!markerInfo.Exists || markerInfo.Length <= 0 || markerInfo.Length > MaximumMarkerBytes ||
            (markerInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The certification runtime ownership marker is unavailable or unsafe.");

        using var stream = new FileStream(
            markerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024,
            FileOptions.SequentialScan);
        var marker = JsonSerializer.Deserialize<RuntimeMarker>(
                         stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                     ?? throw new InvalidDataException("The certification ownership marker is empty.");
        if (marker.SchemaVersion != 1 ||
            !marker.Purpose.Equals(ExpectedRuntimePurpose, StringComparison.Ordinal) ||
            marker.RepositoryFingerprint.Length != 64 ||
            marker.RepositoryFingerprint.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("The certification ownership marker is not valid.");
    }

    private static void ValidateNoReparseTraversal(string runtimeRoot, string candidate)
    {
        var prefix = runtimeRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The certification path escaped its owned runtime root.");
        var current = runtimeRoot;
        RejectExistingReparsePoint(current);
        foreach (var segment in Path.GetRelativePath(runtimeRoot, candidate).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
                break;
            RejectExistingReparsePoint(current);
        }
    }

    private static void RejectExistingReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Certification paths cannot traverse reparse points.");
    }

    private static bool IsCapabilityToken(string? token) =>
        token is { Length: 64 } && token.All(Uri.IsHexDigit);

    private static byte[] HashToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        try
        {
            return SHA256.HashData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private sealed record ArmedFailure(Guid ServerId, Guid OperationId, long ArmedTimestamp);

    private sealed record RuntimeMarker
    {
        public int SchemaVersion { get; init; }
        public string Purpose { get; init; } = "";
        public string RepositoryFingerprint { get; init; } = "";
    }
}
