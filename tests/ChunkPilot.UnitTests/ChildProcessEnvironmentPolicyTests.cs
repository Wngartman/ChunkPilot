using System.Diagnostics;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class ChildProcessEnvironmentPolicyTests
{
    [Fact]
    public void Apply_keeps_only_the_bounded_inherited_baseline()
    {
        var start = NewStartInfo();
        start.Environment["SystemRoot"] = @"C:\Windows";
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = @"D:\task-temp";
        start.Environment["CHUNKPILOT_SYNTHETIC_PARENT_SECRET"] = "must-not-cross-boundary";
        start.Environment["JAVA_TOOL_OPTIONS"] = "-Dsynthetic.secret=must-not-cross-boundary";

        ChildProcessEnvironmentPolicy.Apply(start);

        Assert.Equal(@"C:\Windows", start.Environment["SystemRoot"]);
        Assert.Equal(@"D:\task-temp", start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"]);
        Assert.False(start.Environment.ContainsKey("CHUNKPILOT_SYNTHETIC_PARENT_SECRET"));
        Assert.False(start.Environment.ContainsKey("JAVA_TOOL_OPTIONS"));
        Assert.DoesNotContain(start.Environment.Values,
            value => value is not null &&
                     value.Contains("must-not-cross-boundary", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_adds_server_explicit_environment_after_restricting_inheritance()
    {
        var start = NewStartInfo();
        start.Environment["UNRELATED_PARENT_VALUE"] = "drop-me";
        var explicitEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SERVER_EXPLICIT_VALUE"] = "keep-me",
            ["Path"] = @"D:\server-tools"
        };

        ChildProcessEnvironmentPolicy.Apply(start, explicitEnvironment);

        Assert.False(start.Environment.ContainsKey("UNRELATED_PARENT_VALUE"));
        Assert.Equal("keep-me", start.Environment["SERVER_EXPLICIT_VALUE"]);
        Assert.Equal(@"D:\server-tools", start.Environment["Path"]);
    }

    [Fact]
    public void Apply_refuses_shell_execution_that_cannot_use_an_explicit_environment()
    {
        var start = new ProcessStartInfo("synthetic.exe") { UseShellExecute = true };

        Assert.Throws<InvalidOperationException>(() => ChildProcessEnvironmentPolicy.Apply(start));
    }

    private static ProcessStartInfo NewStartInfo() => new("synthetic.exe")
    {
        UseShellExecute = false
    };
}
