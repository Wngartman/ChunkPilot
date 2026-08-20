using ChunkPilot.App.CreateServerLive;
using ChunkPilot.App.WebUi;

namespace ChunkPilot.UnitTests;

public sealed class PaperWebUiContractTests
{
    [Fact]
    public void Paper_gateway_uses_narrow_named_operations()
    {
        Assert.Equal("PaperVersions", AgentPaperCreationGateway.Operations.Versions);
        Assert.Equal("PaperBuilds", AgentPaperCreationGateway.Operations.Builds);
        Assert.Equal("BeginPaperCreation", AgentPaperCreationGateway.Operations.Begin);
        Assert.Equal("InstallProgress", AgentPaperCreationGateway.Operations.Progress);
        Assert.Equal("CancelInstall", AgentPaperCreationGateway.Operations.Cancel);
    }

    [Fact]
    public void Bridge_allows_exact_build_discovery_without_exposing_a_generic_native_call()
    {
        Assert.True(WebUiMethodPolicy.IsAllowed("creation.catalog"));
        Assert.True(WebUiMethodPolicy.IsAllowed("creation.paperBuilds"));
        Assert.True(WebUiMethodPolicy.IsAllowed("creation.begin"));
        Assert.False(WebUiMethodPolicy.IsAllowed("agent.invoke"));
    }
}
