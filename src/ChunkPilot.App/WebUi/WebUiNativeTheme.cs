using System.Windows.Media;

namespace ChunkPilot.App.WebUi;

/// <summary>Maps the existing native design tokens into WebView2 host properties.</summary>
internal static class WebUiNativeTheme
{
    public static Brush ResolveBrush(string key) =>
        (Brush)Application.Current.FindResource(key);

    public static System.Drawing.Color ResolveWebViewColor(string key)
    {
        var brush = (SolidColorBrush)ResolveBrush(key);
        return System.Drawing.ColorTranslator.FromHtml(
            $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}");
    }
}
