using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Reads Windows Firewall policy. Read-only by construction: there is no method here that can change
/// anything, which is what lets the Agent consult the firewall freely without ever mutating it.
/// </summary>
public interface IWindowsFirewallPolicyReader
{
    FirewallPolicySnapshot Read();
}

/// <summary>
/// Reports how Windows classifies each connected network, keyed by adapter.
/// </summary>
/// <remarks>
/// Per-adapter rather than machine-wide on purpose. A laptop can have an ordinary private Wi-Fi network
/// and a public-classified VPN adapter up at the same time, and "some active profile is Public" must
/// never be allowed to become "create a Public rule".
/// </remarks>
public interface INetworkCategoryView
{
    IReadOnlyList<NetworkCategoryBinding> Enumerate();

    /// <summary>
    /// Returns the same bindings with typed read evidence. Existing deterministic fakes need only
    /// implement <see cref="Enumerate"/>; the Windows implementation overrides this to preserve API
    /// failure information instead of turning it into an empty network list.
    /// </summary>
    NetworkCategorySnapshot Read() => new() { Bindings = Enumerate() };
}

/// <summary>Starts the privileged helper through the documented Windows elevation prompt.</summary>
public interface IFirewallElevationLauncher
{
    Task<FirewallElevationOutcome> LaunchAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>What happened when Windows was asked to run the helper elevated.</summary>
public sealed record FirewallElevationOutcome
{
    /// <summary>The user dismissed the prompt. Not a failure and never retried automatically.</summary>
    public bool Cancelled { get; init; }

    /// <summary>The helper ran to completion and returned <see cref="ExitCode"/>.</summary>
    public bool Ran { get; init; }

    public int ExitCode { get; init; }
    public FirewallElevationFailure Failure { get; init; }
    public string Detail { get; init; } = "";

    public static FirewallElevationOutcome CancelledByUser() =>
        new() { Cancelled = true, Detail = "The Windows administrator prompt was dismissed." };

    public static FirewallElevationOutcome Failed(
        string detail, FirewallElevationFailure failure = FirewallElevationFailure.LaunchFailed) =>
        new() { Detail = detail, Failure = failure };

    public static FirewallElevationOutcome Completed(int exitCode, string detail) =>
        new() { Ran = true, ExitCode = exitCode, Detail = detail };
}
