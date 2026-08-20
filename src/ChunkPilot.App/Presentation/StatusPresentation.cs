using System.Globalization;
using ChunkPilot.Core;

namespace ChunkPilot.App.Presentation;

/// <summary>
/// Maps <see cref="ServerUpdateStatus"/> values to user-facing labels.
/// </summary>
/// <remarks>
/// Keeps presentation concerns out of ChunkPilot.Core. Every defined enum value has an explicit
/// mapping. Unknown values return a neutral fallback that claims nothing about health or progress.
/// </remarks>
public static class ServerUpdateStatusPresentation
{
    public static string ToLabel(ServerUpdateStatus status) => status switch
    {
        ServerUpdateStatus.SourceNotLinked => "Not linked to an update source",
        ServerUpdateStatus.UpToDate => "Up to date",
        ServerUpdateStatus.UpdateAvailable => "Update available",
        ServerUpdateStatus.Checking => "Checking for updates…",
        ServerUpdateStatus.Downloading => "Downloading update…",
        ServerUpdateStatus.ReadyToInstall => "Ready to install",
        ServerUpdateStatus.Updating => "Updating…",
        ServerUpdateStatus.PendingValidation => "Pending validation",
        ServerUpdateStatus.UpdateSuccessful => "Update completed",
        ServerUpdateStatus.UpdateFailed => "Update failed",
        ServerUpdateStatus.RollbackAvailable => "Rollback available",
        ServerUpdateStatus.CheckUnavailable => "Update check unavailable",
        _ => "Update status unavailable"
    };

    public static string ToDetail(ServerUpdateStatus status, string? rawEvidence = null)
    {
        var label = ToLabel(status);
        return rawEvidence is not null && rawEvidence.Length > 0
            ? $"{label} ({rawEvidence})"
            : label;
    }

    public static string? TryMapUnknown(string unknownValue)
    {
        if (string.IsNullOrWhiteSpace(unknownValue))
            return "Update status unavailable";

        var normalized = unknownValue.Trim();
        if (Enum.TryParse<ServerUpdateStatus>(normalized, ignoreCase: true, out var parsed))
            return ToLabel(parsed);

        return NormalizeToReadable(normalized);
    }

    private static readonly char[] _delimiterChars = ['_', '-'];

    private static string NormalizeToReadable(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Update status unavailable";

        var words = input.Split(_delimiterChars, StringSplitOptions.RemoveEmptyEntries);
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < words.Length; i++)
        {
            if (i > 0)
                result.Append(' ');
            var word = words[i];
            if (string.IsNullOrEmpty(word))
                continue;
            result.Append(char.ToUpperInvariant(word[0]));
            if (word.Length > 1)
                result.Append(word.Substring(1).ToLowerInvariant());
        }
        return result.ToString();
    }
}

/// <summary>
/// Maps <see cref="UpdateOperationState"/> values to user-facing labels.
/// </summary>
/// <remarks>
/// Every defined enum value has an explicit mapping. Unknown values return a neutral fallback.
/// </remarks>
public static class UpdateOperationStatePresentation
{
    public static string ToLabel(UpdateOperationState state) => state switch
    {
        UpdateOperationState.Planned => "Update planned",
        UpdateOperationState.WarningPlayers => "Notifying players…",
        UpdateOperationState.Saving => "Saving world data…",
        UpdateOperationState.Stopping => "Stopping server…",
        UpdateOperationState.Snapshotting => "Creating rollback snapshot…",
        UpdateOperationState.Downloading => "Downloading update…",
        UpdateOperationState.Verifying => "Verifying update package…",
        UpdateOperationState.ReadyToInstall => "Ready to install",
        UpdateOperationState.Extracting => "Extracting update package…",
        UpdateOperationState.PlanningMigration => "Planning configuration migration…",
        UpdateOperationState.BuildingCandidate => "Preparing candidate server…",
        UpdateOperationState.Switching => "Switching active instance…",
        UpdateOperationState.Starting => "Starting server…",
        UpdateOperationState.Querying => "Validating server startup…",
        UpdateOperationState.PendingValidation => "Pending validation",
        UpdateOperationState.RollingBack => "Rolling back to previous version…",
        UpdateOperationState.Completed => "Update completed",
        UpdateOperationState.Failed => "Update failed",
        UpdateOperationState.Cancelled => "Update cancelled",
        _ => "Update operation in progress"
    };

    public static string ToDetail(UpdateOperationState state, double? percent, string? currentStep)
    {
        var label = ToLabel(state);
        var step = string.IsNullOrWhiteSpace(currentStep) ? "" : $" · {currentStep}";
        var pct = percent is >= 0 and < 100 ? $" · {percent.Value:F0}%" : "";
        return $"{label}{step}{pct}";
    }

    public static string? TryMapUnknown(string unknownValue)
    {
        if (string.IsNullOrWhiteSpace(unknownValue))
            return "Update operation in progress";

        var normalized = unknownValue.Trim();
        if (Enum.TryParse<UpdateOperationState>(normalized, ignoreCase: true, out var parsed))
            return ToLabel(parsed);

        return NormalizeToReadable(normalized);
    }

    private static readonly char[] _delimiterChars = ['_', '-'];

    private static string NormalizeToReadable(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Update operation in progress";

        var words = input.Split(_delimiterChars, StringSplitOptions.RemoveEmptyEntries);
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < words.Length; i++)
        {
            if (i > 0)
                result.Append(' ');
            var word = words[i];
            if (string.IsNullOrEmpty(word))
                continue;
            result.Append(char.ToUpperInvariant(word[0]));
            if (word.Length > 1)
                result.Append(word.Substring(1).ToLowerInvariant());
        }
        return result.ToString();
    }
}

/// <summary>
/// Formats raw activity action strings for display in normal UI.
/// </summary>
/// <remarks>
/// Preserves meaningful parameters (filenames, recipe names) while normalizing surrounding text.
/// Unknown values are converted to readable text with the raw value preserved for diagnostics.
/// </remarks>
public static class ActivityActionPresentation
{
    private static readonly char[] _delimiterChars = ['_', '-'];

    public static string Format(string? action, string? source = null, string? error = null)
    {
        if (string.IsNullOrWhiteSpace(action))
            return FormatEmptyAction(source, error);

        var trimmed = action.Trim();

        var formatted = trimmed switch
        {
            "Server icon updated" => "Server icon updated",
            var a when a.StartsWith("External program:", StringComparison.OrdinalIgnoreCase)
                => FormatExternalProgramAction(trimmed),
            var a when a.StartsWith("Automation:", StringComparison.OrdinalIgnoreCase)
                => FormatAutomationAction(trimmed),
            _ => FormatReadableAction(trimmed)
        };

        return source is not null && source.Length > 0
            ? $"{formatted} (via {source})"
            : formatted;
    }

    private static string FormatEmptyAction(string? source, string? error)
    {
        var baseText = !string.IsNullOrWhiteSpace(error)
            ? "Activity recorded"
            : "Activity recorded";

        return source is not null && source.Length > 0
            ? $"{baseText} (via {source})"
            : baseText;
    }

    private static string FormatExternalProgramAction(string action)
    {
        var colonIndex = action.IndexOf(':');
        if (colonIndex < 0 || colonIndex == 0)
            return "External program ran";

        var beforeColon = action[..colonIndex].TrimEnd();
        if (!beforeColon.Equals("External program", StringComparison.OrdinalIgnoreCase))
            return "External program ran";

        var filename = action[(colonIndex + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(filename)
            ? $"External program: {filename}"
            : "External program ran";
    }

    private static string FormatAutomationAction(string action)
    {
        var colonIndex = action.IndexOf(':');
        if (colonIndex < 0 || colonIndex == 0)
            return "Automation rule ran";

        var beforeColon = action[..colonIndex].TrimEnd();
        if (!beforeColon.Equals("Automation", StringComparison.OrdinalIgnoreCase))
            return "Automation rule ran";

        var recipeName = action[(colonIndex + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(recipeName)
            ? $"Automation: {recipeName}"
            : "Automation rule ran";
    }

    private static string FormatReadableAction(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return "Activity recorded";

        var normalized = action.Trim();

        if (normalized.Length <= 3)
            return NormalizeDelimiterCase(normalized);

        if (char.IsUpper(normalized[0]) && char.IsUpper(normalized[1]))
        {
            if (IsAllUpper(normalized))
                return ToTitleCase(normalized);
            return NormalizeDelimiterCase(normalized);
        }

        var result = new System.Text.StringBuilder(normalized.Length);
        var prevWasDelimiter = true;
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c == '_' || c == '-')
            {
                result.Append(' ');
                prevWasDelimiter = true;
            }
            else if (char.IsUpper(c) && !prevWasDelimiter && i > 0)
            {
                result.Append(' ');
                result.Append(char.ToLowerInvariant(c));
                prevWasDelimiter = false;
            }
            else
            {
                result.Append(c);
                prevWasDelimiter = false;
            }
        }

        var text = result.ToString();
        if (char.IsLower(text[0]))
            text = char.ToUpperInvariant(text[0]) + text.Substring(1);

        return text;
    }

    private static bool IsAllUpper(string input)
    {
        var alphaCount = 0;
        foreach (var c in input)
        {
            if (char.IsLetter(c))
            {
                alphaCount++;
                if (!char.IsUpper(c))
                    return false;
            }
        }
        return alphaCount > 0;
    }

    private static string ToTitleCase(string input)
    {
        var words = input.Split(_delimiterChars, StringSplitOptions.RemoveEmptyEntries);
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < words.Length; i++)
        {
            if (i > 0)
                result.Append(' ');
            var word = words[i];
            if (string.IsNullOrEmpty(word))
                continue;
            result.Append(char.ToUpperInvariant(word[0]));
            if (word.Length > 1)
                result.Append(word.Substring(1).ToLowerInvariant());
        }
        var text = result.ToString();
        if (text.Length > 0 && char.IsLower(text[0]))
            text = char.ToUpperInvariant(text[0]) + text.Substring(1);
        return text;
    }

    private static string NormalizeDelimiterCase(string input)
    {
        var result = input.Replace('_', ' ').Replace('-', ' ');
        if (char.IsLower(result[0]))
            result = char.ToUpperInvariant(result[0]) + result.Substring(1);
        return result;
    }

    public static string FormatForDiagnostics(string? action)
    {
        var formatted = Format(action);
        if (string.IsNullOrWhiteSpace(action) || IsStandardAction(action))
            return formatted;

        return $"{formatted} [raw: {action}]";
    }

    private static bool IsStandardAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return true;

        var trimmed = action.Trim();
        return trimmed == "Server icon updated" ||
               trimmed.StartsWith("External program:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Automation:", StringComparison.OrdinalIgnoreCase);
    }
}
