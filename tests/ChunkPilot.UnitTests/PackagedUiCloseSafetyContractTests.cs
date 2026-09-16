using System.Text.RegularExpressions;

namespace ChunkPilot.UnitTests;

public sealed class PackagedUiCloseSafetyContractTests
{
    [Fact]
    public void Agent_ownership_uses_connected_pipe_and_held_raw_process_identity()
    {
        var source = Script();
        Assert.Contains("GetProcessTimes(SafeProcessHandle process", source, StringComparison.Ordinal);
        Assert.Contains("QueryFullProcessImageName(SafeProcessHandle process", source, StringComparison.Ordinal);
        Assert.Contains("NtQueryInformationProcess(SafeProcessHandle process", source, StringComparison.Ordinal);
        Assert.Contains("GetNamedPipeServerProcessId(SafePipeHandle pipe", source, StringComparison.Ordinal);
        var identity = Function(source, "Get-ProcessIdentity");
        Assert.Contains("$handle = $Process.SafeHandle", identity, StringComparison.Ordinal);
        Assert.Contains("RawCreationFileTime = $times[0]", identity, StringComparison.Ordinal);
        Assert.Contains("RawExitFileTime = $times[1]", identity, StringComparison.Ordinal);
        var target = Function(source, "Get-ExactTargetAgent");
        Assert.Contains("[IO.Pipes.PipeOptions]::CurrentUserOnly", target, StringComparison.Ordinal);
        Assert.Contains("$pipe.Connect(1000)", target, StringComparison.Ordinal);
        Assert.Contains("$identity = Get-ProcessIdentity $candidate", target, StringComparison.Ordinal);
        Assert.Contains("$identity.ParentProcessId -ne $App.Id", target, StringComparison.Ordinal);
        Assert.Contains("$parentTimes[0] -ne $AppIdentity.RawCreationFileTime", target, StringComparison.Ordinal);
        Assert.Contains("$parentTimes[1] -ne 0", target, StringComparison.Ordinal);
        Assert.Contains("$identity.RawCreationFileTime -lt $parentTimes[0]", target, StringComparison.Ordinal);
        Assert.Contains("$identity.ExecutablePath -ine $ExpectedAgentPath", target, StringComparison.Ordinal);
        Assert.Contains("PipeServer($pipe.SafePipeHandle) -ne $serverId", target, StringComparison.Ordinal);
        Assert.True(target.IndexOf("$identity = Get-ProcessIdentity $candidate", StringComparison.Ordinal) <
                    target.IndexOf("$pipe.Dispose()", StringComparison.Ordinal));
    }

    [Fact]
    public void Pipe_io_and_cleanup_are_bounded_without_process_rediscovery()
    {
        var source = Script();
        Assert.DoesNotContain("Get-CimInstance", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Get-Process -Id", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fallbackTargetAgentIds", source, StringComparison.Ordinal);
        Assert.False(Regex.IsMatch(source, @"\.WaitForExit\s*\(\s*\)",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
        var request = Function(source, "Invoke-AgentRequest");
        Assert.Contains("$write = $writer.WriteLineAsync($request)", request, StringComparison.Ordinal);
        Assert.Contains("$write.Wait(1000)", request, StringComparison.Ordinal);
        Assert.Contains("$read = $reader.ReadLineAsync()", request, StringComparison.Ordinal);
        Assert.Contains("$read.Wait(1000)", request, StringComparison.Ordinal);
        Assert.Contains("$times[0] -ne $ExpectedIdentity.RawCreationFileTime", request, StringComparison.Ordinal);
        Assert.Contains("PipeServer($pipe.SafePipeHandle) -ne $ExpectedProcess.Id", request, StringComparison.Ordinal);
        Assert.True(request.IndexOf("$pipe.Dispose()", StringComparison.Ordinal) <
                    request.IndexOf("$reader.Dispose()", StringComparison.Ordinal));
        var stop = Function(source, "Stop-ExactOwnedProcess");
        Assert.DoesNotContain("GetProcessById", stop, StringComparison.Ordinal);
        Assert.Contains("Times($Process.SafeHandle)", stop, StringComparison.Ordinal);
        Assert.Contains("$times[0] -ne $Identity.RawCreationFileTime", stop, StringComparison.Ordinal);
        Assert.Contains("$Process.Kill()", stop, StringComparison.Ordinal);
        Assert.Contains("$Process.WaitForExit($script:AgentShutdownTimeoutMilliseconds)", stop, StringComparison.Ordinal);
        Assert.Contains("Test-ExactProcessExited $Process $Identity", stop, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_launched_processes_receive_complete_isolation_without_credential_bootstrap()
    {
        var source = Script();
        var environment = Function(source, "Initialize-IsolatedEnvironment");
        Assert.Contains("$Info.Environment['CHUNKPILOT_DATA_ROOT'] = $DataRoot", environment, StringComparison.Ordinal);
        Assert.Contains("$Info.Environment['CHUNKPILOT_MANAGED_SERVERS_ROOT'] = Join-Path $DataRoot 'servers'", environment, StringComparison.Ordinal);
        Assert.Contains("$Info.Environment['CHUNKPILOT_INSTANCE_ID'] = $InstanceId", environment, StringComparison.Ordinal);
        Assert.Contains("@('APPDATA','LOCALAPPDATA','TEMP','TMP')", environment, StringComparison.Ordinal);
        Assert.Contains("$Info.Environment[$variable] = $directory", environment, StringComparison.Ordinal);
        Assert.Contains("$Info.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $DataRoot 'bundle'", environment, StringComparison.Ordinal);
        Assert.Contains("Environment.Remove('CHUNKPILOT_CURSEFORGE_KEY_FILE')", environment, StringComparison.Ordinal);
        Assert.Contains("Environment.Remove('CHUNKPILOT_REACHABILITY_PROBE_URL')", environment, StringComparison.Ordinal);
        Assert.Contains("Initialize-IsolatedEnvironment $info $DataRoot $InstanceId", source, StringComparison.Ordinal);
        Assert.Contains("Initialize-IsolatedEnvironment $appInfo $targetRoot $targetInstanceId", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".missing-curseforge-api-key", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SafeApplicationExit", Function(source, "Initialize-IsolatedEnvironment"), StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_ownership_fails_closed_and_prevents_root_removal()
    {
        var source = Script();
        var exited = Function(source, "Test-ExactProcessExited");
        Assert.Contains("if ($null -eq $Identity) { return $false }", exited, StringComparison.Ordinal);
        Assert.Contains("$times[0] -eq $Identity.RawCreationFileTime -and $times[1] -gt 0", exited, StringComparison.Ordinal);
        Assert.Contains("catch { return $false }", exited, StringComparison.Ordinal);
        var stop = Function(source, "Stop-ExactOwnedProcess");
        Assert.Contains("ownership is unproved; process and root retained.", stop, StringComparison.Ordinal);
        Assert.Contains("cleanup failed; root retained:", stop, StringComparison.Ordinal);
        Assert.Contains("$targetOwnershipComplete = $null -eq $app -or $null -ne $targetAgentIdentity", source, StringComparison.Ordinal);
        Assert.Contains("if ($appExited -and $targetAgentExited -and $targetOwnershipComplete) { $targetClean = Remove-TemporaryRoot $targetRoot }", source, StringComparison.Ordinal);
        Assert.Contains("if ($unrelatedExited) { $unrelatedClean = Remove-TemporaryRoot $unrelatedRoot }", source, StringComparison.Ordinal);
        Assert.Contains("[IO.Path]::GetDirectoryName($fullPath), $tempPath", source, StringComparison.Ordinal);
        Assert.Contains("^ChunkPilot-packaged-ui-close-(target|unrelated)-[a-f0-9]{32}$", source, StringComparison.Ordinal);
        Assert.Contains("[IO.FileAttributes]::ReparsePoint", source, StringComparison.Ordinal);
        Assert.Contains("$script:Failures.Count -eq 0", source, StringComparison.Ordinal);
    }

    private static string Function(string source, string name)
    {
        var match = Regex.Match(source, @"(?ms)^function " + Regex.Escape(name) + @"\b.*?(?=^function |\z)",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Assert.True(match.Success, "Missing safety function: " + name);
        return match.Value;
    }

    private static string Script()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ChunkPilot.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        return File.ReadAllText(Path.Combine(root, "scripts", "test-packaged-ui-close.ps1"));
    }
}
