[CmdletBinding()]
param(
    [string]$PortableRoot
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($PortableRoot)) {
    $PortableRoot = Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\portable-test"
}
$agentPath = Join-Path $PortableRoot "Agent\ChunkPilot.Agent.exe"
if (-not (Test-Path -LiteralPath $agentPath)) {
    throw "Portable agent was not found: $agentPath"
}

$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ("ChunkPilot-package-smoke-" + [Guid]::NewGuid().ToString("N"))
$instanceId = "pkgsmoke-" + [Guid]::NewGuid().ToString("N")
$pipeName = "ChunkPilot.Agent.v1.$instanceId"
New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null

$processInfo = [Diagnostics.ProcessStartInfo]::new()
$processInfo.FileName = $agentPath
$processInfo.WorkingDirectory = Split-Path -Parent $agentPath
$processInfo.UseShellExecute = $false
$processInfo.CreateNoWindow = $true
$processInfo.Environment["CHUNKPILOT_DATA_ROOT"] = $smokeRoot
$processInfo.Environment["CHUNKPILOT_INSTANCE_ID"] = $instanceId
$agent = [Diagnostics.Process]::Start($processInfo)

function Invoke-AgentRequest([string]$Operation) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new(
        ".",
        $pipeName,
        [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous)
    $reader = $null
    $writer = $null
    try {
        $pipe.Connect(10000)
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
        if ([string]::IsNullOrWhiteSpace($line)) {
            throw "Agent returned an empty response."
        }
        $response = $line | ConvertFrom-Json
        if (-not $response.success) {
            throw $response.error
        }
        return $response.payload
    }
    finally {
        if ($reader) { $reader.Dispose() }
        if ($writer) { $writer.Dispose() }
        $pipe.Dispose()
    }
}

try {
    $ping = Invoke-AgentRequest "Ping"
    $selfTest = @(Invoke-AgentRequest "SelfTest")
    $errors = @($selfTest | Where-Object { $_.status -eq "Error" })
    $shutdown = Invoke-AgentRequest "ShutdownAgent"
    if (-not $agent.WaitForExit(10000)) {
        throw "Packaged agent did not shut down."
    }
    if ($agent.ExitCode -ne 0 -or $errors.Count -ne 0) {
        throw "Packaged agent smoke test failed with exit code $($agent.ExitCode) and $($errors.Count) self-test errors."
    }

    [PSCustomObject]@{
        AgentExitCode = $agent.ExitCode
        SelfTestItems = $selfTest.Count
        SelfTestErrors = $errors.Count
        DatabaseCreated = Test-Path -LiteralPath (Join-Path $smokeRoot "chunkpilot.db")
        PingMessage = $ping.message
        ShutdownMessage = $shutdown.message
    }
}
finally {
    if (-not $agent.HasExited) {
        $agent.Kill($true)
        $agent.WaitForExit()
    }
    $resolvedSmoke = [IO.Path]::GetFullPath($smokeRoot)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedSmoke.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedSmoke)) {
        Remove-Item -LiteralPath $resolvedSmoke -Recurse -Force
    }
}
