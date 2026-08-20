using System.Globalization;

namespace ChunkPilot.Core;

/// <summary>One deterministic boundary between user-facing decimal GB and Java-facing whole MiB.</summary>
public static class MemoryAllocationPolicy
{
    public const int MinimumMib = 512;
    public const int MaximumMib = 131_072;

    public static IReadOnlyList<MemoryPreset> CommonPresets { get; } =
    [
        new(2), new(4), new(6), new(8), new(12), new(16)
    ];

    public static MemoryInputResult ParseGigabytes(string? text, CultureInfo? culture = null)
    {
        var input = (text ?? "").Trim();
        culture ??= CultureInfo.CurrentCulture;
        if (input.Length == 0)
            return MemoryInputResult.Invalid("Enter a memory amount.");
        if (input.Contains('e', StringComparison.OrdinalIgnoreCase))
            return MemoryInputResult.Invalid("Use a decimal number without exponent notation.");

        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        if (!decimal.TryParse(input, styles, culture, out var gigabytes))
            return MemoryInputResult.Invalid("Enter a number such as 4 or 4.6.");
        return NormalizeGigabytes(gigabytes);
    }

    public static MemoryInputResult NormalizeGigabytes(decimal gigabytes)
    {
        if (gigabytes <= 0)
            return MemoryInputResult.Invalid("Memory must be greater than zero.");

        try
        {
            var rounded = decimal.Round(checked(gigabytes * 1024m), 0, MidpointRounding.AwayFromZero);
            if (rounded < MinimumMib)
                return MemoryInputResult.Invalid("Memory must be at least 0.5 GB (512 MiB).");
            if (rounded > MaximumMib)
                return MemoryInputResult.Invalid("Memory cannot exceed 128 GB (131072 MiB).");
            return MemoryInputResult.Valid(checked((int)rounded));
        }
        catch (OverflowException)
        {
            return MemoryInputResult.Invalid("That memory amount is too large.");
        }
    }

    public static string FormatGigabytes(int mebibytes, CultureInfo? culture = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(mebibytes);
        culture ??= CultureInfo.CurrentCulture;
        var exact = mebibytes / 1024m;
        for (var decimals = 0; decimals <= 4; decimals++)
        {
            var candidate = decimal.Round(exact, decimals, MidpointRounding.AwayFromZero);
            if (NormalizeGigabytes(candidate).Mebibytes == mebibytes)
                return candidate.ToString(decimals == 0 ? "0" : $"0.{new string('#', decimals)}", culture);
        }
        return exact.ToString("0.##########", culture);
    }

    public static string? ValidatePair(int minimumMib, int maximumMib)
    {
        if (minimumMib < MinimumMib || minimumMib > MaximumMib)
            return $"Minimum memory must be between {MinimumMib} and {MaximumMib} MiB.";
        if (maximumMib < MinimumMib || maximumMib > MaximumMib)
            return $"Maximum memory must be between {MinimumMib} and {MaximumMib} MiB.";
        return minimumMib <= maximumMib ? null : "Minimum memory cannot exceed maximum memory.";
    }
}

public sealed record MemoryPreset(decimal Gigabytes)
{
    public int Mebibytes => MemoryAllocationPolicy.NormalizeGigabytes(Gigabytes).Mebibytes!.Value;
    public string Label => Gigabytes.ToString("0.##", CultureInfo.CurrentCulture) + " GB";
    public override string ToString() => Label;
}

public sealed record MemoryInputResult(bool IsValid, int? Mebibytes, string Error)
{
    public static MemoryInputResult Valid(int mebibytes) => new(true, mebibytes, "");
    public static MemoryInputResult Invalid(string error) => new(false, null, error);
}
