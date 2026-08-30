using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class WindowsStagedVisibleTopLevelWindowTests
{
    [Fact]
    public void Visible_window_owned_by_descendant_is_detected()
    {
        Assert.True(WindowsStagedVisibleTopLevelWindows.HasOwnedWindow(
            [100, 200],
            [300, 200]));
    }

    [Fact]
    public void Visible_window_owned_only_by_unrelated_process_is_ignored()
    {
        Assert.False(WindowsStagedVisibleTopLevelWindows.HasOwnedWindow(
            [100, 200],
            [300, 400]));
    }

    [Fact]
    public void Empty_visible_window_evidence_confirms_headless_state()
    {
        Assert.False(WindowsStagedVisibleTopLevelWindows.HasOwnedWindow(
            [100, 200],
            []));
    }
}
