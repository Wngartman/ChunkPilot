using System.Security.Cryptography;
using System.Text;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>Protects the native password-dialog value before it crosses the local named pipe.</summary>
public static class CurseForgeCredentialTransport
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ChunkPilot.CurseForge.NativeSetup.v1");
    internal const int MaximumProtectedCharacters = 16 * 1024;

    public static string ProtectForCurrentUser(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native API-key setup requires Windows.");
        var trimmed = value.Trim();
        if (!ValidCandidate(trimmed)) throw new ArgumentException("Enter one non-empty API key of at most 4 KB.");
        var bytes = Encoding.UTF8.GetBytes(trimmed);
        byte[]? encrypted = null;
        try
        {
            encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    internal static byte[] UnprotectForCurrentUser(string protectedValue)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native API-key setup requires Windows.");
        if (string.IsNullOrWhiteSpace(protectedValue) || protectedValue.Length > MaximumProtectedCharacters)
            throw new InvalidDataException("The protected credential is missing or exceeds its safe size.");
        var encrypted = Convert.FromBase64String(protectedValue);
        try { return ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
    }

    internal static bool ValidCandidate(string value) => value.Length is >= 8 and <= 4096 &&
        Encoding.UTF8.GetByteCount(value) <= 4096 && value.All(character => !char.IsWhiteSpace(character) && !char.IsControl(character));
}

/// <summary>Validates against the official API before replacing the current native protected key.</summary>
public sealed class CurseForgeCredentialSetupService(ISecretStore secrets, CurseForgeApiClient api)
{
    public async Task<OperationResult> ConfigureAsync(string protectedApiKey, Action demandSession,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demandSession);
        demandSession();
        byte[]? plaintext = null;
        try
        {
            try { plaintext = CurseForgeCredentialTransport.UnprotectForCurrentUser(protectedApiKey); }
            catch (Exception exception) when (exception is CryptographicException or FormatException or InvalidDataException)
            {
                return OperationResult.Fail("The protected API key could not be read. Reopen native setup; existing credentials were not changed.");
            }
            string candidate;
            try { candidate = new UTF8Encoding(false, true).GetString(plaintext); }
            catch (DecoderFallbackException)
            {
                return OperationResult.Fail("The API key is not valid text. Existing credentials were not changed.");
            }
            if (!CurseForgeCredentialTransport.ValidCandidate(candidate))
                return OperationResult.Fail("The API key has an invalid format. Existing credentials were not changed.");
            try { await api.ValidateCredentialAsync(candidate, cancellationToken).ConfigureAwait(false); }
            catch (CurseForgeApiException exception)
            {
                return OperationResult.Fail(exception.Kind == CurseForgeFailureKind.Authentication
                    ? "CurseForge rejected that API key. Existing credentials were not changed."
                    : "CurseForge validation is unavailable right now. Existing credentials were not changed; try again later.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            demandSession();
            secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, candidate);
            return OperationResult.Ok("CurseForge is connected. This key is protected for your Windows account and is never bundled with ChunkPilot.");
        }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }
}
