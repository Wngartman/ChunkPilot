[CmdletBinding()]
param([string]$PortableRoot)

$ErrorActionPreference = "Stop"
# A clean hosted runner pays WebView2 profile creation plus antivirus scanning on
# its first launch. Keep the gate bounded, but allow that cold-start work.
$script:AppStartupTimeoutMilliseconds = 45000
$script:AgentStartupTimeoutMilliseconds = 10000
$script:UiExitTimeoutMilliseconds = 3000
$script:AgentShutdownTimeoutMilliseconds = 10000
$script:PollIntervalMilliseconds = 100
$script:Result = [ordered]@{
    Mode = "WebUI"
    AppLaunched = $false
    MainWindowDetected = $false
    WmCloseSent = $false
    UiExitCode = $null
    UiExitDurationMilliseconds = $null
    TargetAgentExitResult = $false
    UnrelatedAgentSurvivalResult = $false
    InvisibleUiProcessCount = $null
    TemporaryRootCleanupResult = $false
    OverallPass = $false
}
$script:Failures = [Collections.Generic.List[string]]::new()

if (-not ('ChunkPilotPackagedUiIdentity' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
public static class ChunkPilotPackagedUiIdentity {
    [StructLayout(LayoutKind.Sequential)] private struct BasicInformation {
        public IntPtr Reserved1, Peb, Reserved2, Reserved3;
        public UIntPtr ProcessId, ParentProcessId;
    }
    [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", EntryPoint="QueryFullProcessImageNameW", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder image, ref int size);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int kind, out BasicInformation info, int size, out int returned);
    [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    public static long[] Times(SafeProcessHandle process) {
        long creation, exit, kernel, user;
        if (!GetProcessTimes(process,out creation,out exit,out kernel,out user) || creation <= 0)
            throw new InvalidOperationException("The exact process creation/exit identity could not be read.");
        return new long[] { creation, exit };
    }
    public static string Image(SafeProcessHandle process) {
        var image = new StringBuilder(32768); int size = image.Capacity;
        if (!QueryFullProcessImageName(process,0,image,ref size)) throw new InvalidOperationException("The exact process image could not be read.");
        return image.ToString();
    }
    public static int Parent(SafeProcessHandle process) {
        BasicInformation info; int returned;
        if (NtQueryInformationProcess(process,0,out info,Marshal.SizeOf(typeof(BasicInformation)),out returned) != 0)
            throw new InvalidOperationException("The exact process parent could not be read.");
        return checked((int)info.ParentProcessId.ToUInt64());
    }
    public static int PipeServer(SafePipeHandle pipe) {
        uint id;
        if (!GetNamedPipeServerProcessId(pipe,out id) || id == 0) throw new InvalidOperationException("The exact Agent pipe server could not be read.");
        return checked((int)id);
    }
}
'@
}

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutMilliseconds, [string]$Description) {
    $waitClock = [Diagnostics.Stopwatch]::StartNew()
    while ($waitClock.ElapsedMilliseconds -lt $TimeoutMilliseconds) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds $script:PollIntervalMilliseconds
    }
    throw "Timed out after $TimeoutMilliseconds ms waiting for $Description."
}

function Invoke-AgentRequest([string]$PipeName, [string]$Operation, [int]$ConnectTimeoutMilliseconds = 1000,
    [Diagnostics.Process]$ExpectedProcess = $null, $ExpectedIdentity = $null) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new(
        ".", $PipeName, [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly)
    $reader = $null
    $writer = $null
    try {
        $pipe.Connect($ConnectTimeoutMilliseconds)
        if ($ExpectedProcess) {
            $times = [ChunkPilotPackagedUiIdentity]::Times($ExpectedProcess.SafeHandle)
            if ($null -eq $ExpectedIdentity -or $times[0] -ne $ExpectedIdentity.RawCreationFileTime -or $times[1] -ne 0 -or
                [ChunkPilotPackagedUiIdentity]::PipeServer($pipe.SafePipeHandle) -ne $ExpectedProcess.Id) {
                throw 'The connected pipe does not belong to the exact held Agent process.'
            }
        }
        $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 65536, $true)
        $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($pipe, [Text.UTF8Encoding]::new($false), $false, 65536, $true)
        $request = @{
            requestId = [Guid]::NewGuid().ToString("N")
            operation = $Operation
            payload = @{}
        } | ConvertTo-Json -Compress -Depth 5
        $write = $writer.WriteLineAsync($request)
        if (-not $write.Wait(1000)) { throw "Agent request write timed out for $Operation." }
        $read = $reader.ReadLineAsync()
        if (-not $read.Wait(1000)) { throw "Agent response timed out for $Operation." }
        $line = $read.GetAwaiter().GetResult()
        if ([string]::IsNullOrWhiteSpace($line)) { throw "Agent returned an empty response to $Operation." }
        $response = $line | ConvertFrom-Json
        if (-not $response.success) { throw "Agent rejected ${Operation}: $($response.error)" }
        return $response.payload
    }
    finally {
        # Abort any timed-out asynchronous IO before disposing stream wrappers.
        $pipe.Dispose()
        if ($reader) { try { $reader.Dispose() } catch { } }
        if ($writer) { try { $writer.Dispose() } catch { } }
    }
}

function Wait-ForAgent([string]$PipeName, [int]$TimeoutMilliseconds, [string]$Description) {
    Wait-Until {
        try {
            $null = Invoke-AgentRequest $PipeName "Ping"
            return $true
        }
        catch { return $false }
    } $TimeoutMilliseconds $Description | Out-Null
}

function Get-ProcessIdentity([Diagnostics.Process]$Process) {
    $handle = $Process.SafeHandle
    $times = [ChunkPilotPackagedUiIdentity]::Times($handle)
    [PSCustomObject]@{
        ProcessId = $Process.Id
        ParentProcessId = [ChunkPilotPackagedUiIdentity]::Parent($handle)
        RawCreationFileTime = $times[0]
        RawExitFileTime = $times[1]
        ExecutablePath = [ChunkPilotPackagedUiIdentity]::Image($handle)
    }
}

function Initialize-IsolatedEnvironment([Diagnostics.ProcessStartInfo]$Info, [string]$DataRoot, [string]$InstanceId) {
    $Info.Environment['CHUNKPILOT_DATA_ROOT'] = $DataRoot
    $Info.Environment['CHUNKPILOT_MANAGED_SERVERS_ROOT'] = Join-Path $DataRoot 'servers'
    $Info.Environment['CHUNKPILOT_INSTANCE_ID'] = $InstanceId
    [void]$Info.Environment.Remove('CHUNKPILOT_CURSEFORGE_KEY_FILE')
    [void]$Info.Environment.Remove('CHUNKPILOT_REACHABILITY_PROBE_URL')
    foreach ($variable in @('APPDATA','LOCALAPPDATA','TEMP','TMP')) {
        $directory = Join-Path $DataRoot $variable.ToLowerInvariant()
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        $Info.Environment[$variable] = $directory
    }
    $Info.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $DataRoot 'bundle'
}

function Get-ExactTargetAgent([string]$PipeName, [Diagnostics.Process]$App, $AppIdentity, [string]$ExpectedAgentPath) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly)
    $candidate = $null
    try {
        $pipe.Connect(1000)
        $serverId = [ChunkPilotPackagedUiIdentity]::PipeServer($pipe.SafePipeHandle)
        $candidate = [Diagnostics.Process]::GetProcessById($serverId)
        $identity = Get-ProcessIdentity $candidate
        $parentTimes = [ChunkPilotPackagedUiIdentity]::Times($App.SafeHandle)
        if ($identity.RawExitFileTime -ne 0 -or $identity.ParentProcessId -ne $App.Id -or
            $parentTimes[0] -ne $AppIdentity.RawCreationFileTime -or $parentTimes[1] -ne 0 -or
            $identity.RawCreationFileTime -lt $parentTimes[0] -or
            $identity.ExecutablePath -ine $ExpectedAgentPath -or
            [ChunkPilotPackagedUiIdentity]::PipeServer($pipe.SafePipeHandle) -ne $serverId) {
            throw 'The connected Agent could not be proved as a live exact child of the held App process.'
        }
        $result = [pscustomobject]@{ Process = $candidate; Identity = $identity }
        $candidate = $null
        return $result
    }
    finally { if ($candidate) { $candidate.Dispose() }; $pipe.Dispose() }
}

function Test-ExactProcessExited([Diagnostics.Process]$Process, $Identity) {
    if ($null -eq $Process) { return $true }
    if ($null -eq $Identity) { return $false }
    try {
        $times = [ChunkPilotPackagedUiIdentity]::Times($Process.SafeHandle)
        return $times[0] -eq $Identity.RawCreationFileTime -and $times[1] -gt 0
    }
    catch { return $false }
}

function Stop-ExactOwnedProcess([Diagnostics.Process]$Process, $Identity, [string]$Description) {
    if ($null -eq $Process) { return $true }
    if (Test-ExactProcessExited $Process $Identity) { return $true }
    if ($null -eq $Identity) {
        $script:Failures.Add("$Description ownership is unproved; process and root retained.")
        return $false
    }
    try {
        $times = [ChunkPilotPackagedUiIdentity]::Times($Process.SafeHandle)
        if ($times[0] -ne $Identity.RawCreationFileTime) { throw 'Held process creation identity changed.' }
        if ($times[1] -eq 0) { $Process.Kill() }
        if (-not $Process.WaitForExit($script:AgentShutdownTimeoutMilliseconds)) { throw 'Exact process exit exceeded the cleanup deadline.' }
        if (-not (Test-ExactProcessExited $Process $Identity)) { throw 'Exact process exit could not be proved.' }
        return $true
    }
    catch { $script:Failures.Add("$Description cleanup failed; root retained: $($_.Exception.Message)"); return $false }
}

function Start-IsolatedAgent([string]$AgentPath, [string]$DataRoot, [string]$InstanceId) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $AgentPath
    $info.WorkingDirectory = Split-Path -Parent $AgentPath
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    Initialize-IsolatedEnvironment $info $DataRoot $InstanceId
    $process = [Diagnostics.Process]::Start($info)
    if ($null -eq $process) { throw "Windows did not start the unrelated isolated Agent." }
    return $process
}

function Remove-TemporaryRoot([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $tempPath = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($fullPath), $tempPath, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($fullPath) -notmatch '^ChunkPilot-packaged-ui-close-(target|unrelated)-[a-f0-9]{32}$') {
        throw "Refusing to remove a path outside this script's validated temporary-root pattern: $fullPath"
    }
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if (-not (Test-Path -LiteralPath $fullPath)) { return $true }
        if (((Get-Item -LiteralPath $fullPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Refusing to remove a temporary root that became a reparse point.'
        }
        try {
            Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction Stop
        }
        catch [IO.IOException] {
            # WebView2 profile files can remain locked briefly while its child processes exit.
        }
        catch [UnauthorizedAccessException] {
            # Treat a transient profile lock like the equivalent IOException.
        }
        if (-not (Test-Path -LiteralPath $fullPath)) { return $true }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PortableRoot)) { $PortableRoot = Join-Path $repositoryRoot "artifacts\portable-test" }
$portableRootFull = [IO.Path]::GetFullPath($PortableRoot)
$appPath = Join-Path $portableRootFull "ChunkPilot.exe"
$agentPath = Join-Path $portableRootFull "Agent\ChunkPilot.Agent.exe"
$targetRoot = Join-Path ([IO.Path]::GetTempPath()) ("ChunkPilot-packaged-ui-close-target-" + [Guid]::NewGuid().ToString("N"))
$unrelatedRoot = Join-Path ([IO.Path]::GetTempPath()) ("ChunkPilot-packaged-ui-close-unrelated-" + [Guid]::NewGuid().ToString("N"))
$targetInstanceId = "pkg-ui-target-" + [Guid]::NewGuid().ToString("N")
$unrelatedInstanceId = "pkg-ui-unrelated-" + [Guid]::NewGuid().ToString("N")
$targetPipeName = "ChunkPilot.Agent.v1.$targetInstanceId"
$unrelatedPipeName = "ChunkPilot.Agent.v1.$unrelatedInstanceId"
$app = $null
$unrelatedAgent = $null
$targetAgent = $null
$appIdentity = $null
$targetAgentIdentity = $null
$unrelatedAgentIdentity = $null

try {
    Assert-Condition (Test-Path -LiteralPath $appPath) "Published portable App was not found: $appPath"
    Assert-Condition (Test-Path -LiteralPath $agentPath) "Published portable Agent was not found: $agentPath"
    $sourceCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    Assert-Condition ($LASTEXITCODE -eq 0 -and $sourceCommit.Length -eq 40) "Could not determine the source commit for published-layout freshness validation."
    foreach ($path in @($appPath, $agentPath)) {
        $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion
        Assert-Condition (-not [string]::IsNullOrWhiteSpace($productVersion) -and $productVersion.IndexOf($sourceCommit, [StringComparison]::OrdinalIgnoreCase) -ge 0) "Published file is stale for ${sourceCommit}: $path ($productVersion)"
    }

    New-Item -ItemType Directory -Path $targetRoot, $unrelatedRoot -Force | Out-Null
    $unrelatedAgent = Start-IsolatedAgent $agentPath $unrelatedRoot $unrelatedInstanceId
    $unrelatedAgentIdentity = Get-ProcessIdentity $unrelatedAgent
    Assert-Condition ($unrelatedAgentIdentity.ExecutablePath -ieq $agentPath -and $unrelatedAgentIdentity.RawExitFileTime -eq 0) 'The unrelated Agent does not match the live packaged process.'
    Wait-ForAgent $unrelatedPipeName $script:AgentStartupTimeoutMilliseconds "the unrelated isolated Agent pipe"

    $appInfo = [Diagnostics.ProcessStartInfo]::new()
    $appInfo.FileName = $appPath
    $appInfo.WorkingDirectory = Split-Path -Parent $appPath
    $appInfo.UseShellExecute = $false
    Initialize-IsolatedEnvironment $appInfo $targetRoot $targetInstanceId
    $app = [Diagnostics.Process]::Start($appInfo)
    Assert-Condition ($null -ne $app) "Windows did not start the packaged App."
    $script:Result.AppLaunched = $true
    $appIdentity = Get-ProcessIdentity $app
    Assert-Condition ($appIdentity.ExecutablePath -ieq $appPath -and $appIdentity.RawExitFileTime -eq 0) 'The App does not match the live packaged process.'

    Assert-Condition ($app.WaitForInputIdle($script:AppStartupTimeoutMilliseconds)) "Packaged App did not reach input idle."
    Wait-Until {
        $app.Refresh()
        return -not $app.HasExited -and $app.MainWindowHandle -ne [IntPtr]::Zero
    } $script:AppStartupTimeoutMilliseconds "the packaged App main window" | Out-Null
    $script:Result.MainWindowDetected = $true
    Wait-ForAgent $targetPipeName $script:AgentStartupTimeoutMilliseconds "the target isolated Agent pipe"

    $targetProof = Get-ExactTargetAgent $targetPipeName $app $appIdentity $agentPath
    $targetAgent = $targetProof.Process
    $targetAgentIdentity = $targetProof.Identity

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $script:Result.WmCloseSent = $app.CloseMainWindow()
    Assert-Condition $script:Result.WmCloseSent "Process.CloseMainWindow did not send WM_CLOSE to the packaged App."
    Start-Sleep -Milliseconds 50
    try { $null = $app.CloseMainWindow() }
    catch [InvalidOperationException] {
        if (-not (Test-ExactProcessExited $app $appIdentity)) { throw }
    }
    Assert-Condition ($app.WaitForExit($script:UiExitTimeoutMilliseconds)) "Packaged App did not exit within $script:UiExitTimeoutMilliseconds ms after WM_CLOSE."
    $stopwatch.Stop()
    $script:Result.UiExitDurationMilliseconds = [math]::Round($stopwatch.Elapsed.TotalMilliseconds, 0)
    $script:Result.UiExitCode = $app.ExitCode
    Assert-Condition ($stopwatch.ElapsedMilliseconds -lt $script:UiExitTimeoutMilliseconds) "WM_CLOSE took $($stopwatch.ElapsedMilliseconds) ms."

    Wait-Until {
        return Test-ExactProcessExited $targetAgent $targetAgentIdentity
    } $script:AgentShutdownTimeoutMilliseconds "the intended target Agent shutdown" | Out-Null
    $script:Result.TargetAgentExitResult = $true

    $unrelatedTimes = [ChunkPilotPackagedUiIdentity]::Times($unrelatedAgent.SafeHandle)
    Assert-Condition ($unrelatedTimes[0] -eq $unrelatedAgentIdentity.RawCreationFileTime -and $unrelatedTimes[1] -eq 0) "The unrelated isolated Agent exited during target App shutdown."
    $null = Invoke-AgentRequest $unrelatedPipeName 'Ping' -ExpectedProcess $unrelatedAgent -ExpectedIdentity $unrelatedAgentIdentity
    $script:Result.UnrelatedAgentSurvivalResult = $true

    $script:Result.InvisibleUiProcessCount = if (Test-ExactProcessExited $app $appIdentity) { 0 } else { 1 }
    Assert-Condition ($script:Result.InvisibleUiProcessCount -eq 0) "An invisible target UI process remains after WM_CLOSE."

    $script:Result.OverallPass = $true
}
catch {
    $script:Failures.Add($_.Exception.Message)
}
finally {
    # Cleanup never reopens a PID. Missing identity is retained failure evidence,
    # not permission to terminate a discovered process or remove its data root.
    if ($app -and $appIdentity -and -not $targetAgent) {
        # An early window-startup failure may still have created an Agent. The
        # same exact pipe/held-parent proof is allowed once before the App exits.
        try {
            $targetProof = Get-ExactTargetAgent $targetPipeName $app $appIdentity $agentPath
            $targetAgent = $targetProof.Process
            $targetAgentIdentity = $targetProof.Identity
        }
        catch { }
    }
    $appExited = Stop-ExactOwnedProcess $app $appIdentity 'Target App'
    $targetAgentExited = Stop-ExactOwnedProcess $targetAgent $targetAgentIdentity 'Target Agent'
    $targetOwnershipComplete = $null -eq $app -or $null -ne $targetAgentIdentity
    if (-not $targetOwnershipComplete) { $script:Failures.Add('The target Agent was never proved; its possible process and temporary root are retained.') }
    if ($unrelatedAgent -and -not (Test-ExactProcessExited $unrelatedAgent $unrelatedAgentIdentity)) {
        try {
            $null = Invoke-AgentRequest $unrelatedPipeName 'ShutdownAgent' -ExpectedProcess $unrelatedAgent -ExpectedIdentity $unrelatedAgentIdentity
            $null = $unrelatedAgent.WaitForExit($script:AgentShutdownTimeoutMilliseconds)
        }
        catch { }
    }
    $unrelatedExited = Stop-ExactOwnedProcess $unrelatedAgent $unrelatedAgentIdentity 'Unrelated isolated Agent'
    try {
        $targetClean = $false
        $unrelatedClean = $false
        if ($appExited -and $targetAgentExited -and $targetOwnershipComplete) { $targetClean = Remove-TemporaryRoot $targetRoot }
        if ($unrelatedExited) { $unrelatedClean = Remove-TemporaryRoot $unrelatedRoot }
        $script:Result.TemporaryRootCleanupResult = $targetClean -and $unrelatedClean
        if (-not $script:Result.TemporaryRootCleanupResult) { $script:Failures.Add("One or more script-created temporary roots remain.") }
    }
    catch { $script:Failures.Add($_.Exception.Message) }

    $script:Result["TargetAppProcessId"] = if ($appIdentity) { $appIdentity.ProcessId } else { $null }
    $script:Result["TargetAgentProcessId"] = if ($targetAgentIdentity) { $targetAgentIdentity.ProcessId } else { $null }
    $script:Result["UnrelatedAgentProcessId"] = if ($unrelatedAgent) { $unrelatedAgent.Id } else { $null }
    $script:Result["TemporaryRoots"] = @($targetRoot, $unrelatedRoot)
    foreach ($process in @($app, $targetAgent, $unrelatedAgent)) { if ($process) { $process.Dispose() } }
    $script:Result["Failures"] = @($script:Failures)
    $script:Result.OverallPass = $script:Result.OverallPass -and
        $script:Result.TemporaryRootCleanupResult -and
        $script:Failures.Count -eq 0
    $script:Result | ConvertTo-Json -Compress -Depth 5
}

if (-not $script:Result.OverallPass -or -not $script:Result.TemporaryRootCleanupResult) { exit 1 }
exit 0
