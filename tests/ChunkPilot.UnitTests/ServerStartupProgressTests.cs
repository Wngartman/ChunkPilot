using ChunkPilot.Core;

namespace ChunkPilot.UnitTests;

public sealed class ServerStartupProgressTests
{
    [Theory]
    [InlineData("[Server thread/INFO]: Preparing level world", ServerStartupStage.PreparingWorld)]
    [InlineData("[main/INFO]: Loading 327 mods:", ServerStartupStage.LoadingMods)]
    [InlineData("A regular log line", null)]
    [InlineData("Done (0.123s)!", null)]
    public void Only_explicit_phase_markers_are_classified(string line, ServerStartupStage? expected) =>
        Assert.Equal(expected, ServerStartupProgressPolicy.StageFromOutput(line));
}
