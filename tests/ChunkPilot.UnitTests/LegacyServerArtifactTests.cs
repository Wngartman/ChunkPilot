using System.IO.Compression;
using System.Text.Json;
using ChunkPilot.App.WebUi;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class LegacyServerArtifactTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-legacy-artifact-" + Guid.NewGuid().ToString("N"));

    public LegacyServerArtifactTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Inspector_accepts_a_bounded_server_jar_without_executing_it()
    {
        var path = CreateJar("server.jar", serverClass: true);

        var result = await new LegacyServerArtifactInspector().InspectAsync(path, "b1.8.1");

        Assert.Equal("b1.8.1", result.MinecraftVersion);
        Assert.Equal("server.jar", result.FileName);
        Assert.Equal(64, result.Sha256.Length);
        Assert.False(result.MatchesOfficialHash);
        Assert.Contains("user-supplied", result.IdentityEvidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspector_rejects_a_client_or_malformed_jar()
    {
        var path = CreateJar("client.jar", serverClass: false);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => new LegacyServerArtifactInspector().InspectAsync(path, "b1.8"));

        Assert.Contains("server", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Opaque_token_is_version_bound_single_use_and_detects_source_changes()
    {
        var now = new DateTimeOffset(2026, 8, 20, 16, 0, 0, TimeSpan.Zero);
        var inspector = new LegacyServerArtifactInspector();
        var path = CreateJar("private-server.jar", serverClass: true);
        var artifact = await inspector.InspectAsync(path, "b1.8.1");
        var tokens = new WebUiLegacyArtifactTokenStore(inspector, () => now);
        var token = tokens.Issue(artifact);

        var rendererJson = JsonSerializer.Serialize(token, WebUiProtocol.Json);
        Assert.DoesNotContain(root, rendererJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-server.jar" + Path.DirectorySeparatorChar, rendererJson, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<ArgumentException>(() => tokens.ConsumeAsync("b1.8", token.Token, CancellationToken.None));

        token = tokens.Issue(artifact);
        await File.AppendAllTextAsync(path, "changed");
        await Assert.ThrowsAnyAsync<Exception>(() => tokens.ConsumeAsync("b1.8.1", token.Token, CancellationToken.None));

        path = CreateJar("second.jar", serverClass: true);
        artifact = await inspector.InspectAsync(path, "1.0");
        token = tokens.Issue(artifact);
        _ = await tokens.ConsumeAsync("1.0", token.Token, CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => tokens.ConsumeAsync("1.0", token.Token, CancellationToken.None));

        token = tokens.Issue(artifact);
        now = now.AddMinutes(6);
        await Assert.ThrowsAsync<ArgumentException>(() => tokens.ConsumeAsync("1.0", token.Token, CancellationToken.None));
    }

    [Theory]
    [InlineData("1.0", MinecraftReleaseKind.Release)]
    [InlineData("b1.8", MinecraftReleaseKind.Beta)]
    [InlineData("b1.8.1", MinecraftReleaseKind.Beta)]
    public void Exact_historical_targets_have_a_curated_Java_and_headless_launch_profile(
        string version, MinecraftReleaseKind kind)
    {
        var profile = MinecraftLaunchProfileResolver.Resolve(version, kind,
            new DateTimeOffset(2011, 9, 15, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(8, JavaRuntimePolicy.TryRequiredMajorForMinecraft(version));
        Assert.True(profile.IsResolved);
        Assert.Equal(MinecraftLaunchProfileKind.LegacyNogui, profile.Kind);
        Assert.True(profile.Capabilities.StatusQuery);
    }

    [Fact]
    public void Reviewed_user_artifact_unblocks_only_its_exact_historical_creation_plan()
    {
        var version = new VanillaVersionOption
        {
            VersionId = "b1.8.1",
            HasServerDownload = false,
            RequiredJavaMajor = 8,
            LaunchProfile = MinecraftLaunchProfileResolver.Resolve("b1.8.1", MinecraftReleaseKind.Beta,
                new DateTimeOffset(2011, 9, 19, 0, 0, 0, TimeSpan.Zero))
        };
        var plan = new VanillaCreationPlan
        {
            ServerName = "Historical fixture",
            Version = version,
            Eula = new VanillaEulaAcceptance
            {
                Accepted = true,
                AcceptedAtUtc = DateTimeOffset.UtcNow,
                SourceUrl = VanillaEulaAcceptance.OfficialSourceUrl
            },
            UserSuppliedArtifact = new UserSuppliedServerArtifact
            {
                NativePath = @"D:\fixture\server.jar",
                FileName = "server.jar",
                MinecraftVersion = "b1.8.1",
                SizeBytes = 1024,
                Sha256 = new string('a', 64)
            }
        };

        Assert.Empty(plan.Problems());
        Assert.Contains("different Minecraft version",
            string.Join(' ', (plan with { UserSuppliedArtifact = plan.UserSuppliedArtifact with { MinecraftVersion = "1.0" } }).Problems()),
            StringComparison.OrdinalIgnoreCase);
    }

    private string CreateJar(string fileName, bool serverClass)
    {
        var path = Path.Combine(root, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(archive, "META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nMain-Class: net.minecraft.server.MinecraftServer\n");
        for (var index = 0; index < 30; index++)
            Write(archive, index == 0 && serverClass
                ? "net/minecraft/server/MinecraftServer.class"
                : $"net/minecraft/client/Fixture{index}.class", "fixture");
        return path;
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
