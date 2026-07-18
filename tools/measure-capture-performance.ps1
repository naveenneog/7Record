param(
    [int]$DurationSeconds = 15,
    [string]$Configuration = "Debug",
    [string]$SourceWindowTitle = "Android Emulator - actioncut_test:5554",
    [switch]$AttachExisting,
    [switch]$UseExistingSelection,
    [switch]$PreserveSourceSize,
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SevenRecordNativeMouse
{
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(
        uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
"@

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$appOutputRoot = Join-Path $repositoryRoot "src\SevenRecord.App\bin\$Configuration"
$targetTitle = $SourceWindowTitle
$runStartedAt = Get-Date
$sourceProcess = $null
$sourceElement = $null
$sourceWindowHandle = [IntPtr]::Zero
$sourceRect = New-Object SevenRecordNativeMouse+RECT
$appLauncher = $null
$appProcess = $null

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

function Find-DescendantByAutomationId {
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

function Invoke-Element {
    param([System.Windows.Automation.AutomationElement]$Element)

    try {
        $pattern = [System.Windows.Automation.InvokePattern]$Element.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)
        $pattern.Invoke()
    }
    catch {
        throw "Could not invoke '$($Element.Current.Name)' ($($Element.Current.AutomationId)): $($_.Exception.Message)"
    }
}

function Click-Element {
    param([System.Windows.Automation.AutomationElement]$Element)

    $process = Get-Process -Id $Element.Current.ProcessId
    [SevenRecordNativeMouse]::ShowWindow(
        [IntPtr]$process.MainWindowHandle,
        9) | Out-Null
    [SevenRecordNativeMouse]::SetForegroundWindow(
        [IntPtr]$process.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 300
    $bounds = $Element.Current.BoundingRectangle
    $x = [int]($bounds.X + $bounds.Width / 2)
    $y = [int]($bounds.Y + $bounds.Height / 2)
    [SevenRecordNativeMouse]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 100
    [SevenRecordNativeMouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [SevenRecordNativeMouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
}

function Get-CapturePickerWindows {
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $windows = $desktop.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    $pickers = for ($index = 0; $index -lt $windows.Count; $index++) {
        $window = $windows.Item($index)
        if ($window.Current.Name -eq "Capture with 7Record") {
            $window
        }
    }

    return @($pickers)
}

function Close-CapturePickers {
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        $pickers = @(Get-CapturePickerWindows)
        if ($pickers.Count -eq 0) {
            return
        }

        foreach ($picker in $pickers) {
            $cancel = Find-DescendantByAutomationId -Root $picker -AutomationId "CancelButton"
            if ($cancel -and $cancel.Current.IsEnabled) {
                Invoke-Element $cancel
                break
            }
        }

        Start-Sleep -Seconds 1
    }

    throw "A stale capture picker could not be closed."
}

function Get-CaptureProcesses {
    Get-Process |
        Where-Object {
            ($null -ne $appProcess -and $_.Id -eq $appProcess.Id) -or
            ($_.StartTime -ge $runStartedAt.AddSeconds(-2) -and
                $_.ProcessName -in @("SevenRecord.Media.Worker", "ffmpeg"))
        }
}

function Get-CpuSnapshot {
    $snapshot = @{}
    foreach ($process in Get-CaptureProcesses) {
        $snapshot[$process.Id] = [double]$process.CPU
    }

    return $snapshot
}

try {
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $sourceElement = Wait-Until -TimeoutSeconds 10 -FailureMessage "Source window '$targetTitle' is not open." -Condition {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $targetTitle)
        $desktop.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
    }
    $sourceProcess = Get-Process -Id $sourceElement.Current.ProcessId
    $sourceWindowHandle = [IntPtr]$sourceElement.Current.NativeWindowHandle
    [SevenRecordNativeMouse]::GetWindowRect(
        $sourceWindowHandle,
        [ref]$sourceRect) | Out-Null

    if (-not $AttachExisting) {
        $appExecutable = Get-ChildItem `
            -Path $appOutputRoot `
            -Filter "SevenRecord.App.exe" `
            -File `
            -Recurse |
            Where-Object FullName -NotMatch "\\(AppX|publish)\\" |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if (-not $appExecutable) {
            throw "SevenRecord.App.exe was not found under '$appOutputRoot'. Build the solution first."
        }

        $appLauncher = Start-Process `
            -FilePath $appExecutable.FullName `
            -WorkingDirectory $repositoryRoot `
            -PassThru
    }

    $appProcess = Wait-Until -TimeoutSeconds 150 -FailureMessage "7Record did not open." -Condition {
        Get-Process |
            Where-Object {
                $_.ProcessName -eq "SevenRecord.App" -and
                $_.MainWindowHandle -ne 0 -and
                ($AttachExisting -or $_.StartTime -ge $runStartedAt)
            } |
            Sort-Object StartTime -Descending |
            Select-Object -First 1
    }

    $app = [System.Windows.Automation.AutomationElement]::FromHandle($appProcess.MainWindowHandle)
    $startRecording = Find-DescendantByAutomationId `
        -Root $app `
        -AutomationId "StartRecordingButton"
    Wait-Until -TimeoutSeconds 30 -FailureMessage "7Record readiness checks did not finish." -Condition {
        $startRecording.Current.IsEnabled
    } | Out-Null
    Close-CapturePickers
    [SevenRecordNativeMouse]::ShowWindow(
        [IntPtr]$appProcess.MainWindowHandle,
        9) | Out-Null
    [SevenRecordNativeMouse]::SetForegroundWindow(
        [IntPtr]$appProcess.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 500
    if (-not $UseExistingSelection) {
        $chooseSource = Find-DescendantByAutomationId -Root $app -AutomationId "ChooseSourceButton"
        Wait-Until -TimeoutSeconds 15 -FailureMessage "Choose source button did not become available." -Condition {
            $chooseSource.Current.IsEnabled
        } | Out-Null
        for ($attempt = 0; $attempt -lt 3; $attempt++) {
            Click-Element $chooseSource
            Start-Sleep -Seconds 2
            if (@(Get-CapturePickerWindows).Count -gt 0) {
                break
            }
        }

        $picker = Wait-Until -TimeoutSeconds 30 -FailureMessage "Windows capture picker did not open." -Condition {
            (Get-CapturePickerWindows | Select-Object -First 1)
        }

        $listCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)
        $listItems = Wait-Until -TimeoutSeconds 15 -FailureMessage "Capture picker targets did not load." -Condition {
            $items = $picker.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                $listCondition)
            if ($items.Count -gt 1) { $items } else { $null }
        }
        $target = $null
        for ($index = 0; $index -lt $listItems.Count; $index++) {
            $candidate = $listItems.Item($index)
            if ($candidate.Current.Name -like "$targetTitle*") {
                $target = $candidate
                break
            }
        }
        if (-not $target) {
            $availableNames = for ($index = 0; $index -lt $listItems.Count; $index++) {
                $listItems.Item($index).Current.Name
            }
            throw "Benchmark source was not listed in the capture picker. Available: $($availableNames -join ' | ')"
        }

        $selection = [System.Windows.Automation.SelectionItemPattern]$target.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern)
        $selection.Select()
        $accept = Find-DescendantByAutomationId -Root $picker -AutomationId "AcceptButton"
        Invoke-Element $accept
        Wait-Until -TimeoutSeconds 10 -FailureMessage "Windows capture picker did not close." -Condition {
            @(Get-CapturePickerWindows).Count -eq 0
        } | Out-Null
    }

    try {
        Wait-Until -TimeoutSeconds 20 -FailureMessage "Recording button did not become ready." -Condition {
            $startRecording.Current.IsEnabled
        } | Out-Null
    }
    catch {
        $screenStatus = Find-DescendantByAutomationId -Root $app -AutomationId "ScreenStatusText"
        $textCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text)
        $textItems = $app.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $textCondition)
        $textNames = for ($index = 0; $index -lt $textItems.Count; $index++) {
            $textItems.Item($index).Current.Name
        }
        throw "Recording button did not become ready. Screen: '$($screenStatus.Current.Name)'. UI: $($textNames -join ' | ')"
    }
    if (-not $PreserveSourceSize) {
        [SevenRecordNativeMouse]::SetWindowPos(
            $sourceWindowHandle,
            [IntPtr]::Zero,
            0,
            0,
            1080,
            1920,
            0x0044) | Out-Null
        Start-Sleep -Seconds 1
    }
    Click-Element $startRecording
    $recordingHealth = Find-DescendantByAutomationId `
        -Root $app `
        -AutomationId "RecordingHealthExpander"
    if ($recordingHealth) {
        $expandPattern = [System.Windows.Automation.ExpandCollapsePattern]$recordingHealth.GetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($expandPattern.Current.ExpandCollapseState -eq
            [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
            $expandPattern.Expand()
        }
    }
    $frameStatus = Wait-Until -TimeoutSeconds 10 -FailureMessage "Recording frame status did not become available." -Condition {
        Find-DescendantByAutomationId -Root $app -AutomationId "FrameStatusText"
    }

    Wait-Until -TimeoutSeconds 45 -FailureMessage "Capture did not deliver a first frame." -Condition {
        $frameStatus.Current.Name -match "^\d[\d,]* frames"
    } | Out-Null

    $captureStartedAt = Get-Date
    $cpuStart = Get-CpuSnapshot
    $sourceRoot = $sourceElement
    $scrollableCondition = New-Object System.Windows.Automation.OrCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::DataGrid)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::List)))
    $scrollable = $sourceRoot.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $scrollableCondition)
    if ($scrollable) {
        try {
            $scrollable.SetFocus()
        }
        catch {
            $scrollable = $null
        }
    }

    $animationDeadline = (Get-Date).AddSeconds($DurationSeconds)
    $scrollDown = $true
    while ((Get-Date) -lt $animationDeadline) {
        [SevenRecordNativeMouse]::SetForegroundWindow(
            $sourceWindowHandle) | Out-Null
        [System.Windows.Forms.SendKeys]::SendWait(
            $(if ($scrollDown) { "{PGDN}" } else { "{HOME}" }))
        $scrollDown = -not $scrollDown
        Start-Sleep -Milliseconds 100
    }
    $captureEndedAt = Get-Date
    $cpuEnd = Get-CpuSnapshot
    $statusDuringCapture = $frameStatus.Current.Name
    $workingSetBytes = (Get-CaptureProcesses | Measure-Object WorkingSet64 -Sum).Sum

    Click-Element $startRecording
    Wait-Until -TimeoutSeconds 60 -FailureMessage "Segment did not finalize." -Condition {
        $startRecording.Current.Name -eq "Start recording"
    } | Out-Null
    $statusAfterStop = $frameStatus.Current.Name

    $framesReceived = 0
    $framesDropped = 0
    if ($statusDuringCapture -match "^(?<received>[\d,]+) frames, (?<dropped>[\d,]+) dropped") {
        $framesReceived = [int](($Matches.received) -replace ",", "")
        $framesDropped = [int](($Matches.dropped) -replace ",", "")
    }

    $cpuSeconds = 0d
    foreach ($id in $cpuEnd.Keys) {
        $startValue = if ($cpuStart.ContainsKey($id)) { $cpuStart[$id] } else { 0d }
        $cpuSeconds += $cpuEnd[$id] - $startValue
    }

    $wallSeconds = ($captureEndedAt - $captureStartedAt).TotalSeconds
    $coreEquivalentPercent = $cpuSeconds / $wallSeconds * 100d
    $machineCpuPercent = $coreEquivalentPercent / [Environment]::ProcessorCount

    $projectsRoot = Join-Path `
        ([Environment]::GetFolderPath([Environment+SpecialFolder]::MyVideos)) `
        "7Record\Projects"
    $project = Get-ChildItem $projectsRoot -Directory |
        Where-Object LastWriteTime -ge $runStartedAt |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $project) {
        throw "No benchmark project was created."
    }

    $segment = Get-ChildItem (Join-Path $project.FullName "segments\screen") -Recurse -File |
        Where-Object Extension -in @(".mkv", ".mp4") |
        Select-Object -First 1
    if (-not $segment) {
        throw "No encoded benchmark segment was created."
    }
    $probe = ffprobe `
        -v error `
        -show_entries "stream=codec_name,width,height,avg_frame_rate" `
        -show_entries "format=duration,size" `
        -of json `
        $segment.FullName | ConvertFrom-Json

    $result = [PSCustomObject]@{
        capturedAt = $captureStartedAt.ToString("o")
        durationSeconds = [Math]::Round($wallSeconds, 3)
        framesReceived = $framesReceived
        framesDropped = $framesDropped
        captureFramesPerSecond = [Math]::Round($framesReceived / $wallSeconds, 2)
        dropRate = if ($framesReceived -eq 0) { 0 } else { [Math]::Round($framesDropped / $framesReceived, 6) }
        cpuCoreEquivalentPercent = [Math]::Round($coreEquivalentPercent, 2)
        cpuMachinePercent = [Math]::Round($machineCpuPercent, 2)
        workingSetMegabytes = [Math]::Round($workingSetBytes / 1MB, 1)
        codec = $probe.streams[0].codec_name
        width = $probe.streams[0].width
        height = $probe.streams[0].height
        encodedFrameRate = $probe.streams[0].avg_frame_rate
        containerDurationSeconds = [double]$probe.format.duration
        durationErrorMilliseconds = [Math]::Round(([double]$probe.format.duration - $wallSeconds) * 1000d, 1)
        segmentBytes = [long]$probe.format.size
        projectPath = $project.FullName
        status = $statusAfterStop
    }
    $json = $result | ConvertTo-Json -Depth 4
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        $fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
        New-Item -ItemType Directory -Path (Split-Path -Parent $fullOutputPath) -Force | Out-Null
        Set-Content -Path $fullOutputPath -Value $json
    }
    $json
}
finally {
    Close-CapturePickers
    $processesToStop = if ($AttachExisting) { @() } else { @($appProcess, $appLauncher) }
    foreach ($process in $processesToStop) {
        if ($null -ne $process) {
            $running = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
            if ($running) {
                Stop-Process -Id $process.Id
            }
        }
    }
    if (-not $AttachExisting) {
        $lateAppProcesses = Get-Process |
            Where-Object {
                $_.ProcessName -eq "SevenRecord.App" -and
                $_.StartTime -ge $runStartedAt
            }
        foreach ($lateAppProcess in $lateAppProcesses) {
            Stop-Process -Id $lateAppProcess.Id -ErrorAction SilentlyContinue
        }
    }
    if (-not $PreserveSourceSize -and $sourceWindowHandle -ne [IntPtr]::Zero) {
        [SevenRecordNativeMouse]::SetWindowPos(
            $sourceWindowHandle,
            [IntPtr]::Zero,
            $sourceRect.Left,
            $sourceRect.Top,
            $sourceRect.Right - $sourceRect.Left,
            $sourceRect.Bottom - $sourceRect.Top,
            0x0044) | Out-Null
    }
}
