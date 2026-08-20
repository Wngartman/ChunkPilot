using ChunkPilot.App;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class TroubleshootingServiceTests
{
    [Fact]
    public void Startup_help_only_appears_for_an_attempt_that_never_reached_readiness()
    {
        var definition = new ServerDefinition();

        Assert.False(MainViewModel.IsFailedStart(new ServerSnapshot
            { Definition = definition, State = ServerState.Stopped }));
        Assert.True(MainViewModel.IsFailedStart(new ServerSnapshot
            { Definition = definition, State = ServerState.Crashed, LastError = "Port conflict" }));
        Assert.False(MainViewModel.IsFailedStart(new ServerSnapshot
        {
            Definition = definition,
            State = ServerState.Crashed,
            LastError = "Process exited later",
            LastStartReachedReadiness = true
        }));
    }

    [Fact]
    public void Port_conflict_is_ranked_as_an_exact_high_probability_fix()
    {
        var snapshot = Snapshot("Port 25565 is already in use. Stop the other server or choose another port.", 25565);

        var report = TroubleshootingService.Analyze(snapshot);

        Assert.True(report.HasLikelyFix);
        Assert.Equal("port.conflict", report.MostLikely!.Code);
        Assert.Equal("Port 25565 is already in use", report.MostLikely.Title);
        Assert.Equal(99, report.MostLikely.Confidence);
        Assert.Contains(report.MostLikely.Steps, step => step.Contains("server-port", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("UnsupportedClassVersionError: class file version 65.0", "java.version")]
    [InlineData("Unrecognized VM option 'UseOldGC'", "java.arguments")]
    [InlineData("java.lang.OutOfMemoryError: Java heap space", "java.memory")]
    [InlineData("Mod alpha requires library beta which is missing", "mod.dependency")]
    [InlineData("Mixin apply failed example.mixins.json", "mod.mixin")]
    [InlineData("java.lang.NoClassDefFoundError: example/library/Api", "extension.binary")]
    [InlineData("Missing registry data for minecraft:block", "mod.registry")]
    [InlineData("Error: Unable to access jarfile server.jar", "jar.invalid")]
    [InlineData("Failed to lock the world because session.lock is held", "world.lock")]
    [InlineData("Exception loading level: world may be corrupt", "world.corrupt")]
    [InlineData("System.UnauthorizedAccessException: Access is denied", "storage.permission")]
    [InlineData("Authentication servers are down. Please try again later.", "auth.session")]
    [InlineData("A single server tick took 60.00 seconds", "watchdog.timeout")]
    [InlineData("java.net.BindException: Cannot assign requested address", "network.bind")]
    [InlineData("java.lang.UnsatisfiedLinkError: failed to load native library", "java.native")]
    [InlineData("Error occurred while enabling ExamplePlugin", "plugin.datapack")]
    public void Recognizes_common_and_less_common_hosting_failures(string error, string expectedCode)
    {
        var report = TroubleshootingService.Analyze(error);

        Assert.Equal(expectedCode, report.MostLikely?.Code);
        Assert.NotEmpty(report.MostLikely!.Steps);
        Assert.False(string.IsNullOrWhiteSpace(report.MostLikely.Evidence));
    }

    [Fact]
    public void Multiple_signatures_are_ranked_and_limited_to_three_explainable_candidates()
    {
        var report = TroubleshootingService.Analyze("""
            Mod alpha requires library beta which is missing
            Mixin apply failed example.mixins.json
            java.lang.OutOfMemoryError: Java heap space
            Failed to load datapack broken.zip
            """);

        Assert.Equal(["java.memory", "mod.dependency", "mod.mixin"], report.Matches.Select(match => match.Code));
    }

    [Fact]
    public void Activity_errors_use_the_same_classifier_and_unknown_text_does_not_invent_a_fix()
    {
        var activity = new ActivityEntry { Error = "You need to agree to the EULA in order to run the server" };

        Assert.Equal("eula.rejected", TroubleshootingService.Analyze(activity).MostLikely?.Code);
        Assert.False(TroubleshootingService.Analyze("Operation failed for an unknown reason").HasLikelyFix);
    }

    [Fact]
    public void Normal_session_service_startup_line_is_not_misdiagnosed_as_an_auth_outage()
    {
        var report = TroubleshootingService.Analyze(
            "Environment: Environment[sessionHost=https://sessionserver.mojang.com, name=PROD]");

        Assert.False(report.HasLikelyFix);
    }

    [Fact]
    public void Crash_analysis_never_calls_one_pattern_match_confirmed()
    {
        var report = CrashAnalysisService.Analyze(new CrashAnalysisInput
        {
            ServerId = Guid.NewGuid(),
            ExitCode = 1,
            ConfiguredPort = 25565,
            Evidence = [new CrashEvidenceInput("Console tail", "java.lang.OutOfMemoryError: Java heap space")]
        });

        Assert.Equal("java.memory", report.Code);
        Assert.Equal(CrashConfidence.HighlyLikely, report.Confidence);
        Assert.NotEqual(CrashConfidence.Confirmed, report.Confidence);
        Assert.Single(report.Evidence);
        Assert.Contains(report.SafeActions, action => action.Code == "support-bundle");
    }

    [Fact]
    public void Crash_analysis_correlates_distinct_sources_and_redacts_evidence()
    {
        var report = CrashAnalysisService.Analyze(new CrashAnalysisInput
        {
            ServerId = Guid.NewGuid(),
            ExitCode = 1,
            Evidence =
            [
                new CrashEvidenceInput("Console tail", "Mod alpha requires library beta which is missing token=hunter2"),
                new CrashEvidenceInput("Latest log", "Mandatory dependencies were not found: beta")
            ]
        });

        Assert.Equal("mod.dependency", report.Code);
        Assert.Equal(CrashConfidence.HighlyLikely, report.Confidence);
        Assert.Equal(2, report.Evidence.Count);
        Assert.DoesNotContain("hunter2", string.Join(' ', report.Evidence.Select(item => item.Excerpt)));
    }

    [Fact]
    public void Crash_analysis_unknown_state_stays_truthful_and_bounded()
    {
        var report = CrashAnalysisService.Analyze(new CrashAnalysisInput
        {
            ServerId = Guid.NewGuid(),
            ExitCode = -1,
            Evidence = [new CrashEvidenceInput("Console tail", new string('x', 700))]
        });

        Assert.Equal("unknown", report.Code);
        Assert.Equal(CrashConfidence.Unknown, report.Confidence);
        Assert.Single(report.Evidence);
        Assert.True(report.Evidence[0].Excerpt.Length <= 280);
    }

    [Fact]
    public async Task Current_latest_log_outranks_an_older_crash_report()
    {
        var testParent = Path.Combine(AppContext.BaseDirectory, "test-temp");
        var root = Path.Combine(testParent, Guid.NewGuid().ToString("N"));
        var serverRoot = Path.Combine(root, "server");
        Directory.CreateDirectory(Path.Combine(serverRoot, "logs"));
        Directory.CreateDirectory(Path.Combine(serverRoot, "crash-reports"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(serverRoot, "logs", "latest.log"),
                "Mod alpha requires library beta which is missing");
            await File.WriteAllTextAsync(Path.Combine(serverRoot, "crash-reports", "old.txt"),
                "java.lang.OutOfMemoryError: Java heap space");

            var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "managed"));
            var files = new SafeFileService(paths);
            var service = new DiagnosticsService(paths, new JarInventoryService(files, paths));
            var snapshot = Snapshot("", 25565) with
            {
                Definition = Snapshot("", 25565).Definition with
                {
                    RootPath = serverRoot,
                    WorkingDirectory = serverRoot
                }
            };

            var report = await service.TroubleshootAsync(snapshot);

            Assert.Equal("mod.dependency", report.MostLikely?.Code);
        }
        finally
        {
            if (Directory.Exists(root) && Path.GetFullPath(root).StartsWith(Path.GetFullPath(testParent), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, recursive: true);
        }
    }

    private static ServerSnapshot Snapshot(string error, int port) => new()
    {
        Definition = new ServerDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            RootPath = @"D:\Servers\Test",
            Executable = "java.exe",
            WorkingDirectory = @"D:\Servers\Test",
            Port = port
        },
        LastError = error,
        State = ServerState.Crashed
    };
}
