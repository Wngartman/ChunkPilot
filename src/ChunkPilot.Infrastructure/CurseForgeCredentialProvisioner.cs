using System.Security.Cryptography;
using System.Text;

namespace ChunkPilot.Infrastructure;

public sealed record CurseForgeCredentialProvisioningResult(
    bool SourcePresent,
    bool Imported,
    string Detail);

/// <summary>
/// Imports an approved local CurseForge application credential directly into the Windows-protected
/// native secret store. The credential never crosses the App/Agent or WebUI bridge.
/// </summary>
public sealed class CurseForgeCredentialProvisioner(ISecretStore secrets)
{
    public const string KeyFileEnvironmentVariable = "CHUNKPILOT_CURSEFORGE_KEY_FILE";
    public const string DefaultKeyFilePath = @"D:\ChunkPilot\.secrets\curseforge-api-key.txt";
    internal const int MaximumKeyFileBytes = 4 * 1024;

    public CurseForgeCredentialProvisioningResult ProvisionFromEnvironment()
    {
        var configuredPath = Environment.GetEnvironmentVariable(KeyFileEnvironmentVariable);
        if (!TryResolveSourcePath(configuredPath, out var sourcePath, out var error))
            return new(false, false, error);
        return ProvisionFromFile(sourcePath);
    }

    internal CurseForgeCredentialProvisioningResult ProvisionFromFile(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!File.Exists(sourcePath))
            return new(false, false, "No approved local CurseForge credential file is present.");

        var info = new FileInfo(sourcePath);
        if (info.Length is <= 0 or > MaximumKeyFileBytes)
            return new(true, false, "The approved CurseForge credential file has an invalid bounded size.");

        byte[] bytes;
        var length = 0;
        try
        {
            using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.SequentialScan);
            bytes = new byte[MaximumKeyFileBytes + 1];
            while (length < bytes.Length)
            {
                var read = input.Read(bytes, length, bytes.Length - length);
                if (read == 0)
                    break;
                length += read;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(true, false, "The approved CurseForge credential file could not be read.");
        }

        if (length > MaximumKeyFileBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            return new(true, false, "The approved CurseForge credential file exceeds its bounded size.");
        }
        Array.Resize(ref bytes, length);

        try
        {
            var key = Encoding.UTF8.GetString(bytes).Trim();
            if (!IsValidKey(key))
                return new(true, false, "The approved CurseForge credential file is not one non-empty credential value.");
            secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, key);
            return new(true, true, "CurseForge credential imported into Windows-protected native storage.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static bool TryResolveSourcePath(string? configuredPath, out string sourcePath, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            sourcePath = DefaultKeyFilePath;
            return true;
        }

        var candidate = configuredPath.Trim();
        if (candidate.Length > 1024 || candidate.IndexOfAny(['\r', '\n', '\0']) >= 0 ||
            !Path.IsPathFullyQualified(candidate))
        {
            sourcePath = "";
            error = $"{KeyFileEnvironmentVariable} must contain one absolute file path, never a credential value.";
            return false;
        }

        try
        {
            sourcePath = Path.GetFullPath(candidate);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            sourcePath = "";
            error = $"{KeyFileEnvironmentVariable} does not contain a valid absolute file path.";
            return false;
        }
    }

    private static bool IsValidKey(string key) =>
        key.Length is >= 8 and <= MaximumKeyFileBytes &&
        !key.Equals("REPLACE_WITH_APPROVED_CURSEFORGE_APPLICATION_KEY", StringComparison.Ordinal) &&
        key.All(character => !char.IsWhiteSpace(character) && !char.IsControl(character));
}
