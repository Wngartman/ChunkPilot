using System.Diagnostics;

namespace ChunkPilot.App;

public interface IFolderLauncher
{
    FolderLaunchResult OpenExisting(string path);
}

public sealed record FolderLaunchResult(bool Success, string Message)
{
    public static FolderLaunchResult Opened() => new(true, "Server folder opened.");
    public static FolderLaunchResult Failed(string message) => new(false, message);
}

/// <summary>Windows-only exact-path launcher. It never creates, guesses, or changes the requested path.</summary>
public sealed class WindowsFolderLauncher : IFolderLauncher
{
    public FolderLaunchResult OpenExisting(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return FolderLaunchResult.Failed("This server has no recorded folder.");

        string exactPath;
        try
        {
            exactPath = Path.GetFullPath(path);
            if (!Directory.Exists(exactPath))
                return FolderLaunchResult.Failed("The recorded server folder is missing or cannot be accessed.");

            var start = BuildStartInfo(exactPath);
            Process.Start(start);
            return FolderLaunchResult.Opened();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
                                          UnauthorizedAccessException or System.ComponentModel.Win32Exception or
                                          NotSupportedException)
        {
            return FolderLaunchResult.Failed("The recorded server folder could not be opened.");
        }
    }

    internal static ProcessStartInfo BuildStartInfo(string exactPath)
    {
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(exactPath);
        return start;
    }
}
