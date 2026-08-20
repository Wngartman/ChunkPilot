using System.Globalization;
using System.Windows.Data;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.Core;

namespace ChunkPilot.App.CreateServerLive;

/// <summary>Boolean to visibility, declared locally so the window resolves without App.xaml.</summary>
public sealed class LiveBooleanVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is true;
        if (parameter is string text && text.Equals("invert", StringComparison.OrdinalIgnoreCase))
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows an element only while the wizard is on the step named by the parameter.</summary>
public sealed class LiveStepVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not CreationWizardStep step || parameter is not string expected)
            return Visibility.Collapsed;
        return Enum.TryParse<CreationWizardStep>(expected, out var target) && target == step
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows an element only when a collection has entries.</summary>
public sealed class LiveAnyItemsVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is System.Collections.IEnumerable items && items.GetEnumerator().MoveNext()
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows an element only when a string has content.</summary>
public sealed class LiveNonEmptyVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A version's support conclusion as a tone. Text always accompanies it; colour is never the message.
/// </summary>
public sealed class VanillaSupportToneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            VanillaVersionSupport.Supported => AppTone.Success,
            VanillaVersionSupport.SupportedWithWarning => AppTone.Warning,
            VanillaVersionSupport.NoServerArtifact => AppTone.Danger,
            VanillaVersionSupport.JavaRequirementUnknown => AppTone.Danger,
            VanillaVersionSupport.UnsupportedByChunkPilot => AppTone.Danger,
            _ => AppTone.Neutral
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A version's support conclusion as a semantic icon, so the state is not colour alone.</summary>
public sealed class VanillaSupportIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            VanillaVersionSupport.Supported => AppIconKind.Success,
            VanillaVersionSupport.SupportedWithWarning => AppIconKind.Warning,
            VanillaVersionSupport.NoServerArtifact => AppIconKind.Error,
            VanillaVersionSupport.JavaRequirementUnknown => AppIconKind.Error,
            VanillaVersionSupport.UnsupportedByChunkPilot => AppIconKind.Error,
            _ => AppIconKind.Unknown
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>The shortest honest wording for a version's support conclusion.</summary>
public sealed class VanillaSupportBadgeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is VanillaVersionSupport support
            ? VanillaSupportPolicy.BadgeLabel(support)
            : VanillaSupportPolicy.BadgeLabel(VanillaVersionSupport.UnsupportedByChunkPilot);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>One version list row's secondary line. Composed from what exists, never a URL.</summary>
public sealed class VanillaVersionSubtitleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is VanillaVersionOption option ? LiveVanillaReviewBuilder.DescribeVersionLine(option) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The tone of the operation panel, so a finished panel states the outcome rather than staying blue.
/// </summary>
public sealed class CreationStageToneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            CreationStage.Completed => AppTone.Success,
            CreationStage.CompletedWithCleanupWarning => AppTone.Warning,
            CreationStage.WaitingForSafeCheckpoint => AppTone.Warning,
            CreationStage.CancellingSafely => AppTone.Warning,
            CreationStage.RollingBack => AppTone.Warning,
            CreationStage.Cancelled => AppTone.Neutral,
            CreationStage.FailedNothingChanged => AppTone.Danger,
            CreationStage.FailedRolledBack => AppTone.Danger,
            CreationStage.RecoveryRequired => AppTone.Danger,
            _ => AppTone.Accent
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
