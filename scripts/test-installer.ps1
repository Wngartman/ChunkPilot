[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallerPath
)

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') {
    throw 'Installer lifecycle testing is intentionally restricted to a disposable GitHub-hosted Windows runner.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$installer = [IO.Path]::GetFullPath($InstallerPath)
if (-not (Test-Path -LiteralPath $installer)) { throw "Installer was not found: $installer" }
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\ChunkPilot'
$dataRoot = Join-Path $env:LOCALAPPDATA 'ChunkPilot'
$managedRoot = Join-Path $env:USERPROFILE 'ChunkPilot\Servers'
$startMenuRoot = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\ChunkPilot'
$installerLog = Join-Path $env:RUNNER_TEMP 'chunkpilot-installer.log'
$reinstallLog = Join-Path $env:RUNNER_TEMP 'chunkpilot-reinstall.log'
$uninstallLog = Join-Path $env:RUNNER_TEMP 'chunkpilot-uninstall.log'

if (Test-Path -LiteralPath $installRoot) {
    throw "The disposable runner is not clean; the install directory already exists: $installRoot"
}

function Invoke-Setup([string]$LogPath) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/TASKS=', "/LOG=$LogPath")
    $process = Start-Process -FilePath $installer -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -notin @(0, 3010)) {
        throw "ChunkPilot setup failed with exit code $($process.ExitCode). Log: $LogPath"
    }
}

function Get-ChunkPilotUninstallEntry {
    $root = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
    if (-not (Test-Path $root)) { return $null }
    Get-ChildItem $root | ForEach-Object { Get-ItemProperty $_.PSPath } |
        Where-Object { $_.DisplayName -eq 'ChunkPilot' } | Select-Object -First 1
}

function Get-Shortcut([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Expected Start Menu shortcut is missing: $Path" }
    $shell = New-Object -ComObject WScript.Shell
    $shell.CreateShortcut($Path)
}

function Wait-ForAgent([string]$PipeName, [int]$TimeoutMilliseconds = 15000) {
    $deadline = [DateTimeOffset]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try { $null = Invoke-AgentRequest $PipeName 'Ping' @{} 500; return } catch { Start-Sleep -Milliseconds 100 }
    }
    throw "Timed out waiting for the fixture Agent pipe $PipeName."
}

function Invoke-AgentRequest([string]$PipeName, [string]$Operation, [object]$Payload, [int]$TimeoutMilliseconds = 5000) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly)
    $reader = $null
    $writer = $null
    try {
        $pipe.Connect($TimeoutMilliseconds)
        $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 65536, $true)
        $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($pipe, [Text.UTF8Encoding]::new($false), $false, 65536, $true)
        $request = @{ requestId = [Guid]::NewGuid().ToString('N'); operation = $Operation; payload = $Payload } |
            ConvertTo-Json -Compress -Depth 12
        $writer.WriteLine($request)
        $response = $reader.ReadLine() | ConvertFrom-Json
        if (-not $response.success) { throw "Agent rejected ${Operation}: $($response.error)" }
        return $response.payload
    }
    finally {
        if ($reader) { $reader.Dispose() }
        if ($writer) { $writer.Dispose() }
        $pipe.Dispose()
    }
}

Invoke-Setup $installerLog

$app = Join-Path $installRoot 'ChunkPilot.exe'
$agent = Join-Path $installRoot 'Agent\ChunkPilot.Agent.exe'
foreach ($required in @($app, $agent, (Join-Path $installRoot 'WebUi\index.html'), (Join-Path $installRoot 'THIRD-PARTY-NOTICES.txt'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Installed payload is missing: $required" }
}
if (-not (Get-ChunkPilotUninstallEntry)) { throw 'ChunkPilot uninstall registration was not created.' }

$normalShortcutPath = Join-Path $startMenuRoot 'ChunkPilot.lnk'
$previewShortcutPath = Join-Path $startMenuRoot 'ChunkPilot WebUI Preview.lnk'
$normalShortcut = Get-Shortcut $normalShortcutPath
$previewShortcut = Get-Shortcut $previewShortcutPath
if ([IO.Path]::GetFullPath($normalShortcut.TargetPath) -ne [IO.Path]::GetFullPath($app) -or
    -not [string]::IsNullOrWhiteSpace($normalShortcut.Arguments)) {
    throw 'The normal Start Menu shortcut target or arguments are incorrect.'
}
if ([IO.Path]::GetFullPath($previewShortcut.TargetPath) -ne [IO.Path]::GetFullPath($app) -or
    $previewShortcut.Arguments -ne '--webui-preview') {
    throw 'The WebUI Preview Start Menu shortcut target or arguments are incorrect.'
}

$defaultSmoke = (& (Join-Path $repoRoot 'scripts\test-packaged-ui-close.ps1') -PortableRoot $installRoot) | ConvertFrom-Json
if (-not $defaultSmoke.OverallPass) { throw "Installed default-UI launch/close smoke failed: $($defaultSmoke.Failures -join '; ')" }
$previewSmoke = (& (Join-Path $repoRoot 'scripts\test-packaged-ui-close.ps1') -PortableRoot $installRoot -WebUiPreview) | ConvertFrom-Json
if (-not $previewSmoke.OverallPass) { throw "Installed WebUI-preview launch/close smoke failed: $($previewSmoke.Failures -join '; ')" }

# Same-version reinstall/repair must remain non-destructive and leave both entry points intact.
Invoke-Setup $reinstallLog
if (-not (Test-Path -LiteralPath $normalShortcutPath) -or -not (Test-Path -LiteralPath $previewShortcutPath)) {
    throw 'Same-version reinstall did not preserve both Start Menu shortcuts.'
}

# Create a valid schema-v6 database with one fake registered server, plus world, backup, settings,
# cache, and protected-credential-shaped fixture data. This runs only on the disposable runner.
$serverRoot = Join-Path $managedRoot 'Installer Fixture Server'
$worldRoot = Join-Path $serverRoot 'world'
New-Item -ItemType Directory -Path $worldRoot -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $worldRoot 'level.dat'), 'fixture-world', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $serverRoot 'server.properties'), "level-name=world`nserver-port=25565`n", [Text.UTF8Encoding]::new($false))

$instanceId = 'installer-smoke-' + [Guid]::NewGuid().ToString('N')
$pipeName = "ChunkPilot.Agent.v1.$instanceId"
$agentInfo = [Diagnostics.ProcessStartInfo]::new()
$agentInfo.FileName = $agent
$agentInfo.WorkingDirectory = Split-Path -Parent $agent
$agentInfo.UseShellExecute = $false
$agentInfo.CreateNoWindow = $true
$agentInfo.Environment['CHUNKPILOT_INSTANCE_ID'] = $instanceId
$agentProcess = [Diagnostics.Process]::Start($agentInfo)
try {
    Wait-ForAgent $pipeName
    $serverId = [Guid]::NewGuid()
    $definition = @{
        id = $serverId; name = 'Installer Fixture Server'; rootPath = $serverRoot
        executable = "$env:WINDIR\System32\cmd.exe"; arguments = '/c exit'; workingDirectory = $serverRoot
        environment = @{}; saveCommand = 'save-all flush'; saveFallbackCommand = 'save-all'; stopCommand = 'stop'
        readinessPattern = 'fixture-ready'; saveConfirmationPattern = ''; startupTimeoutSeconds = 30
        shutdownTimeoutSeconds = 30; saveTimeoutSeconds = 10; restartDelaySeconds = 1; port = 25565
        gameKind = 'Minecraft'; gameVersion = ''; ecosystem = 'Custom'; minecraftVersion = 'Fixture'
        loaderVersion = ''; autoStart = $false; crashRestartEnabled = $false; crashRestartLimit = 0
        crashRestartDelaySeconds = 1; importedAt = [DateTimeOffset]::UtcNow.ToString('O'); isManaged = $true
        managedInstanceRoot = $serverRoot; runInBackground = $true; minimumRamMb = 1024; maximumRamMb = 2048
        ramArgumentSource = 'Fixture'; userConfiguredHostname = ''; creationNetworkingPreference = 'DecideLater'
    }
    $null = Invoke-AgentRequest $pipeName 'Import' $definition
    $dashboard = Invoke-AgentRequest $pipeName 'Dashboard' @{}
    if (@($dashboard.servers | Where-Object { $_.definition.name -eq 'Installer Fixture Server' }).Count -ne 1) {
        throw 'The fixture server was not registered in the production database.'
    }
    $null = Invoke-AgentRequest $pipeName 'ShutdownAgent' @{}
    if (-not $agentProcess.WaitForExit(15000)) { throw 'Fixture Agent did not shut down cleanly.' }
}
finally {
    if ($agentProcess -and -not $agentProcess.HasExited) { $agentProcess.Kill($true); $agentProcess.WaitForExit() }
}

New-Item -ItemType Directory -Path (Join-Path $dataRoot 'Backups'), (Join-Path $dataRoot 'Cache') -Force | Out-Null
$fixtures = @(
    (Join-Path $dataRoot 'chunkpilot.db'),
    (Join-Path $worldRoot 'level.dat'),
    (Join-Path $dataRoot 'Backups\installer-fixture.cpb'),
    (Join-Path $dataRoot 'settings-fixture.json'),
    (Join-Path $dataRoot 'secrets.dat'),
    (Join-Path $dataRoot 'Cache\catalog-fixture.json')
)
[IO.File]::WriteAllText($fixtures[2], 'fixture-backup', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($fixtures[3], '{"theme":"fixture"}', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($fixtures[4], 'fixture-dpapi-shaped-data', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($fixtures[5], '{"fixture":true}', [Text.UTF8Encoding]::new($false))
$before = @{}
foreach ($path in $fixtures) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Fixture data is missing before uninstall: $path" }
    $before[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

$uninstaller = Join-Path $installRoot 'unins000.exe'
if (-not (Test-Path -LiteralPath $uninstaller)) { throw 'Installed uninstaller is missing.' }
$uninstallProcess = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$uninstallLog") -WindowStyle Hidden -Wait -PassThru
if ($uninstallProcess.ExitCode -notin @(0, 3010)) { throw "Uninstall failed with exit code $($uninstallProcess.ExitCode)." }

if ((Test-Path -LiteralPath $app) -or (Test-Path -LiteralPath $agent)) { throw 'Uninstall left application binaries behind.' }
if ((Test-Path -LiteralPath $normalShortcutPath) -or (Test-Path -LiteralPath $previewShortcutPath)) { throw 'Uninstall left Start Menu shortcuts behind.' }
if (Get-ChunkPilotUninstallEntry) { throw 'Uninstall registration remains after uninstall.' }
$orphans = @(Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath.StartsWith($installRoot, [StringComparison]::OrdinalIgnoreCase)
})
if ($orphans.Count -ne 0) { throw 'An installed ChunkPilot App or Agent process remains after uninstall.' }
foreach ($path in $fixtures) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Default uninstall removed persistent fixture data: $path" }
    $after = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($after -ne $before[$path]) { throw "Default uninstall changed persistent fixture data: $path" }
}

$webViewKey = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
$webViewVersion = (Get-ItemProperty -Path $webViewKey -Name pv -ErrorAction SilentlyContinue).pv
if (-not $webViewVersion) {
    $webViewKey = 'HKCU:\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    $webViewVersion = (Get-ItemProperty -Path $webViewKey -Name pv -ErrorAction SilentlyContinue).pv
}
if (-not $webViewVersion -or $webViewVersion -eq '0.0.0.0') { throw 'WebView2 Runtime is not installed after setup.' }

[PSCustomObject]@{
    CleanInstall = $true
    DefaultLaunch = $defaultSmoke
    WebUiPreviewLaunch = $previewSmoke
    Reinstall = $true
    Uninstall = $true
    PersistentFixtureCount = $fixtures.Count
    PersistentDataUnchanged = $true
    WebView2Version = $webViewVersion
    Pass = $true
} | ConvertTo-Json -Depth 8
