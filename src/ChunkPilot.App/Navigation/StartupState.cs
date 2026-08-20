using CommunityToolkit.Mvvm.ComponentModel;

namespace ChunkPilot.App.Navigation;

/// <summary>
/// Tracks startup progress phases for the splash/loading experience.
/// Does not artificially delay startup or block waiting indefinitely.
/// </summary>
public sealed partial class StartupState : ObservableObject
{
    [ObservableProperty]
    private StartupPhase _phase = StartupPhase.Initializing;

    [ObservableProperty]
    private string _message = "Starting ChunkPilot";

    [ObservableProperty]
    private bool _isComplete;

    [ObservableProperty]
    private bool _hasFailed;

    [ObservableProperty]
    private string? _failureDetail;

    public void Advance(StartupPhase phase, string message)
    {
        Phase = phase;
        Message = message;
    }

    public void Complete()
    {
        Phase = StartupPhase.Ready;
        Message = "Ready";
        IsComplete = true;
    }

    public void Fail(string detail)
    {
        HasFailed = true;
        FailureDetail = detail;
        Message = "Startup failed";
    }
}

public enum StartupPhase
{
    Initializing,
    RestoringWorkspace,
    ConnectingAgent,
    LoadingServers,
    PreparingDashboard,
    Ready
}
