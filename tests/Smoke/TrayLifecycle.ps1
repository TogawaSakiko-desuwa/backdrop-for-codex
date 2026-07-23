# Windows 11 interactive smoke test for the notification-area lifecycle.
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [string]$ExePath,

    [switch]$AttachExisting,

    [switch]$ProbeBeforeClose,

    [ValidateRange(2, 30)]
    [int]$TimeoutSeconds = 8
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class TrayLifecycleNative
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string className, string windowName);
}
"@

$script:launchedProcess = $null
$script:ownsProcess = $false
$script:overflowButton = $null
$script:overflowOpenedByProbe = $false

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Message)

    Write-Host "[tray-smoke] $Message"
}

function Wait-Until {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Condition,
        [Parameter(Mandatory = $true)][int]$Seconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (& $Condition) {
            return $true
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    return $false
}

function Get-MatchingProcesses {
    param([Parameter(Mandatory = $true)][string]$ResolvedExePath)

    $matches = @()
    $candidates = @(
        Get-Process -Name "BackdropForCodex", "CodexWallpaper" `
            -ErrorAction SilentlyContinue
    )
    foreach ($candidate in $candidates) {
        try {
            if ([string]::Equals(
                    [IO.Path]::GetFullPath($candidate.Path),
                    $ResolvedExePath,
                    [StringComparison]::OrdinalIgnoreCase)) {
                $matches += $candidate
            }
        }
        catch {
            # An inaccessible unrelated process is never selected or terminated.
        }
    }

    return @($matches)
}

function Get-DesktopChildByClass {
    param([Parameter(Mandatory = $true)][string]$ClassName)

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $children = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    for ($index = 0; $index -lt $children.Count; $index++) {
        $element = $children.Item($index)
        if ([string]::Equals(
                $element.Current.ClassName,
                $ClassName,
                [StringComparison]::Ordinal)) {
            return $element
        }
    }

    return $null
}

function Test-ContainsBackdropTrayIcon {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Root
    )

    $elements = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    for ($index = 0; $index -lt $elements.Count; $index++) {
        $element = $elements.Item($index)
        $name = $element.Current.Name
        if ($element.Current.ControlType -eq
                [System.Windows.Automation.ControlType]::Button -and
            [string]::Equals(
                $element.Current.AutomationId,
                "NotifyItemIcon",
                [StringComparison]::Ordinal) -and
            [string]::Equals(
                $element.Current.ClassName,
                "SystemTray.NormalButton",
                [StringComparison]::Ordinal) -and
            -not [string]::IsNullOrWhiteSpace($name) -and
            $name -match "(?i)\bBackdrop\s+for\s+Codex\b") {
            return $true
        }
    }

    return $false
}

function Get-OverflowButtonCandidates {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Taskbar
    )

    $elements = $Taskbar.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $candidates = @()
    for ($index = 0; $index -lt $elements.Count; $index++) {
        $element = $elements.Item($index)
        if ([string]::Equals(
                $element.Current.ClassName,
                "SystemTray.NormalButton",
                [StringComparison]::Ordinal) -and
            [string]::Equals(
                $element.Current.AutomationId,
                "SystemTrayIcon",
                [StringComparison]::Ordinal)) {
            $pattern = $null
            if ($element.TryGetCurrentPattern(
                    [System.Windows.Automation.InvokePattern]::Pattern,
                    [ref]$pattern)) {
                $candidates += $element
            }
        }
    }

    return @($candidates |
        Sort-Object { $_.Current.BoundingRectangle.Left } |
        ForEach-Object { $_ })
}

function Open-NotificationOverflow {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Taskbar
    )

    $existing = Get-DesktopChildByClass `
        -ClassName "TopLevelWindowForOverflowXamlIsland"
    if ($null -ne $existing) {
        return $existing
    }

    $candidates = @(Get-OverflowButtonCandidates -Taskbar $Taskbar)
    foreach ($candidate in $candidates) {
        $invokePattern = $candidate.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)
        $invokePattern.Invoke()

        $opened = Wait-Until -Seconds 1 -Condition {
            $null -ne (Get-DesktopChildByClass `
                -ClassName "TopLevelWindowForOverflowXamlIsland")
        }
        if ($opened) {
            $script:overflowButton = $candidate
            $script:overflowOpenedByProbe = $true
            return Get-DesktopChildByClass `
                -ClassName "TopLevelWindowForOverflowXamlIsland"
        }

        # The candidate opened another system flyout. Invoke the same button
        # again to restore the taskbar before trying the next language-neutral
        # candidate.
        $invokePattern.Invoke()
        Start-Sleep -Milliseconds 100
    }

    throw "The Windows notification-area overflow button was not found."
}

function Test-TrayIconPresent {
    $taskbarHandle = [TrayLifecycleNative]::FindWindow(
        "Shell_TrayWnd",
        $null)
    if ($taskbarHandle -eq [IntPtr]::Zero) {
        throw "Windows taskbar window was not found."
    }

    $taskbar =
        [System.Windows.Automation.AutomationElement]::FromHandle($taskbarHandle)
    if (Test-ContainsBackdropTrayIcon -Root $taskbar) {
        return $true
    }

    $overflow = Open-NotificationOverflow -Taskbar $taskbar
    return Test-ContainsBackdropTrayIcon -Root $overflow
}

function Close-NotificationOverflow {
    if (-not $script:overflowOpenedByProbe) {
        return
    }

    try {
        $overflow = Get-DesktopChildByClass `
            -ClassName "TopLevelWindowForOverflowXamlIsland"
        if ($null -ne $overflow) {
            if ($null -eq $script:overflowButton) {
                throw "The overflow panel was opened by the probe without an owning button."
            }

            $invokePattern = $script:overflowButton.GetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern)
            $invokePattern.Invoke()
            $closed = Wait-Until -Seconds 2 -Condition {
                $null -eq (Get-DesktopChildByClass `
                    -ClassName "TopLevelWindowForOverflowXamlIsland")
            }
            if (-not $closed) {
                throw "The Windows notification-area overflow panel did not close."
            }
        }
    }
    finally {
        $script:overflowButton = $null
        $script:overflowOpenedByProbe = $false
    }
}

$startedAt = [DateTime]::UtcNow
$beforeCloseIconPresent = $null
$afterCloseIconPresent = $null
try {
    $repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    if ([string]::IsNullOrWhiteSpace($ExePath)) {
        $ExePath = Join-Path $repositoryRoot (
            "src\BackdropForCodex.App\bin\$Configuration\" +
            "net10.0-windows10.0.22000.0\win-x64\BackdropForCodex.exe")
    }

    $resolvedExePath = [IO.Path]::GetFullPath($ExePath)
    if (-not [IO.File]::Exists($resolvedExePath)) {
        throw "Compiled application was not found: $resolvedExePath"
    }

    $existing = @(Get-MatchingProcesses -ResolvedExePath $resolvedExePath)
    if ($AttachExisting) {
        if ($ProbeBeforeClose) {
            throw "ProbeBeforeClose cannot be combined with AttachExisting."
        }

        if ($existing.Count -ne 1) {
            throw (
                "AttachExisting requires exactly one matching process; " +
                "found $($existing.Count).")
        }

        $script:launchedProcess = $existing[0]
        Write-Step (
            "Attached read-only to PID $($script:launchedProcess.Id); " +
            "the harness will not terminate it.")
    }
    else {
        if ($existing.Count -ne 0) {
            $processIds = ($existing | ForEach-Object Id) -join ", "
            throw (
                "A matching instance already exists (PID $processIds). " +
                "Close it explicitly before running the launch lifecycle test.")
        }

        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $resolvedExePath
        $startInfo.WorkingDirectory = Split-Path -Parent $resolvedExePath
        $startInfo.UseShellExecute = $true
        $script:launchedProcess = [Diagnostics.Process]::Start($startInfo)
        $script:ownsProcess = $true
        if ($null -eq $script:launchedProcess) {
            throw "Starting the application did not return a process."
        }

        Write-Step "Started PID $($script:launchedProcess.Id)."
        $windowOpened = Wait-Until -Seconds $TimeoutSeconds -Condition {
            $script:launchedProcess.Refresh()
            -not $script:launchedProcess.HasExited -and
                $script:launchedProcess.MainWindowHandle -ne [IntPtr]::Zero
        }
        if (-not $windowOpened) {
            throw "The main window did not become visible within the timeout."
        }

        if ($ProbeBeforeClose) {
            try {
                $beforeCloseIconPresent = Test-TrayIconPresent
            }
            finally {
                Close-NotificationOverflow
            }

            if ($beforeCloseIconPresent) {
                Write-Host "[tray-smoke] BEFORE_CLOSE=PASS" -ForegroundColor Green
            }
            else {
                Write-Host "[tray-smoke] BEFORE_CLOSE=FAIL" -ForegroundColor Red
            }
        }

        if (-not $script:launchedProcess.CloseMainWindow()) {
            throw "Windows could not deliver a close request to the main window."
        }

        $windowClosed = Wait-Until -Seconds $TimeoutSeconds -Condition {
            $script:launchedProcess.Refresh()
            $script:launchedProcess.HasExited -or
                $script:launchedProcess.MainWindowHandle -eq [IntPtr]::Zero
        }
        if (-not $windowClosed) {
            throw "The main window did not close within the timeout."
        }

        Write-Step "Closed the main window through its native close request."
    }

    $script:launchedProcess.Refresh()
    if ($script:launchedProcess.HasExited) {
        throw "The application process exited after the main window closed."
    }

    if ($AttachExisting -and
        $script:launchedProcess.MainWindowHandle -ne [IntPtr]::Zero) {
        throw "The attached process still has a visible main window."
    }

    Write-Step "Process-liveness assertion passed."
    try {
        $afterCloseIconPresent = Test-TrayIconPresent
    }
    finally {
        Close-NotificationOverflow
    }

    if ($ProbeBeforeClose) {
        if ($afterCloseIconPresent) {
            Write-Host "[tray-smoke] AFTER_CLOSE=PASS" -ForegroundColor Green
        }
        else {
            Write-Host "[tray-smoke] AFTER_CLOSE=FAIL" -ForegroundColor Red
        }
    }

    if (-not $afterCloseIconPresent) {
        throw (
            "Backdrop for Codex was not discoverable in the Windows " +
            "notification area or its hidden-icons panel.")
    }

    if ($ProbeBeforeClose -and -not $beforeCloseIconPresent) {
        throw (
            "Backdrop for Codex was discoverable after close but not while " +
            "the main window was visible.")
    }

    $elapsed = [DateTime]::UtcNow - $startedAt
    Write-Host (
        "[tray-smoke] PASS: process survived window close and the tray icon " +
        "was discoverable ($([Math]::Round($elapsed.TotalSeconds, 2)) s).") `
        -ForegroundColor Green
}
catch {
    $elapsed = [DateTime]::UtcNow - $startedAt
    Write-Host (
        "[tray-smoke] FAIL: $($_.Exception.Message) " +
        "($([Math]::Round($elapsed.TotalSeconds, 2)) s).") `
        -ForegroundColor Red
    exit 1
}
finally {
    try {
        Close-NotificationOverflow
    }
    catch {
        Write-Warning "Notification-area cleanup failed: $($_.Exception.Message)"
    }

    if ($script:ownsProcess -and $null -ne $script:launchedProcess) {
        try {
            $script:launchedProcess.Refresh()
            if (-not $script:launchedProcess.HasExited) {
                Stop-Process -Id $script:launchedProcess.Id -Force
                Write-Step "Stopped only the process launched by this harness."
            }
        }
        catch {
            Write-Warning (
                "The harness could not clean up PID " +
                "$($script:launchedProcess.Id): $($_.Exception.Message)")
        }
    }
}
