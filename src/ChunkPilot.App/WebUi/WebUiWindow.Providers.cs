using System.Text.Json;
using System.Text.Json.Nodes;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.App.WebUi;

public partial class WebUiWindow
{
    private bool curseForgeSetupOpen;

    private async Task<JsonNode?> ConfigureCurseForgeAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        if (parameters.Count != 0)
            throw new ArgumentException("CurseForge credentials must be entered in the native setup window.");
        if (curseForgeSetupOpen)
            throw new InvalidOperationException("CurseForge setup is already open.");
        curseForgeSetupOpen = true;
        try
        {
            var session = new UiSessionCredential { SessionId = sessionId, Capability = sessionCapability };
            var status = await client.SendAsync<TextResponse>("HasCurseForgeApiKey", session, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            var dialog = new CurseForgeCredentialWindow(status.Value == "configured",
                (password, token) => client.SendAsync<OperationResult>("ConfigureCurseForgeCredential",
                    new CurseForgeCredentialChangeRequest
                    {
                        Session = session,
                        ProtectedApiKey = CurseForgeCredentialTransport.ProtectForCurrentUser(password)
                    }, token),
                token => client.SendAsync<OperationResult>("RemoveCurseForgeCredential", session, token),
                () => OpenExternalHttps("https://support.curseforge.com/support/solutions/articles/9000208346")) { Owner = this };
            using var registration = cancellationToken.Register(() =>
            {
                if (!Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(() => { if (dialog.IsVisible) dialog.Close(); });
            });
            var accepted = dialog.ShowDialog() == true;
            return JsonSerializer.SerializeToNode(new { configured = dialog.Configured, cancelled = !accepted }, WebUiProtocol.Json);
        }
        finally { curseForgeSetupOpen = false; }
    }
}
