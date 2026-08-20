<#
.SYNOPSIS
    Walks the polished live Vanilla wizard, the created server's Overview and the Console, recording
    what each state actually renders.

.DESCRIPTION
    Launches ChunkPilot with --create-server-v2-live-vanilla against isolated CHUNKPILOT_DATA_ROOT and
    CHUNKPILOT_MANAGED_SERVERS_ROOT, drives it through UI Automation, and writes the accessible text
    of each state to a log. A PNG is attempted per state; on a desktop session that does not composite
    for a background capture the bitmap comes back unpainted, and the script says so rather than
    leaving a misleading image.

    Nothing outside the roots this script creates is read or written.
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory = '',
    [string] $Root = (Join-Path $env:TEMP ("ChunkPilot-ux-" + [guid]::NewGuid().ToString('N'))),
    [string] $ServerName = 'UX review server'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $scriptRoot '..\artifacts\create-server-v2-ux-hardening'
}

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class UxCapture
{
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetTopWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$log = New-Object System.Collections.Generic.List[string]
function Write-Step([string] $message) {
    $line = "{0:HH:mm:ss} {1}" -f (Get-Date), $message
    $log.Add($line); Write-Host $line
}
function Save-Log {
    Set-Content -Path (Join-Path $OutputDirectory 'ux-review.log') -Value ($log -join [Environment]::NewLine) -Encoding utf8
}

$dataRoot = Join-Path $Root 'data'
$serversRoot = Join-Path $Root 'servers'
New-Item -ItemType Directory -Force -Path $dataRoot, $serversRoot, $OutputDirectory | Out-Null

$exe = (Resolve-Path (Join-Path $scriptRoot '..\src\ChunkPilot.App\bin\Release\net10.0-windows\ChunkPilot.exe')).Path
$instanceId = [guid]::NewGuid().ToString('N')
Write-Step "Isolated root: $Root"

$info = New-Object System.Diagnostics.ProcessStartInfo
$info.FileName = $exe
$info.Arguments = '--create-server-v2-live-vanilla'
$info.UseShellExecute = $false
$info.WorkingDirectory = Split-Path $exe
$info.Environment['CHUNKPILOT_DATA_ROOT'] = $dataRoot
$info.Environment['CHUNKPILOT_MANAGED_SERVERS_ROOT'] = $serversRoot
$info.Environment['CHUNKPILOT_INSTANCE_ID'] = $instanceId
$process = [System.Diagnostics.Process]::Start($info)
Write-Step "Started pid $($process.Id); this shell keeps the foreground so the raise can be judged."

$desktop = [System.Windows.Automation.AutomationElement]::RootElement

function Find-Window([string] $namePart, [int] $timeoutSeconds = 90) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
        foreach ($scope in @([System.Windows.Automation.TreeScope]::Children,
                             [System.Windows.Automation.TreeScope]::Descendants)) {
            foreach ($window in $desktop.FindAll($scope, $condition)) {
                if ($window.Current.Name -like "*$namePart*" -and
                    $window.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { return $window }
            }
        }
        Start-Sleep -Milliseconds 400
    }
    throw "Window matching '$namePart' never appeared."
}

function Find-Element($parent, [string] $name, $controlType = $null, [int] $timeoutSeconds = 30) {
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

function Invoke-Element($element) {
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 600
}
function Select-Element($element) {
    $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
}
function Set-Text($element, [string] $value) {
    $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
    Start-Sleep -Milliseconds 500
}
function Set-Toggle($element, [bool] $on) {
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $target = if ($on) { 'On' } else { 'Off' }
    $guard = 0
    while ($pattern.Current.ToggleState -ne $target -and $guard -lt 3) { $pattern.Toggle(); Start-Sleep -Milliseconds 300; $guard++ }
    Start-Sleep -Milliseconds 400
}

# The accessible text is what the state actually renders, and unlike a bitmap it can be inspected
# here. Every walked state is recorded this way whether or not a PNG comes out painted.
function Record-State($window, [string] $name) {
    Write-Step "--- state: $name"
    $texts = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text)))
    foreach ($t in $texts) { if ($t.Current.Name -and -not $t.Current.IsOffscreen) { $log.Add("    " + $t.Current.Name) } }
    Save-Shot $window $name
    Save-Log
}

function Save-Shot($window, [string] $name) {
    try {
        $handle = [IntPtr]$window.Current.NativeWindowHandle
        [void][UxCapture]::SetForegroundWindow($handle)
        Start-Sleep -Milliseconds 400
        $rect = New-Object UxCapture+RECT
        [void][UxCapture]::GetWindowRect($handle, [ref]$rect)
        $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
        if ($w -le 0 -or $h -le 0) { return }
        $bitmap = New-Object System.Drawing.Bitmap($w, $h)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $hdc = $graphics.GetHdc()
        [void][UxCapture]::PrintWindow($handle, $hdc, 2)
        $graphics.ReleaseHdc($hdc)
        $graphics.Dispose()
        # A frame whose client area is a single colour is an unpainted capture, not a screenshot.
        $sample = @($bitmap.GetPixel([int]($w/2), [int]($h/2)), $bitmap.GetPixel([int]($w/3), [int]($h*2/3)),
                    $bitmap.GetPixel([int]($w*2/3), [int]($h/2)))
        $painted = ($sample | Select-Object -ExpandProperty Name -Unique).Count -gt 1
        if ($painted) {
            $bitmap.Save((Join-Path $OutputDirectory "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
            $log.Add("    [captured $name.png]")
        }
        else { $log.Add("    [capture unpainted; no image written]") }
        $bitmap.Dispose()
    }
    catch { $log.Add("    [capture failed: $_]") }
}

try {
    $shell = Find-Window 'ChunkPilot'
    $wizard = Find-Window 'Create a server'
    $handle = [IntPtr]$wizard.Current.NativeWindowHandle
    $foreground = [UxCapture]::GetForegroundWindow()
    Write-Step ("Foreground after launch is the wizard: " + ($foreground -eq $handle))
    Write-Step ("Wizard focused element: " +
        ([System.Windows.Automation.AutomationElement]::FocusedElement).Current.Name)
    # Foreground and Z-order are different questions. Activation can be refused while the window is
    # still raised in front of everything else, which is what the topmost toggle achieves.
    $zIndex = -1; $walk = [UxCapture]::GetTopWindow([IntPtr]::Zero); $seen = 0
    while ($walk -ne [IntPtr]::Zero -and $seen -lt 200) {
        if ([UxCapture]::IsWindowVisible($walk)) {
            if ($walk -eq $handle) { $zIndex = $seen; break }
            $seen++
        }
        $walk = [UxCapture]::GetWindow($walk, 2)
    }
    Write-Step ("Wizard visible Z-order index (0 is frontmost): " + $zIndex)

    [void][UxCapture]::MoveWindow($handle, 60, 40, 1440, 900, $true); Start-Sleep -Milliseconds 800
    Record-State $wizard '01-intent'

    Select-Element (Find-Element $wizard 'Just Minecraft. The official game, exactly as Mojang ships it. Available')
    Invoke-Element (Find-Element $wizard 'Next step' ([System.Windows.Automation.ControlType]::Button))
    Start-Sleep -Seconds 8
    Record-State $wizard '02-setup-versions'

    $nameBox = Find-Element $wizard 'Server name' ([System.Windows.Automation.ControlType]::Edit)
    Set-Text $nameBox 'CON:bad name.'
    Record-State $wizard '03-setup-invalid-name'
    Set-Text $nameBox $ServerName
    Start-Sleep -Seconds 2
    Record-State $wizard '04-setup-valid-name'

    $snapshots = Find-Element $wizard 'Include in-development snapshots' ([System.Windows.Automation.ControlType]::CheckBox)
    Set-Toggle $snapshots $true; Start-Sleep -Seconds 7
    Record-State $wizard '05-setup-snapshots'
    Set-Toggle $snapshots $false; Start-Sleep -Seconds 5

    $list = Find-Element $wizard 'Minecraft version' ([System.Windows.Automation.ControlType]::List)
    $chosen = $null
    foreach ($item in $list.FindAll([System.Windows.Automation.TreeScope]::Children,
                                    [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($item.Current.IsEnabled) { $chosen = $item; break }
    }
    if ($null -eq $chosen) { throw 'No selectable version.' }
    Write-Step "Chose: $($chosen.Current.Name)"
    Select-Element $chosen
    Record-State $wizard '06-setup-selected'

    foreach ($size in @(@(800,600), @(1000,700), @(1440,900))) {
        [void][UxCapture]::MoveWindow($handle, 40, 30, $size[0], $size[1], $true); Start-Sleep -Milliseconds 900
        Record-State $wizard ("07-size-{0}x{1}" -f $size[0], $size[1])
    }
    [void][UxCapture]::ShowWindow($handle, 3); Start-Sleep -Seconds 1
    Record-State $wizard '08-maximised'
    [void][UxCapture]::ShowWindow($handle, 1)
    [void][UxCapture]::MoveWindow($handle, 60, 40, 1440, 900, $true); Start-Sleep -Milliseconds 800

    Invoke-Element (Find-Element $wizard 'Next step' ([System.Windows.Automation.ControlType]::Button))
    Start-Sleep -Seconds 1
    Record-State $wizard '09-review-eula-unchecked'

    Set-Toggle (Find-Element $wizard 'I have read and accept the Minecraft End User Licence Agreement' ([System.Windows.Automation.ControlType]::CheckBox)) $true
    Record-State $wizard '10-review-eula-accepted'

    Invoke-Element (Find-Element $wizard 'Create this server now' ([System.Windows.Automation.ControlType]::Button))
    Start-Sleep -Seconds 3
    Record-State $wizard '11-creating'

    $deadline = (Get-Date).AddMinutes(10); $done = $false
    while ((Get-Date) -lt $deadline -and -not $done) {
        Start-Sleep -Seconds 4
        if ($null -ne (Find-Element $wizard 'Close this window' ([System.Windows.Automation.ControlType]::Button) 2)) { $done = $true }
    }
    if (-not $done) { throw 'Creation never reached a result.' }
    Record-State $wizard '12-result'

    Invoke-Element (Find-Element $wizard 'Open the server that was just created' ([System.Windows.Automation.ControlType]::Button))
    Start-Sleep -Seconds 4
    $shellHandle = [IntPtr]$shell.Current.NativeWindowHandle
    foreach ($size in @(@(800,600), @(1000,700), @(1440,900))) {
        [void][UxCapture]::MoveWindow($shellHandle, 40, 30, $size[0], $size[1], $true); Start-Sleep -Seconds 1
        Record-State $shell ("13-overview-{0}x{1}" -f $size[0], $size[1])
    }
    [void][UxCapture]::ShowWindow($shellHandle, 3); Start-Sleep -Seconds 1
    Record-State $shell '14-overview-maximised'

    $console = Find-Element $shell 'Console'
    if ($null -ne $console) {
        Invoke-Element $console
        Start-Sleep -Seconds 2
        Record-State $shell '15-console-stopped'
        $command = Find-Element $shell 'Console command' ([System.Windows.Automation.ControlType]::Edit)
        if ($null -ne $command) {
            Set-Text $command '   '
            $send = Find-Element $shell 'Send this command to the server' ([System.Windows.Automation.ControlType]::Button)
            Write-Step ("Send enabled with whitespace only: " + $send.Current.IsEnabled)
            Set-Text $command 'list'
            Start-Sleep -Milliseconds 500
            $send = Find-Element $shell 'Send this command to the server' ([System.Windows.Automation.ControlType]::Button)
            Write-Step ("Send enabled with a real command: " + $send.Current.IsEnabled)
        }
    }

    Write-Step 'Walk complete.'
}
catch {
    Write-Step "FAILED: $_"
    throw
}
finally {
    Save-Log
    Write-Host "Log: $(Join-Path $OutputDirectory 'ux-review.log')"
    Write-Host "Isolated root: $Root"
}
