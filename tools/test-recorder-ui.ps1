param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Add-Type -AssemblyName UIAutomationClient
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SevenRecordUiQaNative
{
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(
        uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
"@

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $repositoryRoot "src\SevenRecord.App\bin\$Configuration"
$appExecutable = Get-ChildItem `
    -Path $outputRoot `
    -Filter "SevenRecord.App.exe" `
    -File `
    -Recurse |
    Where-Object FullName -NotMatch "\\(AppX|publish)\\" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $appExecutable) {
    throw "SevenRecord.App.exe was not found. Build the solution first."
}

function Wait-Until {
    param(
        [scriptblock]$Condition,
        [int]$TimeoutSeconds,
        [string]$FailureMessage
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $result = & $Condition
        if ($result) {
            return $result
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    throw $FailureMessage
}

function Find-ById {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Find-ByName {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Click-Element {
    param([System.Windows.Automation.AutomationElement]$Element)

    $process = Get-Process -Id $Element.Current.ProcessId
    [SevenRecordUiQaNative]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    $bounds = $Element.Current.BoundingRectangle
    [SevenRecordUiQaNative]::SetCursorPos(
        [int]($bounds.X + $bounds.Width / 2),
        [int]($bounds.Y + $bounds.Height / 2)) | Out-Null
    [SevenRecordUiQaNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [SevenRecordUiQaNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
}

function Select-NavigationItem {
    param([System.Windows.Automation.AutomationElement]$Element)

    $selection = [System.Windows.Automation.SelectionItemPattern]$Element.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
}

$process = $null
try {
    $process = Start-Process -FilePath $appExecutable.FullName -PassThru
    $process = Wait-Until -TimeoutSeconds 30 -FailureMessage "7Record did not open." -Condition {
        $candidate = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
        if ($candidate -and $candidate.MainWindowHandle -ne 0) {
            $candidate
        }
    }

    $app = [System.Windows.Automation.AutomationElement]::FromHandle(
        $process.MainWindowHandle)
    $record = Wait-Until -TimeoutSeconds 30 -FailureMessage "Record did not become ready." -Condition {
        $candidate = Find-ById -Root $app -AutomationId "StartRecordingButton"
        if ($candidate -and $candidate.Current.IsEnabled) { $candidate }
    }
    $status = Find-ById -Root $app -AutomationId "ReadinessInfoBar"
    $cameraToggle = Find-ById -Root $app -AutomationId "CameraOverlayToggle"
    $recorderNav = Find-ById -Root $app -AutomationId "RecorderNavigationItem"
    $projectsNav = Find-ById -Root $app -AutomationId "ProjectsNavigationItem"
    if (-not $recorderNav -or -not $projectsNav) {
        $togglePane = Find-ById -Root $app -AutomationId "TogglePaneButton"
        if ($togglePane) {
            $invoke = [System.Windows.Automation.InvokePattern]$togglePane.GetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern)
            $invoke.Invoke()
            Start-Sleep -Milliseconds 500
            $recorderNav = Find-ById -Root $app -AutomationId "RecorderNavigationItem"
            $projectsNav = Find-ById -Root $app -AutomationId "ProjectsNavigationItem"
        }
    }

    Assert-True ($record.Current.Name -eq "Start recording") `
        "Record has the wrong automation name: '$($record.Current.Name)'."
    Assert-True ($record.Current.BoundingRectangle.Height -ge 40) `
        "Record target is below 40 DIP."
    Assert-True ($recorderNav.Current.BoundingRectangle.Height -ge 40) `
        "Recorder navigation target is below 40 DIP."
    Assert-True ($projectsNav.Current.BoundingRectangle.Height -ge 40) `
        "Projects navigation target is below 40 DIP."
    Assert-True (-not [string]::IsNullOrWhiteSpace($status.Current.Name)) `
        "Recorder status has no accessible name."
    Assert-True ($status.Current.Name -ne "Recorder status") `
        "Recorder status accessible name is not dynamic."
    Assert-True ($cameraToggle.Current.Name -eq "Include camera overlay") `
        "Camera toggle has the wrong automation name."
    $recordHeight = $record.Current.BoundingRectangle.Height
    $recorderNavHeight = $recorderNav.Current.BoundingRectangle.Height
    $projectsNavHeight = $projectsNav.Current.BoundingRectangle.Height

    Select-NavigationItem $projectsNav
    $projectsList = Wait-Until -TimeoutSeconds 10 -FailureMessage "Projects workspace did not open." -Condition {
        Find-ById -Root $app -AutomationId "RecentProjectsList"
    }
    Assert-True ($projectsList.Current.Name -eq "Recent recordings") `
        "Projects list has the wrong accessible name."
    $projectPlayback = $false
    $projectItems = Wait-Until -TimeoutSeconds 15 -FailureMessage "Recent recording items did not load." -Condition {
        $items = $projectsList.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)
        if ($items.Count -gt 0) { $items }
    }
    if ($projectItems.Count -gt 0) {
        $firstProject = $projectItems.Item(0)
        $scrollItem = [System.Windows.Automation.ScrollItemPattern]$firstProject.GetCurrentPattern(
            [System.Windows.Automation.ScrollItemPattern]::Pattern)
        $scrollItem.ScrollIntoView()
        $projectElements = $firstProject.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        $openProject = $null
        for ($index = 0; $index -lt $projectElements.Count; $index++) {
            $candidate = $projectElements.Item($index)
            if ($candidate.Current.Name -like "Open Recording*") {
                $openProject = $candidate
                break
            }
        }
    } else {
        $openProject = $null
    }
    if ($null -ne $openProject) {
        $invoke = [System.Windows.Automation.InvokePattern]$openProject.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)
        $invoke.Invoke()
        $openExternal = Wait-Until -TimeoutSeconds 15 -FailureMessage "Recording preview did not open." -Condition {
            $candidate = Find-ById -Root $app -AutomationId "OpenRecordingExternallyButton"
            if ($candidate -and $candidate.Current.IsEnabled) { $candidate }
        }
        $playButton = Wait-Until -TimeoutSeconds 15 -FailureMessage "Recording preview transport controls did not load." -Condition {
            Find-ById -Root $app -AutomationId "PlayPauseButton"
        }
        Assert-True ($playButton.Current.IsEnabled) `
            "Recording preview Play control is disabled."
        $projectPlayback = $openExternal.Current.IsEnabled
    }
    if ($projectItems.Count -gt 0) {
        Assert-True $projectPlayback `
            "A recent recording existed but its playback controls did not open."
    }
    Select-NavigationItem $recorderNav
    Wait-Until -TimeoutSeconds 10 -FailureMessage "Recorder workspace did not reopen." -Condition {
        Find-ById -Root $app -AutomationId "StartRecordingButton"
    } | Out-Null

    [SevenRecordUiQaNative]::SetWindowPos(
        $process.MainWindowHandle,
        [IntPtr]::Zero,
        80,
        40,
        1024,
        720,
        0x0040) | Out-Null
    Start-Sleep -Seconds 2

    $chooseSource = Find-ById -Root $app -AutomationId "ChooseSourceButton"
    $status = Find-ById -Root $app -AutomationId "ReadinessInfoBar"
    Assert-True (
        $status.Current.BoundingRectangle.Y -gt
        $chooseSource.Current.BoundingRectangle.Bottom) `
        "Recorder source/health rail did not stack below the preview at 1024x720."

    [PSCustomObject]@{
        passed = $true
        recordTargetHeight = [Math]::Round($recordHeight, 1)
        recorderNavHeight = [Math]::Round($recorderNavHeight, 1)
        projectsNavHeight = [Math]::Round($projectsNavHeight, 1)
        statusName = $status.Current.Name
        adaptiveStacking = $true
        projectsNavigation = $true
        projectPlayback = $projectPlayback
    } | ConvertTo-Json
}
finally {
    if ($process) {
        $running = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
        if ($running) {
            Stop-Process -Id $process.Id
        }
    }
}
