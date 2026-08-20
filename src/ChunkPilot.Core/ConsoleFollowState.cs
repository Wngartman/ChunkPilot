namespace ChunkPilot.Core;

public sealed class ConsoleFollowState
{
    public bool IsFollowing { get; private set; } = true;
    public int UnseenLineCount { get; private set; }

    public void OnViewportChanged(bool isAtBottom)
    {
        IsFollowing = isAtBottom;
        if (isAtBottom)
            UnseenLineCount = 0;
    }

    public bool OnLinesAdded(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
            return false;
        if (IsFollowing)
            return true;
        UnseenLineCount = checked(UnseenLineCount + count);
        return false;
    }

    public void JumpToLatest()
    {
        IsFollowing = true;
        UnseenLineCount = 0;
    }

    public void CommandSent() => JumpToLatest();
}
