<#
.SYNOPSIS
    Walks the live Vanilla wizard and the server workspace against an isolated ChunkPilot instance,
    capturing one PNG per state the stabilization milestone changed.

.DESCRIPTION
    Everything this script touches is created by this script. CHUNKPILOT_INSTANCE_ID scopes the
    single-instance mutexes and the Agent's named pipe, CHUNKPILOT_DATA_ROOT scopes the database,
    logs and managed Java, and CHUNKPILOT_MANAGED_SERVERS_ROOT scopes the server folder. A ChunkPilot
    session already running for this Windows user is therefore untouched, as is the real data root,
    the real managed-servers folder, the registry and the firewall.

    The run contacts Mojang and Adoptium, because creating a genuine Vanilla server is the point.

.PARAMETER AppDirectory
    A published Release build of the App with the Agent beside it in an Agent subfolder.

.PARAMETER OutputDirectory
    Where the PNGs and the log go. Defaults to the ignored artifact directory for this milestone.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $AppDirectory,
    [string] $OutputDirectory = '',
    [string] $Root = (Join-Path $env:TEMP ("ChunkPilot-stabilization-" + [guid]::NewGuid().ToString('N'))),
    [string] $ServerName = 'Review workspace'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $scriptRoot '..\artifacts\vanilla-workspace-stabilization'
}

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win32Ui
{
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    public const uint WM_CLOSE = 0x0010;
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
    public const uint ES_CONTINUOUS = 0x80000000;
    public const uint ES_DISPLAY_REQUIRED = 0x00000002;
    public const uint ES_SYSTEM_REQUIRED = 0x00000001;
}
'@

$log = New-Object System.Collections.Generic.List[string]
function Write-Step([string] $message) {
    $line = "{0:HH:mm:ss} {1}" -f (Get-Date), $message
    $log.Add($line); Write-Host $line
}

$dataRoot = Join-Path $Root 'data'
$serversRoot = Join-Path $Root 'servers'
New-Item -ItemType Directory -Force -Path $dataRoot, $serversRoot, $OutputDirectory | Out-Null

$exe = Join-Path $AppDirectory 'ChunkPilot.exe'
if (-not (Test-Path $exe)) { throw "Published App not found: $exe" }

# Ask Windows to keep the display on for this walk only. Reverted in the finally, and it changes no
# setting: it is a request this process holds while it runs.
[void][Win32Ui]::SetThreadExecutionState(
    [Win32Ui]::ES_CONTINUOUS -bor [Win32Ui]::ES_DISPLAY_REQUIRED -bor [Win32Ui]::ES_SYSTEM_REQUIRED)

$instanceId = [guid]::NewGuid().ToString('N')
Write-Step "Isolated root: $Root"
Write-Step "Instance id:   $instanceId"
Write-Step "App:           $exe"

$info = New-Object System.Diagnostics.ProcessStartInfo
$info.FileName = $exe
$info.Arguments = '--create-server-v2-live-vanilla'
$info.UseShellExecute = $false
$info.WorkingDirectory = $AppDirectory
$info.Environment['CHUNKPILOT_DATA_ROOT'] = $dataRoot
$info.Environment['CHUNKPILOT_MANAGED_SERVERS_ROOT'] = $serversRoot
$info.Environment['CHUNKPILOT_INSTANCE_ID'] = $instanceId
$process = [System.Diagnostics.Process]::Start($info)
Write-Step "Started ChunkPilot pid $($process.Id)"

$desktop = [System.Windows.Automation.AutomationElement]::RootElement

function Find-Window([string] $namePart, [int] $timeoutSeconds = 60) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
        foreach ($scope in @([System.Windows.Automation.TreeScope]::Children,
                             [System.Windows.Automation.TreeScope]::Descendants)) {
            foreach ($window in $desktop.FindAll($scope, $condition)) {
                if ($window.Current.Name -like "*$namePart*" -and
                    $window.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) {
                    return $window
                }
            }
        }
        Start-Sleep -Milliseconds 400
    }
    throw "Window matching '$namePart' never appeared."
}

function Find-Descendant($parent, [string] $name, [int] $timeoutSeconds = 20, $controlType = $null) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)
        foreach ($found in $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
            if ($found.Current.IsOffscreen) { continue }
            if ($null -ne $controlType -and $found.Current.ControlType -ne $controlType) { continue }
            return $found
        }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

# Accessible names are sentences, and a sentence is easier to change than a control. Substring
# matching keeps the walk working when copy is edited, which this milestone did a great deal of.
function Find-DescendantLike($parent, [string] $namePart, [int] $timeoutSeconds = 20, $controlType = $null) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($found in $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                 [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($found.Current.IsOffscreen) { continue }
            if ($null -ne $controlType -and $found.Current.ControlType -ne $controlType) { continue }
            if ($found.Current.Name -like "*$namePart*") { return $found }
        }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Invoke-Element($element) {
    if ($null -eq $element) { throw 'Cannot invoke a control that was not found.' }
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 600
}

function Select-Element($element) {
    if ($null -eq $element) { throw 'Cannot select a control that was not found.' }
    try {
        $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    }
    catch {
        # Not a selectable item itself: fall back to focus, which is what a click would do anyway.
        $element.SetFocus()
    }
    Start-Sleep -Milliseconds 400
}

# A row's accessible name is the data item's own text, so the file name is a substring of it. The
# control type matters: the label inside the row carries the same text and cannot be selected.
function Select-Row($scope, [string] $textPart, [int] $timeoutSeconds = 15) {
    $row = Find-DescendantLike $scope $textPart $timeoutSeconds ([System.Windows.Automation.ControlType]::ListItem)
    if ($null -eq $row) { return $false }
    Select-Element $row
    return $true
}

function Set-Text($element, [string] $value) {
    if ($null -eq $element) { throw 'Cannot type into a control that was not found.' }
    $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
    Start-Sleep -Milliseconds 400
}

function Get-Text($element) {
    return $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}

function Set-Toggle($element, [bool] $on) {
    if ($null -eq $element) { throw 'Cannot toggle a control that was not found.' }
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $target = if ($on) { 'On' } else { 'Off' }
    $guard = 0
    while ($pattern.Current.ToggleState -ne $target -and $guard -lt 3) {
        $pattern.Toggle(); Start-Sleep -Milliseconds 400; $guard++
    }
    Start-Sleep -Milliseconds 400
}

function Save-Shot($window, [string] $name) {
    $handle = [IntPtr]$window.Current.NativeWindowHandle
    [void][Win32Ui]::SetForegroundWindow($handle)
    # A long unattended walk lets the display sleep, and a sleeping display gives PrintWindow an
    # unpainted surface - every capture comes back blank while the application is working perfectly.
    # One pixel of real mouse input keeps the session awake, and the composited surface with it.
    Nudge-Pointer
    Start-Sleep -Milliseconds 700
    $rect = New-Object Win32Ui+RECT
    [void][Win32Ui]::GetWindowRect($handle, [ref]$rect)
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) { throw "Window has no size for capture '$name'." }
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    $printed = [Win32Ui]::PrintWindow($handle, $hdc, 2)
    $graphics.ReleaseHdc($hdc)
    if (-not $printed) {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($width, $height)))
    }
    $bitmap.Save((Join-Path $OutputDirectory ("{0}.png" -f $name)), [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
    Write-Step "Captured $name ($width x $height)"
}

function Resize-Window($window, [int] $width, [int] $height) {
    $handle = [IntPtr]$window.Current.NativeWindowHandle
    [void][Win32Ui]::ShowWindow($handle, 1)
    [void][Win32Ui]::MoveWindow($handle, 40, 30, $width, $height, $true)
    Start-Sleep -Milliseconds 900
}

# The server sub-navigation and the global rail both contain a "Settings" row, so the destination has
# to be found inside the right list. The server list is the one that also offers Protection.
function Get-ServerNav($shell) {
    foreach ($list in $shell.FindAll([System.Windows.Automation.TreeScope]::Descendants,
             (New-Object System.Windows.Automation.PropertyCondition(
                 [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                 [System.Windows.Automation.ControlType]::List)))) {
        foreach ($item in $list.FindAll([System.Windows.Automation.TreeScope]::Children,
                 [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($item.Current.Name -like '*Protection*') { return $list }
        }
    }
    return $null
}

function Open-Destination($shell, [string] $label) {
    $nav = Get-ServerNav $shell
    if ($null -eq $nav) { throw 'The server sub-navigation was not found; is a server workspace open?' }
    $item = Find-DescendantLike $nav $label 10 ([System.Windows.Automation.ControlType]::ListItem)
    if ($null -eq $item) { throw "Destination '$label' was not found in the server navigation." }
    try { Select-Element $item } catch { Invoke-Element $item }
    Start-Sleep -Seconds 2
}

# The notification area is several toolbars plus an overflow flyout, and which one holds an icon is
# not the application's choice. Every one of them is searched by name.
function Find-TrayIcon($desktop) {
    foreach ($toolbarName in @('User Promoted Notification Area', 'Notification Area',
                               'System Promoted Notification Area', 'Overflow Notification Area',
                               'System tray overflow window')) {
        $toolbar = Find-Descendant $desktop $toolbarName 2
        if ($null -eq $toolbar) { continue }
        foreach ($button in $toolbar.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                 [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($button.Current.Name -like '*ChunkPilot*') { return $button }
        }
    }
    return $null
}

# The header reports lifecycle state as a word, which is exactly what a person waits for.
function Wait-ForState($shell, [string] $state, [int] $timeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ($null -ne (Find-Descendant $shell $state 2)) { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Nudge-Pointer {
    $point = New-Object Win32Ui+POINT
    if (-not [Win32Ui]::GetCursorPos([ref]$point)) { return }
    [void][Win32Ui]::SetCursorPos($point.X + 1, $point.Y)
    Start-Sleep -Milliseconds 60
    [void][Win32Ui]::SetCursorPos($point.X, $point.Y)
}

function Get-FreePort {
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    return $port
}

function Drag-Vertically([int] $x, [int] $y, [int] $dy) {
    [void][Win32Ui]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 250
    [Win32Ui]::mouse_event([Win32Ui]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    for ($step = 1; $step -le 10; $step++) {
        [void][Win32Ui]::SetCursorPos($x, $y + [int]($dy * $step / 10))
        Start-Sleep -Milliseconds 40
    }
    [Win32Ui]::mouse_event([Win32Ui]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 400
}

$serverFolder = $null

try {
    # ── 1. Wizard: dark chrome, blank name, watermark, caret, EULA ─────────────────────────────
    $shell = Find-Window 'ChunkPilot' 120
    $wizard = Find-Window 'Create a server' 120
    Resize-Window $wizard 1200 820
    Save-Shot $wizard '01-wizard-intent-dark-chrome'

    $vanilla = Find-DescendantLike $wizard 'Just Minecraft' 20 ([System.Windows.Automation.ControlType]::ListItem)
    Select-Element $vanilla
    Invoke-Element (Find-Descendant $wizard 'Next step' 20 ([System.Windows.Automation.ControlType]::Button))
    Start-Sleep -Seconds 7
    Save-Shot $wizard '02-setup-blank-name-watermark'

    $nameBox = Find-Descendant $wizard 'Server name' 20 ([System.Windows.Automation.ControlType]::Edit)
    $initial = Get-Text $nameBox
    Write-Step "Server name field on arrival: '$initial' (length $($initial.Length))"
    if ($initial.Length -ne 0) { throw "The name field was pre-filled with '$initial'." }

    # Caret evidence: focus with nothing typed, then type and place the caret by keyboard.
    $nameBox.SetFocus()
    Start-Sleep -Milliseconds 700
    Save-Shot $wizard '03-setup-focused-empty-caret'
    [System.Windows.Forms.SendKeys]::SendWait('Sunday world')
    Start-Sleep -Milliseconds 700
    Save-Shot $wizard '04-setup-typed-caret-at-end'
    [System.Windows.Forms.SendKeys]::SendWait('{HOME}')
    Start-Sleep -Milliseconds 600
    Save-Shot $wizard '05-setup-caret-home'
    [System.Windows.Forms.SendKeys]::SendWait('{END}+{LEFT 5}')
    Start-Sleep -Milliseconds 600
    Save-Shot $wizard '06-setup-selection'
    Set-Text $nameBox $ServerName
    Start-Sleep -Seconds 2

    $versionList = Find-Descendant $wizard 'Minecraft version' 30 ([System.Windows.Automation.ControlType]::List)
    $chosen = $null
    foreach ($item in $versionList.FindAll([System.Windows.Automation.TreeScope]::Children,
             [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($item.Current.IsEnabled) { $chosen = $item; break }
    }
    Select-Element $chosen
    Write-Step "Chose version: $($chosen.Current.Name)"
    Save-Shot $wizard '07-setup-location-and-version'

    Invoke-Element (Find-Descendant $wizard 'Next step' 20 ([System.Windows.Automation.ControlType]::Button))
    Start-Sleep -Seconds 1
    Save-Shot $wizard '08-review-eula-unaccepted'
    Set-Toggle (Find-Descendant $wizard 'Accept the Minecraft end user licence agreement' 20 ([System.Windows.Automation.ControlType]::CheckBox)) $true
    Save-Shot $wizard '09-review-eula-accepted'

    # ── 2. Create, and open the server ────────────────────────────────────────────────────────
    Invoke-Element (Find-Descendant $wizard 'Create this server now' 20 ([System.Windows.Automation.ControlType]::Button))
    Start-Sleep -Seconds 4
    Save-Shot $wizard '10-creating'
    $deadline = (Get-Date).AddMinutes(12)
    $openServer = $null
    while ((Get-Date) -lt $deadline -and $null -eq $openServer) {
        Start-Sleep -Seconds 5
        $openServer = Find-Descendant $wizard 'Open the server that was just created' 2 ([System.Windows.Automation.ControlType]::Button)
    }
    if ($null -eq $openServer) { throw 'Creation did not reach a successful result within twelve minutes.' }
    Save-Shot $wizard '11-created'
    Invoke-Element $openServer
    Start-Sleep -Seconds 3

    $serverFolder = Get-ChildItem -Directory $serversRoot | Select-Object -First 1 -ExpandProperty FullName
    Write-Step "Server folder: $serverFolder"

    # The review server has to bind a port nobody else on this machine is using. A real ChunkPilot
    # session on the same computer is very likely to be running a server on 25565 already, and a
    # Minecraft server that cannot bind exits during startup - which would look like a ChunkPilot
    # defect rather than an occupied port. Only this run's own isolated file is touched.
    $properties = Join-Path $serverFolder 'server.properties'
    if (Test-Path $properties) {
        $freePort = Get-FreePort
        (Get-Content $properties) -replace '^server-port=.*', "server-port=$freePort" |
            Set-Content $properties -Encoding ascii
        Write-Step "Review server port set to $freePort so it cannot collide with a real session."
    }

    Resize-Window $shell 1440 900
    Save-Shot $shell '12-overview-connection'
    Resize-Window $shell 1000 700
    Save-Shot $shell '13-overview-1000x700'
    Resize-Window $shell 800 600
    Save-Shot $shell '14-overview-800x600'
    Resize-Window $shell 1440 900

    # ── 3. Settings: memory in GB, game rules while stopped ───────────────────────────────────
    Open-Destination $shell 'Settings'
    Save-Shot $shell '15-settings-memory-gb-and-rules-stopped'

    # ── 4. Manage: exact names, text editor, binary state ─────────────────────────────────────
    Open-Destination $shell 'Manage'
    Save-Shot $shell '16-manage-file-list'
    if (Select-Row $shell 'eula.txt') { Start-Sleep -Seconds 2; Save-Shot $shell '17-manage-text-file-loaded' }
    else { Write-Step 'WARNING: eula.txt row not found for selection.' }
    if (Select-Row $shell 'server.jar') { Start-Sleep -Seconds 2; Save-Shot $shell '18-manage-binary-file' }
    else { Write-Step 'WARNING: server.jar row not found for selection.' }
    if (Select-Row $shell 'server.properties') {
        Start-Sleep -Seconds 2
        $editor = Find-Descendant $shell 'File contents' 10 ([System.Windows.Automation.ControlType]::Edit)
        if ($null -ne $editor) {
            $before = Get-Text $editor
            Set-Text $editor ($before + "`r`n# ChunkPilot review edit`r`n")
            Start-Sleep -Milliseconds 600
            Save-Shot $shell '19-manage-dirty-editor'
            Invoke-Element (Find-Descendant $shell 'Save changes to this file' 10 ([System.Windows.Automation.ControlType]::Button))
            Start-Sleep -Seconds 2
            Save-Shot $shell '20-manage-saved'
        }
    }

    # ── 5. Scroll bar: drag from the content side ─────────────────────────────────────────────
    $rect = New-Object Win32Ui+RECT
    [void][Win32Ui]::GetWindowRect([IntPtr]$shell.Current.NativeWindowHandle, [ref]$rect)
    Drag-Vertically ($rect.Right - 20) ($rect.Top + 400) 260
    Save-Shot $shell '21-scrollbar-dragged-from-content-side'

    # ── 6. Start the server, then Access and Protection with a live server ───────────────────
    Invoke-Element (Find-Descendant $shell 'Start' 15 ([System.Windows.Automation.ControlType]::Button))
    Write-Step 'Start requested; waiting for readiness.'
    # A first start generates the world, which takes far longer than a later one. Wait for the state
    # the header reports rather than for a fixed number of seconds.
    if (-not (Wait-ForState $shell 'Running' 240)) {
        Save-Shot $shell '99-start-never-reached-running'
        throw 'The server never reached Running.'
    }
    Write-Step 'Server reported Running.'
    Save-Shot $shell '22-running-overview'

    Open-Destination $shell 'Access'
    Start-Sleep -Seconds 3
    Save-Shot $shell '23-access-running-empty'

    $addName = Find-Descendant $shell 'Player name to add to the whitelist' 15 ([System.Windows.Automation.ControlType]::Edit)
    Set-Text $addName 'Traffic_Tom'
    Invoke-Element (Find-Descendant $shell 'Add this player to the whitelist' 15 ([System.Windows.Automation.ControlType]::Button))
    Start-Sleep -Seconds 3
    Save-Shot $shell '24-access-whitelist-added'

    # Operator through the Console, to prove the page follows the server rather than the click.
    Open-Destination $shell 'Console'
    Save-Shot $shell '25-console-command-hint'
    $command = Find-Descendant $shell 'Console command' 15 ([System.Windows.Automation.ControlType]::Edit)
    $command.SetFocus()
    Start-Sleep -Milliseconds 600
    Save-Shot $shell '26-console-focused-caret'
    [System.Windows.Forms.SendKeys]::SendWait('op Xustar{ENTER}')
    Start-Sleep -Seconds 3
    Save-Shot $shell '27-console-after-enter'

    Open-Destination $shell 'Access'
    Start-Sleep -Seconds 3
    Save-Shot $shell '28-access-operator-from-console'

    $whitelistSwitch = Find-Descendant $shell 'Whitelist' 10 ([System.Windows.Automation.ControlType]::CheckBox)
    if ($null -ne $whitelistSwitch) {
        Set-Toggle $whitelistSwitch $true
        Start-Sleep -Seconds 3
        Save-Shot $shell '29-access-whitelist-on'
    }

    # ── 7. Protection: backup while running, then stopped ────────────────────────────────────
    Open-Destination $shell 'Protection'
    Save-Shot $shell '30-protection-before-backup'
    Invoke-Element (Find-Descendant $shell 'Backup now' 15 ([System.Windows.Automation.ControlType]::Button))
    Start-Sleep -Seconds 25
    Save-Shot $shell '31-protection-running-backup'

    Open-Destination $shell 'Settings'
    Start-Sleep -Seconds 4
    Save-Shot $shell '32-settings-gamerules-live'
    # Focusing a control scrolls it into view, which is how the memory card is captured without
    # guessing at a scroll offset.
    $memory = Find-Descendant $shell 'Maximum memory in gigabytes' 10
    if ($null -ne $memory) { $memory.SetFocus(); Start-Sleep -Seconds 1; Save-Shot $shell '32b-settings-memory-gb' }
    $keepInventory = Find-Descendant $shell 'Keep items on death' 15 ([System.Windows.Automation.ControlType]::CheckBox)
    if ($null -ne $keepInventory) {
        Set-Toggle $keepInventory $true
        Start-Sleep -Seconds 3
        Save-Shot $shell '33-settings-gamerule-boolean-changed'
    } else { Write-Step 'WARNING: keepInventory switch not found.' }
    $tickSpeed = Find-Descendant $shell 'Random tick speed' 10
    if ($null -ne $tickSpeed) {
        $increase = Find-Descendant $tickSpeed 'Increase value' 5 ([System.Windows.Automation.ControlType]::Button)
        if ($null -ne $increase) { Invoke-Element $increase; Start-Sleep -Seconds 3; Save-Shot $shell '34-settings-gamerule-number-changed' }
    }

    Invoke-Element (Find-Descendant $shell 'Stop' 15 ([System.Windows.Automation.ControlType]::Button))
    Write-Step 'Stop requested.'
    if (-not (Wait-ForState $shell 'Stopped' 120)) { Write-Step 'WARNING: the server did not report Stopped.' }
    Open-Destination $shell 'Protection'
    Invoke-Element (Find-Descendant $shell 'Backup now' 15 ([System.Windows.Automation.ControlType]::Button))
    Start-Sleep -Seconds 20
    Save-Shot $shell '35-protection-stopped-backup'

    # ── 8. Tray: minimize, then one left click ───────────────────────────────────────────────
    $shellHandle = [IntPtr]$shell.Current.NativeWindowHandle
    [void][Win32Ui]::ShowWindow($shellHandle, 6)
    Start-Sleep -Seconds 3
    $hiddenAfterMinimize = -not [Win32Ui]::IsWindowVisible($shellHandle)
    Write-Step "Hidden to the notification area after minimize: $hiddenAfterMinimize"

    # The icon may be in the notification-area overflow, which is a separate flyout that has to be
    # opened before its contents exist in the tree at all.
    $trayButton = Find-TrayIcon $desktop
    if ($null -eq $trayButton) {
        foreach ($chevronName in @('Show Hidden Icons', 'Notification Chevron', 'Show hidden icons')) {
            $chevron = Find-Descendant $desktop $chevronName 3 ([System.Windows.Automation.ControlType]::Button)
            if ($null -eq $chevron) { continue }
            Write-Step "Opening the notification-area overflow via '$chevronName'."
            try { Invoke-Element $chevron } catch { $chevron.SetFocus() }
            Start-Sleep -Seconds 1
            $trayButton = Find-TrayIcon $desktop
            if ($null -ne $trayButton) { break }
        }
    }
    if ($null -eq $trayButton) {
        Write-Step 'WARNING: the tray icon was not reachable through UI Automation (overflow flyout). Restoring by click was not exercised here.'
    }
    else {
        $point = $trayButton.GetClickablePoint()
        Write-Step "Tray icon '$($trayButton.Current.Name)' at $([int]$point.X),$([int]$point.Y)"
        $before = @(Get-CimInstance Win32_Process -Filter "Name='ChunkPilot.exe'" |
            Where-Object { $_.CommandLine -like "*$AppDirectory*" }).Count
        # One left click. Not a double click: requiring one was the defect.
        [void][Win32Ui]::SetCursorPos([int]$point.X, [int]$point.Y)
        Start-Sleep -Milliseconds 300
        [Win32Ui]::mouse_event([Win32Ui]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
        [Win32Ui]::mouse_event([Win32Ui]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Seconds 3
        $restored = [Win32Ui]::IsWindowVisible($shellHandle) -and -not [Win32Ui]::IsIconic($shellHandle)
        $after = @(Get-CimInstance Win32_Process -Filter "Name='ChunkPilot.exe'" |
            Where-Object { $_.CommandLine -like "*$AppDirectory*" }).Count
        Write-Step "Restored by a single left click: $restored (App processes before=$before after=$after)"
        if ($restored) { Save-Shot $shell '36-restored-from-tray' }
    }

    # ── 9. Close, and account for every process ──────────────────────────────────────────────
    Write-Step 'Sending WM_CLOSE to the shell.'
    [void][Win32Ui]::SendMessage($shellHandle, [Win32Ui]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Seconds 12
    $appAlive = -not $process.HasExited
    Write-Step "App process still alive after WM_CLOSE: $appAlive"
    $agents = Get-CimInstance Win32_Process -Filter "Name='ChunkPilot.Agent.exe'" |
        Where-Object { $_.CommandLine -like "*$AppDirectory*" }
    $javas = Get-CimInstance Win32_Process -Filter "Name='java.exe'" |
        Where-Object { $_.CommandLine -like "*$Root*" }
    Write-Step "Agents owned by this run still alive: $(@($agents).Count)"
    Write-Step "Java processes owned by this run still alive: $(@($javas).Count)"
    Write-Step 'Walk complete.'
}
catch {
    Write-Step "FAILED: $_"
    try { Save-Shot $shell '99-failure-shell' } catch { }
    try { Save-Shot $wizard '99-failure-wizard' } catch { }
    throw
}
finally {
    [void][Win32Ui]::SetThreadExecutionState([Win32Ui]::ES_CONTINUOUS)
    if ($null -ne $serverFolder -and (Test-Path $serverFolder)) {
        Write-Step "Server folder contents: $((Get-ChildItem $serverFolder | Select-Object -ExpandProperty Name) -join ', ')"
    }
    Set-Content -Path (Join-Path $OutputDirectory 'workspace-walkthrough.log') -Value ($log -join [Environment]::NewLine) -Encoding utf8
    Write-Host "Log:  $(Join-Path $OutputDirectory 'workspace-walkthrough.log')"
    Write-Host "Root: $Root"
}
