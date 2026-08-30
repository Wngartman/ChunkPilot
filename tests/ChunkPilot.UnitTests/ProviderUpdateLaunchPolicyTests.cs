using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class ProviderUpdateLaunchPolicyTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "ChunkPilot-provider-launch-" + Guid.NewGuid().ToString("N"));

    public ProviderUpdateLaunchPolicyTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Provider_scripts_remain_inert_when_one_native_server_jar_exists()
    {
        var bat = Write("run.bat", "@echo off\r\njava -jar server.jar\r\n");
        var cmd = Write("start.cmd", "java -jar server.jar\r\n");
        var ps1 = Write("launch.ps1", "java -jar server.jar\r\n");
        var jar = Write("server.jar", "fixture");
        var detected = Detection(
            Candidate(bat, "cmd.exe"),
            Candidate(cmd, "cmd.exe"),
            Candidate(ps1, "powershell.exe"),
            Candidate(jar, "java"));

        var selected = ServerPackUpdateService.SelectProviderNativeLaunch(root, detected);

        Assert.Empty(selected.Executable);
        Assert.Equal(jar, selected.SourcePath);
        Assert.Contains("-jar", selected.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain(".bat", selected.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".cmd", selected.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".ps1", selected.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("@echo off\r\njava -jar server.jar\r\n", File.ReadAllText(bat));
        Assert.Equal("java -jar server.jar\r\n", File.ReadAllText(cmd));
        Assert.Equal("java -jar server.jar\r\n", File.ReadAllText(ps1));
    }

    [Fact]
    public void Provider_script_only_pack_fails_closed_without_altering_the_script()
    {
        const string contents = "@echo off\r\necho provider-controlled\r\n";
        var script = Write("run.bat", contents);
        var detected = Detection(Candidate(script, "cmd.exe"));

        var exception = Assert.Throws<InvalidDataException>(() =>
            ServerPackUpdateService.SelectProviderNativeLaunch(root, detected));

        Assert.Contains("never executed automatically", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(contents, File.ReadAllText(script));
    }

    [Fact]
    public void Provider_argument_file_and_script_remain_inert_without_a_known_server_jar()
    {
        var script = Write("run.ps1", "java @libraries\\net\\minecraftforge\\win_args.txt\r\n");
        const string argumentContents = "-cp libraries\\* net.minecraftforge.bootstrap.ForgeBootstrap";
        var arguments = Write(
            Path.Combine("libraries", "net", "minecraftforge", "win_args.txt"),
            argumentContents);
        var detected = Detection(Candidate(script, "powershell.exe"));

        Assert.Null(ServerPackUpdateService.TrySelectProviderNativeLaunch(root, detected));
        var exception = Assert.Throws<InvalidDataException>(() =>
            ServerPackUpdateService.SelectProviderNativeLaunch(root, detected));

        Assert.Contains("argument files", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(script));
        Assert.Equal(argumentContents, File.ReadAllText(arguments));
    }

    [Fact]
    public void Provider_pack_with_multiple_native_profiles_fails_closed()
    {
        var serverJar = Write("server.jar", "fixture-a");
        var paperJar = Write("paper-1.21.1.jar", "fixture-b");
        var detected = Detection(
            Candidate(serverJar, "java"),
            Candidate(paperJar, "java"));

        var exception = Assert.Throws<InvalidDataException>(() =>
            ServerPackUpdateService.SelectProviderNativeLaunch(root, detected));

        Assert.Contains("multiple ambiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Root_content_jar_is_inert_without_an_exact_materializer_profile()
    {
        var script = Write("run.cmd", "java -jar server-content.jar\r\n");
        var contentJar = Write("server-content.jar", "fixture-content");
        var detected = Detection(
            Candidate(script, "cmd.exe"),
            Candidate(contentJar, "java"));

        var exception = Assert.Throws<InvalidDataException>(() =>
            ServerPackUpdateService.SelectProviderNativeLaunch(root, detected));

        Assert.Contains("no unambiguous native Java", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("fixture-content", File.ReadAllText(contentJar));
        Assert.True(File.Exists(script));
    }

    [Theory]
    [InlineData("Fabric", InstallSourceType.Fabric)]
    [InlineData("Quilt", InstallSourceType.Quilt)]
    [InlineData("Forge", InstallSourceType.Forge)]
    [InlineData("NeoForge", InstallSourceType.NeoForge)]
    public void Script_only_provider_pack_can_materialize_only_an_exact_supported_loader(
        string loader,
        InstallSourceType expected)
    {
        var script = Write("run.bat", "java provider-script\r\n");
        var detected = Detection(Candidate(script, "cmd.exe"));
        var target = new PackVersionInfo
        {
            MinecraftVersion = "1.21.1",
            Loader = loader,
            LoaderVersion = "exact-loader-version"
        };

        Assert.Null(ServerPackUpdateService.TrySelectProviderNativeLaunch(root, detected));
        Assert.Equal(expected, ServerPackUpdateService.ResolveExactManagedProviderLoader(target));
        Assert.True(File.Exists(script));
    }

    [Theory]
    [InlineData("Custom", "1.0")]
    [InlineData("Fabric", "")]
    public void Script_only_provider_pack_fails_when_loader_identity_is_not_exact(
        string loader,
        string loaderVersion)
    {
        var target = new PackVersionInfo
        {
            MinecraftVersion = "1.21.1",
            Loader = loader,
            LoaderVersion = loaderVersion
        };

        Assert.Throws<InvalidDataException>(() =>
            ServerPackUpdateService.ResolveExactManagedProviderLoader(target));
    }

    [Fact]
    public void Provider_argument_file_does_not_make_one_known_server_jar_ambiguous()
    {
        var script = Write("run.bat", "java -jar server.jar\r\n");
        var arguments = Write(
            Path.Combine("libraries", "net", "minecraftforge", "win_args.txt"),
            "provider-controlled arguments");
        var serverJar = Write("server.jar", "fixture-a");
        var detected = Detection(
            Candidate(script, "cmd.exe"),
            Candidate(arguments, "java"),
            Candidate(serverJar, "java"));

        var selected = ServerPackUpdateService.SelectProviderNativeLaunch(root, detected);

        Assert.Equal(serverJar, selected.SourcePath);
        Assert.Empty(selected.Executable);
        Assert.DoesNotContain("run.bat", selected.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("win_args.txt", selected.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("provider-controlled arguments", File.ReadAllText(arguments));
        Assert.True(File.Exists(script));
    }

    private string Write(string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private ServerDetectionResult Detection(params LaunchCandidate[] candidates) => new()
    {
        RootPath = root,
        Candidates = candidates
    };

    private static LaunchCandidate Candidate(string sourcePath, string executable) => new()
    {
        DisplayName = Path.GetFileName(sourcePath),
        SourcePath = sourcePath,
        Executable = executable,
        Arguments = sourcePath,
        WorkingDirectory = Path.GetDirectoryName(sourcePath)!,
        Recommendation = RecommendationLevel.Recommended
    };

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
