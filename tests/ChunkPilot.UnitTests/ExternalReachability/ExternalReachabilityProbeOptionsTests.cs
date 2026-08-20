using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests.ExternalReachability;

/// <summary>
/// Endpoint configuration fails closed. Anything that is not exactly an HTTPS origin is refused
/// rather than repaired, so a malformed or hostile setting cannot redirect a probe.
/// </summary>
public sealed class ExternalReachabilityProbeOptionsTests
{
    [Fact]
    public void No_configuration_means_the_feature_is_unavailable_rather_than_broken()
    {
        foreach (var empty in new[] { null, "", "   " })
        {
            var options = ExternalReachabilityProbeOptions.Configure(empty);
            Assert.False(options.IsConfigured);
            Assert.Null(options.ProbeUrl);
            Assert.NotEqual("", options.Detail);
        }
    }

    [Fact]
    public void An_https_origin_is_accepted_and_the_probe_path_is_appended_by_the_client()
    {
        var options = ExternalReachabilityProbeOptions.Configure(
            "https://chunkpilot-reachability-probe.example.workers.dev");

        Assert.True(options.IsConfigured);
        Assert.Equal("https://chunkpilot-reachability-probe.example.workers.dev/v1/probe",
            options.ProbeUrl!.ToString());
    }

    [Fact]
    public void A_trailing_slash_is_still_an_origin()
    {
        var options = ExternalReachabilityProbeOptions.Configure("https://probe.example.workers.dev/");

        Assert.True(options.IsConfigured);
        Assert.Equal("https://probe.example.workers.dev/v1/probe", options.ProbeUrl!.ToString());
    }

    [Theory]
    [InlineData("http://probe.example.workers.dev", "not HTTPS")]
    [InlineData("ftp://probe.example.workers.dev", "not HTTPS")]
    [InlineData("file:///C:/probe", "without a path")]
    [InlineData("https://user:secret@probe.example.workers.dev", "credentials")]
    [InlineData("https://probe.example.workers.dev/v1/probe", "without a path")]
    [InlineData("https://probe.example.workers.dev/anything", "without a path")]
    [InlineData("https://probe.example.workers.dev?target=victim", "query or fragment")]
    [InlineData("https://probe.example.workers.dev#fragment", "query or fragment")]
    [InlineData("probe.example.workers.dev", "absolute URL")]
    // Windows parses a bare authority as a UNC file URI, so it is refused by the scheme rule rather
    // than the absolute-URL rule. Either way nothing is configured.
    [InlineData("//probe.example.workers.dev", "not HTTPS")]
    [InlineData("javascript:alert(1)", "without a path")]
    public void Anything_that_is_not_an_https_origin_is_refused_with_a_reason(string configured, string reason)
    {
        var options = ExternalReachabilityProbeOptions.Configure(configured);

        Assert.False(options.IsConfigured);
        Assert.Null(options.ProbeUrl);
        Assert.Contains(reason, options.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Plain HTTP exists solely so an in-process test fixture can serve on loopback. Production never
    /// opts in, and even the opt-in refuses a non-loopback host.
    /// </summary>
    [Fact]
    public void Plain_http_is_only_ever_possible_on_loopback_and_only_when_a_test_asks()
    {
        Assert.True(ExternalReachabilityProbeOptions.Configure("http://127.0.0.1:8787", allowLoopbackHttp: true)
            .IsConfigured);
        Assert.True(ExternalReachabilityProbeOptions.Configure("http://localhost:8787", allowLoopbackHttp: true)
            .IsConfigured);
        Assert.False(ExternalReachabilityProbeOptions.Configure("http://127.0.0.1:8787").IsConfigured);
        Assert.False(ExternalReachabilityProbeOptions
            .Configure("http://probe.example.workers.dev", allowLoopbackHttp: true).IsConfigured);
        Assert.False(ExternalReachabilityProbeOptions
            .Configure("http://10.0.0.140:8787", allowLoopbackHttp: true).IsConfigured);
    }

    [Fact]
    public void The_environment_variable_is_the_only_configuration_surface()
    {
        Assert.Equal("CHUNKPILOT_REACHABILITY_PROBE_URL",
            ExternalReachabilityProbeOptions.EnvironmentVariable);
    }

    [Fact]
    public void Waits_and_response_reads_are_bounded_by_default()
    {
        var options = new ExternalReachabilityProbeOptions();

        Assert.True(options.Timeout > TimeSpan.Zero);
        Assert.True(options.Timeout <= TimeSpan.FromSeconds(30));
        Assert.True(options.MaximumResponseBytes is > 0 and <= 65536);
    }
}
