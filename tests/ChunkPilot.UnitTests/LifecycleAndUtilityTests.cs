using ChunkPilot.Core;

namespace ChunkPilot.UnitTests;

public sealed class LifecycleAndUtilityTests
{
    [Fact]
    public void Lifecycle_rejects_duplicate_or_invalid_start_sequences()
    {
        var lifecycle = new LifecycleStateMachine();
        lifecycle.TransitionTo(ServerState.Starting);
        Assert.False(lifecycle.CanTransitionTo(ServerState.Starting) is false);
        Assert.False(lifecycle.CanTransitionTo(ServerState.Restoring));
        lifecycle.TransitionTo(ServerState.Running);
        lifecycle.TransitionTo(ServerState.Saving);
        lifecycle.TransitionTo(ServerState.Running);
        lifecycle.TransitionTo(ServerState.Stopping);
        lifecycle.TransitionTo(ServerState.Stopped);
        Assert.Equal(ServerState.Stopped, lifecycle.State);
    }

    [Fact]
    public void Windows_argument_quoting_handles_spaces_quotes_and_trailing_slashes()
    {
        Assert.Equal("plain", CommandLineQuoter.QuoteWindowsArgument("plain"));
        Assert.Equal("\"path with spaces\"", CommandLineQuoter.QuoteWindowsArgument("path with spaces"));
        Assert.Equal("\"a\\\"b\"", CommandLineQuoter.QuoteWindowsArgument("a\"b"));
        Assert.EndsWith("\\\\\"", CommandLineQuoter.QuoteWindowsArgument(@"C:\path with space\"));
    }

    [Fact]
    public void Console_buffer_is_bounded_and_removes_ansi_sequences()
    {
        var buffer = new BoundedConsoleBuffer(100);
        for (var index = 0; index < 150; index++)
            buffer.Add("stdout", $"\u001b[31mline {index}\u001b[0m");
        var snapshot = buffer.Snapshot(200);
        Assert.Equal(100, snapshot.Count);
        Assert.DoesNotContain("\u001b", snapshot[0].Text, StringComparison.Ordinal);
        Assert.Contains("line 149", snapshot[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_redaction_removes_common_secret_forms()
    {
        var value = SecretRedactor.Redact("rcon-password=hunter2 Authorization: Bearer abc.def token=xyz https://me:pass@example.test");
        Assert.DoesNotContain("hunter2", value, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def", value, StringComparison.Ordinal);
        Assert.DoesNotContain("xyz", value, StringComparison.Ordinal);
        Assert.DoesNotContain(":pass@", value, StringComparison.Ordinal);
        Assert.Contains("<redacted>", value, StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_redaction_covers_authorization_and_query_credentials_but_retains_diagnostic_context()
    {
        var value = SecretRedactor.Redact(
            "Authorization: Basic ZmFrZTpzZWNyZXQ= access_token=query-secret client_secret=client-secret " +
            "player=FixturePlayer uuid=12345678-1234-1234-1234-1234567890ab ip=10.20.30.40 path=C:\\Fixture\\world " +
            "{\"apiKey\":\"json-secret\",\"credential\":\"json-credential\"}");

        Assert.DoesNotContain("ZmFrZTpzZWNyZXQ", value, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", value, StringComparison.Ordinal);
        Assert.DoesNotContain("client-secret", value, StringComparison.Ordinal);
        Assert.DoesNotContain("json-secret", value, StringComparison.Ordinal);
        Assert.DoesNotContain("json-credential", value, StringComparison.Ordinal);
        Assert.Contains("FixturePlayer", value, StringComparison.Ordinal);
        Assert.Contains("12345678-1234-1234-1234-1234567890ab", value, StringComparison.Ordinal);
        Assert.Contains("10.20.30.40", value, StringComparison.Ordinal);
        Assert.Contains("C:\\Fixture\\world", value, StringComparison.Ordinal);
    }

    [Fact]
    public void Statistics_downsampling_preserves_bounds_and_aggregates_real_values()
    {
        var samples = Enumerable.Range(0, 1_000).Select(index => new StatisticsSample
        {
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(index),
            CpuPercent = index % 100,
            WorkingSetBytes = index * 1_024,
            PeakWorkingSetBytes = index * 2_048,
            ProcessCount = 1,
            ThreadCount = 4
        }).ToArray();
        var result = StatisticsDownsampler.Downsample(samples, 100);
        Assert.Equal(100, result.Count);
        Assert.True(result[0].Timestamp < result[^1].Timestamp);
        Assert.True(result.Max(sample => sample.WorkingSetBytes) > result.Min(sample => sample.WorkingSetBytes));
    }
}

