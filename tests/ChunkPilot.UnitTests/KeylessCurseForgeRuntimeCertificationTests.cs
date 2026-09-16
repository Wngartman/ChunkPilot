using ChunkPilot.Certification;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class KeylessCurseForgeRuntimeCertificationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "chunkpilot-keyless-runtime-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true, "approved.key", null)]
    [InlineData(true, "", "retained.json")]
    [InlineData(false, "", null)]
    public void Ambiguous_or_nonfresh_access_selection_is_rejected(bool keyless, string key, string? resume)
    {
        Assert.Throws<ArgumentException>(() =>
            CurseForgeRuntimeCertificationCommand.ValidateAccessSelection(keyless, key, resume));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Explicit_keyless_selection_needs_no_key_path_and_preserves_personal_default()
    {
        CurseForgeRuntimeCertificationCommand.ValidateAccessSelection(true, null, null);
        CurseForgeRuntimeCertificationCommand.ValidateAccessSelection(false, "approved.key", null);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Keyless_agent_launch_never_receives_a_credential_bootstrap_variable()
    {
        var temporary = Path.Combine(root, "temp");
        Directory.CreateDirectory(temporary);
        var start = HeadlessAgentLaunch.CreateStartInfo(new HeadlessAgentLaunchOptions(
            Path.Combine(root, "ChunkPilot.Agent.exe"), Path.Combine(root, "data"),
            Path.Combine(root, "servers"), temporary, "", "keyless-fixture",
            RequireApplicationService: true));

        Assert.False(start.Environment.ContainsKey(CurseForgeCredentialProvisioner.KeyFileEnvironmentVariable));
        Assert.Empty(start.ArgumentList);
        Assert.True(start.CreateNoWindow);
        Assert.False(start.UseShellExecute);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "data")), start.Environment["CHUNKPILOT_DATA_ROOT"]);
        Assert.Equal(Path.GetFullPath(temporary), start.Environment["TEMP"]);
    }

    [Fact]
    public void Keyless_agent_launch_rejects_a_key_source_before_creating_any_directories()
    {
        Assert.Throws<ArgumentException>(() => HeadlessAgentLaunch.CreateStartInfo(new HeadlessAgentLaunchOptions(
            Path.Combine(root, "ChunkPilot.Agent.exe"), Path.Combine(root, "data"),
            Path.Combine(root, "servers"), Path.Combine(root, "temp"), "approved.key", "fixture",
            RequireApplicationService: true)));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Empty_store_proof_rejects_even_an_unreadable_existing_store_without_reading_it()
    {
        Directory.CreateDirectory(root);
        CurseForgeRuntimeCertificationController.RequireNoProtectedCredentialFile(root);
        using var occupied = new FileStream(Path.Combine(root, "secrets.dat"), FileMode.CreateNew,
            FileAccess.Write, FileShare.None);

        Assert.Throws<InvalidOperationException>(() =>
            CurseForgeRuntimeCertificationController.RequireNoProtectedCredentialFile(root));
        Assert.Equal(0, occupied.Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Each_agent_lifetime_proves_service_access_without_a_personal_credential(bool bootstrap)
    {
        Directory.CreateDirectory(root);
        var transport = new AccessTransport();
        var report = new CurseForgeRuntimeCertificationReport();
        var session = Session(transport);

        await session.RegisterAndAuthenticateAsync(Options(), report, 1234, 5678, bootstrap, CancellationToken.None);

        Assert.Equal("ApplicationService", report.AccessMode);
        Assert.Equal(bootstrap, report.BootstrapPersonalCredentialAbsent);
        Assert.Equal(!bootstrap, report.RelaunchPersonalCredentialAbsent);
        Assert.False(report.BootstrapProtectedCredentialConfigured);
        Assert.False(report.DpapiRelaunchProtectedCredentialConfigured);
        Assert.Contains("GetCurseForgeAccess", transport.Operations);
        Assert.DoesNotContain("HasCurseForgeApiKey", transport.Operations);
        if (!bootstrap)
        {
            await session.ExecuteCertifiedWorkAsync(Options(), report, CancellationToken.None);
            Assert.True(report.RelaunchAuthenticatedCatalogResolve);
            Assert.False(report.DpapiRelaunchAuthenticatedCatalogResolve);
            Assert.True(report.RelaunchProviderConfigured);
            Assert.False(report.DpapiRelaunchProviderConfigured);
            Assert.DoesNotContain("BeginModpackCreation", transport.Operations);
        }
    }

    [Theory]
    [InlineData("ApplicationService", true, true)]
    [InlineData("ApplicationService", false, false)]
    [InlineData("PersonalKey", true, true)]
    [InlineData("Unavailable", false, false)]
    public async Task A_contradictory_agent_access_status_cannot_pass(string mode, bool available, bool hasKey)
    {
        Directory.CreateDirectory(root);
        var transport = new AccessTransport(mode, available, hasKey);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Session(transport).RegisterAndAuthenticateAsync(
            Options(), new CurseForgeRuntimeCertificationReport(), 1234, 5678, true, CancellationToken.None));

        Assert.DoesNotContain("ResolveCatalogProject", transport.Operations);
    }

    [Fact]
    public void Final_keyless_gate_needs_both_clean_lifetimes_and_authenticated_resolution()
    {
        var report = new CurseForgeRuntimeCertificationReport
        {
            AccessMode = "ApplicationService", BootstrapAccessMode = "ApplicationService",
            RelaunchAccessMode = "ApplicationService", FreshSecretStoreVerified = true,
            CredentialStoreRemainedEmpty = true, BootstrapPersonalCredentialAbsent = true,
            RelaunchPersonalCredentialAbsent = true, BootstrapProviderConfigured = true,
            RelaunchProviderConfigured = true, RelaunchAuthenticatedCatalogResolve = true
        };
        Assert.True(CurseForgeRuntimeCertificationController.HasRequiredAccessEvidence(report, true));
        Assert.False(CurseForgeRuntimeCertificationController.HasRequiredAccessEvidence(report, false));
        report.CredentialStoreRemainedEmpty = false;
        Assert.False(CurseForgeRuntimeCertificationController.HasRequiredAccessEvidence(report, true));
        report.CredentialStoreRemainedEmpty = true;
        report.RelaunchPersonalCredentialAbsent = false;
        Assert.False(CurseForgeRuntimeCertificationController.HasRequiredAccessEvidence(report, true));
        report.RelaunchPersonalCredentialAbsent = true;
        report.RelaunchAuthenticatedCatalogResolve = false;
        Assert.False(CurseForgeRuntimeCertificationController.HasRequiredAccessEvidence(report, true));
        report.DpapiRelaunchAuthenticatedCatalogResolve = true;
        Assert.True(CurseForgeRuntimeCertificationController.HasRequiredAccessEvidence(report, false));
    }

    private CurseForgeRuntimeCertificationSession Session(AccessTransport transport) =>
        new(transport, new CurseForgePayloadBudget(Path.Combine(root, "payload-ledger.json")),
            static (_, _) => Task.CompletedTask);

    private CurseForgeRuntimeCertificationOptions Options() => new()
    {
        RepositoryRoot = root, RuntimeRoot = Path.Combine(root, "artifacts", "runtime"),
        ExpectedGitSha = new string('a', 40), AgentExecutablePath = Path.Combine(root, "ChunkPilot.Agent.exe"),
        DataRoot = Path.Combine(root, "data"), ManagedServersRoot = Path.Combine(root, "servers"),
        TemporaryRoot = Path.Combine(root, "temp"), ApprovedKeyFilePath = "", RequireApplicationService = true,
        PayloadLedgerPath = Path.Combine(root, "payload-ledger.json"), ProjectReference = "123", ClientFileId = "456",
        Phase = CurseForgeRuntimeCertificationPhase.Metadata
    };

    private sealed class AccessTransport(string mode = "ApplicationService", bool available = true, bool hasKey = false)
        : ICertificationAgentTransport
    {
        public List<string> Operations { get; } = [];
        public Task WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<TResponse> SendAsync<TResponse>(string operation, object? payload = null,
            CancellationToken cancellationToken = default)
        {
            Operations.Add(operation);
            object result = operation switch
            {
                "RegisterUiSession" => Register((UiSessionRegistrationRequest)payload!),
                "GetCurseForgeAccess" => new CertificationCurseForgeAccessStatus
                { Mode = mode, CanAccess = available, HasPersonalCredential = hasKey },
                "CatalogProviderStatuses" => new[] { new CatalogProviderStatus(CatalogProvider.CurseForge, true, "available") },
                "ResolveCatalogProject" => new CatalogItem
                {
                    Provider = CatalogProvider.CurseForge, ContentType = CatalogContentType.Modpack,
                    ProjectId = "123", Name = "Fixture", Versions = [new CatalogVersion
                    { VersionId = "456", ClientFileId = "456", VersionName = "Fixture 1", MinecraftVersion = "1.21.1" }]
                },
                _ => throw new InvalidOperationException("Unexpected operation: " + operation)
            };
            return Task.FromResult((TResponse)result);
        }

        private static UiSessionRegistrationResult Register(UiSessionRegistrationRequest request) =>
            new(new ApplicationSession { SessionId = Guid.NewGuid(), ProcessId = request.ProcessId,
                ProcessCreationTicks = request.ProcessCreationTicks }, false, "") { SessionCapability = "fixture-capability" };
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
