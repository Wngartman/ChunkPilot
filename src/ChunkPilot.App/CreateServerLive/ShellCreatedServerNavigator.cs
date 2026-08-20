using ChunkPilot.App.Navigation;

namespace ChunkPilot.App.CreateServerLive;

/// <summary>Refreshes the shell, opens the exact registered server, and presents its Overview.</summary>
internal sealed class ShellCreatedServerNavigator(MainViewModel viewModel, MainWindow shell)
    : ICreatedServerNavigator
{
    public async Task OpenAsync(Guid serverId)
    {
        await viewModel.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
        var created = viewModel.Servers.FirstOrDefault(server => server.Definition.Id == serverId);
        if (created is null)
            return;

        viewModel.SelectedServer = created;
        viewModel.Navigation.NavigateServer(ServerDestination.Overview, serverId);
        shell.PresentServerWorkspace();
    }
}
