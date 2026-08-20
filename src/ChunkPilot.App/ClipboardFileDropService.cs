using System.Collections.Specialized;

namespace ChunkPilot.App;

public static class ClipboardFileDropService
{
    public static StringCollection Prepare(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The file to copy does not exist.", fullPath);
        return new StringCollection { fullPath };
    }

    public static void Copy(string path) =>
        System.Windows.Clipboard.SetFileDropList(Prepare(path));
}
