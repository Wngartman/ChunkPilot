using System.Text.Json.Nodes;
using ChunkPilot.App;
using ChunkPilot.App.WebUi;
using ChunkPilot.Core;

namespace ChunkPilot.UnitTests;

public sealed class WebUiRollbackContractTests
{
    [Fact]
    public void Rollback_contract_requires_explicit_confirmation_and_exact_identities()
    {
        var serverId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var values = new JsonObject
        {
            ["serverId"] = serverId.ToString(),
            ["versionId"] = snapshotId.ToString(),
            ["confirmed"] = true
        };

        var parsed = WebUiWindow.ValidateRollbackParameters(values);

        Assert.Equal(serverId, parsed.ServerId);
        Assert.Equal(snapshotId, parsed.SnapshotId);
        values["confirmed"] = false;
        Assert.Throws<ArgumentException>(() => WebUiWindow.ValidateRollbackParameters(values));
        values["confirmed"] = true;
        values["versionId"] = Guid.NewGuid().ToString();
        Assert.NotEqual(snapshotId, WebUiWindow.ValidateRollbackParameters(values).SnapshotId);
    }

    [Fact]
    public void Stale_rollback_request_cannot_reselect_its_old_server()
    {
        var alphaId = Guid.NewGuid();
        var bravoId = Guid.NewGuid();

        WebUiWindow.RequireSelectedRollbackServer(bravoId, bravoId);
        var error = Assert.Throws<InvalidOperationException>(() =>
            WebUiWindow.RequireSelectedRollbackServer(alphaId, bravoId));

        Assert.Contains("selected server changed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WebUi_rollback_sends_only_the_exact_selected_server_and_snapshot()
    {
        var server = Server("Alpha");
        var snapshotPath = Path.GetTempFileName();
        try
        {
            var target = Snapshot(server.Definition.Id, snapshotPath);
            var active = target with { Id = Guid.NewGuid(), IsActive = true, SnapshotPath = "", VersionName = "Current" };
            var client = new RollbackClient([active]);
            var viewModel = new MainViewModel(client, new NoDialogs()) { SelectedServer = server };
            viewModel.Versions.Add(target);

            var result = await viewModel.RollbackVersionFromWebUiAsync(server.Definition.Id, target.Id);

            Assert.True(result.Success);
            Assert.Equal(new VersionSnapshotRequest(server.Definition.Id, target.Id), client.RollbackRequest);
            Assert.Equal(active.Id, viewModel.SelectedVersion?.Id);
        }
        finally
        {
            File.Delete(snapshotPath);
        }
    }

    [Fact]
    public async Task Late_rollback_completion_does_not_apply_version_state_to_a_new_server_selection()
    {
        var alpha = Server("Alpha");
        var bravo = Server("Bravo");
        var snapshotPath = Path.GetTempFileName();
        try
        {
            var target = Snapshot(alpha.Definition.Id, snapshotPath);
            var client = new RollbackClient([target with { IsActive = true }]) { BlockRollback = true };
            var viewModel = new MainViewModel(client, new NoDialogs()) { SelectedServer = alpha };
            viewModel.Versions.Add(target);

            var rollback = viewModel.RollbackVersionFromWebUiAsync(alpha.Definition.Id, target.Id);
            await client.RollbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            viewModel.SelectedServer = bravo;
            client.ReleaseRollback.TrySetResult();
            await rollback;

            Assert.Equal(bravo.Definition.Id, viewModel.SelectedServer.Definition.Id);
            Assert.DoesNotContain(viewModel.Versions, version => version.ServerId == alpha.Definition.Id);
            Assert.Null(viewModel.CurrentUpdateCheck);
        }
        finally
        {
            File.Delete(snapshotPath);
        }
    }

    private static ServerSnapshot Server(string name) => new()
    {
        Definition = new ServerDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            RootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            WorkingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            Executable = "java.exe"
        },
        State = ServerState.Stopped
    };

    private static VersionSnapshot Snapshot(Guid serverId, string path) => new()
    {
        Id = Guid.NewGuid(),
        ServerId = serverId,
        VersionId = "previous",
        VersionName = "Previous",
        SnapshotPath = path,
        Verified = true,
        IsActive = false
    };

    private sealed class RollbackClient(IReadOnlyList<VersionSnapshot> versions) : IAgentClient
    {
        private readonly TaskCompletionSource blockedCapabilities =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockRollback { get; init; }
        public TaskCompletionSource RollbackEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRollback { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public VersionSnapshotRequest? RollbackRequest { get; private set; }

        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<TResponse> SendAsync<TResponse>(
            string operation,
            object? payload = null,
            CancellationToken cancellationToken = default)
        {
            object response;
            switch (operation)
            {
                case "GetCapabilities":
                    await blockedCapabilities.Task.WaitAsync(cancellationToken);
                    response = new ServerCapabilityProfile();
                    break;
                case "RollbackVersion":
                    RollbackRequest = Assert.IsType<VersionSnapshotRequest>(payload);
                    RollbackEntered.TrySetResult();
                    if (BlockRollback)
                        await ReleaseRollback.Task.WaitAsync(cancellationToken);
                    response = OperationResult.Ok("Rollback complete.");
                    break;
                case "ListVersions":
                    response = versions;
                    break;
                case "GetLatestUpdateCheck":
                    response = new UpdateCheckResponse(new UpdateCheckResult { Message = "Current after rollback." });
                    break;
                case "GetUpdateHistory":
                    response = Array.Empty<UpdateHistoryEntry>();
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected operation: {operation}");
            }
            return (TResponse)response;
        }
    }

    private sealed class NoDialogs : IDialogService
    {
        public string? SelectFolder(string title, string? initialPath = null) => null;
        public string? SelectFile(string title, string filter) => null;
        public bool Confirm(string title, string message) => false;
        public void ShowError(string title, string message) { }
        public void ShowInformation(string title, string message) { }
    }
}
