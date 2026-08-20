namespace ChunkPilot.App.WebUi;

internal static class WebUiPreviewOptions
{
    public const string PreviewArgument = "--webui-preview";

    public static bool IsRequested(IEnumerable<string> arguments) =>
        arguments.Any(argument => string.Equals(argument, PreviewArgument, StringComparison.OrdinalIgnoreCase));
}
