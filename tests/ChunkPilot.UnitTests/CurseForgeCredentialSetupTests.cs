using System.Net;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeCredentialSetupTests
{
    private const string Candidate = "synthetic-local-setup-key";

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public async Task Only_authenticated_candidate_replaces_current_key(HttpStatusCode status, bool expected)
    {
        var secrets = new MemorySecrets();
        using var api = new CurseForgeApiClient(secrets, new Handler(request =>
        {
            Assert.Equal("api.curseforge.com", request.RequestUri!.Host);
            Assert.Equal(Candidate, Assert.Single(request.Headers.GetValues("x-api-key")));
            return new(status) { Content = new StringContent("{\"data\":{\"id\":432}}", System.Text.Encoding.UTF8, "application/json") };
        }));
        var encrypted = CurseForgeCredentialTransport.ProtectForCurrentUser(Candidate);
        Assert.DoesNotContain(Candidate, encrypted, StringComparison.Ordinal);
        var demands = 0;
        var result = await new CurseForgeCredentialSetupService(secrets, api).ConfigureAsync(encrypted, () => demands++);
        Assert.Equal(expected, result.Success);
        Assert.Equal(expected ? Candidate : "old-synthetic-key", secrets.Value);
        Assert.Equal(expected ? 2 : 1, demands);
        var response = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(Candidate, response, StringComparison.Ordinal);
        Assert.DoesNotContain(encrypted, response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Native_session_expiry_after_authentication_preserves_previous_key()
    {
        var secrets = new MemorySecrets();
        using var api = new CurseForgeApiClient(secrets, new Handler(_ =>
            new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"id\":432}}", System.Text.Encoding.UTF8, "application/json") }));
        var demands = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new CurseForgeCredentialSetupService(secrets, api)
            .ConfigureAsync(CurseForgeCredentialTransport.ProtectForCurrentUser(Candidate), () =>
            {
                if (++demands == 2) throw new UnauthorizedAccessException("Synthetic session ended.");
            }));
        Assert.Equal("old-synthetic-key", secrets.Value);
    }

    [Theory]
    [InlineData("not-valid-base64")]
    [InlineData("")]
    public async Task Invalid_protected_input_never_requests_provider_or_changes_storage(string invalid)
    {
        var secrets = new MemorySecrets();
        using var api = new CurseForgeApiClient(secrets, new Handler(_ => throw new InvalidOperationException("No HTTP expected.")));
        var result = await new CurseForgeCredentialSetupService(secrets, api).ConfigureAsync(invalid, () => { });
        Assert.False(result.Success);
        Assert.Equal("old-synthetic-key", secrets.Value);
    }

    [Fact]
    public void Protected_transport_property_is_redacted_and_large_plaintext_is_rejected()
    {
        Assert.DoesNotContain("synthetic-ciphertext", SecretRedactor.Redact("{\"protectedApiKey\":\"synthetic-ciphertext\"}"), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => CurseForgeCredentialTransport.ProtectForCurrentUser(new string('x', 4097)));
    }

    private sealed class MemorySecrets : ISecretStore
    {
        public string? Value { get; private set; } = "old-synthetic-key";
        public bool Contains(string key) => Value is not null;
        public string? GetSecret(string key) => Value;
        public void SetSecret(string key, string value) => Value = value;
        public void Delete(string key) => Value = null;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
