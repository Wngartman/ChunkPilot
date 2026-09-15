using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class ProcessTreeOwnershipTests
{
    [Theory]
    [InlineData(100, 0, 101, true)]
    [InlineData(100, 200, 150, true)]
    [InlineData(100, 200, 201, false)] // Child of a later, reused parent PID.
    [InlineData(100, 0, 99, false)] // An older foreign process is not a descendant.
    [InlineData(0, 0, 101, false)]
    [InlineData(100, 0, 0, false)]
    public void Descendant_identity_requires_the_exact_parent_lifetime(long created, long exited,
        long childCreated, bool expected) =>
        Assert.Equal(expected, ProcessTree.IsWithinParentLifetime(created, exited, childCreated));
}
