using ChunkPilot.App.WebUi;

namespace ChunkPilot.UnitTests;

public sealed class WebUiAddonFenceTests
{
    [Theory]
    [InlineData("mods.search")]
    [InlineData("mods.release")]
    [InlineData("mods.plan")]
    [InlineData("plugins.search")]
    [InlineData("plugins.release")]
    [InlineData("plugins.plan")]
    public async Task Delayed_addon_metadata_from_server_a_is_rejected_after_switching_to_server_b(
        string method)
    {
        var alphaId = Guid.NewGuid();
        var bravoId = Guid.NewGuid();
        Guid? selectedServerId = alphaId;
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var request = WebUiWindow.ExecuteFencedAddonMetadataRequestAsync(
            alphaId,
            () => selectedServerId,
            async cancellationToken =>
            {
                requestStarted.TrySetResult();
                await releaseRequest.Task.WaitAsync(cancellationToken);
                return method;
            },
            CancellationToken.None);

        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        selectedServerId = bravoId;
        releaseRequest.TrySetResult();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => request);
        Assert.Contains("selected server changed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_server_install_is_rejected_before_the_agent_or_observer_is_called()
    {
        var requestedServerId = Guid.NewGuid();
        var selectedServerId = Guid.NewGuid();
        var agentCalled = false;
        var observerCalled = false;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WebUiWindow.ExecuteFencedAddonInstallRequestAsync(
                requestedServerId,
                () => selectedServerId,
                _ =>
                {
                    agentCalled = true;
                    return Task.FromResult(Guid.NewGuid());
                },
                _ => observerCalled = true,
                CancellationToken.None));

        Assert.Contains("selected server changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(agentCalled);
        Assert.False(observerCalled);
    }

    [Fact]
    public async Task Started_install_keeps_its_operation_identity_observed_when_selection_changes()
    {
        var alphaId = Guid.NewGuid();
        var bravoId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        Guid? selectedServerId = alphaId;
        Guid? observedOperationId = null;
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var request = WebUiWindow.ExecuteFencedAddonInstallRequestAsync(
            alphaId,
            () => selectedServerId,
            async cancellationToken =>
            {
                requestStarted.TrySetResult();
                await releaseRequest.Task.WaitAsync(cancellationToken);
                return operationId;
            },
            observed => observedOperationId = observed,
            CancellationToken.None);

        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        selectedServerId = bravoId;
        releaseRequest.TrySetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => request);
        Assert.Equal(operationId, observedOperationId);
    }

    [Fact]
    public void All_remote_addon_reads_and_install_starts_use_the_cancellable_dispatch_path()
    {
        string[] expected =
        [
            "plugins.providers", "mods.providers",
            "plugins.search", "mods.search",
            "plugins.release", "mods.release",
            "plugins.plan", "mods.plan",
            "plugins.install", "mods.install",
            "plugins.installPlan", "mods.installPlan",
            "content.operations"
        ];

        Assert.All(expected, method => Assert.True(WebUiWindow.IsCancellableAddonRequestMethod(method), method));
        Assert.False(WebUiWindow.IsCancellableAddonRequestMethod("content.cancel"));
        Assert.False(WebUiWindow.IsCancellableAddonRequestMethod("mods.remove"));
    }
}
