using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class DetectionAndParsingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-tests-" + Guid.NewGuid().ToString("N"));

    public DetectionAndParsingTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Batch_parser_extracts_java_arguments_variables_and_detach_warning()
    {
        var script = Path.Combine(root, "start.bat");
        File.WriteAllText(script, """
            @echo off
            set "JAVA=C:\Program Files\Java\bin\java.exe"
            start "" "%JAVA%" @user_jvm_args.txt -jar server.jar nogui
            """);
        var parsed = new BatchFileParser().Parse(script);
        Assert.True(parsed.Detaches);
        Assert.Contains("server.jar", parsed.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(parsed.Problems, problem => problem.Contains("detach", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Server_detection_is_bounded_read_only_and_ranks_common_script()
    {
        var script = Path.Combine(root, "run.bat");
        var properties = Path.Combine(root, "server.properties");
        File.WriteAllText(script, "@echo off\r\njava -Xmx4G -jar paper-1.21.1.jar nogui\r\n");
        File.WriteAllText(properties, "server-port=25570\r\n");
        using (var archive = System.IO.Compression.ZipFile.Open(Path.Combine(root, "paper-1.21.1.jar"), System.IO.Compression.ZipArchiveMode.Create))
            archive.CreateEntry("META-INF/MANIFEST.MF");
        var before = Directory.EnumerateFiles(root).ToDictionary(path => path, File.GetLastWriteTimeUtc);
        var detector = new ServerDetectionService(new JavaDiscoveryService());
        var result = await detector.DetectAsync(root);
        var after = Directory.EnumerateFiles(root).ToDictionary(path => path, File.GetLastWriteTimeUtc);
        Assert.Equal(25570, result.Port);
        Assert.Equal(ServerEcosystem.Paper, result.Ecosystem);
        Assert.NotEmpty(result.Candidates);
        Assert.Equal("run.bat", result.Candidates[0].DisplayName, ignoreCase: true);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Import_candidate_reports_detaching_scripts_instead_of_hiding_problem()
    {
        var script = Path.Combine(root, "start.cmd");
        File.WriteAllText(script, "start \"\" java -jar server.jar nogui");
        var parsed = new BatchFileParser().Parse(script);
        Assert.True(parsed.Detaches);
        Assert.NotEmpty(parsed.Problems);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}

