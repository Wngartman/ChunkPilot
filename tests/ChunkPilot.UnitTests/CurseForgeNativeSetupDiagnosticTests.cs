using System.Net;
using System.Text.Json;
using ChunkPilot.Certification;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeNativeSetupDiagnosticTests
{
    [Fact]
    public void Malformed_archive_and_provider_data_are_reported_without_an_unhandled_process_crash()
    {
        Assert.True(CurseForgeMetadataInspectionCommand.IsReportableFailure(new InvalidDataException("Invalid manifest")));
        Assert.True(CurseForgeMetadataInspectionCommand.IsReportableFailure(new JsonException("Invalid JSON")));
        Assert.False(CurseForgeMetadataInspectionCommand.IsReportableFailure(new NotSupportedException()));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public async Task Diagnostic_clears_then_runs_native_setup_and_emits_only_sanitized_proof(HttpStatusCode status, bool expected)
    {
        const string candidate = "synthetic-native-diagnostic-key";
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-native-setup-diagnostic-" + Guid.NewGuid().ToString("N"));
        var paths = new AppDataPaths(root);
        paths.EnsureCreated();
        try
        {
            var secrets = new DpapiSecretStore(paths);
            secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, candidate);
            var calls = 0;
            using var api = new CurseForgeApiClient(secrets, new Handler(request =>
            {
                calls++;
                Assert.False(secrets.Contains(CurseForgeUpdateProvider.ApiKeyName));
                Assert.Equal("api.curseforge.com", request.RequestUri!.Host);
                Assert.Equal(candidate, Assert.Single(request.Headers.GetValues("x-api-key")));
                return new(status) { Content = new StringContent("{\"data\":{\"id\":432}}", System.Text.Encoding.UTF8, "application/json") };
            }));
            var proof = await CurseForgeMetadataInspectionCommand.VerifyNativeCredentialSetupAsync(secrets, api, CancellationToken.None);
            Assert.Equal(1, calls);
            Assert.Equal(expected, proof.Success);
            Assert.Equal(expected, proof.StoredCredentialReadable);
            Assert.Equal(expected ? 2 : 1, proof.SessionChecks);
            var reopened = new DpapiSecretStore(paths);
            Assert.Equal(expected ? candidate : null, reopened.GetSecret(CurseForgeUpdateProvider.ApiKeyName));
            Assert.DoesNotContain(candidate, JsonSerializer.Serialize(proof), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Credential_only_diagnostic_refuses_payload_options_before_reading_credentials()
    {
        Assert.Equal(64, await CurseForgeMetadataInspectionCommand.RunAsync(
            ["--verify-native-credential-setup", "--verify-official-archives"]));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
