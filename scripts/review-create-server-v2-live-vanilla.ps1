<#
.SYNOPSIS
    Drives the development-gated live Vanilla wizard against an isolated data root and captures it.

.DESCRIPTION
    Launches ChunkPilot with --create-server-v2-live-vanilla using a temporary CHUNKPILOT_DATA_ROOT,
    CHUNKPILOT_MANAGED_SERVERS_ROOT and CHUNKPILOT_INSTANCE_ID, walks the wizard through UI Automation,
    and writes one PNG per materially distinct state.

    Nothing outside the roots this script creates is read or written. The real ChunkPilot data root,
    the real managed-servers folder, the registry, the firewall and any real server are untouched.
    The run contacts the official Mojang and Adoptium services only, and only because creating a real
    server is the point of the review.

.PARAMETER OutputDirectory
    Where the PNGs and the run log go. Defaults to the ignored review-artifact directory.

.PARAMETER Root
    The isolated root for this run. Defaults to a fresh temporary directory.

.PARAMETER ServerName
    The name to create. Defaults to a name that is obviously a review fixture.

.PARAMETER SkipCreate
    Walk and capture the wizard but stop before the Create action, so no server is created.
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory = '',
    [string] $Root = (Join-Path $env:TEMP ("ChunkPilot-live-review-" + [guid]::NewGuid().ToString('N'))),
    [string] $ServerName = 'Review vanilla server',
    [switch] $SkipCreate
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

# Resolved here rather than in a parameter default: Windows PowerShell does not populate
# $PSScriptRoot early enough for a default value when the script is run with -File.
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $scriptRoot '..\artifacts\create-server-v2-vanilla-vertical-slice'
}

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win32Capture
{
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$log = New-Object System.Collections.Generic.List[string]
function Write-Step([string] $message) {
    $line = "{0:HH:mm:ss} {1}" -f (Get-Date), $message
    $log.Add($line)
    Write-Host $line
}

$dataRoot = Join-Path $Root 'data'
$serversRoot = Join-Path $Root 'servers'
New-Item -ItemType Directory -Force -Path $dataRoot, $serversRoot, $OutputDirectory | Out-Null

$exe = Join-Path $scriptRoot '..\src\ChunkPilot.App\bin\Release\net10.0-windows\ChunkPilot.exe'
if (-not (Test-Path $exe)) { throw "Build the Release App first: $exe" }
$exe = (Resolve-Path $exe).Path

$instanceId = [guid]::NewGuid().ToString('N')
Write-Step "Isolated root: $Root"
Write-Step "Instance id:   $instanceId"

$info = New-Object System.Diagnostics.ProcessStartInfo
$info.FileName = $exe
$info.Arguments = '--create-server-v2-live-vanilla'
$info.UseShellExecute = $false
$info.WorkingDirectory = Split-Path $exe
$info.Environment['CHUNKPILOT_DATA_ROOT'] = $dataRoot
$info.Environment['CHUNKPILOT_MANAGED_SERVERS_ROOT'] = $serversRoot
$info.Environment['CHUNKPILOT_INSTANCE_ID'] = $instanceId
$process = [System.Diagnostics.Process]::Start($info)
Write-Step "Started ChunkPilot pid $($process.Id)"

# ---------------------------------------------------------------- automation helpers

# Named for what it is; $Root is already the isolated filesystem root and PowerShell variable names
# are case-insensitive, so reusing it here would silently overwrite the parameter.
$desktop = [System.Windows.Automation.AutomationElement]::RootElement

# An owned window is a *descendant* of its owner in the UI Automation tree, not a sibling, so the
# wizard is reached by searching the whole subtree rather than the desktop's immediate children.
function Find-Window([string] $namePart, [int] $timeoutSeconds = 60) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
        foreach ($scope in @([System.Windows.Automation.TreeScope]::Children,
                             [System.Windows.Automation.TreeScope]::Descendants)) {
            $windows = $desktop.FindAll($scope, $condition)
            foreach ($window in $windows) {
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

# A label and the control it names share the same accessible name, so the control type has to be
# part of the query or the search finds the caption and then fails to type into it.
function Find-Descendant($parent, [string] $name, [int] $timeoutSeconds = 30, $controlType = $null) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)
        $candidates = $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
        foreach ($found in $candidates) {
            if ($found.Current.IsOffscreen) { continue }
            if ($null -ne $controlType -and $found.Current.ControlType -ne $controlType) { continue }
            return $found
        }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Invoke-Element($element) {
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Start-Sleep -Milliseconds 500
}

function Select-Element($element) {
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
    Start-Sleep -Milliseconds 400
}

function Set-Text($element, [string] $value) {
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($value)
    Start-Sleep -Milliseconds 400
}

function Set-Toggle($element, [bool] $on) {
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $target = if ($on) { 'On' } else { 'Off' }
    $guard = 0
    while ($pattern.Current.ToggleState -ne $target -and $guard -lt 3) {
        $pattern.Toggle(); Start-Sleep -Milliseconds 300; $guard++
    }
    Start-Sleep -Milliseconds 400
}

function Save-Shot($window, [string] $name) {
    $handle = [IntPtr]$window.Current.NativeWindowHandle
    [void][Win32Capture]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 500
    $rect = New-Object Win32Capture+RECT
    [void][Win32Capture]::GetWindowRect($handle, [ref]$rect)
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) { throw "Window has no size for capture '$name'." }
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    # PrintWindow with PW_RENDERFULLCONTENT asks the window to draw itself, which is the only capture
    # that is reliable for a composited WPF window; copying from the screen returns whatever happens
    # to be painted there, which on a non-interactive desktop is nothing.
    $hdc = $graphics.GetHdc()
    $printed = [Win32Capture]::PrintWindow($handle, $hdc, 2)
    $graphics.ReleaseHdc($hdc)
    if (-not $printed) {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($width, $height)))
    }
    $path = Join-Path $OutputDirectory ("{0}.png" -f $name)
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
    Write-Step "Captured $name ($width x $height)"
    return $path
}

function Resize-Window($window, [int] $width, [int] $height) {
    $handle = [IntPtr]$window.Current.NativeWindowHandle
    [void][Win32Capture]::ShowWindow($handle, 1)
    [void][Win32Capture]::MoveWindow($handle, 60, 40, $width, $height, $true)
    Start-Sleep -Milliseconds 900
}

# ---------------------------------------------------------------- walk

try {
    $shell = Find-Window 'ChunkPilot' 90
    Write-Step "Shell window: $($shell.Current.Name)"
    $wizard = Find-Window 'Create a server' 90
    Write-Step "Wizard window: $($wizard.Current.Name)"
    Resize-Window $wizard 1440 900
    Save-Shot $wizard '01-intent-live' | Out-Null

    $vanilla = Find-Descendant $wizard 'Just Minecraft. The official game, exactly as Mojang ships it. Available in this development build.'
    if ($null -eq $vanilla) { throw 'The Vanilla intent card was not found.' }
    Select-Element $vanilla
    Save-Shot $wizard '02-intent-vanilla-selected' | Out-Null

    Invoke-Element (Find-Descendant $wizard 'Next step' 30 ([System.Windows.Automation.ControlType]::Button))
    Start-Sleep -Seconds 1
    Save-Shot $wizard '03-setup-loading-or-versions' | Out-Null
    Start-Sleep -Seconds 6
    Save-Shot $wizard '04-setup-stable-releases' | Out-Null

    $snapshots = Find-Descendant $wizard 'Also show in-development snapshots' 30 ([System.Windows.Automation.ControlType]::CheckBox)
    Set-Toggle $snapshots $true
    Start-Sleep -Seconds 6
    Save-Shot $wizard '05-setup-snapshots-included' | Out-Null
    Set-Toggle $snapshots $false
    Start-Sleep -Seconds 4

    $nameBox = Find-Descendant $wizard 'Server name' 30 ([System.Windows.Automation.ControlType]::Edit)
    Set-Text $nameBox 'CON:bad name.'
    Save-Shot $wizard '06-setup-invalid-name' | Out-Null
    Set-Text $nameBox $ServerName
    Start-Sleep -Seconds 2

    $versionList = Find-Descendant $wizard 'Minecraft version' 30 ([System.Windows.Automation.ControlType]::List)
    $items = $versionList.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    if ($items.Count -eq 0) { throw 'No versions were offered.' }
    $chosen = $null
    foreach ($item in $items) {
        if ($item.Current.IsEnabled) { $chosen = $item; break }
    }
    if ($null -eq $chosen) { throw 'No selectable version was offered.' }
    Write-Step "Chose version row: $($chosen.Current.Name)"
    Select-Element $chosen
    Save-Shot $wizard '07-setup-version-chosen' | Out-Null

    Resize-Window $wizard 800 600
    Save-Shot $wizard '08-size-800x600' | Out-Null
    Resize-Window $wizard 1000 700
    Save-Shot $wizard '09-size-1000x700' | Out-Null
    Resize-Window $wizard 1440 900
    Save-Shot $wizard '10-size-1440x900' | Out-Null
    $handle = [IntPtr]$wizard.Current.NativeWindowHandle
    [void][Win32Capture]::ShowWindow($handle, 3)
    Start-Sleep -Milliseconds 900
    Save-Shot $wizard '11-size-maximised' | Out-Null
    Resize-Window $wizard 1440 900

    Invoke-Element (Find-Descendant $wizard 'Next step' 30 ([System.Windows.Automation.ControlType]::Button))
    Start-Sleep -Seconds 1
    Save-Shot $wizard '12-review-eula-unchecked' | Out-Null

    $eula = Find-Descendant $wizard 'I have read and accept the Minecraft End User Licence Agreement.' 30 ([System.Windows.Automation.ControlType]::CheckBox)
    Set-Toggle $eula $true
    Save-Shot $wizard '13-review-eula-accepted' | Out-Null

    if ($SkipCreate) {
        Write-Step 'SkipCreate was set: stopping before the Create action. Nothing was created.'
    }
    else {
        Invoke-Element (Find-Descendant $wizard 'Create this server now' 30 ([System.Windows.Automation.ControlType]::Button))
        Start-Sleep -Seconds 2
        Save-Shot $wizard '14-creating-early' | Out-Null
        Start-Sleep -Seconds 8
        Save-Shot $wizard '15-creating-progress' | Out-Null

        $deadline = (Get-Date).AddMinutes(10)
        $done = $false
        while ((Get-Date) -lt $deadline -and -not $done) {
            Start-Sleep -Seconds 5
            $close = Find-Descendant $wizard 'Close this window' 2 ([System.Windows.Automation.ControlType]::Button)
            if ($null -ne $close) { $done = $true }
        }
        if (-not $done) { throw 'The creation did not reach a result within ten minutes.' }
        Save-Shot $wizard '16-result' | Out-Null
        Write-Step 'Creation reached a result.'
    }

    Write-Step 'Walk complete.'
}
catch {
    Write-Step "FAILED: $_"
    try { Save-Shot $wizard '99-failure' | Out-Null } catch { }
    throw
}
finally {
    $logPath = Join-Path $OutputDirectory 'live-vanilla-review.log'
    Set-Content -Path $logPath -Value ($log -join [Environment]::NewLine) -Encoding utf8
    Write-Host "Log: $logPath"
    Write-Host "Isolated root left in place for inspection: $Root"
    Write-Host "ChunkPilot pid $($process.Id) is still running; close it with the shell window."
}
