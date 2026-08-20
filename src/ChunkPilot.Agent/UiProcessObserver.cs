using System.ComponentModel;
using System.Diagnostics;
using ChunkPilot.Core;

namespace ChunkPilot.Agent;

public enum UiProcessLiveness
{
    Unknown,
    Alive,
    Gone
}

public interface IUiProcessObserver
{
    UiProcessLiveness Observe(int processId, long creationTicks);
}

/// <summary>Observes the exact UI process, independently of pipes and heartbeats.</summary>
public sealed class SystemUiProcessObserver : IUiProcessObserver
{
    public UiProcessLiveness Observe(int processId, long creationTicks)
    {
        if (processId <= 0 || creationTicks == ProcessCreationIdentity.Unknown)
            return UiProcessLiveness.Unknown;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
                return UiProcessLiveness.Gone;
            var observed = ProcessCreationIdentity.Of(process.SafeHandle);
            if (observed == ProcessCreationIdentity.Unknown)
                return UiProcessLiveness.Unknown;
            return ProcessCreationIdentity.Matches(observed, creationTicks)
                ? UiProcessLiveness.Alive
                : UiProcessLiveness.Gone;
        }
        catch (ArgumentException)
        {
            return UiProcessLiveness.Gone;
        }
        catch (InvalidOperationException)
        {
            return UiProcessLiveness.Gone;
        }
        catch (Win32Exception)
        {
            return UiProcessLiveness.Unknown;
        }
        catch (NotSupportedException)
        {
            return UiProcessLiveness.Unknown;
        }
    }
}
