using ChunkPilot.App.WebUi;
using ChunkPilot.Core;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeManagedContentPlanEvidenceStoreTests
{
    [Fact]
    public void Sixty_five_cancelled_reviews_surface_every_exact_authorization_for_revocation()
    {
        var store = new CurseForgeManagedContentPlanEvidenceStore();
        var serverId = Guid.NewGuid();
        var revoked = new HashSet<Guid>();

        for (var index = 0; index < 65; index++)
        {
            var authorization = Authorization();
            var generation = store.InvalidateWithEvidence().Generation;
            Assert.True(store.TryCommit(
                generation, serverId, PluginProviderKind.CurseForge,
                $"project-{index}", $"version-{index}", authorization, out _));

            var cancellation = store.InvalidateWithEvidence();
            Assert.Equal(authorization.AuthorizationId, cancellation.AuthorizationId);
            var exactAuthorizationId = cancellation.AuthorizationId.GetValueOrDefault();
            Assert.NotEqual(Guid.Empty, exactAuthorizationId);
            Assert.True(revoked.Add(exactAuthorizationId));
        }

        Assert.Equal(65, revoked.Count);
        Assert.Null(store.InvalidateWithEvidence().AuthorizationId);
    }

    [Fact]
    public void Server_switch_revokes_only_when_the_review_belongs_to_another_server()
    {
        var store = new CurseForgeManagedContentPlanEvidenceStore();
        var alpha = Guid.NewGuid();
        var bravo = Guid.NewGuid();
        var authorization = Authorization();
        var generation = store.InvalidateWithEvidence().Generation;
        Assert.True(store.TryCommit(
            generation, alpha, PluginProviderKind.CurseForge,
            "project", "version", authorization, out _));

        Assert.Null(store.InvalidateForServerChange(alpha).AuthorizationId);
        Assert.Equal(authorization.AuthorizationId,
            store.InvalidateForServerChange(bravo).AuthorizationId);
        Assert.Null(store.InvalidateForServerChange(bravo).AuthorizationId);
    }

    [Fact]
    public void Late_cleanup_and_stale_consume_cannot_erase_a_newer_review()
    {
        var store = new CurseForgeManagedContentPlanEvidenceStore();
        var serverId = Guid.NewGuid();
        var first = Authorization();
        var second = Authorization();
        var generation = store.InvalidateWithEvidence().Generation;
        Assert.True(store.TryCommit(
            generation, serverId, PluginProviderKind.CurseForge,
            "old-project", "old-version", first, out _));
        generation = store.InvalidateWithEvidence().Generation;
        Assert.True(store.TryCommit(
            generation, serverId, PluginProviderKind.CurseForge,
            "new-project", "new-version", second, out _));

        Assert.False(store.InvalidateIfAuthorizationId(first.AuthorizationId));
        Assert.Throws<StaleCurseForgeManagedContentPlanException>(() => store.Consume(
            serverId, PluginProviderKind.CurseForge,
            "old-project", "old-version", first));

        var consumed = store.Consume(
            serverId, PluginProviderKind.CurseForge,
            "new-project", "new-version", second);
        Assert.Equal(second.AuthorizationId, consumed.AuthorizationId);
    }

    [Fact]
    public void Failed_install_invalidation_is_selection_scoped()
    {
        var store = new CurseForgeManagedContentPlanEvidenceStore();
        var serverId = Guid.NewGuid();
        var authorization = Authorization();
        var generation = store.InvalidateWithEvidence().Generation;
        Assert.True(store.TryCommit(
            generation, serverId, PluginProviderKind.CurseForge,
            "project", "version", authorization, out _));

        Assert.Null(store.InvalidateIfSelection(
            serverId, PluginProviderKind.CurseForge, "different", "version"));
        Assert.Equal(authorization.AuthorizationId, store.InvalidateIfSelection(
            serverId, PluginProviderKind.CurseForge, "project", "version"));
    }

    private static ManagedContentPlanAuthorization Authorization() => new()
    {
        AuthorizationId = Guid.NewGuid(),
        Digest = new string('a', 64)
    };
}
