using System.Globalization;
using System.Windows.Data;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.Core;

namespace ChunkPilot.App.CreateServer;

/// <summary>
/// Maps a compatibility conclusion to a semantic tone.
/// </summary>
/// <remarks>
/// Tone never carries the meaning on its own: every surface that uses this also renders
/// <see cref="CompatibilityConclusionPolicy.ShortLabel"/> as text and an icon beside it. An
/// unrecognised conclusion resolves to Neutral, which claims nothing.
/// </remarks>
public sealed class CompatibilityToneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            CompatibilityConclusion.VerifiedCompatible => AppTone.Success,
            CompatibilityConclusion.ProviderDeclaredCompatible => AppTone.Info,
            CompatibilityConclusion.Inferred => AppTone.Warning,
            CompatibilityConclusion.VerifiedIncompatible => AppTone.Danger,
            CompatibilityConclusion.NoServerPackAvailable => AppTone.Danger,
            CompatibilityConclusion.UnsupportedByChunkPilot => AppTone.Danger,
            CompatibilityConclusion.TemporarilyUnavailable => AppTone.Warning,
            CompatibilityConclusion.RequiresAuthentication => AppTone.Warning,
            CompatibilityConclusion.RequiresUserSuppliedArtifact => AppTone.Warning,
            _ => AppTone.Neutral
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a compatibility conclusion to a semantic icon, so the state is not colour alone.</summary>
public sealed class CompatibilityIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            CompatibilityConclusion.VerifiedCompatible => AppIconKind.Success,
            CompatibilityConclusion.ProviderDeclaredCompatible => AppIconKind.Info,
            CompatibilityConclusion.Inferred => AppIconKind.Warning,
            CompatibilityConclusion.VerifiedIncompatible => AppIconKind.Error,
            CompatibilityConclusion.NoServerPackAvailable => AppIconKind.Error,
            CompatibilityConclusion.UnsupportedByChunkPilot => AppIconKind.Error,
            CompatibilityConclusion.TemporarilyUnavailable => AppIconKind.Clock,
            CompatibilityConclusion.RequiresAuthentication => AppIconKind.Key,
            CompatibilityConclusion.RequiresUserSuppliedArtifact => AppIconKind.Folder,
            _ => AppIconKind.Unknown
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Renders the conclusion's badge text. Text is mandatory on every compatibility surface.</summary>
public sealed class CompatibilityLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is CompatibilityConclusion conclusion
            ? CompatibilityConclusionPolicy.ShortLabel(conclusion)
            : CompatibilityConclusionPolicy.ShortLabel(CompatibilityConclusion.Unknown);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows an element only while the wizard is on the step named by the converter parameter.</summary>
public sealed class WizardStepVisibilityConverter : IValueConverter
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

/// <summary>Renders the shortest honest wording, for a badge inside a dense list row.</summary>
public sealed class CompatibilityBadgeLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is CompatibilityConclusion conclusion
            ? CompatibilityConclusionPolicy.BadgeLabel(conclusion)
            : CompatibilityConclusionPolicy.BadgeLabel(CompatibilityConclusion.Unknown);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the wizard is on the step named by the converter parameter.</summary>
/// <remarks>Separate from the visibility converter so a trigger can compare against a boolean.</remarks>
public sealed class WizardStepMatchConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is CreationWizardStep step &&
        parameter is string expected &&
        Enum.TryParse<CreationWizardStep>(expected, out var target) &&
        target == step;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows an element only when a string has content, so an empty value never renders a bare row.</summary>
public sealed class NonEmptyVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows an element only when a collection has entries.</summary>
public sealed class AnyItemsVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is System.Collections.IEnumerable items && items.GetEnumerator().MoveNext()
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Boolean to visibility, declared locally so the preview window resolves without App.xaml.</summary>
public sealed class PreviewBooleanVisibilityConverter : IValueConverter
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
