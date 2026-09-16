using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class JavaArgumentFilePathTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-argfile-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(259, false)]
    [InlineData(260, true)]
    [InlineData(275, true)]
    public void Only_long_canonical_windows_paths_receive_the_native_prefix(int length, bool extended)
    {
        var path = @"D:\" + new string('a', length - 16) + @"\win_args.txt";
        Assert.Equal(length, path.Length);
        Assert.Equal(extended ? @"\\?\" + path : path, JavaArgumentFilePath.ForLauncher(path));
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(260, true)]
    public void Unc_paths_use_the_explicit_extended_unc_namespace(int fillerLength, bool extended)
    {
        var path = @"\\fixture-host\share\" + new string('a', fillerLength) + @"\win_args.txt";
        Assert.Equal(extended ? @"\\?\UNC\" + path[2..] : path, JavaArgumentFilePath.ForLauncher(path));
    }

    [Theory]
    [InlineData("relative\\win_args.txt")]
    [InlineData("D:win_args.txt")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\D:\win_args.txt")]
    [InlineData("D:\\safe\n\\win_args.txt")]
    public void Relative_device_and_control_paths_are_not_launcher_authority(string path) =>
        Assert.Throws<ArgumentException>(() => JavaArgumentFilePath.ForLauncher(path));

    [Fact]
    public void Long_unicode_and_space_paths_remain_one_quoted_argument()
    {
        var path = @"D:\space café\" + new string('a', 250) + @"\win_args.txt";
        Assert.Equal("@\"" + @"\\?\" + path + "\" nogui", JavaArgumentFilePath.LaunchArguments(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Provider_argfile_rebasing_rechecks_the_final_path_limit(bool longCandidate)
    {
        var candidate = Path.Combine(root, longCandidate ? new string('c', 170) : "candidate");
        var active = Path.Combine(root, longCandidate ? "active" : new string('a', 170));
        const string relative = "libraries/net/neoforged/neoforge/21.1.249/win_args.txt";
        var launchPath = Path.GetFullPath(Path.Combine(candidate, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(launchPath)!);
        File.WriteAllText(launchPath, "-version");
        var java = Path.Combine(root, "java.exe");
        File.WriteAllText(java, "fixture; never executed");
        var launch = ServerPackUpdateService.SelectVerifiedMaterializedLaunch(candidate, launchPath, true)
            with { Executable = java };
        var current = new ServerDefinition { RootPath = active, Executable = java, WorkingDirectory = active };
        var updated = ServerPackUpdateService.BuildDefinition(current, candidate, launch,
            new PackVersionInfo { Loader = "NeoForge" });

        Assert.Equal(JavaArgumentFilePath.LaunchArguments(Path.GetFullPath(Path.Combine(active, relative))), updated.Arguments);
        Assert.DoesNotContain(candidate, updated.Arguments, StringComparison.OrdinalIgnoreCase);
        ServerPackUpdateService.ValidateCandidate(candidate, updated, launch);
        Assert.True(File.Exists(launchPath));
    }

    [Theory]
    [InlineData("server.jar", "-jar ")]
    [InlineData("run.bat", "/c ")]
    [InlineData("run.ps1", "-File ")]
    [InlineData("win_args.txt", "@")]
    public void Existing_custom_extended_path_profiles_keep_their_original_rebase_behavior(string file, string prefix)
    {
        const string candidate = @"D:\candidate";
        const string active = @"D:\active";
        var source = @"\\?\" + candidate + "\\" + file;
        var launch = new LaunchCandidate
        {
            SourcePath = source, Executable = "java", Arguments = prefix + source + " nogui",
            WorkingDirectory = candidate
        };
        var updated = ServerPackUpdateService.BuildDefinition(new ServerDefinition { RootPath = active },
            candidate, launch, new PackVersionInfo());

        Assert.Equal(launch.Arguments.Replace(candidate, active, StringComparison.Ordinal), updated.Arguments);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("foreign")]
    [InlineData("working-directory")]
    public void Provider_profile_guard_rejects_extra_foreign_or_rebased_directory_arguments(string failure)
    {
        var candidate = Path.Combine(root, "candidate");
        var path = Path.Combine(candidate, "libraries", "win_args.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "-version");
        var java = Path.Combine(root, "java.exe");
        File.WriteAllText(java, "fixture; never executed");
        var launch = ServerPackUpdateService.SelectVerifiedMaterializedLaunch(candidate, path, true) with { Executable = java };
        var current = new ServerDefinition { RootPath = Path.Combine(root, "active"), Executable = java };
        var updated = ServerPackUpdateService.BuildDefinition(current, candidate, launch, new PackVersionInfo());
        updated = failure switch
        {
            "extra" => updated with { Arguments = updated.Arguments + " @foreign.txt" },
            "foreign" => updated with { Arguments = JavaArgumentFilePath.LaunchArguments(Path.Combine(root, "foreign.txt")) },
            _ => updated with { WorkingDirectory = root }
        };
        Assert.Throws<InvalidDataException>(() => ServerPackUpdateService.ValidateCandidate(candidate, updated, launch));
    }

    [Fact]
    public void Existing_native_extended_path_jar_passes_the_exact_profile_guard_without_using_the_argfile_adapter()
    {
        var candidate = @"\\?\" + Path.Combine(root, "candidate");
        Directory.CreateDirectory(candidate);
        var jar = Path.Combine(candidate, "server.jar");
        File.WriteAllText(jar, "fixture; never executed");
        var java = Path.Combine(root, "java.exe");
        File.WriteAllText(java, "fixture; never executed");
        var launch = ServerPackUpdateService.SelectProviderNativeLaunch(candidate, new ServerDetectionResult
        {
            RootPath = candidate,
            Candidates = [new LaunchCandidate { SourcePath = jar, Executable = "java" }]
        }) with { Executable = java };
        var active = @"\\?\" + Path.Combine(root, "active");
        var updated = ServerPackUpdateService.BuildDefinition(new ServerDefinition { RootPath = active },
            candidate, launch, new PackVersionInfo());

        ServerPackUpdateService.ValidateCandidate(candidate, updated, launch);
        Assert.Equal($"-jar {CommandLineQuoter.QuoteWindowsArgument(Path.Combine(active, "server.jar"))} nogui", updated.Arguments);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
