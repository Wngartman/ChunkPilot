using System.Globalization;
using System.Windows;
using ChunkPilot.App;
using ChunkPilot.App.DesignSystem;

namespace ChunkPilot.UnitTests;

/// <summary>
/// Converters introduced for the Visual System v2 core-page migration.
/// </summary>
/// <remarks>
/// The Automation and Activity pages needed a "count == 0" visibility check that did not exist
/// (only the positive-count converter did), and the Activity page needed to turn the agent's
/// free-text <c>Result</c> field into an <see cref="AppTone"/> without guessing at every possible
/// failure string.
/// </remarks>
public sealed class ConvertersTests
{
    [Theory]
    [InlineData(0, Visibility.Visible)]
    [InlineData(1, Visibility.Collapsed)]
    [InlineData(5, Visibility.Collapsed)]
    public void ZeroIntToVisibilityConverter_shows_only_at_zero(int count, Visibility expected)
    {
        var converter = new ZeroIntToVisibilityConverter();
        Assert.Equal(expected, converter.Convert(count, typeof(Visibility), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ZeroIntToVisibilityConverter_treats_a_non_int_as_not_zero()
    {
        var converter = new ZeroIntToVisibilityConverter();
        Assert.Equal(Visibility.Collapsed, converter.Convert(null!, typeof(Visibility), null!, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("Success", AppTone.Success)]
    [InlineData("Failed", AppTone.Danger)]
    [InlineData("Exit 1", AppTone.Danger)]
    [InlineData("", AppTone.Neutral)]
    [InlineData(null, AppTone.Neutral)]
    public void ActivityResultToneConverter_maps_the_one_positive_value_and_treats_everything_else_as_needing_attention(
        string? result, AppTone expected)
    {
        var converter = new ActivityResultToneConverter();
        Assert.Equal(expected, converter.Convert(result!, typeof(AppTone), null!, CultureInfo.InvariantCulture));
    }
}
