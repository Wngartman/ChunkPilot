[CmdletBinding()]
param(
    [string]$PortableRoot,
    [ValidatePattern('\A(?:|https://[a-z0-9-]+(?:\.[a-z0-9-]+)+(?::443)?/?)\z')]
    [string]$CurseForgeServiceEndpoint = ''
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

if (-not ('ChunkPilotPackagedSmokeIdentity' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ChunkPilotPackagedSmokeIdentity {
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    public static long CurrentCreationTicks() {
        long creation, exit, kernel, user;
        if (!GetProcessTimes(GetCurrentProcess(), out creation, out exit, out kernel, out user) || creation <= 0)
            throw new InvalidOperationException("Could not prove the smoke caller's raw process creation identity.");
        return creation;
    }
}
'@
}

$processInfo = [Diagnostics.ProcessStartInfo]::new()
$processInfo.FileName = $agentPath
$processInfo.WorkingDirectory = Split-Path -Parent $agentPath
$processInfo.UseShellExecute = $false
$processInfo.CreateNoWindow = $true
$processInfo.Environment["CHUNKPILOT_DATA_ROOT"] = $smokeRoot
$processInfo.Environment["CHUNKPILOT_MANAGED_SERVERS_ROOT"] = Join-Path $smokeRoot 'servers'
$processInfo.Environment["CHUNKPILOT_INSTANCE_ID"] = $instanceId
[void]$processInfo.Environment.Remove('CHUNKPILOT_CURSEFORGE_KEY_FILE')
[void]$processInfo.Environment.Remove('CHUNKPILOT_REACHABILITY_PROBE_URL')
foreach ($variable in @('APPDATA', 'LOCALAPPDATA', 'TEMP', 'TMP')) {
    $directory = Join-Path $smokeRoot $variable.ToLowerInvariant()
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $processInfo.Environment[$variable] = $directory
}
$processInfo.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $smokeRoot 'bundle'
$agent = [Diagnostics.Process]::Start($processInfo)
if ($null -eq $agent) { throw 'Windows did not start the packaged Agent.' }

function Invoke-AgentRequest([string]$Operation, [hashtable]$Payload = @{}) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new(
        ".",
        $pipeName,
        [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly)
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
            payload = $Payload
        } | ConvertTo-Json -Compress -Depth 5
        $writer.WriteLine($request)
        $read = $reader.ReadLineAsync()
        if (-not $read.Wait(10000)) { throw 'Packaged Agent response exceeded the bounded timeout.' }
        $line = $read.GetAwaiter().GetResult()
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
    $registration = Invoke-AgentRequest 'RegisterUiSession' @{
        processId = $PID
        processCreationTicks = [ChunkPilotPackagedSmokeIdentity]::CurrentCreationTicks()
    }
    if (-not $registration.session.sessionId -or -not $registration.sessionCapability) {
        throw 'Packaged Agent did not authenticate the exact smoke caller.'
    }
    $access = Invoke-AgentRequest 'GetCurseForgeAccess' @{
        sessionId = $registration.session.sessionId
        capability = $registration.sessionCapability
    }
    $expectedAccessMode = if ($CurseForgeServiceEndpoint) { 'ApplicationService' } else { 'Unavailable' }
    if ($access.mode -cne $expectedAccessMode -or $access.hasPersonalCredential -isnot [bool] -or
        $access.hasPersonalCredential -or $access.canAccess -isnot [bool] -or
        $access.canAccess -ne [bool]$CurseForgeServiceEndpoint -or
        (Test-Path -LiteralPath (Join-Path $smokeRoot 'secrets.dat'))) {
        throw 'The packaged Agent access mode does not match its expected credential-free build configuration.'
    }
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
        CurseForgeAccessMode = $access.mode
        HasPersonalCredential = $access.hasPersonalCredential
        AccessConfigurationVerified = $true
    }
}
finally {
    if (-not $agent.HasExited) {
        # This empty-data smoke never starts server children; terminate only the held Agent process.
        $agent.Kill()
        if (-not $agent.WaitForExit(10000)) { throw 'The exact packaged smoke Agent did not exit within its cleanup deadline.' }
    }
    $agent.Dispose()
    $resolvedSmoke = [IO.Path]::GetFullPath($smokeRoot)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedSmoke.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedSmoke)) {
        Remove-Item -LiteralPath $resolvedSmoke -Recurse -Force
    }
}
