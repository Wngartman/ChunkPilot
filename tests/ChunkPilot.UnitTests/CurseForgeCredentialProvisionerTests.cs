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
    public void Approved_file_is_imported_without_returning_credential_material()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-curseforge-credential-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sentinel = string.Concat("CURSEFORGE-SENTINEL-", Guid.NewGuid().ToString("N"));
            var path = Path.Combine(root, "curseforge-api-key.txt");
            File.WriteAllText(path, "  " + sentinel + Environment.NewLine);
            var secrets = new MemorySecrets();

            var result = new CurseForgeCredentialProvisioner(secrets).ProvisionFromFile(path);

            Assert.True(result.SourcePresent);
            Assert.True(result.Imported);
            Assert.Equal(sentinel, secrets.GetSecret(CurseForgeUpdateProvider.ApiKeyName));
            Assert.DoesNotContain(sentinel, result.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(path, result.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_empty_oversized_and_unreadable_sources_are_rejected_generically()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-curseforge-boundaries-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "curseforge-api-key.txt");
            var provisioner = new CurseForgeCredentialProvisioner(new MemorySecrets());
            Assert.False(provisioner.ProvisionFromFile(path).SourcePresent);

            File.WriteAllText(path, "");
            Assert.False(provisioner.ProvisionFromFile(path).Imported);

            File.WriteAllBytes(path, new byte[CurseForgeCredentialProvisioner.MaximumKeyFileBytes + 1]);
            Assert.False(provisioner.ProvisionFromFile(path).Imported);

            File.WriteAllText(path, "synthetic-locked-key");
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var unreadable = provisioner.ProvisionFromFile(path);
            Assert.True(unreadable.SourcePresent);
            Assert.False(unreadable.Imported);
            Assert.Contains("could not be read", unreadable.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Provisioning_rotates_an_existing_native_value()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-curseforge-rotation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "curseforge-api-key.txt");
            var secrets = new MemorySecrets();
            var provisioner = new CurseForgeCredentialProvisioner(secrets);
            File.WriteAllText(path, "synthetic-first-key");
            Assert.True(provisioner.ProvisionFromFile(path).Imported);
            File.WriteAllText(path, "synthetic-rotated-key");

            Assert.True(provisioner.ProvisionFromFile(path).Imported);
            Assert.Equal("synthetic-rotated-key", secrets.GetSecret(CurseForgeUpdateProvider.ApiKeyName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Windows_dpapi_store_round_trips_the_import_in_an_isolated_data_root()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-curseforge-dpapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var keyPath = Path.Combine(root, "curseforge-api-key.txt");
            File.WriteAllText(keyPath, "synthetic-dpapi-key");
            var store = new DpapiSecretStore(new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers")));

            var result = new CurseForgeCredentialProvisioner(store).ProvisionFromFile(keyPath);

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
    public void Placeholder_or_malformed_values_are_not_imported(string value)
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-curseforge-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "curseforge-api-key.txt");
            File.WriteAllText(path, value);
            var secrets = new MemorySecrets();

            var result = new CurseForgeCredentialProvisioner(secrets).ProvisionFromFile(path);

            Assert.True(result.SourcePresent);
            Assert.False(result.Imported);
            Assert.Null(secrets.GetSecret(CurseForgeUpdateProvider.ApiKeyName));
            Assert.DoesNotContain(value, result.Detail, StringComparison.Ordinal);
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
}
