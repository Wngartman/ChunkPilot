[CmdletBinding()]
param(
    [string]$PortableRoot,
    [switch]$WebUiPreview
)

$ErrorActionPreference = "Stop"
$script:AppStartupTimeoutMilliseconds = 15000
$script:AgentStartupTimeoutMilliseconds = 10000
$script:UiExitTimeoutMilliseconds = 3000
$script:AgentShutdownTimeoutMilliseconds = 10000
$script:PollIntervalMilliseconds = 100
$script:Result = [ordered]@{
    Mode = if ($WebUiPreview) { "WebUI preview" } else { "accepted UI" }
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

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutMilliseconds, [string]$Description) {
    $deadline = [DateTimeOffset]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds $script:PollIntervalMilliseconds
    }
    throw "Timed out after $TimeoutMilliseconds ms waiting for $Description."
}

function Invoke-AgentRequest([string]$PipeName, [string]$Operation, [int]$ConnectTimeoutMilliseconds = 1000) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new(
        ".", $PipeName, [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly)
    $reader = $null
    $writer = $null
    try {
        $pipe.Connect($ConnectTimeoutMilliseconds)
        $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 65536, $true)
        $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($pipe, [Text.UTF8Encoding]::new($false), $false, 65536, $true)
        $request = @{
            requestId = [Guid]::NewGuid().ToString("N")
            operation = $Operation
            payload = @{}
        } | ConvertTo-Json -Compress -Depth 5
        $writer.WriteLine($request)
        $line = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($line)) { throw "Agent returned an empty response to $Operation." }
        $response = $line | ConvertFrom-Json
        if (-not $response.success) { throw "Agent rejected ${Operation}: $($response.error)" }
        return $response.payload
    }
    finally {
        if ($reader) { $reader.Dispose() }
        if ($writer) { $writer.Dispose() }
        $pipe.Dispose()
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

function Get-ProcessIdentity([int]$ProcessId) {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }
    [PSCustomObject]@{
        ProcessId = [int]$process.ProcessId
        ParentProcessId = [int]$process.ParentProcessId
        CreationDate = [datetime]$process.CreationDate
        ExecutablePath = $process.ExecutablePath
        WorkingDirectory = $process.CommandLine
        CommandLine = $process.CommandLine
    }
}

function Start-IsolatedAgent([string]$AgentPath, [string]$DataRoot, [string]$InstanceId) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $AgentPath
    $info.WorkingDirectory = Split-Path -Parent $AgentPath
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.Environment["CHUNKPILOT_DATA_ROOT"] = $DataRoot
    $info.Environment["CHUNKPILOT_INSTANCE_ID"] = $InstanceId
    $process = [Diagnostics.Process]::Start($info)
    if ($null -eq $process) { throw "Windows did not start the unrelated isolated Agent." }
    return $process
}

function Remove-TemporaryRoot([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $tempPath = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $fullPath.StartsWith($tempPath, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($fullPath).StartsWith("ChunkPilot-packaged-ui-close-", [StringComparison]::OrdinalIgnoreCase))) {
        throw "Refusing to remove a path outside this script's validated temporary-root pattern: $fullPath"
    }
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if (-not (Test-Path -LiteralPath $fullPath)) { return $true }
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
    Wait-ForAgent $unrelatedPipeName $script:AgentStartupTimeoutMilliseconds "the unrelated isolated Agent pipe"

    $appInfo = [Diagnostics.ProcessStartInfo]::new()
    $appInfo.FileName = $appPath
    $appInfo.WorkingDirectory = Split-Path -Parent $appPath
    $appInfo.UseShellExecute = $false
    if ($WebUiPreview) { $appInfo.Arguments = "--webui-preview" }
    $appInfo.Environment["CHUNKPILOT_DATA_ROOT"] = $targetRoot
    $appInfo.Environment["CHUNKPILOT_INSTANCE_ID"] = $targetInstanceId
    $app = [Diagnostics.Process]::Start($appInfo)
    Assert-Condition ($null -ne $app) "Windows did not start the packaged App."
    $script:Result.AppLaunched = $true
    Wait-Until {
        $candidate = Get-ProcessIdentity $app.Id
        if ($null -eq $candidate) { return $false }
        if ([string]::IsNullOrWhiteSpace($candidate.ExecutablePath)) { return $false }
        $script:appIdentity = $candidate
        return [string]::Equals(
            [IO.Path]::GetFullPath($candidate.ExecutablePath),
            [IO.Path]::GetFullPath($appPath),
            [StringComparison]::OrdinalIgnoreCase)
    } $script:AppStartupTimeoutMilliseconds "the packaged App process identity" | Out-Null

    Assert-Condition ($app.WaitForInputIdle($script:AppStartupTimeoutMilliseconds)) "Packaged App did not reach input idle."
    Wait-Until {
        $app.Refresh()
        return -not $app.HasExited -and $app.MainWindowHandle -ne [IntPtr]::Zero
    } $script:AppStartupTimeoutMilliseconds "the packaged App main window" | Out-Null
    $script:Result.MainWindowDetected = $true
    Wait-ForAgent $targetPipeName $script:AgentStartupTimeoutMilliseconds "the target isolated Agent pipe"

    Wait-Until {
        $candidate = Get-CimInstance Win32_Process | Where-Object {
            $_.ParentProcessId -eq $app.Id -and $_.ExecutablePath -eq $agentPath -and
            $_.CommandLine -match 'ChunkPilot\.Agent\.exe'
        } | Select-Object -First 1
        if ($candidate) {
            $script:targetAgentIdentity = [PSCustomObject]@{
                ProcessId = [int]$candidate.ProcessId
                ParentProcessId = [int]$candidate.ParentProcessId
                CreationDate = [datetime]$candidate.CreationDate
                ExecutablePath = $candidate.ExecutablePath
                CommandLine = $candidate.CommandLine
            }
            $script:targetAgent = Get-Process -Id $candidate.ProcessId -ErrorAction Stop
            return $true
        }
        return $false
    } $script:AgentStartupTimeoutMilliseconds "the target Agent identity" | Out-Null
    Assert-Condition ($targetAgentIdentity.ParentProcessId -eq $app.Id -and $targetAgentIdentity.ExecutablePath -eq $agentPath) "Target Agent identity does not match the App parent and packaged Agent path."

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $script:Result.WmCloseSent = $app.CloseMainWindow()
    Assert-Condition $script:Result.WmCloseSent "Process.CloseMainWindow did not send WM_CLOSE to the packaged App."
    Start-Sleep -Milliseconds 50
    $null = $app.CloseMainWindow()
    Assert-Condition ($app.WaitForExit($script:UiExitTimeoutMilliseconds)) "Packaged App did not exit within $script:UiExitTimeoutMilliseconds ms after WM_CLOSE."
    $stopwatch.Stop()
    $script:Result.UiExitDurationMilliseconds = [math]::Round($stopwatch.Elapsed.TotalMilliseconds, 0)
    $script:Result.UiExitCode = $app.ExitCode
    Assert-Condition ($stopwatch.ElapsedMilliseconds -lt $script:UiExitTimeoutMilliseconds) "WM_CLOSE took $($stopwatch.ElapsedMilliseconds) ms."

    Wait-Until {
        $candidate = Get-CimInstance Win32_Process -Filter "ProcessId = $($targetAgentIdentity.ProcessId)" -ErrorAction SilentlyContinue
        return $null -eq $candidate -or [datetime]$candidate.CreationDate -ne $targetAgentIdentity.CreationDate
    } $script:AgentShutdownTimeoutMilliseconds "the intended target Agent shutdown" | Out-Null
    $script:Result.TargetAgentExitResult = $true

    $unrelatedAgent.Refresh()
    Assert-Condition (-not $unrelatedAgent.HasExited) "The unrelated isolated Agent exited during target App shutdown."
    $null = Invoke-AgentRequest $unrelatedPipeName "Ping"
    $script:Result.UnrelatedAgentSurvivalResult = $true

    $leftover = Get-CimInstance Win32_Process -Filter "ProcessId = $($appIdentity.ProcessId)" -ErrorAction SilentlyContinue
    $script:Result.InvisibleUiProcessCount = if ($leftover -and
        [datetime]$leftover.CreationDate -eq $appIdentity.CreationDate -and
        $leftover.ExecutablePath -eq $appIdentity.ExecutablePath) { 1 } else { 0 }
    Assert-Condition ($script:Result.InvisibleUiProcessCount -eq 0) "An invisible target UI process remains after WM_CLOSE."

    $script:Result.OverallPass = $true
}
catch {
    $script:Failures.Add($_.Exception.Message)
}
finally {
    $fallbackTargetAgentIds = @()
    if ($app) {
        $fallbackTargetAgentIds = @(Get-CimInstance Win32_Process | Where-Object {
            $_.ParentProcessId -eq $app.Id -and $_.ExecutablePath -eq $agentPath -and
            $_.CommandLine -match 'ChunkPilot\.Agent\.exe'
        } | ForEach-Object { [int]$_.ProcessId })
    }
    if ($app -and -not $app.HasExited) {
        $app.Kill()
        $app.WaitForExit()
    }
    if ($targetAgent -and -not $targetAgent.HasExited) {
        $targetAgent.Kill()
        $targetAgent.WaitForExit()
    }
    foreach ($fallbackTargetAgentId in $fallbackTargetAgentIds) {
        $fallbackTargetAgent = Get-Process -Id $fallbackTargetAgentId -ErrorAction SilentlyContinue
        if ($fallbackTargetAgent -and -not $fallbackTargetAgent.HasExited) {
            $fallbackTargetAgent.Kill()
            $fallbackTargetAgent.WaitForExit()
        }
    }
    if ($unrelatedAgent -and -not $unrelatedAgent.HasExited) {
        try { $null = Invoke-AgentRequest $unrelatedPipeName "ShutdownAgent" } catch { }
        if (-not $unrelatedAgent.WaitForExit($script:AgentShutdownTimeoutMilliseconds)) {
            $unrelatedAgent.Kill()
            $unrelatedAgent.WaitForExit()
        }
    }
    try {
        $targetClean = Remove-TemporaryRoot $targetRoot
        $unrelatedClean = Remove-TemporaryRoot $unrelatedRoot
        $script:Result.TemporaryRootCleanupResult = $targetClean -and $unrelatedClean
        if (-not $script:Result.TemporaryRootCleanupResult) { $script:Failures.Add("One or more script-created temporary roots remain.") }
    }
    catch { $script:Failures.Add($_.Exception.Message) }

    $script:Result["TargetAppProcessId"] = if ($appIdentity) { $appIdentity.ProcessId } else { $null }
    $script:Result["TargetAgentProcessId"] = if ($targetAgentIdentity) { $targetAgentIdentity.ProcessId } else { $null }
    $script:Result["UnrelatedAgentProcessId"] = if ($unrelatedAgent) { $unrelatedAgent.Id } else { $null }
    $script:Result["TemporaryRoots"] = @($targetRoot, $unrelatedRoot)
    $script:Result["Failures"] = @($script:Failures)
    $script:Result.OverallPass = $script:Result.OverallPass -and
        $script:Result.TemporaryRootCleanupResult -and
        $script:Failures.Count -eq 0
    $script:Result | ConvertTo-Json -Compress -Depth 5
}

if (-not $script:Result.OverallPass -or -not $script:Result.TemporaryRootCleanupResult) { exit 1 }
exit 0
