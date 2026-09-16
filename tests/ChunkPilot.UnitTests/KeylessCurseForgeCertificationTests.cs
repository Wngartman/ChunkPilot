using System.Net;
using System.Text;
using ChunkPilot.Certification;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class KeylessCurseForgeCertificationTests
{
    [Fact]
    public async Task Service_identity_is_verified_without_reading_a_key()
    {
        var secrets = new EmptySecrets();
        var transport = new IdentityHandler(432);
        using var api = new CurseForgeApiClient(secrets, transport, new Uri("https://service.example"));
        await KeylessCurseForgeCertification.VerifyAsync(api, CancellationToken.None);
        Assert.Equal(1, transport.Requests);
    }

    [Fact]
    public async Task Unconfigured_build_cannot_pass_the_no_key_check()
    {
        var transport = new IdentityHandler(432);
        using var api = new CurseForgeApiClient(new EmptySecrets(), transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            KeylessCurseForgeCertification.VerifyAsync(api, CancellationToken.None));
        Assert.Equal(0, transport.Requests);
    }

    [Fact]
    public async Task Existing_personal_credential_invalidates_clean_install_evidence()
    {
        var transport = new IdentityHandler(432);
        using var api = new CurseForgeApiClient(new EmptySecrets(true), transport, new Uri("https://service.example"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            KeylessCurseForgeCertification.VerifyAsync(api, CancellationToken.None));
        Assert.Equal(0, transport.Requests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task Wrong_game_identity_never_passes(int gameId)
    {
        using var api = new CurseForgeApiClient(new EmptySecrets(), new IdentityHandler(gameId), new Uri("https://service.example"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            KeylessCurseForgeCertification.VerifyAsync(api, CancellationToken.None));
    }

    private sealed class EmptySecrets(bool contains = false) : ISecretStore
    {
        public bool Contains(string key) => contains;
        public string? GetSecret(string key) => throw new InvalidOperationException("No secret read is permitted.");
        public void SetSecret(string key, string value) => throw new InvalidOperationException("No secret write is permitted.");
        public void Delete(string key) => throw new InvalidOperationException("No secret deletion is permitted.");
    }

    private sealed class IdentityHandler(int gameId) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("https://service.example/v1/games/432", request.RequestUri!.AbsoluteUri);
            Assert.False(request.Headers.Contains("x-api-key"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { data = new { id = gameId } }),
                    Encoding.UTF8, "application/json")
            });
        }
    }
}
