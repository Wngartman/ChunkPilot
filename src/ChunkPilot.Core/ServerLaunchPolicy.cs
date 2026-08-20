using System.Text.RegularExpressions;

namespace ChunkPilot.Core;

public static partial class ServerLaunchPolicy
{
    public static string EnsureNoGui(string arguments, ServerEcosystem ecosystem, bool backgroundEnabled = true)
    {
        arguments ??= "";
        if (!backgroundEnabled || !SupportsNoGui(ecosystem) || NoGuiRegex().IsMatch(arguments))
            return arguments.Trim();
        return string.IsNullOrWhiteSpace(arguments) ? "nogui" : $"{arguments.Trim()} nogui";
    }

    public static bool SupportsNoGui(ServerEcosystem ecosystem) =>
        ecosystem is ServerEcosystem.Vanilla or ServerEcosystem.Paper or ServerEcosystem.Purpur or
        ServerEcosystem.Spigot or ServerEcosystem.Bukkit or ServerEcosystem.Fabric or
        ServerEcosystem.Quilt or ServerEcosystem.Forge or ServerEcosystem.NeoForge or ServerEcosystem.Hybrid;

    public static bool IsDetachedLaunch(string executable, string arguments)
    {
        var fileName = Path.GetFileName(executable);
        return fileName.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase) ||
               DetachedRegex().IsMatch(arguments ?? "");
    }

    [GeneratedRegex(@"(?<![\w-])(?:--?)?nogui(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoGuiRegex();

    [GeneratedRegex(@"(?:^|[&|]\s*|\s)(?:start|start-process)\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DetachedRegex();
}

public static class ServerPropertyValidation
{
    public static readonly IReadOnlyList<string> GameModes = ["survival", "creative", "adventure", "spectator"];
    public static readonly IReadOnlyList<string> Difficulties = ["peaceful", "easy", "normal", "hard"];

    public static IReadOnlyDictionary<string, string> Validate(IReadOnlyDictionary<string, string> values)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ValidateChoice(values, "gamemode", GameModes, errors);
        ValidateChoice(values, "difficulty", Difficulties, errors);
        ValidateInteger(values, "server-port", 1, 65_535, errors);
        ValidateInteger(values, "max-players", 1, 10_000, errors);
        ValidateInteger(values, "view-distance", 2, 32, errors);
        ValidateInteger(values, "simulation-distance", 2, 32, errors);
        ValidateInteger(values, "spawn-protection", 0, 65_535, errors);
        // Minutes before an idle player is disconnected; 0 is Vanilla's "never".
        ValidateInteger(values, "player-idle-timeout", 0, 525_600, errors);
        return errors;
    }

    private static void ValidateChoice(
        IReadOnlyDictionary<string, string> values,
        string key,
        IReadOnlyCollection<string> choices,
        IDictionary<string, string> errors)
    {
        if (values.TryGetValue(key, out var value) && !choices.Contains(value, StringComparer.OrdinalIgnoreCase))
            errors[key] = $"Choose one of: {string.Join(", ", choices)}.";
    }

    private static void ValidateInteger(
        IReadOnlyDictionary<string, string> values,
        string key,
        int minimum,
        int maximum,
        IDictionary<string, string> errors)
    {
        if (values.TryGetValue(key, out var value) &&
            (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum))
            errors[key] = $"Enter a whole number from {minimum} through {maximum}.";
    }
}

public static class RamRecommendationCalculator
{
    public static RamRecommendation Calculate(
        long totalPhysicalBytes,
        long availableBytes,
        ServerEcosystem ecosystem,
        int modCount,
        int pluginCount,
        int expectedPlayers,
        int otherConfiguredXmxMb)
    {
        var totalMb = checked((int)Math.Min(int.MaxValue, totalPhysicalBytes / 1024 / 1024));
        var availableMb = checked((int)Math.Min(int.MaxValue, availableBytes / 1024 / 1024));
        var reserveMb = Math.Max(6_144, totalMb / 5);
        var maximumSafe = Math.Max(1_024, Math.Min(totalMb - reserveMb - Math.Max(0, otherConfiguredXmxMb), availableMb - 2_048));
        var baseMb = ecosystem is ServerEcosystem.Forge or ServerEcosystem.NeoForge or ServerEcosystem.Fabric or ServerEcosystem.Quilt
            ? 4_096
            : 2_048;
        var workload = modCount * 24 + pluginCount * 12 + Math.Max(0, expectedPlayers - 5) * 96;
        var recommended = Math.Clamp(RoundTo512(baseMb + workload), 1_024, Math.Max(1_024, maximumSafe));
        var minimum = Math.Min(recommended, ecosystem is ServerEcosystem.Forge or ServerEcosystem.NeoForge ? 3_072 : 1_024);
        var warnings = new List<string>();
        if (otherConfiguredXmxMb + recommended > totalMb - reserveMb)
            warnings.Add("Configured allocations may leave too little memory for Windows, the Minecraft client, and other applications.");
        if (maximumSafe <= 1_024)
            warnings.Add("Available host memory is currently low; stop other workloads before starting this server.");
        return new RamRecommendation
        {
            MinimumMb = minimum,
            RecommendedMb = recommended,
            MaximumSafeMb = maximumSafe,
            Warnings = warnings,
            Explanation = "This range is an estimate based on host headroom, server type, extensions, players, and other configured servers."
        };
    }

    public static IReadOnlyList<string> Validate(int xmsMb, int xmxMb, long totalPhysicalBytes, int totalConfiguredXmxMb)
    {
        var warnings = new List<string>();
        var totalMb = totalPhysicalBytes / 1024 / 1024;
        if (xmsMb > xmxMb)
            warnings.Add("Xms cannot exceed Xmx.");
        if (xmxMb > totalMb)
            warnings.Add("Xmx cannot exceed physical host memory.");
        if (totalConfiguredXmxMb > totalMb)
            warnings.Add("Combined configured Xmx values exceed physical host memory.");
        if (xmxMb < 1_024)
            warnings.Add("This allocation is unusually low for a modern Minecraft server.");
        return warnings;
    }

    private static int RoundTo512(int value) => checked((int)Math.Ceiling(value / 512d) * 512);
}
