namespace ChunkPilot.IntegrationTests;

internal static class IntegrationTestRuntime
{
    public static string DotnetPath(string repositoryRoot)
    {
        var repositoryLocal = Path.Combine(repositoryRoot, ".tools", "dotnet", "dotnet.exe");
        if (File.Exists(repositoryLocal))
            return repositoryLocal;

        var sdkHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(sdkHost) && File.Exists(sdkHost))
            return Path.GetFullPath(sdkHost);

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), "dotnet.exe");
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        throw new FileNotFoundException(
            "Integration tests require the pinned .NET SDK either in .tools\\dotnet or on PATH.",
            repositoryLocal);
    }
}
