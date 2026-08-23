[CmdletBinding()]
param(
    [string]$PortableRoot,
    [ValidateRange(3, 60)]
    [int]$SampleSeconds = 5,
    [ValidateRange(1, 10)]
    [int]$Iterations = 3,
    [ValidateRange(1, 30)]
    [int]$WarmupSeconds = 3
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PortableRoot)) { $PortableRoot = Join-Path $repositoryRoot "artifacts\portable-test" }
$appPath = Join-Path ([IO.Path]::GetFullPath($PortableRoot)) "ChunkPilot.exe"
if (-not (Test-Path -LiteralPath $appPath)) { throw "Published ChunkPilot executable was not found: $appPath" }

$measurementRoot = Join-Path ([IO.Path]::GetTempPath()) ("ChunkPilot-webui-performance-" + [Guid]::NewGuid().ToString("N"))
$app = $null
$ownedIds = @()
New-Item -ItemType Directory -Path $measurementRoot -Force | Out-Null

function Get-ProcessTreeSnapshot([int]$RootProcessId) {
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
    @($ids | ForEach-Object {
        $currentId = $_
        $process = Get-Process -Id $currentId -ErrorAction SilentlyContinue
        $record = $inventory | Where-Object { [int]$_.ProcessId -eq $currentId } | Select-Object -First 1
        if ($process) {
            [PSCustomObject]@{
                Id = [int]$process.Id
                ProcessName = $process.ProcessName
                CommandLine = [string]$record.CommandLine
                CpuMilliseconds = [double]$process.TotalProcessorTime.TotalMilliseconds
                WorkingSetBytes = [long]$process.WorkingSet64
                PrivateBytes = [long]$process.PrivateMemorySize64
                IoOperations = [long]$record.ReadOperationCount + [long]$record.WriteOperationCount +
                    [long]$record.OtherOperationCount
            }
        }
    })
}

function Get-GroupCpuPercent($Before, $After, [scriptblock]$Predicate, [int]$Seconds) {
    $milliseconds = 0.0
    foreach ($later in @($After | Where-Object $Predicate)) {
        $earlier = $Before | Where-Object Id -eq $later.Id | Select-Object -First 1
        if ($earlier) { $milliseconds += [math]::Max(0, $later.CpuMilliseconds - $earlier.CpuMilliseconds) }
    }
    (($milliseconds / ($Seconds * 1000 * [Environment]::ProcessorCount)) * 100)
}

function Get-GroupSum($Items, [scriptblock]$Predicate, [string]$Property) {
    $sum = @($Items | Where-Object $Predicate | Measure-Object -Property $Property -Sum).Sum
    if ($null -eq $sum) { return 0 }
    $sum
}

$results = [Collections.Generic.List[object]]::new()
try {
    for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
        $instanceId = "webui-performance-" + [Guid]::NewGuid().ToString("N")
        $info = [Diagnostics.ProcessStartInfo]::new()
        $info.FileName = $appPath
        $info.WorkingDirectory = Split-Path -Parent $appPath
        $info.UseShellExecute = $false
        $info.Environment["CHUNKPILOT_DATA_ROOT"] = $measurementRoot
        $info.Environment["CHUNKPILOT_MANAGED_SERVERS_ROOT"] = Join-Path $measurementRoot "ManagedServers"
        $info.Environment["CHUNKPILOT_INSTANCE_ID"] = $instanceId
        $started = [Diagnostics.Stopwatch]::StartNew()
        $app = [Diagnostics.Process]::Start($info)
        if ($null -eq $app) { throw "Windows did not start packaged ChunkPilot." }
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
        do {
            if ($app.HasExited) { throw "Packaged WebUI exited before creating its main window." }
            $app.Refresh()
            if ($app.MainWindowHandle -ne [IntPtr]::Zero) { break }
            Start-Sleep -Milliseconds 50
        } while ([DateTimeOffset]::UtcNow -lt $deadline)
        if ($app.MainWindowHandle -eq [IntPtr]::Zero) { throw "Timed out waiting for the packaged WebUI main window." }
        $started.Stop()

        Start-Sleep -Seconds $WarmupSeconds
        $before = @(Get-ProcessTreeSnapshot $app.Id)
        Start-Sleep -Seconds $SampleSeconds
        $after = @(Get-ProcessTreeSnapshot $app.Id)
        $ownedIds = @($after.Id)
        $hostGroup = { $_.Id -eq $app.Id }
        $agentGroup = { $_.ProcessName -eq "ChunkPilot.Agent" }
        $webViewGroup = { $_.ProcessName -eq "msedgewebview2" }
        $rendererGroup = { $_.ProcessName -eq "msedgewebview2" -and $_.CommandLine -match "--type=renderer(?:\s|$)" }
        $ioBefore = Get-GroupSum $before { $true } "IoOperations"
        $ioAfter = Get-GroupSum $after { $true } "IoOperations"

        $closing = [Diagnostics.Stopwatch]::StartNew()
        if (-not $app.CloseMainWindow()) { throw "Packaged WebUI did not accept a normal window close." }
        if (-not $app.WaitForExit(5000)) { throw "Packaged WebUI did not close within five seconds after measurement." }
        do {
            $remaining = @($ownedIds | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
            if ($remaining.Count -eq 0) { break }
            Start-Sleep -Milliseconds 25
        } while ($closing.Elapsed -lt [TimeSpan]::FromSeconds(5))
        $closing.Stop()

        $results.Add([PSCustomObject]@{
            Iteration = $iteration
            StartupKind = if ($iteration -eq 1) { "Cold isolated profile" } else { "Warm reused profile" }
            MainWindowMilliseconds = [math]::Round($started.Elapsed.TotalMilliseconds, 1)
            SampleSeconds = $SampleSeconds
            HostCpuPercent = [math]::Round((Get-GroupCpuPercent $before $after $hostGroup $SampleSeconds), 4)
            AgentCpuPercent = [math]::Round((Get-GroupCpuPercent $before $after $agentGroup $SampleSeconds), 4)
            WebViewCpuPercent = [math]::Round((Get-GroupCpuPercent $before $after $webViewGroup $SampleSeconds), 4)
            CombinedCpuPercent = [math]::Round((Get-GroupCpuPercent $before $after { $true } $SampleSeconds), 4)
            HostWorkingSetMiB = [math]::Round((Get-GroupSum $after $hostGroup "WorkingSetBytes") / 1MB, 1)
            AgentWorkingSetMiB = [math]::Round((Get-GroupSum $after $agentGroup "WorkingSetBytes") / 1MB, 1)
            WebViewWorkingSetMiB = [math]::Round((Get-GroupSum $after $webViewGroup "WorkingSetBytes") / 1MB, 1)
            CombinedWorkingSetMiB = [math]::Round((Get-GroupSum $after { $true } "WorkingSetBytes") / 1MB, 1)
            CombinedPrivateMiB = [math]::Round((Get-GroupSum $after { $true } "PrivateBytes") / 1MB, 1)
            ProcessCount = $after.Count
            WebViewProcessCount = @($after | Where-Object $webViewGroup).Count
            WebViewRendererCount = @($after | Where-Object $rendererGroup).Count
            IdleIoOperations = [math]::Max(0, $ioAfter - $ioBefore)
            CloseMilliseconds = [math]::Round($closing.Elapsed.TotalMilliseconds, 1)
            OwnedProcessesAfterClose = $remaining.Count
        })
        if ($remaining.Count -ne 0) {
            throw "Packaged WebUI left $($remaining.Count) measured owned process(es) after normal close."
        }
        $app.Dispose()
        $app = $null
        $ownedIds = @()
    }

    $results
}
finally {
    if ($app -and -not $app.HasExited) {
        $null = $app.CloseMainWindow()
        $null = $app.WaitForExit(5000)
    }
    foreach ($processId in $ownedIds) {
        $owned = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($owned) { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue }
    }
    for ($attempt = 0; $attempt -lt 50 -and (Test-Path -LiteralPath $measurementRoot); $attempt++) {
        try { Remove-Item -LiteralPath $measurementRoot -Recurse -Force -ErrorAction Stop }
        catch [IO.IOException] { Start-Sleep -Milliseconds 100 }
        catch [UnauthorizedAccessException] { Start-Sleep -Milliseconds 100 }
    }
    if (Test-Path -LiteralPath $measurementRoot) { throw "The isolated measurement root remained locked: $measurementRoot" }
}
