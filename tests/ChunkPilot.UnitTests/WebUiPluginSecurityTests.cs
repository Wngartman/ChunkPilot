using System.Text.Json;
using ChunkPilot.App.WebUi;

namespace ChunkPilot.UnitTests;

public sealed class WebUiPluginSecurityTests
{
    [Fact]
    public void Local_plugin_token_never_serializes_native_path_and_is_single_use()
    {
        var now = new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
        var store = new WebUiLocalPluginTokenStore(() => now);
        var serverId = Guid.NewGuid();
        var path = @"D:\private\plugins\Fixture.jar";

        var selection = store.Issue(serverId, path);
        var json = JsonSerializer.Serialize(selection, WebUiProtocol.Json);

        Assert.DoesNotContain("D:", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Fixture.jar", selection.FileName);
        Assert.Equal(path, store.Consume(serverId, selection.Token));
        Assert.Throws<ArgumentException>(() => store.Consume(serverId, selection.Token));
    }

    [Fact]
    public void Local_plugin_token_is_server_bound_and_expires()
    {
        var now = new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
        var store = new WebUiLocalPluginTokenStore(() => now);
        var serverId = Guid.NewGuid();
        var selection = store.Issue(serverId, @"D:\plugins\Fixture.jar");

        Assert.Throws<ArgumentException>(() => store.Consume(Guid.NewGuid(), selection.Token));

        selection = store.Issue(serverId, @"D:\plugins\Fixture.jar");
        now = now.AddMinutes(6);
        Assert.Throws<ArgumentException>(() => store.Consume(serverId, selection.Token));
    }
}
