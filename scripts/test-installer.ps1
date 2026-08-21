[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallerPath,
    [string]$PreviousInstallerPath = ''
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
$previousInstallLog = Join-Path $env:RUNNER_TEMP 'chunkpilot-previous-install.log'
$upgradeLog = Join-Path $env:RUNNER_TEMP 'chunkpilot-upgrade.log'
$upgradeUninstallLog = Join-Path $env:RUNNER_TEMP 'chunkpilot-upgrade-uninstall.log'

if (Test-Path -LiteralPath $installRoot) {
    throw "The disposable runner is not clean; the install directory already exists: $installRoot"
}

function Invoke-Setup([string]$SetupPath, [string]$LogPath) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/TASKS=', "/LOG=$LogPath")
    $process = Start-Process -FilePath $SetupPath -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -notin @(0, 3010)) {
        throw "ChunkPilot setup failed with exit code $($process.ExitCode). Log: $LogPath"
    }
}

function Get-ChunkPilotUninstallEntry {
    # Inno derives the key from AppId and defaults DisplayName to AppVerName
    # ("ChunkPilot 1.3.0"), not AppName. Check the stable identity directly.
    $path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{C609C59D-FD5A-4A18-91C8-2D04F7177A69}_is1'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    Get-ItemProperty -LiteralPath $path
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

Invoke-Setup $installer $installerLog

$app = Join-Path $installRoot 'ChunkPilot.exe'
$agent = Join-Path $installRoot 'Agent\ChunkPilot.Agent.exe'
foreach ($required in @($app, $agent, (Join-Path $installRoot 'WebUi\index.html'), (Join-Path $installRoot 'THIRD-PARTY-NOTICES.txt'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Installed payload is missing: $required" }
}
$uninstallEntry = Get-ChunkPilotUninstallEntry
if (-not $uninstallEntry) { throw 'ChunkPilot uninstall registration was not created.' }
$displayNameMatches = $uninstallEntry.DisplayName -eq 'ChunkPilot 1.3.0'
$displayVersionMatches = $uninstallEntry.DisplayVersion -eq '1.3.0'
$registeredInstallRoot = [IO.Path]::GetFullPath([string]$uninstallEntry.InstallLocation).TrimEnd(
    [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$expectedInstallRoot = [IO.Path]::GetFullPath($installRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$installLocationMatches = $registeredInstallRoot -eq $expectedInstallRoot
$hasUninstallString = -not [string]::IsNullOrWhiteSpace($uninstallEntry.UninstallString)
if (-not ($displayNameMatches -and $displayVersionMatches -and $installLocationMatches -and $hasUninstallString)) {
    throw "ChunkPilot uninstall registration metadata is incorrect. " +
        "DisplayNameMatches=$displayNameMatches; DisplayVersionMatches=$displayVersionMatches; " +
        "InstallLocationMatches=$installLocationMatches; HasUninstallString=$hasUninstallString."
}

$normalShortcutPath = Join-Path $startMenuRoot 'ChunkPilot.lnk'
$normalShortcut = Get-Shortcut $normalShortcutPath
if ([IO.Path]::GetFullPath($normalShortcut.TargetPath) -ne [IO.Path]::GetFullPath($app) -or
    -not [string]::IsNullOrWhiteSpace($normalShortcut.Arguments)) {
    throw 'The normal Start Menu shortcut target or arguments are incorrect.'
}
$defaultSmoke = (& (Join-Path $repoRoot 'scripts\test-packaged-ui-close.ps1') -PortableRoot $installRoot) | ConvertFrom-Json
if (-not $defaultSmoke.OverallPass) { throw "Installed WebUI launch/close smoke failed: $($defaultSmoke.Failures -join '; ')" }

# Same-version reinstall/repair must remain non-destructive and leave the current entry point intact.
Invoke-Setup $installer $reinstallLog
if (-not (Test-Path -LiteralPath $normalShortcutPath)) {
    throw 'Same-version reinstall did not preserve the Start Menu shortcut.'
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
if ($uninstallProcess.ExitCode -notin @(0, 3010)) {
    if (Test-Path -LiteralPath $uninstallLog) {
        Write-Host '--- Inno uninstall log (last 250 lines) ---'
        Get-Content -LiteralPath $uninstallLog -Tail 250 | ForEach-Object { Write-Host $_ }
        Write-Host '--- End Inno uninstall log ---'
    }
    throw "Uninstall failed with exit code $($uninstallProcess.ExitCode)."
}

if ((Test-Path -LiteralPath $app) -or (Test-Path -LiteralPath $agent)) { throw 'Uninstall left application binaries behind.' }
if (Test-Path -LiteralPath $normalShortcutPath) { throw 'Uninstall left the Start Menu shortcut behind.' }
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

$previousUpgrade = $false
if (-not [string]::IsNullOrWhiteSpace($PreviousInstallerPath)) {
    $previousInstaller = [IO.Path]::GetFullPath($PreviousInstallerPath)
    if (-not (Test-Path -LiteralPath $previousInstaller -PathType Leaf)) {
        throw "Previous installer was not found: $previousInstaller"
    }
    $upgradeBefore = @{}
    foreach ($path in $fixtures) { $upgradeBefore[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    Invoke-Setup $previousInstaller $previousInstallLog
    if (-not (Test-Path -LiteralPath $app)) { throw 'Previous prerelease did not install before upgrade.' }
    Invoke-Setup $installer $upgradeLog
    $upgradedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($app).ProductVersion
    if (-not $upgradedVersion.StartsWith('1.3.0-alpha.4+', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Prior-release upgrade did not install Alpha 4 binaries: $upgradedVersion"
    }
    if (Test-Path -LiteralPath (Join-Path $startMenuRoot 'ChunkPilot WebUI Preview.lnk')) {
        throw 'Prior-release upgrade left the obsolete WebUI Preview shortcut behind.'
    }
    foreach ($path in $fixtures) {
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $upgradeBefore[$path]) {
            throw "Prior-release upgrade changed persistent fixture data: $path"
        }
    }
    $upgradeUninstaller = Join-Path $installRoot 'unins000.exe'
    $upgradeUninstall = Start-Process -FilePath $upgradeUninstaller -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$upgradeUninstallLog") `
        -WindowStyle Hidden -Wait -PassThru
    if ($upgradeUninstall.ExitCode -notin @(0, 3010)) { throw 'Uninstall after prior-release upgrade failed.' }
    foreach ($path in $fixtures) {
        if (-not (Test-Path -LiteralPath $path) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $upgradeBefore[$path]) {
            throw "Uninstall after prior-release upgrade changed persistent fixture data: $path"
        }
    }
    $previousUpgrade = $true
}

[PSCustomObject]@{
    CleanInstall = $true
    DefaultLaunch = $defaultSmoke
    Reinstall = $true
    Uninstall = $true
    PreviousReleaseUpgrade = $previousUpgrade
    PersistentFixtureCount = $fixtures.Count
    PersistentDataUnchanged = $true
    WebView2Version = $webViewVersion
    Pass = $true
} | ConvertTo-Json -Depth 8
