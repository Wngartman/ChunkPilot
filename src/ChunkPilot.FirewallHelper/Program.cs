using System.Runtime.InteropServices;
using ChunkPilot.Core;
using ChunkPilot.FirewallHelper;

// ChunkPilot's only privileged process. It exists for one operation and then ends.
//
// Its whole vocabulary is FirewallHelperCommand: create, update or remove one inbound allow rule for
// one program on one TCP port in one Windows profile. There is no argument that can name a command to
// run, a script, a file, a registry key, a service or a firewall setting, so it cannot be turned into
// a general administrator tool by feeding it different input — and its manifest requires elevation, so
// it cannot be run at all without Windows asking the user first.
//
// It writes nothing but its exit code. Whether the firewall is actually configured is decided by the
// Agent afterwards, by reading Windows Firewall back for itself.

if (args.Length == 0)
{
    Console.Error.WriteLine("ChunkPilot Firewall Helper is started by ChunkPilot and takes no interactive use.");
    return (int)FirewallHelperExitCode.InvalidArguments;
}

// A self-test runs the same validation and the same operation logic against an in-memory store. It
// exists so the shipped executable can be exercised end to end without the real policy store ever
// being opened, and it is not a privileged capability: it changes nothing anywhere.
var selfTest = args[0] == FirewallHelperCommandParser.SelfTestFlag;
var operationArguments = selfTest ? args[1..] : args;

var parsed = FirewallHelperCommandParser.Parse(operationArguments);
if (parsed.Command is null)
{
    Console.Error.WriteLine(parsed.Error);
    return (int)FirewallHelperExitCode.InvalidArguments;
}

try
{
    if (selfTest)
    {
        var rehearsal = FirewallHelperRunner.Run(parsed.Command, new InMemoryFirewallBackend());
        Console.Out.WriteLine(rehearsal.Detail);
        return (int)rehearsal.ExitCode;
    }

    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("Windows Firewall is only available on Windows.");
        return (int)FirewallHelperExitCode.FirewallUnavailable;
    }

    using var backend = new NetFwMutationBackend();
    var result = FirewallHelperRunner.Run(parsed.Command, backend);
    if (result.ExitCode == FirewallHelperExitCode.Applied)
        Console.Out.WriteLine(result.Detail);
    else
        Console.Error.WriteLine(result.Detail);
    return (int)result.ExitCode;
}
catch (UnauthorizedAccessException exception)
{
    Console.Error.WriteLine(exception.Message);
    return (int)FirewallHelperExitCode.AccessDenied;
}
catch (COMException exception) when (exception.HResult == unchecked((int)0x80070005))
{
    Console.Error.WriteLine(exception.Message);
    return (int)FirewallHelperExitCode.AccessDenied;
}
catch (Exception exception) when (exception is COMException or InvalidOperationException or
                                      MissingMemberException or InvalidCastException)
{
    Console.Error.WriteLine(exception.Message);
    return (int)FirewallHelperExitCode.UnexpectedFailure;
}
