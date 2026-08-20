using System.Text.RegularExpressions;

namespace ChunkPilot.Core;

/// <summary>A locally-derived, explainable diagnosis for server-hosting failures.</summary>
public sealed record TroubleshootingMatch
{
    public string Code { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public int Confidence { get; init; }
    public string ConfidenceLabel => Confidence >= 93
        ? "Highly likely"
        : "Possible";
    public string Evidence { get; init; } = "";
    public IReadOnlyList<string> Steps { get; init; } = [];
}

/// <summary>Ranked fixes inferred from a server snapshot or an activity failure.</summary>
public sealed record TroubleshootingReport
{
    public IReadOnlyList<TroubleshootingMatch> Matches { get; init; } = [];
    public TroubleshootingMatch? MostLikely => Matches.Count > 0 ? Matches[0] : null;
    public bool HasLikelyFix => MostLikely is not null;
}

/// <summary>
/// Ranks known Minecraft and Java server failures using only local error/log text. The rules are
/// deliberately deterministic: the UI can show exactly what matched and never uploads a log.
/// </summary>
public static class TroubleshootingService
{
    private static readonly IReadOnlyList<Rule> Rules =
    [
        MakeRule("port.conflict", 99, "The server port is already in use",
            "Another process is already listening on the port this server needs.",
            [@"port\s+\d+\s+is already in use", @"failed to bind to port", @"address already in use"],
            ["Stop the other ChunkPilot server that uses this port, if one is running.",
             "If no server should be using it, open Manage > Server properties and choose an unused server-port, then save.",
             "Start the server again. If the conflict returns, use Protection > Diagnostics to confirm that another local application owns the port."]),

        MakeRule("eula.rejected", 99, "The Minecraft EULA has not been accepted",
            "Minecraft stops before creating the world when eula.txt still contains eula=false.",
            [@"you need to agree to the eula", @"eula\.txt", @"eula=false"],
            ["Open eula.txt from the server folder and read the linked Minecraft EULA.",
             "If you accept it, change eula=false to eula=true and save the file.",
             "Start the server again."]),

        MakeRule("java.version", 98, "This server needs a different Java version",
            "The selected Java runtime cannot load this Minecraft, loader, mod, or plugin build.",
            [@"unsupportedclassversionerror", @"class file version \d+", @"only recognizes class file versions", @"requires java (?:version )?\d+", @"java \d+ or newer"],
            ["Open Manage > Runtime and check the Java version assigned to this server.",
             "Use the Java major version required by the first incompatible-class line in the log.",
             "Start again. Do not change system PATH or JAVA_HOME; ChunkPilot-managed Java is server-specific."]),

        MakeRule("java.arguments", 98, "The Java launch settings are invalid",
            "Java rejected a heap size or JVM option before the Minecraft server could start.",
            [@"invalid maximum heap size", @"initial heap size set to a larger value than the maximum heap size", @"unrecognized vm option", @"could not create the java virtual machine"],
            ["Open this server's Settings and review Minimum RAM, Maximum RAM, and custom JVM arguments.",
             "Remove the rejected option or correct the memory values; keep Minimum RAM at or below Maximum RAM.",
             "Start again using the server-specific managed Java runtime."]),

        MakeRule("java.memory", 97, "Java ran out of memory",
            "The JVM could not reserve memory or the server exhausted its available heap/native memory.",
            [@"outofmemoryerror", @"could not reserve enough space", @"unable to allocate .* heap", @"native memory allocation .* failed", @"paging file is too small"],
            ["Stop other memory-heavy applications and check available host memory.",
             "Open Settings for this server and compare Maximum RAM with physically available RAM; leave headroom for Windows and Java native memory.",
             "If this began after adding content, disable the newest mod/plugin in Manage and retry before simply increasing memory."]),

        MakeRule("mod.dependency", 96, "A mod dependency is missing or incompatible",
            "The loader found a required mod, loader, or version constraint it cannot satisfy.",
            [@"requires .* which is missing", @"depends on .* which is missing", @"mandatory dependencies.*not found", @"mod resolution encountered an incompatible mod set", @"incompatible mods found"],
            ["Read the first dependency error and note the exact mod ID and required version range.",
             "In Manage > Mods, install the matching dependency or replace the incompatible mod with the build for this Minecraft and loader version.",
             "Keep one copy of each mod, then start again."]),

        MakeRule("mod.mixin", 94, "A mod transformation failed",
            "A mixin or transformed class did not match the game/mod version it expected, usually after a version mismatch or conflict.",
            [@"mixin apply failed", @"invalidmixinexception", @"mixintransformererror", @"critical injection failure", @"injectionerror"],
            ["Find the first 'Caused by' or mixin line and identify the named mod, not only the final crash line.",
             "Replace that mod with the build for this exact Minecraft and loader version, and verify its dependencies.",
             "If two mods alter the same feature, disable the most recently added one and retry."]),

        MakeRule("extension.binary", 92, "A required mod or plugin class is incompatible",
            "A class or method expected by the loader, mod, or plugin is missing from the installed versions.",
            [@"nosuchmethoderror", @"noclassdeffounderror", @"classnotfoundexception"],
            ["Use the first missing class/method line and its first 'Caused by' block to identify the responsible extension.",
             "Verify that extension and its libraries target this exact Minecraft, loader, and Java version.",
             "Replace the mismatched build or restore the last working set; do not remove the world."]),

        MakeRule("mod.registry", 91, "Mod registry data no longer matches the world",
            "Blocks, items, dimensions, or other registry entries expected by the world are missing or incompatible.",
            [@"missing registry data", @"unbound values in registry", @"registry remapping failed", @"failed to synchronize registry"],
            ["Stop the server and back up the world before accepting any missing-registry removal.",
             "Restore the mod/version set that last opened this world and confirm all dependencies are present.",
             "Only remove registry entries after reviewing the affected IDs and keeping a recoverable backup."]),

        MakeRule("jar.invalid", 93, "The server launch file is missing or invalid",
            "Java could not read the configured server JAR or find its startup class.",
            [@"unable to access jarfile", @"invalid or corrupt jarfile", @"could not find or load main class", @"no main manifest attribute", @"error opening zip file"],
            ["Open Manage > Files and confirm the configured launch JAR exists and is not zero bytes.",
             "Verify the startup executable/arguments point to the loader-generated launch target for this server type.",
             "Re-download or reinstall only the damaged server/loader file; do not delete the world folder."]),

        MakeRule("world.lock", 93, "The world is locked by another server process",
            "Minecraft found a session lock, which commonly means another process still has the same world open.",
            [@"failed to lock the world", @"session\.lock", @"already locked.*world", @"another instance of minecraft"],
            ["Confirm no other ChunkPilot server or leftover Java process is using this same server folder.",
             "Stop that process normally and wait for it to exit before retrying.",
             "Do not delete session.lock while a Java process may still be running; that can let two processes write the same world."]),

        MakeRule("world.corrupt", 91, "The world could not be loaded safely",
            "The log indicates damaged level data, a broken region, or an incompatible world/datapack.",
            [@"failed to load world", @"exception loading level", @"world.*corrupt", @"failed to read chunk", @"errors in currently selected datapacks prevented the world from loading"],
            ["Stop the server and create a backup before changing world files.",
             "Review the first affected dimension, region, or datapack named in latest.log or the crash report.",
             "Try the last known-good backup or remove only the named incompatible datapack/mod; preserve the current world for recovery."]),

        MakeRule("storage.permission", 90, "ChunkPilot or Java cannot write a required file",
            "The server folder is read-only, protected, locked, or the drive is out of usable space.",
            [@"access is denied", @"permission denied", @"unauthorizedaccessexception", @"read-only file system", @"no space left on device", @"not enough space on the disk"],
            ["Check free space on the drive that contains the server and its backups.",
             "Close editors, sync tools, or antivirus quarantine dialogs that may hold the named file.",
             "Confirm your Windows account can create a small file in the server folder, then retry. Avoid taking ownership of broad system folders."]),

        MakeRule("auth.session", 88, "Minecraft authentication services are unavailable",
            "The server could not reach or validate a Mojang/Microsoft session service.",
            [@"failed to verify username", @"authentication servers are down", @"couldn't verify username because servers are unavailable", @"yggdrasilauthenticationservice.*(?:failed|exception)"],
            ["Check that this PC has working internet access and correct date/time.",
             "Retry after a few minutes; temporary session-service outages usually require no server changes.",
             "Keep online-mode enabled unless you deliberately understand and accept the identity/security tradeoff of changing it."]),

        MakeRule("watchdog.timeout", 87, "The server stopped responding for too long",
            "Minecraft's watchdog ended a server tick that exceeded its time limit.",
            [@"a single server tick took", @"watchdog.*server thread", @"server has stopped responding", @"tick took \d+"],
            ["Open the crash report and identify what the server thread was doing near the top of its stack.",
             "Check for heavy world generation, a runaway command block, or the most recently added mod/plugin.",
             "Restore a backup before editing world data; do not only raise max-tick-time until the underlying stall is understood."]),

        MakeRule("network.bind", 86, "The configured server address cannot be used",
            "server-ip is set to an address this PC does not currently own, or networking could not bind it.",
            [@"cannot assign requested address", @"can't assign requested address", @"failed to bind.*server-ip", @"could not bind to a host"],
            ["Open Manage > Server properties and clear server-ip unless you intentionally bind a specific local adapter.",
             "Leave server-port set to the intended local port and save.",
             "Start again, then test local/LAN access before configuring router or firewall rules."]),

        MakeRule("java.native", 85, "A required native Java library could not load",
            "Java found a missing, blocked, or wrong-architecture native library.",
            [@"unsatisfiedlinkerror", @"failed to load native library", @"no .{1,80} in java\.library\.path"],
            ["Confirm the assigned Java architecture matches the server and extension binaries (normally x64 on this PC).",
             "Remove manually copied native files or java.library.path overrides and retry with the managed runtime.",
             "If the error names one mod/plugin, replace it with the Windows build for this Minecraft and loader version."]),

        MakeRule("plugin.datapack", 82, "A plugin or datapack failed during startup",
            "An extension reported a load/enable error; the earliest named extension is usually the best lead.",
            ["""could not load ['"].*\.jar""", @"error occurred while enabling", @"failed to load datapack", @"couldn't load tag", @"failed to parse .*\.json"],
            ["Find the earliest error that names a plugin or datapack and read its first 'Caused by' line.",
             "Verify that extension targets this Minecraft/server software version and that its dependencies are installed.",
             "Disable only the named extension and retry; keep the world and unrelated extensions intact."])
    ];

    public static TroubleshootingReport Analyze(ServerSnapshot? snapshot)
    {
        if (snapshot is null)
            return new TroubleshootingReport();

        var text = string.Join(Environment.NewLine,
            new[] { snapshot.LastError }
                .Concat(snapshot.Console.TakeLast(250).Select(line => line.Text)));
        return Analyze(text, snapshot.Definition.Port);
    }

    public static TroubleshootingReport Analyze(ActivityEntry? activity) =>
        activity is null ? new TroubleshootingReport() : Analyze(activity.Error);

    public static TroubleshootingReport Analyze(string? text, int? configuredPort = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TroubleshootingReport();

        var safeText = SecretRedactor.Redact(text);
        var matches = new List<TroubleshootingMatch>();
        foreach (var rule in Rules)
        {
            var evidence = FindEvidence(safeText, rule.Patterns);
            if (evidence is null)
                continue;

            var evidencePort = rule.Code == "port.conflict"
                ? Regex.Match(evidence, @"port\s+(?<port>\d+)", RegexOptions.IgnoreCase).Groups["port"].Value
                : "";
            var title = rule.Code == "port.conflict" && (evidencePort.Length > 0 || configuredPort is not null)
                ? $"Port {(evidencePort.Length > 0 ? evidencePort : configuredPort)} is already in use"
                : rule.Title;
            matches.Add(new TroubleshootingMatch
            {
                Code = rule.Code,
                Title = title,
                Summary = rule.Summary,
                Confidence = rule.Confidence,
                Evidence = evidence,
                Steps = rule.Steps
            });
        }

        return new TroubleshootingReport
        {
            Matches = matches
                .OrderByDescending(match => match.Confidence)
                .ThenBy(match => match.Title, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToArray()
        };
    }

    private static string? FindEvidence(string text, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;
            var lineStart = text.LastIndexOfAny(['\r', '\n'], Math.Max(0, match.Index - 1));
            var lineEnd = text.IndexOfAny(['\r', '\n'], match.Index + match.Length);
            if (lineEnd < 0)
                lineEnd = text.Length;
            var line = text[(lineStart + 1)..lineEnd].Trim();
            return line.Length <= 280 ? line : line[..277] + "...";
        }
        return null;
    }

    private static Rule MakeRule(string code, int confidence, string title, string summary,
        IReadOnlyList<string> patterns, IReadOnlyList<string> steps) =>
        new(code, confidence, title, summary, patterns, steps);

    private sealed record Rule(string Code, int Confidence, string Title, string Summary,
        IReadOnlyList<string> Patterns, IReadOnlyList<string> Steps);
}
