using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ChunkPilot.App.Presentation;
using ChunkPilot.Core;

namespace ChunkPilot.App;

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility visibility && visibility != Visibility.Visible;
}

public sealed class PositiveIntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int number && number > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ZeroIntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int number && number == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Shows an element only for the named file-editor states.
/// </summary>
/// <remarks>
/// The file pane has one state at a time - nothing selected, loading, text, empty, binary, too large,
/// failed - and each needs different content. Naming the states in the parameter keeps that mapping in
/// the XAML beside the panel it governs, rather than in a boolean per state on the view model.
/// </remarks>
public sealed class FileEditorStateVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not FileEditorState state || parameter is not string names)
            return Visibility.Collapsed;
        foreach (var name in names.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse<FileEditorState>(name, ignoreCase: true, out var candidate) && candidate == state)
                return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible only when a string has something in it. For inline messages that are usually absent.</summary>
public sealed class NonEmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string text && text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var bytes = value switch { long number => number, int number => number, _ => 0L };
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var size = (double)Math.Max(0, bytes);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:F1} {units[unit]}";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps a server lifecycle state to a design-system status brush.
/// </summary>
/// <remarks>
/// Resolves the brush by token key rather than constructing a colour, so a status indicator follows
/// the palette and the high-contrast overlay like everything else. An unrecognised or transitional
/// state resolves to the neutral token, which claims nothing about health.
/// </remarks>
public sealed class StateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ServerState.Running => "AppSuccess",
            ServerState.Starting or ServerState.Saving or ServerState.Restarting or ServerState.BackingUp => "AppWarning",
            ServerState.Crashed or ServerState.Unresponsive => "AppDanger",
            _ => "AppNeutral"
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// True when a value was never established, for an info row's unknown state.
/// </summary>
/// <remarks>
/// A row with a null value must say what it does not know rather than render an empty line, and
/// <c>AppInfoRow.IsUnknown</c> is how it does that. This is the binding that decides it.
/// </remarks>
public sealed class IsNullConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is not null;
        if (parameter is string text && text.Equals("invert", StringComparison.OrdinalIgnoreCase))
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EcosystemSectionNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ServerEcosystem.Paper or ServerEcosystem.Purpur or ServerEcosystem.Spigot
            ? "Plugins"
            : "Mods";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps a ServerState to a concise human-readable label for the Dashboard server card.
/// </summary>
public sealed class ServerStateTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            ServerState.Running => "Running",
            ServerState.Stopped => "Stopped",
            ServerState.Starting => "Starting…",
            ServerState.Stopping => "Stopping…",
            ServerState.Restarting => "Restarting…",
            ServerState.Saving => "Saving…",
            ServerState.BackingUp => "Backing up…",
            ServerState.Restoring => "Restoring…",
            ServerState.Crashed => "Crashed",
            ServerState.Unresponsive => "Not responding",
            _ => "Unknown"
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps a ServerState to an AppTone for use by design-system components.
/// </summary>
public sealed class ServerStateToneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            ServerState.Running => DesignSystem.AppTone.Success,
            ServerState.Starting or ServerState.Restarting => DesignSystem.AppTone.Info,
            ServerState.Saving or ServerState.BackingUp => DesignSystem.AppTone.Warning,
            ServerState.Stopping or ServerState.Crashed or ServerState.Unresponsive => DesignSystem.AppTone.Danger,
            _ => DesignSystem.AppTone.Neutral
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Converts a boolean to Visibility where false = Visible (inverse of BoolVisibility).
/// Used for empty-state display.
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Shows when the server is in a state that supports starting.
/// </summary>
public sealed class StateToStartVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ServerState state && (state is ServerState.Stopped or ServerState.Crashed)
            ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Shows when the server is in a state that supports stopping.
/// </summary>
public sealed class StateToStopVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ServerState state && state is ServerState.Starting or ServerState.Running or
            ServerState.Stopping or ServerState.Saving or ServerState.Restarting or ServerState.Unresponsive
            ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Compares two ServerSnapshot references by Definition.Id for IsSelected bindings.
/// </summary>
public sealed class ServerEqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is [ServerSnapshot a, ServerSnapshot b, ..])
            return a.Definition.Id == b.Definition.Id;
        return false;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Loads a server icon into memory without giving WPF a mutable file path to retain.</summary>
public sealed class ServerIconRootConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string root || string.IsNullOrWhiteSpace(root))
            return null;
        try
        {
            return ServerIconImageLoader.LoadDetached(Path.Combine(root, "server-icon.png"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or NotSupportedException or InvalidDataException)
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps a raw activity action string to a user-facing label.
/// </summary>
public sealed class ActivityActionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        ActivityActionPresentation.Format(value as string) ?? "Activity recorded";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps the free-text <c>ActivityEntry.Result</c> to an AppTone. The field is not an enum - the
/// agent records values like "Success", "Failed" and "Exit 1" - so this recognises the one
/// positive value and treats everything else as a state that needs the reader's attention rather
/// than guessing at every possible failure string.
/// </summary>
public sealed class ActivityResultToneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value as string) switch
        {
            null or "" => DesignSystem.AppTone.Neutral,
            "Success" => DesignSystem.AppTone.Success,
            _ => DesignSystem.AppTone.Danger
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class TroubleshootingMatchVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ActivityEntry entry && TroubleshootingService.Analyze(entry).HasLikelyFix
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
