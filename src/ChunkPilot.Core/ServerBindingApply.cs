namespace ChunkPilot.Core;

public sealed record ApplyServerBindingRequest(Guid ServerId, NetworkMode Mode, bool Confirmed, bool RestartIfRunning)
{
    // Filled only by the native host; the renderer never receives this capability.
    public UiSessionCredential Session { get; init; } = new();
}

public static class ServerBindingApplyPolicy
{
    public static string UnavailableReason(ServerDefinition server)
    {
        if (!server.IsManaged)
            return "Imported servers keep their existing configuration. Use a managed copy to apply a binding here.";
        if (server.GameKind != ServerGameKind.Minecraft ||
            server.Ecosystem is ServerEcosystem.Unknown or ServerEcosystem.Custom)
            return "This launch profile does not have a supported Java server binding.";
        try
        {
            if (string.IsNullOrWhiteSpace(server.RootPath) || string.IsNullOrWhiteSpace(server.WorkingDirectory) ||
                !Path.GetFullPath(server.RootPath).Equals(Path.GetFullPath(server.WorkingDirectory), StringComparison.OrdinalIgnoreCase))
                return "This launch profile uses a separate working directory; its binding needs manual review.";
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "The server configuration path could not be verified.";
        }
        return "";
    }

    public static string BindAddress(NetworkMode mode) => mode switch
    {
        NetworkMode.ThisComputerOnly => "127.0.0.1",
        NetworkMode.HomeNetwork or NetworkMode.PortForwarding => "",
        _ => throw new ArgumentException("Choose Local only, LAN, or Internet before applying the server binding.")
    };
}
