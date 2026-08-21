using System.Net;
using System.Net.Sockets;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

if (args.FirstOrDefault()?.Equals("ui-session-owner", StringComparison.OrdinalIgnoreCase) == true)
{
    if (args.Length != 2)
        return 64;
    using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(10_000);
    using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 65_536, leaveOpen: true);
    await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 65_536, leaveOpen: true)
    {
        AutoFlush = true
    };
    var request = new
    {
        requestId = Guid.NewGuid().ToString("N"),
        operation = "RegisterUiSession",
        payload = new
        {
            processId = Environment.ProcessId,
            processCreationTicks = NativeProcessCreation.ReadCurrent()
        }
    };
    await writer.WriteLineAsync(JsonSerializer.Serialize(request));
    Console.WriteLine(await reader.ReadLineAsync() ?? "");
    await Console.Out.FlushAsync();
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

if (args.Contains("-XshowSettings:properties", StringComparer.OrdinalIgnoreCase) &&
    args.Contains("-version", StringComparer.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Property settings:");
    Console.Error.WriteLine("    java.vendor = Eclipse Adoptium");
    Console.Error.WriteLine("    sun.arch.data.model = 64");
    Console.Error.WriteLine("openjdk version \"21.0.8\" 2026-07-15");
    return 0;
}

if (args.Contains("--installServer", StringComparer.OrdinalIgnoreCase))
{
    Directory.CreateDirectory(Path.Combine("libraries", "fixture"));
    await File.WriteAllTextAsync("run.bat", "@echo off\r\njava @libraries\\fixture\\win_args.txt %*\r\n");
    await File.WriteAllTextAsync(Path.Combine("libraries", "fixture", "win_args.txt"), "-cp fixture server.Main\r\n");
    return 0;
}

if (args.Contains("install", StringComparer.OrdinalIgnoreCase) &&
    args.Contains("server", StringComparer.OrdinalIgnoreCase))
{
    await File.WriteAllBytesAsync("quilt-server-launch.jar", [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
    return 0;
}

var mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "normal";
if (mode == "terraria")
{
    Console.WriteLine("Terraria Server v1.4.5.6");
    Console.WriteLine("Listening on port 7777");
    while (await Console.In.ReadLineAsync() is { } terrariaCommand)
    {
        switch (terrariaCommand.Trim().ToLowerInvariant())
        {
            case "playing":
                Console.WriteLine("No players connected.");
                break;
            case "save":
                Console.WriteLine("World saved");
                break;
            case "exit":
                Console.WriteLine("World saved");
                Console.WriteLine("Server stopped");
                return 0;
            default:
                Console.WriteLine($"Executed Terraria command: {terrariaCommand}");
                break;
        }
    }
    return 0;
}
if (mode == "immediate-crash")
{
    Console.Error.WriteLine("Simulated startup crash");
    return 7;
}
if (mode == "bind-failure")
{
    Console.Error.WriteLine("FAILED TO BIND TO PORT");
    Console.Error.WriteLine("Address already in use");
    return 0;
}
if (mode == "known-startup-failure")
{
    Console.Error.WriteLine("Error: Unable to access jarfile server.jar");
    return 1;
}
if (mode == "conflicting-startup-failures")
{
    Console.Error.WriteLine("Error: Unable to access jarfile server.jar");
    Console.Error.WriteLine("FAILED TO BIND TO PORT");
    return 0;
}

using var statusCancellation = new CancellationTokenSource();
var statusTask = int.TryParse(Environment.GetEnvironmentVariable("CHUNKPILOT_FAKE_STATUS_PORT"), out var statusPort)
    ? RunStatusServerAsync(statusPort, statusCancellation.Token)
    : Task.CompletedTask;

Console.WriteLine("[Server thread/INFO]: Starting fake Minecraft server");
if (mode == "stderr")
    Console.Error.WriteLine("[Server thread/WARN]: simulated stderr warning");
if (mode == "high-volume")
{
    for (var index = 0; index < 10_000; index++)
        Console.WriteLine($"[Server thread/INFO]: generated line {index}");
}
if (mode != "no-readiness")
    Console.WriteLine("[Server thread/INFO]: Done (0.123s)! For help, type \"help\"");

if (mode == "crash")
{
    await Task.Delay(500);
    Console.Error.WriteLine("Simulated crash after readiness");
    statusCancellation.Cancel();
    return 9;
}

// Game rules the fixture reports and remembers, so a query has a real answer to return and a change
// is observable afterwards. Values only; the real server owns the vocabulary.
var gamerules = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["keepInventory"] = "false",
    ["doDaylightCycle"] = "true",
    ["doFireTick"] = "true",
    ["mobGriefing"] = "true",
    ["doMobSpawning"] = "true",
    ["doWeatherCycle"] = "true",
    ["doInsomnia"] = "true",
    ["announceAdvancements"] = "true",
    ["commandBlockOutput"] = "true",
    ["showDeathMessages"] = "true",
    ["doImmediateRespawn"] = "false",
    ["fallDamage"] = "true",
    ["randomTickSpeed"] = "3",
    ["spawnRadius"] = "10",
    ["playersSleepingPercentage"] = "100",
    ["maxEntityCramming"] = "24"
};

while (await Console.In.ReadLineAsync() is { } command)
{
    // Parametrised commands are answered the way Vanilla answers them, because ChunkPilot waits for
    // that answer before it claims anything changed. "Ghost" is the fixture's unknown player, so a
    // refusal path can be exercised without inventing a new protocol.
    if (TryAnswerParametrised(command.Trim(), gamerules, mode))
        continue;

    switch (command.Trim().ToLowerInvariant())
    {
        case "save-off":
            Console.WriteLine("[Server thread/INFO]: Automatic saving is now disabled");
            break;
        case "save-on":
            Console.WriteLine("[Server thread/INFO]: Automatic saving is now enabled");
            break;
        case "save-all flush" when mode == "old-save":
            Console.WriteLine("Unknown or incomplete command");
            break;
        case "save-all flush":
            Console.WriteLine("[Server thread/INFO]: Saved the game");
            break;
        case "save-all":
            Console.WriteLine("[Server thread/INFO]: Saved the world and chunks");
            break;
        case "stop" when mode == "ignore-stop":
            Console.WriteLine("[Server thread/WARN]: intentionally ignoring stop");
            break;
        case "stop":
            if (mode == "slow-stop")
                await Task.Delay(2_000);
            Console.WriteLine("[Server thread/INFO]: Stopping server");
            statusCancellation.Cancel();
            return 0;
        default:
            Console.WriteLine($"[Server thread/INFO]: Executed command: {command}");
            break;
    }
}

// A real Java server can outlive the agent that owned its redirected console. This fixture mode
// deliberately survives that broken stdin so replacement-agent recovery can be tested end to end.
if (mode == "survive-eof")
    await Task.Delay(Timeout.InfiniteTimeSpan);

statusCancellation.Cancel();
return 0;

/// <summary>
/// Answers the commands that carry an argument, using Vanilla's own wording.
/// </summary>
/// <remarks>
/// These replies are the contract ChunkPilot reads: it waits for the server to confirm a moderation
/// command before it reports the change, and it parses a gamerule query rather than assuming a
/// default. A fixture that only echoed the command back could not exercise either path.
/// </remarks>
static bool TryAnswerParametrised(string command, Dictionary<string, string> gamerules, string mode)
{
    var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0)
        return false;
    var verb = parts[0].ToLowerInvariant();
    var target = parts.Length > 1 ? parts[1] : "";
    const string unknown = "[Server thread/INFO]: That player does not exist";

    switch (verb)
    {
        case "whitelist" when parts.Length >= 3 && parts[1].Equals("add", StringComparison.OrdinalIgnoreCase):
            Console.WriteLine(parts[2] == "Ghost"
                ? unknown
                : $"[Server thread/INFO]: Added {parts[2]} to the whitelist");
            return true;
        case "whitelist" when parts.Length >= 3 && parts[1].Equals("remove", StringComparison.OrdinalIgnoreCase):
            Console.WriteLine($"[Server thread/INFO]: Removed {parts[2]} from the whitelist");
            return true;
        case "whitelist" when target.Equals("on", StringComparison.OrdinalIgnoreCase):
            Console.WriteLine("[Server thread/INFO]: Whitelist is now turned on");
            return true;
        case "whitelist" when target.Equals("off", StringComparison.OrdinalIgnoreCase):
            Console.WriteLine("[Server thread/INFO]: Whitelist is now turned off");
            return true;
        case "op" when target.Length > 0:
            Console.WriteLine(target == "Ghost"
                ? unknown
                : $"[Server thread/INFO]: Made {target} a server operator");
            return true;
        case "deop" when target.Length > 0:
            Console.WriteLine($"[Server thread/INFO]: Made {target} no longer a server operator");
            return true;
        case "ban" when target.Length > 0:
            Console.WriteLine($"[Server thread/INFO]: Banned {target}: Banned by an operator");
            return true;
        case "pardon" when target.Length > 0:
            Console.WriteLine($"[Server thread/INFO]: Unbanned {target}");
            return true;
        case "kick" when target.Length > 0:
            Console.WriteLine($"[Server thread/INFO]: Kicked {target}: Kicked by an operator");
            return true;
        // A server whose version does not have these rules refuses them the way Brigadier does: an
        // error, then the command echoed with a caret at the point of failure. Minecraft 26.2 answers
        // every rule name ChunkPilot knows exactly like this.
        case "gamerule" when mode == "reject-gamerules":
            Console.WriteLine("[Server thread/INFO]: Incorrect argument for command");
            Console.WriteLine($"[Server thread/INFO]: gamerule {target}<--[HERE]");
            return true;
        case "gamerule" when parts.Length == 2:
            Console.WriteLine(gamerules.TryGetValue(target, out var current)
                ? $"[Server thread/INFO]: Gamerule {target} is currently set to: {current}"
                : "[Server thread/INFO]: Unknown or incomplete command");
            return true;
        case "gamerule" when parts.Length >= 3:
            gamerules[target] = parts[2];
            Console.WriteLine($"[Server thread/INFO]: Gamerule {target} is now set to: {parts[2]}");
            return true;
        // Fixture-only: lets a test make a player arrive and leave, which is how the real server
        // reports presence.
        case "fixture-join" when target.Length > 0:
            Console.WriteLine($"[Server thread/INFO]: {target} joined the game");
            return true;
        case "fixture-leave" when target.Length > 0:
            Console.WriteLine($"[Server thread/INFO]: {target} left the game");
            return true;
        default:
            return false;
    }
}

static async Task RunStatusServerAsync(int port, CancellationToken cancellationToken)
{
    var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            _ = await ReadVarIntAsync(stream, cancellationToken);
            _ = await ReadVarIntAsync(stream, cancellationToken);
            _ = await ReadVarIntAsync(stream, cancellationToken);
            var hostLength = await ReadVarIntAsync(stream, cancellationToken);
            var hostAndPort = new byte[hostLength + 2];
            await stream.ReadExactlyAsync(hostAndPort, cancellationToken);
            _ = await ReadVarIntAsync(stream, cancellationToken);
            _ = await ReadVarIntAsync(stream, cancellationToken);
            _ = await ReadVarIntAsync(stream, cancellationToken);
            var json = Encoding.UTF8.GetBytes("""{"version":{"name":"fake","protocol":0},"players":{"max":20,"online":0},"description":{"text":"ChunkPilot fake server"}}""");
            using var payload = new MemoryStream();
            WriteVarInt(payload, 0);
            WriteVarInt(payload, json.Length);
            payload.Write(json);
            using var packet = new MemoryStream();
            WriteVarInt(packet, checked((int)payload.Length));
            payload.Position = 0;
            payload.CopyTo(packet);
            await stream.WriteAsync(packet.ToArray(), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    finally
    {
        listener.Stop();
    }
}

static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken cancellationToken)
{
    var value = 0;
    var position = 0;
    while (position < 35)
    {
        var buffer = new byte[1];
        await stream.ReadExactlyAsync(buffer, cancellationToken);
        value |= (buffer[0] & 0x7F) << position;
        if ((buffer[0] & 0x80) == 0)
            return value;
        position += 7;
    }
    throw new InvalidDataException("VarInt is too long.");
}

static void WriteVarInt(Stream stream, int value)
{
    do
    {
        var current = (byte)(value & 0x7F);
        value = (int)((uint)value >> 7);
        if (value != 0)
            current |= 0x80;
        stream.WriteByte(current);
    } while (value != 0);
}

static class NativeProcessCreation
{
    public static long ReadCurrent() =>
        GetProcessTimes(GetCurrentProcess(), out var creation, out _, out _, out _) ? creation : 0;

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        nint process,
        out long creationTime,
        out long exitTime,
        out long kernelTime,
        out long userTime);
}
