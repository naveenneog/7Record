using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SevenRecord.Analysis;
using SevenRecord.Audio.Windows;
using SevenRecord.Capture.Abstractions;
using SevenRecord.Capture.Windows;
using SevenRecord.Camera.Windows;
using SevenRecord.Domain.Captions;
using SevenRecord.Domain.Input;
using SevenRecord.Domain.Projects;
using SevenRecord.Domain.Timeline;
using SevenRecord.Editor;
using SevenRecord.Export;
using SevenRecord.Infrastructure;
using SevenRecord.Input.Windows;
using SevenRecord.Media;
using SevenRecord.Recording;
using SevenRecord.Transcription;

namespace SevenRecord.App;

public sealed partial class MainPage : Page
{
    private static readonly JsonSerializerOptions AudioRepairSerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions RenderPlanSerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly CaptureReadinessService _readinessService = new(
    [
        new WindowsCaptureReadinessProbe(
            new CaptureReadinessSelection(
                RequireCamera: false,
                RequireMicrophone: true,
                RequireSystemAudio: true)),
        new StorageReadinessProbe(),
        new EncoderReadinessProbe(),
    ]);
    private WindowsScreenCaptureSession? _captureSession;
    private string? _activeProjectRoot;
    private RecoverableAudioRecordingSession? _audioRecorder;
    private RecoverableCameraRecordingSession? _cameraRecorder;
    private bool _cameraEnabled;
    private CursorMetadataRecorder? _cursorRecorder;
    private SurfaceScreenSegmentRecorder? _segmentRecorder;
    private CaptureReadinessSnapshot? _lastSnapshot;
    private WindowsCaptureTarget? _selectedScreen;
    private TimelineDocument? _currentTimeline;
    private CaptionEditSession? _captionEditSession;
    private readonly HashSet<string> _disabledAutomation =
        new(StringComparer.Ordinal);

    public MainPage()
    {
        InitializeComponent();
    }

    private async void OnConfigureCameraClicked(object sender, RoutedEventArgs e)
    {
        ConfigureCameraButton.IsEnabled = false;
        CameraStatusText.Text = "Testing camera frames...";
        string probeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "7Record",
            "CameraProbe",
            Guid.NewGuid().ToString("N"));

        try
        {
            ProjectClock clock = ProjectClock.StartNew();
            await using RecoverableCameraRecordingSession camera =
                await RecoverableCameraRecordingSession.CreateAsync(probeRoot, clock);
            _cameraEnabled = true;
            CameraStatusText.Text =
                $"{camera.DeviceName} ({camera.Width} x {camera.Height}) is ready.";
        }
        catch (Exception exception)
        {
            _cameraEnabled = false;
            CameraStatusText.Text = $"Camera unavailable: {exception.Message}";
        }
        finally
        {
            if (Directory.Exists(probeRoot))
            {
                Directory.Delete(probeRoot, recursive: true);
            }

            ConfigureCameraButton.IsEnabled = true;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshReadinessAsync();
        await RefreshProjectsAsync();
    }

    private async void OnRefreshReadinessClicked(object sender, RoutedEventArgs e)
    {
        await RefreshReadinessAsync();
    }

    private void OnProjectsClicked(object sender, RoutedEventArgs e) =>
        ProjectsSection.StartBringIntoView();

    private async void OnRefreshProjectsClicked(object sender, RoutedEventArgs e)
    {
        await RefreshProjectsAsync();
    }

    private async void OnProjectItemClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ListViewItem { Tag: string projectPath })
        {
            return;
        }

        try
        {
            TimelineDocument timeline = await ProjectTimelineLoader.LoadAsync(projectPath);
            _currentTimeline = timeline;
            _disabledAutomation.Clear();
            await LoadCaptionEditorAsync(projectPath);
            TimelineProjectTitle.Text =
                $"{Path.GetFileName(projectPath)}  |  {timeline.Duration:hh\\:mm\\:ss}";
            TimelineItemsList.Items.Clear();
            foreach (TimelineClip clip in timeline.Clips)
            {
                TimelineItemsList.Items.Add(
                    $"{clip.Track}  |  {clip.Range.Start:hh\\:mm\\:ss\\.fff} - " +
                    $"{clip.Range.End:hh\\:mm\\:ss\\.fff}  |  {clip.SourcePath}");
            }

            foreach (TimelineAutomationEvent automation in timeline.Automation)
            {
                CheckBox toggle = new()
                {
                    Content =
                        $"Automation -> {automation.TargetTrack}  |  {automation.Kind}  |  " +
                        automation.Description,
                    IsChecked = true,
                    Tag = automation.Id,
                };
                toggle.Checked += OnAutomationToggled;
                toggle.Unchecked += OnAutomationToggled;
                TimelineItemsList.Items.Add(toggle);
            }
            foreach (TimelineCaption caption in timeline.Captions)
            {
                TimelineItemsList.Items.Add(
                    $"Caption  |  {caption.Range.Start:hh\\:mm\\:ss\\.fff} - " +
                    $"{caption.Range.End:hh\\:mm\\:ss\\.fff}  |  {caption.Text}");
            }

            UpdateRenderPlanSummary();
            TimelineSection.Visibility = Visibility.Visible;
            TimelineSection.StartBringIntoView();
        }
        catch (Exception exception)
        {
            TimelineProjectTitle.Text = $"Timeline could not be loaded: {exception.Message}";
            TimelineSection.Visibility = Visibility.Visible;
        }
    }

    private void OnAutomationToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string automationId } toggle)
        {
            return;
        }

        if (toggle.IsChecked is true)
        {
            _disabledAutomation.Remove(automationId);
        }
        else
        {
            _disabledAutomation.Add(automationId);
        }

        UpdateRenderPlanSummary();
    }

    private void OnRenderPresetChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateRenderPlanSummary();

    private async void OnSaveRenderPlanClicked(object sender, RoutedEventArgs e)
    {
        if (_currentTimeline is null)
        {
            return;
        }

        _ = await SaveRenderPlanAsync();
        RenderPlanSummaryText.Text += " Saved to render-plan.json.";
    }

    private async void OnExportMp4Clicked(object sender, RoutedEventArgs e)
    {
        if (_currentTimeline is null)
        {
            return;
        }

        ExportMp4Button.IsEnabled = false;
        try
        {
            string renderPlanPath = await SaveRenderPlanAsync();
            string exportsDirectory = Path.Combine(
                _currentTimeline.ProjectPath,
                "exports");
            Directory.CreateDirectory(exportsDirectory);
            string outputPath = Path.Combine(
                exportsDirectory,
                $"7record-{CurrentRenderPlan().Preset.ToString().ToLowerInvariant()}.mp4");
            string workerPath = Path.Combine(
                AppContext.BaseDirectory,
                "MediaWorker",
                "SevenRecord.Media.Worker.exe");
            RenderPlanSummaryText.Text = "Exporting MP4...";
            RenderPlanExportResult result = await MediaWorkerExportClient.ExportAsync(
                workerPath,
                renderPlanPath,
                outputPath);
            RenderPlanSummaryText.Text = result.Succeeded
                ? $"Export complete: {result.OutputPath}"
                : $"Export failed: {result.Error}";
        }
        finally
        {
            ExportMp4Button.IsEnabled = true;
        }
    }

    private async void OnGenerateCaptionsClicked(object sender, RoutedEventArgs e)
    {
        if (_currentTimeline is null)
        {
            return;
        }

        TimelineClip? microphone = _currentTimeline.Clips.FirstOrDefault(
            clip => clip.Track is TimelineTrackKind.Microphone);
        if (microphone is null)
        {
            RenderPlanSummaryText.Text = "Caption generation requires a microphone track.";
            return;
        }

        GenerateCaptionsButton.IsEnabled = false;
        try
        {
            RenderPlanSummaryText.Text =
                "Generating captions locally. The first run downloads the Whisper tiny model.";
            string modelPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "7Record",
                "Models",
                "ggml-tiny.bin");
            string audioPath = Path.Combine(
                _currentTimeline.ProjectPath,
                microphone.SourcePath);
            CaptionDocument captions = await Task.Run(
                async () => await LocalWhisperTranscriber.TranscribeAsync(
                    audioPath,
                    modelPath,
                    "auto"));
            string captionPath = Path.Combine(
                _currentTimeline.ProjectPath,
                "captions.json");
            string temporaryPath = captionPath + ".tmp";
            string json = JsonSerializer.Serialize(
                captions,
                RenderPlanSerializerOptions);
            await File.WriteAllTextAsync(temporaryPath, json);
            File.Move(temporaryPath, captionPath, overwrite: true);
            await File.WriteAllTextAsync(
                Path.Combine(_currentTimeline.ProjectPath, "captions.srt"),
                CaptionFormatter.ToSrt(captions));
            await File.WriteAllTextAsync(
                Path.Combine(_currentTimeline.ProjectPath, "captions.vtt"),
                CaptionFormatter.ToVtt(captions));

            TimelineDocument refreshed =
                await ProjectTimelineLoader.LoadAsync(_currentTimeline.ProjectPath);
            _currentTimeline = refreshed;
            _captionEditSession = new CaptionEditSession(captions);
            PopulateCaptionEditor();
            for (int index = TimelineItemsList.Items.Count - 1; index >= 0; index--)
            {
                if (TimelineItemsList.Items[index] is string item &&
                    item.StartsWith("Caption  |", StringComparison.Ordinal))
                {
                    TimelineItemsList.Items.RemoveAt(index);
                }
            }

            foreach (TimelineCaption caption in refreshed.Captions)
            {
                TimelineItemsList.Items.Add(
                    $"Caption  |  {caption.Range.Start:hh\\:mm\\:ss\\.fff} - " +
                    $"{caption.Range.End:hh\\:mm\\:ss\\.fff}  |  {caption.Text}");
            }

            RenderPlanSummaryText.Text =
                $"Generated {captions.Segments.Count} caption segment(s); SRT and VTT saved.";
        }
        catch (Exception exception)
        {
            RenderPlanSummaryText.Text = $"Caption generation failed: {exception.Message}";
        }
        finally
        {
            GenerateCaptionsButton.IsEnabled = true;
        }
    }

    private void OnCaptionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_captionEditSession is null ||
            CaptionSelectorComboBox.SelectedItem is not ComboBoxItem { Tag: string id })
        {
            return;
        }

        CaptionSegment segment = _captionEditSession.Current.Segments
            .Single(item => item.Id == id);
        CaptionTextBox.Text = segment.Text;
        CaptionStartNumberBox.Value = segment.Start.TotalSeconds;
        CaptionEndNumberBox.Value = segment.End.TotalSeconds;
    }

    private async void OnApplyCaptionEditClicked(object sender, RoutedEventArgs e)
    {
        if (_captionEditSession is null ||
            CaptionSelectorComboBox.SelectedItem is not ComboBoxItem { Tag: string id })
        {
            return;
        }

        try
        {
            _captionEditSession.UpdateCaption(
                id,
                CaptionTextBox.Text,
                TimeSpan.FromSeconds(CaptionStartNumberBox.Value),
                TimeSpan.FromSeconds(CaptionEndNumberBox.Value));
            await PersistCaptionEditsAsync(id);
        }
        catch (Exception exception)
        {
            RenderPlanSummaryText.Text = $"Caption edit failed: {exception.Message}";
        }
    }

    private async void OnUndoCaptionClicked(object sender, RoutedEventArgs e)
    {
        if (_captionEditSession?.Undo() is true)
        {
            await PersistCaptionEditsAsync();
        }
    }

    private async void OnRedoCaptionClicked(object sender, RoutedEventArgs e)
    {
        if (_captionEditSession?.Redo() is true)
        {
            await PersistCaptionEditsAsync();
        }
    }

    private void UpdateRenderPlanSummary()
    {
        if (_currentTimeline is null ||
            RenderPlanSummaryText is null ||
            RenderPresetComboBox is null)
        {
            return;
        }

        RenderPlan plan = CurrentRenderPlan();
        RenderPlanSummaryText.Text =
            $"{plan.Canvas.Width} × {plan.Canvas.Height}; " +
            $"{plan.Clips.Count} source clips; " +
            $"{plan.Automation.Count} enabled automation events; " +
            $"{_currentTimeline.Captions.Count} captions.";
    }

    private async Task LoadCaptionEditorAsync(string projectPath)
    {
        string path = Path.Combine(projectPath, "captions.json");
        if (!File.Exists(path))
        {
            _captionEditSession = null;
            CaptionEditorPanel.Visibility = Visibility.Collapsed;
            return;
        }

        string json = await File.ReadAllTextAsync(path);
        CaptionDocument? document = JsonSerializer.Deserialize<CaptionDocument>(
            json,
            RenderPlanSerializerOptions);
        _captionEditSession = document is null ? null : new CaptionEditSession(document);
        PopulateCaptionEditor();
    }

    private void PopulateCaptionEditor(string? selectedId = null)
    {
        CaptionSelectorComboBox.Items.Clear();
        if (_captionEditSession is null ||
            _captionEditSession.Current.Segments.Count == 0)
        {
            CaptionEditorPanel.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (CaptionSegment segment in _captionEditSession.Current.Segments)
        {
            CaptionSelectorComboBox.Items.Add(
                new ComboBoxItem
                {
                    Content =
                        $"{segment.Start:mm\\:ss\\.fff}  {segment.Text}",
                    Tag = segment.Id,
                });
        }

        int selectedIndex = selectedId is null
            ? 0
            : _captionEditSession.Current.Segments
                .Select((segment, index) => (segment, index))
                .FirstOrDefault(item => item.segment.Id == selectedId)
                .index;
        CaptionSelectorComboBox.SelectedIndex = Math.Clamp(
            selectedIndex,
            0,
            CaptionSelectorComboBox.Items.Count - 1);
        CaptionEditorPanel.Visibility = Visibility.Visible;
        UndoCaptionButton.IsEnabled = _captionEditSession.CanUndo;
        RedoCaptionButton.IsEnabled = _captionEditSession.CanRedo;
    }

    private async Task PersistCaptionEditsAsync(string? selectedId = null)
    {
        if (_captionEditSession is null || _currentTimeline is null)
        {
            return;
        }

        CaptionDocument document = _captionEditSession.Current;
        string path = Path.Combine(_currentTimeline.ProjectPath, "captions.json");
        string temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(document, RenderPlanSerializerOptions));
        File.Move(temporaryPath, path, overwrite: true);
        await File.WriteAllTextAsync(
            Path.Combine(_currentTimeline.ProjectPath, "captions.srt"),
            CaptionFormatter.ToSrt(document));
        await File.WriteAllTextAsync(
            Path.Combine(_currentTimeline.ProjectPath, "captions.vtt"),
            CaptionFormatter.ToVtt(document));

        _currentTimeline = await ProjectTimelineLoader.LoadAsync(
            _currentTimeline.ProjectPath);
        for (int index = TimelineItemsList.Items.Count - 1; index >= 0; index--)
        {
            if (TimelineItemsList.Items[index] is string item &&
                item.StartsWith("Caption  |", StringComparison.Ordinal))
            {
                TimelineItemsList.Items.RemoveAt(index);
            }
        }

        foreach (TimelineCaption caption in _currentTimeline.Captions)
        {
            TimelineItemsList.Items.Add(
                $"Caption  |  {caption.Range.Start:hh\\:mm\\:ss\\.fff} - " +
                $"{caption.Range.End:hh\\:mm\\:ss\\.fff}  |  {caption.Text}");
        }

        PopulateCaptionEditor(selectedId);
        UpdateRenderPlanSummary();
        RenderPlanSummaryText.Text += " Caption edits saved.";
    }

    private RenderPlan CurrentRenderPlan() =>
        RenderPlanBuilder.Build(
            _currentTimeline ??
                throw new InvalidOperationException("No timeline is selected."),
            RenderPresetComboBox.SelectedIndex switch
            {
                1 => ExportAspectRatioPreset.Portrait1080p,
                2 => ExportAspectRatioPreset.Square1080p,
                _ => ExportAspectRatioPreset.Landscape1080p,
            },
            _disabledAutomation);

    private async Task<string> SaveRenderPlanAsync()
    {
        RenderPlan plan = CurrentRenderPlan();
        string path = Path.Combine(plan.ProjectPath, "render-plan.json");
        string temporaryPath = path + ".tmp";
        string json = JsonSerializer.Serialize(plan, RenderPlanSerializerOptions);
        await File.WriteAllTextAsync(temporaryPath, json);
        File.Move(temporaryPath, path, overwrite: true);
        return path;
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        await StopCaptureAsync();
    }

    private async void OnStartRecordingClicked(object sender, RoutedEventArgs e)
    {
        if (_captureSession is not null)
        {
            await StopCaptureAsync();
            return;
        }

        if (_selectedScreen is null || _lastSnapshot?.CanRecord is not true)
        {
            UpdateReadinessSummary();
            return;
        }

        StartRecordingButton.IsEnabled = false;
        ReadinessInfoBar.Title = "Preparing encoder";
        ReadinessInfoBar.Message = "Validating the isolated media worker.";
        ReadinessInfoBar.Severity = InfoBarSeverity.Informational;

        SurfaceScreenSegmentRecorder? pendingSegmentRecorder = null;
        RecoverableAudioRecordingSession? pendingAudioRecorder = null;
        RecoverableCameraRecordingSession? pendingCameraRecorder = null;
        CursorMetadataRecorder? pendingCursorRecorder = null;
        try
        {
            string projectRoot = CreateProjectRoot();
            ProjectClock projectClock = ProjectClock.StartNew();
            try
            {
                pendingCursorRecorder = CursorMetadataRecorder.Start(projectClock);
            }
            catch (InvalidOperationException exception)
            {
                System.Diagnostics.Debug.WriteLine(exception);
            }
            pendingSegmentRecorder = await SurfaceScreenSegmentRecorder.CreateAsync(
                projectRoot,
                _selectedScreen.Width,
                _selectedScreen.Height);
            pendingAudioRecorder = RecoverableAudioRecordingSession.Start(
                projectRoot,
                projectClock);
            pendingAudioRecorder.HealthChanged += OnAudioHealthChanged;
            if (_cameraEnabled)
            {
                pendingCameraRecorder =
                    await RecoverableCameraRecordingSession.CreateAsync(
                        projectRoot,
                        projectClock);
            }
            WindowsScreenCaptureSession capture = WindowsScreenCaptureSession.Start(
                _selectedScreen.Item,
                projectClock,
                pendingSegmentRecorder.ProcessFrameAsync);
            capture.HealthChanged += OnCaptureHealthChanged;
            capture.CaptureClosed += OnCaptureClosed;
            capture.CaptureFailed += OnCaptureFailed;
            _segmentRecorder = pendingSegmentRecorder;
            _audioRecorder = pendingAudioRecorder;
            _cameraRecorder = pendingCameraRecorder;
            _cursorRecorder = pendingCursorRecorder;
            _activeProjectRoot = projectRoot;
            pendingSegmentRecorder = null;
            pendingAudioRecorder = null;
            pendingCameraRecorder = null;
            pendingCursorRecorder = null;
            _captureSession = capture;

            StartRecordingButton.Content = "Stop recording";
            StartRecordingButton.IsEnabled = true;
            ChooseSourceButton.IsEnabled = false;
            RefreshReadinessButton.IsEnabled = false;
            ReadinessInfoBar.Title = "Recording";
            ReadinessInfoBar.Message = "Capturing Direct3D surfaces with Media Foundation.";
            ReadinessInfoBar.Severity = InfoBarSeverity.Informational;
            FrameStatusText.Text = "Waiting for the first frame...";
        }
        catch (Exception exception)
        {
            if (pendingSegmentRecorder is not null)
            {
                await pendingSegmentRecorder.DisposeAsync();
            }
            if (pendingAudioRecorder is not null)
            {
                pendingAudioRecorder.HealthChanged -= OnAudioHealthChanged;
                await pendingAudioRecorder.DisposeAsync();
            }
            if (pendingCameraRecorder is not null)
            {
                await pendingCameraRecorder.DisposeAsync();
            }
            if (pendingCursorRecorder is not null)
            {
                await pendingCursorRecorder.DisposeAsync();
            }

            System.Diagnostics.Debug.WriteLine(exception);
            StartRecordingButton.IsEnabled = true;
            FrameStatusText.Text =
                $"Recording start failed: {exception.GetType().Name} " +
                $"0x{exception.HResult:X8}: {exception.Message}";
            ReadinessInfoBar.Title = "Recording could not start";
            ReadinessInfoBar.Message = exception.Message;
            ReadinessInfoBar.Severity = InfoBarSeverity.Error;
        }
    }

    private async void OnChooseSourceClicked(object sender, RoutedEventArgs e)
    {
        ChooseSourceButton.IsEnabled = false;

        try
        {
            App application = (App)Application.Current;
            nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(application.MainWindow);
            WindowsCaptureTarget? target = await WindowsCaptureSourcePicker.PickAsync(windowHandle);
            if (target is null)
            {
                return;
            }

            _selectedScreen = target;
            ScreenStatusText.Text = $"{target.DisplayName} ({target.Width} x {target.Height})";
            UpdateReadinessSummary();
        }
        catch (COMException exception)
        {
            ReadinessInfoBar.Title = "Screen selection failed";
            ReadinessInfoBar.Message = $"Windows capture picker failed (0x{exception.HResult:X8}).";
            ReadinessInfoBar.Severity = InfoBarSeverity.Error;
        }
        finally
        {
            ChooseSourceButton.IsEnabled = true;
        }
    }

    private async Task RefreshReadinessAsync()
    {
        try
        {
            RefreshReadinessButton.IsEnabled = false;
            StartRecordingButton.IsEnabled = false;
            ReadinessInfoBar.Title = "Preparing recorder";
            ReadinessInfoBar.Message = "Recording checks are running.";
            ReadinessInfoBar.Severity = InfoBarSeverity.Informational;

            CaptureReadinessSnapshot snapshot = await _readinessService.CheckAsync();
            _lastSnapshot = snapshot;

            if (_selectedScreen is null)
            {
                CaptureReadinessItem screen =
                    snapshot.Items.Single(item => item.Key == "screen");
                CaptureReadinessItem graphics =
                    snapshot.Items.Single(item => item.Key == "graphics-device");
                ScreenStatusText.Text = $"{screen.Message} {graphics.Message}";
            }

            ApplyStatus(CameraStatusText, snapshot.Items.Single(item => item.Key == "camera"));

            CaptureReadinessItem microphone = snapshot.Items.Single(item => item.Key == "microphone");
            CaptureReadinessItem systemAudio = snapshot.Items.Single(item => item.Key == "system-audio");
            AudioStatusText.Text = $"{microphone.Message} {systemAudio.Message}";

            ApplyStatus(StorageStatusText, snapshot.Items.Single(item => item.Key == "storage"));
            ApplyStatus(EncoderStatusText, snapshot.Items.Single(item => item.Key == "encoder"));

            UpdateReadinessSummary();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            StartRecordingButton.IsEnabled = false;
            ReadinessInfoBar.Title = "Readiness check failed";
            ReadinessInfoBar.Message = exception.Message;
            ReadinessInfoBar.Severity = InfoBarSeverity.Error;
        }
        finally
        {
            RefreshReadinessButton.IsEnabled = true;
        }
    }

    private static void ApplyStatus(TextBlock textBlock, CaptureReadinessItem item)
    {
        textBlock.Text = item.Message;
    }

    private void UpdateReadinessSummary()
    {
        if (_captureSession is not null)
        {
            return;
        }

        if (_lastSnapshot is null)
        {
            StartRecordingButton.IsEnabled = false;
            return;
        }

        bool canRecord = _lastSnapshot.CanRecord && _selectedScreen is not null;
        StartRecordingButton.IsEnabled = canRecord;

        if (!_lastSnapshot.CanRecord)
        {
            ReadinessInfoBar.Title = "Recorder needs attention";
            ReadinessInfoBar.Message = string.Join(
                " ",
                _lastSnapshot.BlockingItems.Select(item => item.Message));
            ReadinessInfoBar.Severity = InfoBarSeverity.Error;
            return;
        }

        if (_selectedScreen is null)
        {
            ReadinessInfoBar.Title = "Choose what to record";
            ReadinessInfoBar.Message = "Select a display or application window before recording.";
            ReadinessInfoBar.Severity = InfoBarSeverity.Warning;
            return;
        }

        ReadinessInfoBar.Title = "Ready to record";
        ReadinessInfoBar.Message = "The selected source, audio, storage, and media checks passed.";
        ReadinessInfoBar.Severity = InfoBarSeverity.Success;
    }

    private void OnCaptureHealthChanged(CaptureFrameHealthSnapshot health)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            FrameStatusText.Text =
                $"{health.FramesReceived:N0} frames, " +
                $"{health.FramesDropped:N0} dropped, " +
                $"{health.LastProjectTime:mm\\:ss\\.fff} elapsed.";
        });
    }

    private void OnCaptureClosed() =>
        DispatcherQueue.TryEnqueue(StopCaptureFromDispatcher);

    private void OnCaptureFailed(Exception exception) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            FrameStatusText.Text = $"Capture failed: {exception.Message}";
            ReadinessInfoBar.Title = "Screen capture failed";
            ReadinessInfoBar.Message = exception.Message;
            ReadinessInfoBar.Severity = InfoBarSeverity.Error;
        });

    private async void StopCaptureFromDispatcher()
    {
        await StopCaptureAsync();
    }

    private async Task StopCaptureAsync()
    {
        WindowsScreenCaptureSession? capture = _captureSession;
        if (capture is null)
        {
            return;
        }

        _captureSession = null;
        capture.HealthChanged -= OnCaptureHealthChanged;
        capture.CaptureClosed -= OnCaptureClosed;
        capture.CaptureFailed -= OnCaptureFailed;
        await capture.DisposeAsync();

        CaptureFrameHealthSnapshot health = capture.Health;
        SurfaceScreenSegmentRecorder? segmentRecorder = _segmentRecorder;
        RecoverableAudioRecordingSession? audioRecorder = _audioRecorder;
        RecoverableCameraRecordingSession? cameraRecorder = _cameraRecorder;
        CursorMetadataRecorder? cursorRecorder = _cursorRecorder;
        string? projectRoot = _activeProjectRoot;
        _segmentRecorder = null;
        _audioRecorder = null;
        _cameraRecorder = null;
        _cursorRecorder = null;
        _activeProjectRoot = null;
        if (audioRecorder is not null)
        {
            audioRecorder.HealthChanged -= OnAudioHealthChanged;
        }

        try
        {
            if (audioRecorder is not null)
            {
                await audioRecorder.StopAsync();
            }
            if (cameraRecorder is not null)
            {
                await cameraRecorder.StopAsync();
            }
            if (cursorRecorder is not null && projectRoot is not null)
            {
                CursorMetadataDocument cursor =
                    await cursorRecorder.CompleteAsync(projectRoot);
                IReadOnlyList<CursorZoomEvent> zooms =
                    CursorZoomPlanner.CreatePlan(cursor);
                string zoomPath = Path.Combine(projectRoot, "cursor-zoom-plan.json");
                string temporaryZoomPath = zoomPath + ".tmp";
                await File.WriteAllTextAsync(
                    temporaryZoomPath,
                    JsonSerializer.Serialize(
                        zooms,
                        AudioRepairSerializerOptions));
                File.Move(temporaryZoomPath, zoomPath, overwrite: true);
            }

            if (segmentRecorder is not null)
            {
                RecordingSegmentEntry segment = await segmentRecorder.CompleteAsync();
                AudioRecordingResult? audio = audioRecorder is null
                    ? null
                    : await audioRecorder.PublishAsync();
                CameraRecordingResult? camera = cameraRecorder is null
                    ? null
                    : await cameraRecorder.PublishAsync();
                int repairEvents = 0;
                if (audio is not null && projectRoot is not null)
                {
                    repairEvents = await SaveAudioRepairPlanAsync(
                        projectRoot,
                        audio.Timing);
                }
                FrameStatusText.Text =
                    $"Saved {health.FramesReceived:N0} captured frames to {segment.RelativePath}; " +
                    $"{health.FramesDropped:N0} dropped" +
                    (audio is null
                        ? "."
                        : $"; audio saved to {audio.Microphone.RelativePath} and " +
                          $"{audio.SystemAudio.RelativePath}; {repairEvents} timing repairs suggested") +
                    (camera is null
                        ? "."
                        : $"; camera saved to {camera.Segment.RelativePath} with {camera.Layout.Mode} layout.");
            }
            else
            {
                FrameStatusText.Text =
                    $"Stopped after {health.FramesReceived:N0} frames; " +
                    $"{health.FramesDropped:N0} dropped.";
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            FrameStatusText.Text = $"Segment finalization failed: {exception.Message}";
            ReadinessInfoBar.Title = "Recording could not be finalized";
            ReadinessInfoBar.Message = exception.Message;
            ReadinessInfoBar.Severity = InfoBarSeverity.Error;
        }
        finally
        {
            if (segmentRecorder is not null)
            {
                await segmentRecorder.DisposeAsync();
            }

            if (audioRecorder is not null)
            {
                await audioRecorder.DisposeAsync();
            }
            if (cameraRecorder is not null)
            {
                await cameraRecorder.DisposeAsync();
            }
            if (cursorRecorder is not null)
            {
                await cursorRecorder.DisposeAsync();
            }
        }

        StartRecordingButton.Content = "New recording";
        ChooseSourceButton.IsEnabled = true;
        RefreshReadinessButton.IsEnabled = true;
        if (ReadinessInfoBar.Severity is not InfoBarSeverity.Error)
        {
            UpdateReadinessSummary();
        }
        await RefreshProjectsAsync();
    }

    private static string CreateProjectRoot()
    {
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        string projectName =
            $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        return Path.Combine(videos, "7Record", "Projects", projectName);
    }

    private void OnAudioHealthChanged(AudioCaptureHealth health)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            string source = health.Source is AudioCaptureSource.Microphone
                ? "Mic"
                : "System";
            AudioStatusText.Text =
                $"{source}: {health.Drift.Drift.TotalMilliseconds:+0.0;-0.0;0.0} ms drift, " +
                $"{health.Discontinuities} discontinuities.";
        });
    }

    private async Task RefreshProjectsAsync()
    {
        RefreshProjectsButton.IsEnabled = false;
        try
        {
            string projectsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "7Record",
                "Projects");
            IReadOnlyList<ProjectSummary> projects =
                await ProjectLibraryService.ListAsync(projectsRoot);
            RecentProjectsList.Items.Clear();
            foreach (ProjectSummary project in projects)
            {
                RecentProjectsList.Items.Add(
                    new ListViewItem
                    {
                        Content =
                            $"{project.RecoveryState}  |  {project.Name}  |  " +
                            $"{project.Duration:hh\\:mm\\:ss}  |  " +
                            $"{project.MediaSegments} source(s){Environment.NewLine}" +
                            project.StatusMessage,
                        Tag = project.Path,
                    });
            }

            ProjectsEmptyText.Visibility = projects.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            RefreshProjectsButton.IsEnabled = true;
        }
    }

    private static async Task<int> SaveAudioRepairPlanAsync(
        string projectRoot,
        SevenRecord.Domain.Audio.AudioTimingManifest timing)
    {
        IReadOnlyList<SevenRecord.Domain.Audio.AudioRepairEvent> repairs =
            AudioRepairPlanner.CreatePlan(timing);
        string path = Path.Combine(projectRoot, "audio-repair-plan.json");
        string temporaryPath = path + ".tmp";
        string json = JsonSerializer.Serialize(
            repairs,
            AudioRepairSerializerOptions);
        await File.WriteAllTextAsync(temporaryPath, json);
        File.Move(temporaryPath, path, overwrite: true);
        return repairs.Count;
    }
}
