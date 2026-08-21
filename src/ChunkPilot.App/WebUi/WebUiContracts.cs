using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ChunkPilot.App.WebUi;

internal static class WebUiProtocol
{
    public const int Version = 1;
    public const int MaximumInboundBytes = 256 * 1024;
    public const string Origin = "https://chunkpilot.local/";
    public const string EntryPoint = Origin + "index.html";

    public static bool IsTrustedSource(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.IdnHost, "chunkpilot.local", StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };
}

internal static class WebUiMethodPolicy
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "renderer.ready", "snapshot.get", "snapshot.refresh", "snapshot.selectServer", "bridge.cancel",
        "window.drag", "window.minimize", "window.toggleMaximize", "window.close",
        "servers.start", "servers.stop", "servers.restart", "servers.openFolder",
        "servers.deletePreflight", "servers.delete", "servers.createManagedCopy",
        "diagnostics.openLogs", "diagnostics.bundle",
        "servers.import", "servers.rename", "servers.changeIcon",
        "appearance.chooseIcon",
        "console.send", "workspace.load", "files.openFolder", "files.navigate", "files.read", "files.write",
        "plugins.openFolder", "plugins.chooseLocal", "plugins.installLocal", "plugins.providers", "plugins.search", "plugins.release",
        "plugins.install", "plugins.plan", "plugins.installPlan", "plugins.setEnabled", "plugins.remove", "plugins.configFiles", "plugins.saveConfig",
        "mods.openFolder", "mods.chooseLocal", "mods.installLocal", "mods.providers", "mods.search", "mods.release",
        "mods.install", "mods.plan", "mods.installPlan", "mods.setEnabled", "mods.remove", "mods.configFiles", "mods.saveConfig",
        "content.operations", "content.cancel",
        "modpacks.providers", "modpacks.versions", "modpacks.cache", "modpacks.search", "modpacks.resolveLink", "modpacks.image", "modpacks.chooseLocal",
        "backups.create", "backups.restore", "backups.verify", "players.moderate",
        "schedules.upsert", "schedules.delete",
        "settings.saveGlobal", "settings.saveServer",
        "connectivity.copyAddress", "connectivity.open", "connectivity.setMode",
        "connectivity.router.check", "connectivity.router.confirm", "connectivity.router.cancelConsent",
        "connectivity.router.stop", "connectivity.router.cancel", "connectivity.router.retry",
        "connectivity.external.check", "connectivity.external.cancel",
        "connectivity.firewall.primary", "connectivity.firewall.secondary", "connectivity.firewall.confirm",
        "connectivity.firewall.cancelConsent", "connectivity.firewall.remove", "connectivity.firewall.cancel",
        "versions.check", "versions.install", "versions.rollback", "versions.verify", "versions.cancel",
        "creation.catalog", "creation.paperBuilds", "creation.loaderBuilds", "creation.previewDestination", "creation.chooseFolder", "creation.chooseLegacyArtifact",
        "creation.begin", "creation.operations", "creation.progress", "creation.cancel"
    };

    public static bool IsAllowed(string method) => Allowed.Contains(method);
    public static IReadOnlyCollection<string> Methods => Allowed;
}

internal sealed record WebUiRequest(int ProtocolVersion, string Id, string Method, JsonObject Params);

internal sealed record WebUiError(string Code, string Message, string? Details = null);

internal sealed record WebUiResponse(
    int ProtocolVersion,
    string Id,
    bool Ok,
    JsonNode? Result = null,
    WebUiError? Error = null);

internal sealed record WebUiEvent(
    int ProtocolVersion,
    string Event,
    long Revision,
    JsonNode Payload);
