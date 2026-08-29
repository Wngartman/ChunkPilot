using System.Text.Json;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class CertificationUpdateFaultInjectorTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Disabled_registry_refuses_arming()
    {
        var root = TemporaryDirectory();
        try
        {
            var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers"));
            Environment.SetEnvironmentVariable(
                CertificationUpdateFaultInjector.TokenEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(
                CertificationUpdateFaultInjector.RuntimeRootEnvironmentVariable, null);
            var registry = CertificationUpdateFaultInjector.CreateFromEnvironment(paths);

            Assert.False(registry.Enabled);
            Assert.False(registry.Arm(Guid.NewGuid(), Guid.NewGuid(), Token).Success);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Exact_failure_is_idempotent_one_shot_and_mismatch_safe()
    {
        var registry = CertificationUpdateFaultInjector.CreateForTesting(Token);
        var serverId = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        Assert.True(registry.Arm(serverId, operationId, Token).Success);
        Assert.True(registry.Arm(serverId, operationId, Token).Success);
        Assert.False(registry.Arm(serverId, Guid.NewGuid(), Token).Success);
        Assert.False(registry.TryConsume(Guid.NewGuid(), operationId));
        Assert.False(registry.TryConsume(serverId, Guid.NewGuid()));
        Assert.True(registry.TryConsume(serverId, operationId));
        Assert.False(registry.TryConsume(serverId, operationId));
    }

    [Fact]
    public void Wrong_token_cannot_arm_failure()
    {
        var registry = CertificationUpdateFaultInjector.CreateForTesting(Token);
        Assert.False(registry.Arm(
            Guid.NewGuid(), Guid.NewGuid(), new string('f', 64)).Success);
    }

    [Fact]
    public void Monotonic_expiry_removes_armed_failure()
    {
        var clock = new ManualTimeProvider();
        var registry = CertificationUpdateFaultInjector.CreateForTesting(
            Token, clock, TimeSpan.FromSeconds(5));
        var serverId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        Assert.True(registry.Arm(serverId, operationId, Token).Success);

        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.False(registry.TryConsume(serverId, operationId));
        Assert.True(registry.Arm(serverId, operationId, Token).Success);
    }

    [Fact]
    public void Startup_authority_requires_exact_marked_fresh_run_and_clears_environment()
    {
        var root = TemporaryDirectory();
        var runtime = Path.Combine(root, "runtime");
        var run = Path.Combine(runtime, "runs", "run-test");
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(runtime,
            CertificationUpdateFaultInjector.RuntimeMarkerFileName), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            purpose = "ChunkPilot CurseForge runtime certification",
            repositoryFingerprint = new string('a', 64)
        }));
        var paths = new AppDataPaths(Path.Combine(run, "data"), Path.Combine(run, "servers"));
        try
        {
            Environment.SetEnvironmentVariable(
                CertificationUpdateFaultInjector.TokenEnvironmentVariable, Token);
            Environment.SetEnvironmentVariable(
                CertificationUpdateFaultInjector.RuntimeRootEnvironmentVariable, runtime);

            var registry = CertificationUpdateFaultInjector.CreateFromEnvironment(paths);

            Assert.True(registry.Enabled);
            Assert.Null(Environment.GetEnvironmentVariable(
                CertificationUpdateFaultInjector.TokenEnvironmentVariable));
            Assert.Null(Environment.GetEnvironmentVariable(
                CertificationUpdateFaultInjector.RuntimeRootEnvironmentVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                CertificationUpdateFaultInjector.TokenEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(
                CertificationUpdateFaultInjector.RuntimeRootEnvironmentVariable, null);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Startup_authority_refuses_unmarked_or_non_sibling_paths()
    {
        var root = TemporaryDirectory();
        var runtime = Path.Combine(root, "runtime");
        Directory.CreateDirectory(runtime);
        var paths = new AppDataPaths(
            Path.Combine(runtime, "runs", "run-test", "data"),
            Path.Combine(runtime, "other", "servers"));
        try
        {
            Environment.SetEnvironmentVariable(
                CertificationUpdateFaultInjector.TokenEnvironmentVariable, Token);
            Environment.SetEnvironmentVariable(
                CertificationUpdateFaultInjector.RuntimeRootEnvironmentVariable, runtime);

            Assert.False(CertificationUpdateFaultInjector.CreateFromEnvironment(paths).Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                CertificationUpdateFaultInjector.TokenEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(
                CertificationUpdateFaultInjector.RuntimeRootEnvironmentVariable, null);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ChunkPilot-certification-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan value) => timestamp += value.Ticks;
    }
}
