using System.Net;
using System.Text;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeCredentialProvisionerTests
{
    [Fact]
    public void Default_source_is_the_one_authorized_repository_local_file()
    {
        Assert.Equal(@"D:\ChunkPilot\.secrets\curseforge-api-key.txt",
            CurseForgeCredentialProvisioner.DefaultKeyFilePath);
        Assert.Equal("CHUNKPILOT_CURSEFORGE_KEY_FILE",
            CurseForgeCredentialProvisioner.KeyFileEnvironmentVariable);
    }

    [Fact]
    public void Environment_override_is_path_only_and_never_treated_as_key_material()
    {
        var looksLikeAKey = string.Concat("synthetic-", Guid.NewGuid().ToString("N"));

        var accepted = CurseForgeCredentialProvisioner.TryResolveSourcePath(
            looksLikeAKey, out var path, out var error);

        Assert.False(accepted);
        Assert.Empty(path);
        Assert.DoesNotContain(looksLikeAKey, error, StringComparison.Ordinal);
        Assert.Contains("absolute file path", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Managed_child_environment_never_inherits_the_credential_source_path()
    {
        if (!OperatingSystem.IsWindows()) return;
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("set");
        startInfo.ArgumentList.Add(CurseForgeCredentialProvisioner.KeyFileEnvironmentVariable);
        startInfo.Environment[CurseForgeCredentialProvisioner.KeyFileEnvironmentVariable] =
            @"D:\synthetic-fixture\credential-source.txt";

        CurseForgeCredentialEnvironment.RemoveFromChild(startInfo);

        Assert.False(startInfo.Environment.ContainsKey(
            CurseForgeCredentialProvisioner.KeyFileEnvironmentVariable));
        using var child = System.Diagnostics.Process.Start(startInfo)
                          ?? throw new InvalidOperationException("The harmless environment probe did not start.");
        var output = await child.StandardOutput.ReadToEndAsync();
        var error = await child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync();
        Assert.NotEqual(0, child.ExitCode);
        Assert.Empty(output);
        Assert.DoesNotContain("synthetic-fixture", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential-source.txt", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Approved_file_is_authenticated_then_imported_without_returning_credential_material()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-curseforge-credential-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sentinel = string.Concat("CURSEFORGE-SENTINEL-", Guid.NewGuid().ToString("N"));
            var path = Path.Combine(root, "curseforge-api-key.txt");
            File.WriteAllText(path, "  " + sentinel + Environment.NewLine);
            var secrets = new MemorySecrets();
            var handler = new Handler(request =>
            {
                Assert.Equal(sentinel, request.Headers.GetValues("x-api-key").Single());
                return Accepted();
            });
            using var api = new CurseForgeApiClient(secrets, handler);

            var result = await new CurseForgeCredentialProvisioner(secrets)
                .ProvisionFromFileAsync(path, api);

            Assert.True(result.SourcePresent);
            Assert.True(result.Imported);
            Assert.Equal(sentinel, secrets.GetSecret(CurseForgeUpdateProvider.ApiKeyName));
            Assert.DoesNotContain(sentinel, result.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(path, result.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, handler.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_empty_oversized_and_unreadable_sources_are_rejected_without_network_access()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-curseforge-boundaries-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "curseforge-api-key.txt");
            var secrets = new MemorySecrets();
            var handler = new Handler(_ => throw new InvalidOperationException("network must not run"));
            using var api = new CurseForgeApiClient(secrets, handler);
            var provisioner = new CurseForgeCredentialProvisioner(secrets);
            Assert.False((await provisioner.ProvisionFromFileAsync(path, api)).SourcePresent);

            File.WriteAllText(path, "");
            Assert.False((await provisioner.ProvisionFromFileAsync(path, api)).Imported);

            File.WriteAllBytes(path, new byte[CurseForgeCredentialProvisioner.MaximumKeyFileBytes + 1]);
            Assert.False((await provisioner.ProvisionFromFileAsync(path, api)).Imported);

            File.WriteAllText(path, "synthetic-locked-key");
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var unreadable = await provisioner.ProvisionFromFileAsync(path, api);
            Assert.True(unreadable.SourcePresent);
            Assert.False(unreadable.Imported);
            Assert.Contains("could not be read", unreadable.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, handler.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Provisioning_rotates_an_existing_native_value_only_after_each_acceptance()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-curseforge-rotation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "curseforge-api-key.txt");
            var secrets = new MemorySecrets();
            var provisioner = new CurseForgeCredentialProvisioner(secrets);
            var handler = new Handler(_ => Accepted());
            using var api = new CurseForgeApiClient(secrets, handler);
            File.WriteAllText(path, "synthetic-first-key");
            Assert.True((await provisioner.ProvisionFromFileAsync(path, api)).Imported);
            File.WriteAllText(path, "synthetic-rotated-key");

            Assert.True((await provisioner.ProvisionFromFileAsync(path, api)).Imported);
            Assert.Equal("synthetic-rotated-key", secrets.GetSecret(CurseForgeUpdateProvider.ApiKeyName));
            Assert.Equal(2, handler.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Windows_dpapi_store_round_trips_the_authenticated_import_in_an_isolated_data_root()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-curseforge-dpapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var keyPath = Path.Combine(root, "curseforge-api-key.txt");
            File.WriteAllText(keyPath, "synthetic-dpapi-key");
            var store = new DpapiSecretStore(new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers")));
            using var api = new CurseForgeApiClient(store,
                new Handler(_ => Accepted()));

            var result = await new CurseForgeCredentialProvisioner(store)
                .ProvisionFromFileAsync(keyPath, api);

            Assert.True(result.Imported);
            Assert.Equal("synthetic-dpapi-key", store.GetSecret(CurseForgeUpdateProvider.ApiKeyName));
            Assert.DoesNotContain("synthetic-dpapi-key", File.ReadAllText(
                new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers")).SecretsPath),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("REPLACE_WITH_APPROVED_CURSEFORGE_APPLICATION_KEY")]
    [InlineData("two values")]
    [InlineData("short")]
    public async Task Placeholder_or_malformed_values_are_not_imported(string value)
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-curseforge-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "curseforge-api-key.txt");
            File.WriteAllText(path, value);
            var secrets = new MemorySecrets();
            var handler = new Handler(_ => throw new InvalidOperationException("network must not run"));
            using var api = new CurseForgeApiClient(secrets, handler);

            var result = await new CurseForgeCredentialProvisioner(secrets)
                .ProvisionFromFileAsync(path, api);

            Assert.True(result.SourcePresent);
            Assert.False(result.Imported);
            Assert.Null(secrets.GetSecret(CurseForgeUpdateProvider.ApiKeyName));
            Assert.DoesNotContain(value, result.Detail, StringComparison.Ordinal);
            Assert.Equal(0, handler.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Windows_dpapi_contains_checks_presence_without_decrypting_the_value()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(),
            "ChunkPilot-curseforge-dpapi-presence-" + Guid.NewGuid().ToString("N"));
        var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers"));
        paths.EnsureCreated();
        try
        {
            File.WriteAllText(paths.SecretsPath,
                "{\"curseforge-api-key\":\"not-valid-base64\"}");
            var store = new DpapiSecretStore(paths);

            Assert.True(store.Contains(CurseForgeUpdateProvider.ApiKeyName));
            Assert.Null(store.GetSecret(CurseForgeUpdateProvider.ApiKeyName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CurseForgeFailureKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, CurseForgeFailureKind.Authentication)]
    [InlineData(HttpStatusCode.TooManyRequests, CurseForgeFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, CurseForgeFailureKind.Server)]
    public async Task Failed_validation_preserves_the_last_valid_native_credential(
        HttpStatusCode status,
        CurseForgeFailureKind expectedKind)
    {
        await WithCandidateFileAsync("synthetic-candidate-key", async path =>
        {
            var secrets = new MemorySecrets();
            secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "synthetic-existing-key");
            using var api = new CurseForgeApiClient(secrets,
                new Handler(_ => new HttpResponseMessage(status)));

            var result = await new CurseForgeCredentialProvisioner(secrets)
                .ProvisionFromFileAsync(path, api);

            Assert.False(result.Imported);
            Assert.Equal(expectedKind, result.FailureKind);
            Assert.True(result.ExistingCredentialPreserved);
            Assert.Equal("synthetic-existing-key", secrets.GetSecret(CurseForgeUpdateProvider.ApiKeyName));
            Assert.DoesNotContain("synthetic-candidate-key", result.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-existing-key", result.Detail, StringComparison.Ordinal);
            if (expectedKind == CurseForgeFailureKind.Authentication)
                Assert.Contains("rejected", result.Detail, StringComparison.OrdinalIgnoreCase);
            else
                Assert.Contains("temporarily unavailable", result.Detail, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Offline_and_timeout_validation_preserve_the_last_valid_native_credential()
    {
        foreach (var failure in new Exception[]
                 {
                     new HttpRequestException("fixture offline"),
                     new TaskCanceledException("fixture timeout")
                 })
        {
            await WithCandidateFileAsync("synthetic-candidate-key", async path =>
            {
                var secrets = new MemorySecrets();
                secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "synthetic-existing-key");
                using var api = new CurseForgeApiClient(secrets,
                    new Handler((_, _) => Task.FromException<HttpResponseMessage>(failure)));

                var result = await new CurseForgeCredentialProvisioner(secrets)
                    .ProvisionFromFileAsync(path, api);

                Assert.False(result.Imported);
                Assert.True(result.ExistingCredentialPreserved);
                Assert.True(result.FailureKind is CurseForgeFailureKind.Offline or CurseForgeFailureKind.Timeout);
                Assert.Equal("synthetic-existing-key", secrets.GetSecret(CurseForgeUpdateProvider.ApiKeyName));
            });
        }
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_preserves_the_last_valid_native_credential()
    {
        await WithCandidateFileAsync("synthetic-candidate-key", async path =>
        {
            var secrets = new MemorySecrets();
            secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "synthetic-existing-key");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new Handler(async (_, cancellationToken) =>
            {
                entered.SetResult();
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                return Accepted();
            });
            using var api = new CurseForgeApiClient(secrets, handler);
            using var cancellation = new CancellationTokenSource();

            var provisioning = new CurseForgeCredentialProvisioner(secrets)
                .ProvisionFromFileAsync(path, api, cancellation.Token);
            await entered.Task;
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provisioning);
            Assert.Equal("synthetic-existing-key", secrets.GetSecret(CurseForgeUpdateProvider.ApiKeyName));
        });
    }

    [Fact]
    public async Task Candidate_is_sent_directly_but_not_persisted_until_live_validation_succeeds()
    {
        await WithCandidateFileAsync("synthetic-candidate-key", async path =>
        {
            var secrets = new MemorySecrets();
            secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "synthetic-existing-key");
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new Handler(async (request, cancellationToken) =>
            {
                Assert.Equal("synthetic-candidate-key", request.Headers.GetValues("x-api-key").Single());
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return Accepted();
            });
            using var api = new CurseForgeApiClient(secrets, handler);

            var provisioning = new CurseForgeCredentialProvisioner(secrets)
                .ProvisionFromFileAsync(path, api);
            await entered.Task;
            Assert.Equal("synthetic-existing-key", secrets.GetSecret(CurseForgeUpdateProvider.ApiKeyName));
            release.SetResult();

            var result = await provisioning;
            Assert.True(result.Imported);
            Assert.Equal("synthetic-candidate-key", secrets.GetSecret(CurseForgeUpdateProvider.ApiKeyName));
        });
    }

    [Fact]
    public async Task Rejected_first_import_leaves_native_storage_empty()
    {
        await WithCandidateFileAsync("synthetic-candidate-key", async path =>
        {
            var secrets = new MemorySecrets();
            using var api = new CurseForgeApiClient(secrets,
                new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

            var result = await new CurseForgeCredentialProvisioner(secrets)
                .ProvisionFromFileAsync(path, api);

            Assert.False(result.Imported);
            Assert.False(result.ExistingCredentialPreserved);
            Assert.Null(secrets.GetSecret(CurseForgeUpdateProvider.ApiKeyName));
        });
    }

    [Fact]
    public async Task Unexpected_game_identity_is_not_accepted_or_persisted()
    {
        await WithCandidateFileAsync("synthetic-candidate-key", async path =>
        {
            var secrets = new MemorySecrets();
            secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "synthetic-existing-key");
            using var api = new CurseForgeApiClient(secrets,
                new Handler(_ => Json("""{"data":{"id":1}}""")));

            var result = await new CurseForgeCredentialProvisioner(secrets)
                .ProvisionFromFileAsync(path, api);

            Assert.False(result.Imported);
            Assert.Equal(CurseForgeFailureKind.MalformedResponse, result.FailureKind);
            Assert.Equal("synthetic-existing-key", secrets.GetSecret(CurseForgeUpdateProvider.ApiKeyName));
        });
    }

    private static HttpResponseMessage Accepted() => Json("""{"data":{"id":432}}""");

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static async Task WithCandidateFileAsync(string value, Func<string, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(),
            "ChunkPilot-curseforge-candidate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "curseforge-api-key.txt");
            File.WriteAllText(path, value);
            await action(path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
        public bool Contains(string key) => values.ContainsKey(key);
        public void SetSecret(string key, string value) => values[key] = value;
        public string? GetSecret(string key) => values.GetValueOrDefault(key);
        public void Delete(string key) => values.Remove(key);
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response;
        private int count;

        public Handler(Func<HttpRequestMessage, HttpResponseMessage> response)
            : this((request, _) => Task.FromResult(response(request)))
        {
        }

        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
            this.response = response;

        public int Count => Volatile.Read(ref count);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref count);
            return response(request, cancellationToken);
        }
    }
}
