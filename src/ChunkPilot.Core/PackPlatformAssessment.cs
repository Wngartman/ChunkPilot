namespace ChunkPilot.Core;

public enum PackPlatformKind
{
    VanillaCompatible,
    PaperCompatible,
    Fabric,
    NeoForge,
    Forge,
    Quilt,
    Unknown,
    ConflictingUnsupported
}

/// <summary>
/// Provider-neutral platform evidence for a future server-pack workflow. This model deliberately
/// does not install packs; it prevents Paper from being offered for loader-based packs.
/// </summary>
public sealed record PackPlatformEvidence
{
    public IReadOnlyList<string> DeclaredLoaders { get; init; } = [];
    public bool ContainsModJars { get; init; }
    public bool ExplicitServerBundle { get; init; }
}

public sealed record PackPlatformAssessment
{
    public PackPlatformKind Platform { get; init; } = PackPlatformKind.Unknown;
    public bool PaperEligible { get; init; }
    public string Reason { get; init; } = "The pack does not declare enough platform metadata.";
}

public static class PackPlatformPolicy
{
    public static PackPlatformAssessment Assess(PackPlatformEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var platforms = evidence.DeclaredLoaders
            .Select(Normalize)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .Distinct()
            .ToArray();

        if (platforms.Length > 1 ||
            evidence.ContainsModJars && platforms.Any(value => value is PackPlatformKind.PaperCompatible or PackPlatformKind.VanillaCompatible))
            return Result(PackPlatformKind.ConflictingUnsupported, false,
                "The pack mixes incompatible server-platform evidence. ChunkPilot does not build hybrid Paper/mod-loader servers.");

        if (platforms.Length == 1)
        {
            var platform = platforms[0];
            return platform switch
            {
                PackPlatformKind.PaperCompatible => Result(platform, true,
                    "The server bundle explicitly targets Paper, Bukkit, or Spigot and declares no mod-loader content."),
                PackPlatformKind.VanillaCompatible => Result(platform, false,
                    "The pack declares a Vanilla server bundle. Paper is not inferred from Vanilla compatibility."),
                PackPlatformKind.Fabric or PackPlatformKind.NeoForge or PackPlatformKind.Forge or PackPlatformKind.Quilt =>
                    Result(platform, false,
                        $"The pack requires {Label(platform)}. Paper is unavailable because mod-loader packs must use their declared loader."),
                _ => Result(PackPlatformKind.Unknown, false,
                    "The declared loader is not supported by the platform assessment.")
            };
        }

        if (evidence.ContainsModJars)
            return Result(PackPlatformKind.Unknown, false,
                "The bundle contains mod JARs but does not identify one loader. Paper is unavailable until the actual loader is known.");
        if (evidence.ExplicitServerBundle)
            return Result(PackPlatformKind.VanillaCompatible, false,
                "The server bundle contains no mod-loader declaration. It is treated as Vanilla-compatible, not implicitly Paper-compatible.");
        return Result(PackPlatformKind.Unknown, false,
            "The pack does not declare an authoritative server platform.");
    }

    private static PackPlatformAssessment Result(PackPlatformKind platform, bool paper, string reason) =>
        new() { Platform = platform, PaperEligible = paper, Reason = reason };

    private static PackPlatformKind? Normalize(string value) => value.Trim().ToLowerInvariant() switch
    {
        "vanilla" or "minecraft" => PackPlatformKind.VanillaCompatible,
        "paper" or "bukkit" or "spigot" => PackPlatformKind.PaperCompatible,
        "fabric" => PackPlatformKind.Fabric,
        "neoforge" => PackPlatformKind.NeoForge,
        "forge" => PackPlatformKind.Forge,
        "quilt" => PackPlatformKind.Quilt,
        _ => null
    };

    private static string Label(PackPlatformKind platform) => platform switch
    {
        PackPlatformKind.NeoForge => "NeoForge",
        _ => platform.ToString()
    };
}
