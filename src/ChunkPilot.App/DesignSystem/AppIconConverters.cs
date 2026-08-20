using System.Globalization;
using System.Windows.Data;
using FluentGlyph = FluentIcons.Common.Icon;
using FluentGlyphSize = FluentIcons.Common.IconSize;
using FluentGlyphVariant = FluentIcons.Common.IconVariant;

namespace ChunkPilot.App.DesignSystem;

/// <summary>
/// Adapts <see cref="AppIcon"/> properties to the icon package inside the shared
/// <see cref="AppIcon"/> template. These converters exist only for that template.
/// </summary>
public sealed class AppIconKindToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is AppIconKind kind ? AppIconMap.Resolve(kind) : FluentGlyph.Question;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Maps a design-system size step to a purpose-drawn glyph size.</summary>
public sealed class AppIconScaleToSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is AppIconScale scale
            ? scale switch
            {
                AppIconScale.Small => FluentGlyphSize.Size16,
                AppIconScale.Medium => FluentGlyphSize.Size20,
                AppIconScale.Large => FluentGlyphSize.Size24,
                AppIconScale.Hero => FluentGlyphSize.Size32,
                _ => FluentGlyphSize.Size20
            }
            : FluentGlyphSize.Size20;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Maps the design-system glyph weight to the icon package variant.</summary>
public sealed class AppIconVariantConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is AppIconVariant.Filled ? FluentGlyphVariant.Filled : FluentGlyphVariant.Regular;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
