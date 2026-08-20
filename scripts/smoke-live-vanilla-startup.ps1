<#
.SYNOPSIS
    Starts one isolated Vanilla server once, waits for real readiness, stops it cleanly, and reports.

.DESCRIPTION
    Uses the exact server artifact and the exact managed Java runtime a live creation produced, inside
    the temporary root that creation used. Binds to loopback on a high isolated port, keeps online mode
    on, adds no firewall rule, enables no RCON and no query, connects no client, and stops with the
    server's own `stop` command.

    Readiness is taken from the server's own log line, never from the process merely existing.

.PARAMETER ServerDirectory
    The created server's folder. Must sit inside a temporary validation root.

.PARAMETER JavaPath
    The managed java.exe assigned to that server.

.PARAMETER Port
    A high local port for this test only.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ServerDirectory,
    [Parameter(Mandatory = $true)] [string] $JavaPath,
    [int] $Port = 47654
)

$ErrorActionPreference = 'Stop'

if ($ServerDirectory -notlike "$env:TEMP*") {
    throw "Refusing to start a server outside a temporary validation root: $ServerDirectory"
}
if (-not (Test-Path (Join-Path $ServerDirectory 'server.jar'))) { throw 'No server.jar in that folder.' }
if (-not (Test-Path $JavaPath)) { throw "No java at $JavaPath" }

# Test-only properties: loopback only, a high isolated port, tiny distances so generation is quick.
# online-mode stays true, and RCON and query stay off, exactly as they are by default.
$propertiesPath = Join-Path $ServerDirectory 'server.properties'
$properties = Get-Content $propertiesPath
$set = @{
    'server-ip'            = '127.0.0.1'
    'server-port'          = "$Port"
    'view-distance'        = '4'
    'simulation-distance'  = '4'
    'max-players'          = '1'
    'online-mode'          = 'true'
    'enable-rcon'          = 'false'
    'enable-query'         = 'false'
    'enable-status'        = 'false'
}
foreach ($key in $set.Keys) {
    if ($properties -match "^$key=") {
        $properties = $properties -replace "^$key=.*", "$key=$($set[$key])"
    }
    else {
        $properties += "$key=$($set[$key])"
    }
}
Set-Content -Path $propertiesPath -Value $properties -Encoding ascii

$logPath = Join-Path $ServerDirectory 'startup-smoke.log'
$info = New-Object System.Diagnostics.ProcessStartInfo
$info.FileName = $JavaPath
$info.Arguments = '-Xms512M -Xmx1536M -jar "server.jar" nogui'
$info.WorkingDirectory = $ServerDirectory
$info.UseShellExecute = $false
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$info.RedirectStandardInput = $true

$server = New-Object System.Diagnostics.Process
$server.StartInfo = $info
$lines = New-Object System.Collections.Concurrent.ConcurrentQueue[string]
$handler = { if ($EventArgs.Data) { $Event.MessageData.Enqueue($EventArgs.Data) } }
Register-ObjectEvent -InputObject $server -EventName OutputDataReceived -Action $handler -MessageData $lines | Out-Null
Register-ObjectEvent -InputObject $server -EventName ErrorDataReceived -Action $handler -MessageData $lines | Out-Null

Write-Host "Starting: $JavaPath $($info.Arguments)"
$started = Get-Date
[void]$server.Start()
$server.BeginOutputReadLine()
$server.BeginErrorReadLine()

$captured = New-Object System.Collections.Generic.List[string]
$ready = $false
$deadline = (Get-Date).AddMinutes(8)
while ((Get-Date) -lt $deadline -and -not $ready -and -not $server.HasExited) {
    Start-Sleep -Milliseconds 500
    $line = $null
    while ($lines.TryDequeue([ref]$line)) {
        $captured.Add($line)
        if ($captured.Count -le 4000) { }
        if ($line -match 'Done \(.+?\)!') { $ready = $true }
    }
}

$readySeconds = ((Get-Date) - $started).TotalSeconds
if (-not $ready) {
    if (-not $server.HasExited) { $server.Kill($true) }
    Set-Content -Path $logPath -Value ($captured -join [Environment]::NewLine) -Encoding utf8
    throw "The server never reported readiness. Log: $logPath"
}
Write-Host ("Ready after {0:N1}s" -f $readySeconds)

# Readiness is not enough on its own: a server that reports ready and then dies is not a working
# server, so it is left running briefly and checked again before being asked to stop.
Start-Sleep -Seconds 20
if ($server.HasExited) { throw 'The server exited immediately after reporting readiness.' }
Write-Host 'Still alive 20s after readiness.'

# Windows PowerShell gives the child's stdin a UTF-8 writer that emits a byte-order mark on its
# first write, and the server reads those bytes as part of the command and rejects it — a failure
# that looks exactly like a server refusing to stop. The mark is flushed on a line of its own first,
# so the command that follows is clean.
$stdin = $server.StandardInput
if ($stdin.Encoding.GetPreamble().Length -gt 0) {
    $stdin.WriteLine()
    $stdin.Flush()
    Start-Sleep -Milliseconds 500
}
$stdin.WriteLine('stop')
$stdin.Flush()
$exited = $server.WaitForExit(120000)
$line = $null
while ($lines.TryDequeue([ref]$line)) { $captured.Add($line) }
Set-Content -Path $logPath -Value ($captured -join [Environment]::NewLine) -Encoding utf8

if (-not $exited) {
    $server.Kill($true)
    throw "The server did not stop within two minutes. Log: $logPath"
}

Write-Host ("Exit code: " + $server.ExitCode)
$stray = Get-Process java -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path -like "$env:TEMP*" }
Write-Host ("Stray isolated java processes: " + (($stray | Measure-Object).Count))
Write-Host ("Ready line: " + ($captured | Where-Object { $_ -match 'Done \(' } | Select-Object -First 1))
Write-Host ("Stop lines: " + (($captured | Where-Object { $_ -match 'Stopping|Saving' } | Select-Object -First 4) -join ' | '))
Write-Host ("World folders: " + ((Get-ChildItem $ServerDirectory -Directory | Select-Object -ExpandProperty Name) -join ', '))
Write-Host ("Log: " + $logPath)
