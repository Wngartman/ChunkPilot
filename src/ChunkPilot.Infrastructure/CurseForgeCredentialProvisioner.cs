using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ChunkPilot.Infrastructure;

public sealed record CurseForgeCredentialProvisioningResult(
    bool SourcePresent,
    bool Imported,
    string Detail,
    CurseForgeFailureKind? FailureKind = null,
    bool ExistingCredentialPreserved = false);

public static class CurseForgeCredentialEnvironment
{
    public static void ClearFromCurrentProcess() =>
        Environment.SetEnvironmentVariable(
            CurseForgeCredentialProvisioner.KeyFileEnvironmentVariable, null);

    public static void RemoveFromChild(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.Environment.Remove(CurseForgeCredentialProvisioner.KeyFileEnvironmentVariable);
    }
}

/// <summary>
/// Imports an approved local CurseForge application credential directly into the Windows-protected
/// native secret store. The credential never crosses the App/Agent or WebUI bridge.
/// </summary>
public sealed class CurseForgeCredentialProvisioner(ISecretStore secrets)
{
    public const string KeyFileEnvironmentVariable = "CHUNKPILOT_CURSEFORGE_KEY_FILE";
    internal const int MaximumKeyFileBytes = 4 * 1024;
    private readonly Action<ReadOnlyMemory<byte>>? plaintextBufferClearedForTesting;

    internal CurseForgeCredentialProvisioner(
        ISecretStore secrets,
        Action<ReadOnlyMemory<byte>> plaintextBufferClearedForTesting)
        : this(secrets) =>
        this.plaintextBufferClearedForTesting = plaintextBufferClearedForTesting;

    public Task<CurseForgeCredentialProvisioningResult> ProvisionFromEnvironmentAsync(
        CurseForgeApiClient api,
        CancellationToken cancellationToken = default)
    {
        var configuredPath = Environment.GetEnvironmentVariable(KeyFileEnvironmentVariable);
        if (!TryResolveSourcePath(configuredPath, out var sourcePath, out var error))
            return Task.FromResult(new CurseForgeCredentialProvisioningResult(
                false,
                false,
                error,
                ExistingCredentialPreserved: HasExistingCredential()));
        return ProvisionFromFileAsync(sourcePath, api, cancellationToken);
    }

    internal async Task<CurseForgeCredentialProvisioningResult> ProvisionFromFileAsync(
        string sourcePath,
        CurseForgeApiClient api,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(api);
        var hadExistingCredential = HasExistingCredential();
        if (!File.Exists(sourcePath))
            return new(false, false, "No approved local CurseForge credential file is present.",
                ExistingCredentialPreserved: hadExistingCredential);

        try
        {
            var info = new FileInfo(sourcePath);
            if (info.Length is <= 0 or > MaximumKeyFileBytes)
                return new(true, false, "The approved CurseForge credential file has an invalid bounded size.",
                    ExistingCredentialPreserved: hadExistingCredential);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(true, false, "The approved CurseForge credential file could not be read.",
                ExistingCredentialPreserved: hadExistingCredential);
        }

        byte[]? bytes = null;
        var length = 0;
        try
        {
            bytes = new byte[MaximumKeyFileBytes + 1];
            try
            {
                using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 4096, FileOptions.SequentialScan);
                while (length < bytes.Length)
                {
                    var read = await input.ReadAsync(bytes.AsMemory(length, bytes.Length - length), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                        break;
                    length += read;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new(true, false, "The approved CurseForge credential file could not be read.",
                    ExistingCredentialPreserved: hadExistingCredential);
            }

            if (length > MaximumKeyFileBytes)
                return new(true, false, "The approved CurseForge credential file exceeds its bounded size.",
                    ExistingCredentialPreserved: hadExistingCredential);

            var key = Encoding.UTF8.GetString(bytes.AsSpan(0, length)).Trim();
            if (!IsValidKey(key))
                return new(true, false,
                    "The approved CurseForge credential file is not one non-empty credential value.",
                    ExistingCredentialPreserved: hadExistingCredential);

            try
            {
                await api.ValidateCredentialAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (CurseForgeApiException exception)
            {
                var detail = exception.Kind == CurseForgeFailureKind.Authentication
                    ? "CurseForge rejected the candidate credential; native protected storage was not changed."
                    : "CurseForge credential validation is temporarily unavailable; native protected storage was not changed.";
                return new(true, false, detail, exception.Kind, hadExistingCredential);
            }

            secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, key);
            return new(true, true,
                "CurseForge credential authenticated and imported into Windows-protected native storage.");
        }
        finally
        {
            if (bytes is not null)
            {
                CryptographicOperations.ZeroMemory(bytes);
                plaintextBufferClearedForTesting?.Invoke(bytes);
            }
        }
    }

    internal static bool TryResolveSourcePath(string? configuredPath, out string sourcePath, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            sourcePath = "";
            error = "No explicit developer credential source is configured.";
            return false;
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

    private bool HasExistingCredential() =>
        secrets.Contains(CurseForgeUpdateProvider.ApiKeyName);
}
