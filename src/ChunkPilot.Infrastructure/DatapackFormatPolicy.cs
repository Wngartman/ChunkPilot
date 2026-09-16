using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed partial class DatapackService
{
    private readonly record struct PackVersion(int Major, int Minor)
    {
        public int CompareTo(PackVersion other) => Major == other.Major
            ? Minor.CompareTo(other.Minor)
            : Major.CompareTo(other.Major);

        public override string ToString() => Minor == int.MaxValue ? $"{Major}.x" : $"{Major}.{Minor}";
    }

    private static DatapackInspection InspectMetadata(JsonElement pack, string minecraftVersion)
    {
        if (pack.ValueKind != JsonValueKind.Object)
            return Invalid("pack.mcmeta must contain a pack object.");
        var hasLegacy = pack.TryGetProperty("pack_format", out var legacyValue);
        int? legacy = hasLegacy ? NonnegativeInteger(legacyValue, "pack_format") : null;
        var hasMinimum = pack.TryGetProperty("min_format", out var minimumValue);
        var hasMaximum = pack.TryGetProperty("max_format", out var maximumValue);
        var hasSupported = pack.TryGetProperty("supported_formats", out var supportedValue);
        PackVersion minimum;
        PackVersion maximum;
        if (hasMinimum || hasMaximum)
        {
            if (!hasMinimum || !hasMaximum)
                return Invalid("Modern datapacks must declare both min_format and max_format.");
            minimum = ReadPackVersion(minimumValue, maximum: false);
            maximum = ReadPackVersion(maximumValue, maximum: true);
            if (minimum.Major < 82 && (!hasLegacy || !hasSupported))
                return Invalid("A modern pack that also supports formats below 82 must include pack_format and supported_formats for those older versions.");
            if (minimum.Major >= 82 && hasSupported)
                return Invalid("A modern-only datapack must use min_format/max_format, not supported_formats.");
            if (hasSupported)
            {
                var (olderMinimum, olderMaximum) = ReadLegacyRange(supportedValue);
                if (olderMinimum > olderMaximum || legacy < olderMinimum || legacy > olderMaximum)
                    return Invalid("supported_formats must be an ordered range containing pack_format.");
            }
            if (legacy is { } declared && (declared < minimum.Major || declared > maximum.Major))
                return Invalid("pack_format is outside the declared min_format/max_format range.");
        }
        else
        {
            if (legacy is null)
                return Invalid("Datapacks must declare pack_format or modern min_format/max_format metadata.");
            var (low, high) = hasSupported ? ReadLegacyRange(supportedValue) : (legacy.Value, legacy.Value);
            if (low > high || legacy < low || legacy > high)
                return Invalid("supported_formats must be an ordered range containing pack_format.");
            if (high >= 82)
                return Invalid("Datapack formats 82 and newer require min_format and max_format.");
            minimum = new PackVersion(low, 0);
            maximum = new PackVersion(high, int.MaxValue);
        }
        if (minimum.CompareTo(maximum) > 0)
            return Invalid("min_format must not exceed max_format.");

        var description = pack.TryGetProperty("description", out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString()
            : "";
        var expected = ExpectedPackFormat(minecraftVersion);
        var compatibility = CompatibilityState.Unknown;
        if (expected is { } target)
        {
            compatibility = target.CompareTo(minimum) >= 0 && target.CompareTo(maximum) <= 0
                ? CompatibilityState.Compatible
                // Mojang defines a newer minor within the same major as backwards-compatible.
                : target.Major == maximum.Major && target.Minor > maximum.Minor
                    ? CompatibilityState.LikelyCompatible
                    : CompatibilityState.Incompatible;
        }
        var detail = expected is { } known
            ? $"Declared pack format range {minimum}–{maximum}; Minecraft {minecraftVersion} uses {known}. This checks metadata, not datapack gameplay."
            : $"Declared pack format range {minimum}–{maximum}; the exact format for Minecraft {minecraftVersion} is unverified. Review compatibility before use.";
        // The existing inventory schema stores the major number. Minor/range evidence stays in the
        // inspection detail, avoiding a lossy decimal conversion or an unrelated database migration.
        return new DatapackInspection(true, legacy ?? minimum.Major, description, compatibility, detail);
    }

    private static PackVersion ReadPackVersion(JsonElement value, bool maximum)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return new PackVersion(NonnegativeInteger(value, "pack format"), maximum ? int.MaxValue : 0);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 2)
            throw new InvalidDataException("A pack format must be an integer or a one/two-integer [major, minor] array.");
        var major = NonnegativeInteger(value[0], "pack format major");
        var minor = value.GetArrayLength() == 2
            ? NonnegativeInteger(value[1], "pack format minor")
            : maximum ? int.MaxValue : 0;
        return new PackVersion(major, minor);
    }

    private static (int Minimum, int Maximum) ReadLegacyRange(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            var format = NonnegativeInteger(value, "supported_formats");
            return (format, format);
        }
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 2)
            return (NonnegativeInteger(value[0], "supported_formats minimum"),
                NonnegativeInteger(value[1], "supported_formats maximum"));
        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("min_inclusive", out var low) &&
            value.TryGetProperty("max_inclusive", out var high))
            return (NonnegativeInteger(low, "supported_formats minimum"),
                NonnegativeInteger(high, "supported_formats maximum"));
        throw new InvalidDataException("supported_formats must be an integer, two-integer range, or min_inclusive/max_inclusive object.");
    }

    private static int NonnegativeInteger(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) && result >= 0
            ? result
            : throw new InvalidDataException($"{field} must be a nonnegative 32-bit integer.");

    private static PackVersion? ExpectedPackFormat(string version)
    {
        // Exact modern releases are taken from Mojang's release notes. Do not assign yesterday's
        // format to an unknown future release or a pre-release that may use a different minor.
        // minecraft.net/en-us/article/minecraft-java-edition-{26-1,26-2,26-3,1-21-9,1-21-11}
        if (version == "26.1") return new PackVersion(101, 1);
        if (version == "26.2") return new PackVersion(107, 1);
        if (version == "26.3") return new PackVersion(121, 0);
        if (version == "1.21.11") return new PackVersion(94, 1);
        if (version is "1.21.9" or "1.21.10") return new PackVersion(88, 0);
        if (!Version.TryParse(version, out var parsed) || parsed.Major != 1 || parsed >= new Version(1, 21, 9))
            return null;
        var major = parsed switch
        {
            _ when parsed >= new Version(1, 21, 7) => 81,
            _ when parsed >= new Version(1, 21, 6) => 80,
            _ when parsed >= new Version(1, 21, 5) => 71,
            _ when parsed >= new Version(1, 21, 4) => 61,
            _ when parsed >= new Version(1, 21, 2) => 57,
            _ when parsed >= new Version(1, 21) => 48,
            _ when parsed >= new Version(1, 20, 5) => 41,
            _ when parsed >= new Version(1, 20, 3) => 26,
            _ when parsed >= new Version(1, 20, 2) => 18,
            _ when parsed >= new Version(1, 20) => 15,
            _ when parsed >= new Version(1, 19, 4) => 12,
            _ when parsed >= new Version(1, 19) => 10,
            _ when parsed >= new Version(1, 18, 2) => 9,
            _ => 0
        };
        return major == 0 ? null : new PackVersion(major, 0);
    }
}
