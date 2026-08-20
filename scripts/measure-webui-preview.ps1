[CmdletBinding()]
param(
    [string]$PortableRoot,
    [ValidateRange(3, 60)]
    [int]$SampleSeconds = 10
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PortableRoot)) { $PortableRoot = Join-Path $repositoryRoot "artifacts\portable-test" }
$appPath = Join-Path ([IO.Path]::GetFullPath($PortableRoot)) "ChunkPilot.exe"
if (-not (Test-Path -LiteralPath $appPath)) { throw "Published ChunkPilot executable was not found: $appPath" }

$measurementRoot = Join-Path ([IO.Path]::GetTempPath()) ("ChunkPilot-webui-performance-" + [Guid]::NewGuid().ToString("N"))
$instanceId = "webui-performance-" + [Guid]::NewGuid().ToString("N")
$app = $null
New-Item -ItemType Directory -Path $measurementRoot -Force | Out-Null

function Get-ProcessTree([int]$RootProcessId) {
    $inventory = @(Get-CimInstance Win32_Process)
    $ids = [Collections.Generic.HashSet[int]]::new()
    $pending = [Collections.Generic.Queue[int]]::new()
    $pending.Enqueue($RootProcessId)
    while ($pending.Count -gt 0) {
        $parent = $pending.Dequeue()
        if (-not $ids.Add($parent)) { continue }
        foreach ($child in @($inventory | Where-Object { [int]$_.ParentProcessId -eq $parent })) {
            $pending.Enqueue([int]$child.ProcessId)
        }
    }
    @($ids | ForEach-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
}

try {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $appPath
    $info.Arguments = "--webui-preview"
    $info.WorkingDirectory = Split-Path -Parent $appPath
    $info.UseShellExecute = $false
    $info.Environment["CHUNKPILOT_DATA_ROOT"] = $measurementRoot
    $info.Environment["CHUNKPILOT_INSTANCE_ID"] = $instanceId
    $started = [Diagnostics.Stopwatch]::StartNew()
    $app = [Diagnostics.Process]::Start($info)
    if ($null -eq $app) { throw "Windows did not start the packaged WebUI preview." }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        if ($app.HasExited) { throw "Packaged WebUI exited before creating its main window." }
        $app.Refresh()
        if ($app.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 50
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if ($app.MainWindowHandle -eq [IntPtr]::Zero) { throw "Timed out waiting for the packaged WebUI main window." }
    $started.Stop()

    # Let first render, Agent handshake, and WebView child creation settle before the idle sample.
    Start-Sleep -Seconds 3
    $before = @(Get-ProcessTree $app.Id)
    $beforeCpu = ($before | ForEach-Object { $_.TotalProcessorTime.TotalMilliseconds } | Measure-Object -Sum).Sum
    Start-Sleep -Seconds $SampleSeconds
    $after = @(Get-ProcessTree $app.Id)
    $afterCpu = ($after | ForEach-Object { $_.TotalProcessorTime.TotalMilliseconds } | Measure-Object -Sum).Sum
    $logicalProcessors = [Environment]::ProcessorCount
    $cpuPercent = (($afterCpu - $beforeCpu) / ($SampleSeconds * 1000 * $logicalProcessors)) * 100
    $hostProcess = $after | Where-Object Id -eq $app.Id
    $agent = $after | Where-Object ProcessName -eq "ChunkPilot.Agent"
    $webView = $after | Where-Object ProcessName -eq "msedgewebview2"

    [PSCustomObject]@{
        MainWindowMilliseconds = [math]::Round($started.Elapsed.TotalMilliseconds, 1)
        SampleSeconds = $SampleSeconds
        CombinedCpuPercent = [math]::Round($cpuPercent, 4)
        HostWorkingSetMiB = [math]::Round((($hostProcess | Measure-Object WorkingSet64 -Sum).Sum / 1MB), 1)
        AgentWorkingSetMiB = [math]::Round((($agent | Measure-Object WorkingSet64 -Sum).Sum / 1MB), 1)
        WebViewWorkingSetMiB = [math]::Round((($webView | Measure-Object WorkingSet64 -Sum).Sum / 1MB), 1)
        CombinedWorkingSetMiB = [math]::Round((($after | Measure-Object WorkingSet64 -Sum).Sum / 1MB), 1)
        CombinedPrivateMiB = [math]::Round((($after | Measure-Object PrivateMemorySize64 -Sum).Sum / 1MB), 1)
        ProcessCount = $after.Count
    }
}
finally {
    if ($app -and -not $app.HasExited) {
        $null = $app.CloseMainWindow()
        if (-not $app.WaitForExit(5000)) { throw "Packaged WebUI did not close within five seconds after measurement." }
    }
    for ($attempt = 0; $attempt -lt 50 -and (Test-Path -LiteralPath $measurementRoot); $attempt++) {
        try { Remove-Item -LiteralPath $measurementRoot -Recurse -Force -ErrorAction Stop }
        catch [IO.IOException] { Start-Sleep -Milliseconds 100 }
        catch [UnauthorizedAccessException] { Start-Sleep -Milliseconds 100 }
    }
    if (Test-Path -LiteralPath $measurementRoot) { throw "The isolated measurement root remained locked: $measurementRoot" }
}
