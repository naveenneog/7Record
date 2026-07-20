using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Storage;
using Windows.System;
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
using SevenRecord.Recording.Windows;
using SevenRecord.Transcription;

namespace SevenRecord.App;

public sealed partial class MainPage : Page, IDisposable
{
    private static readonly TimeSpan AudioDriftWarningThreshold =
        TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan AudioMissingWarningThreshold =
        TimeSpan.FromMilliseconds(100);
    private static readonly JsonSerializerOptions RenderPlanSerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ProjectPostProcessingPipeline _postProcessingPipeline = new();
    private readonly CancellationTokenSource _postProcessingCancellation = new();
    private readonly RecorderStateMachine _recorderState = new();
    private readonly DispatcherTimer _recordingUiTimer;
    private readonly object _stopCaptureGate = new();
    private readonly CaptureReadinessService _readinessService = new(
    [
        new WindowsCaptureReadinessProbe(
            new CaptureReadinessSelection(
                RequireCamera: false,
                RequireMicrophone: false,
                RequireSystemAudio: false)),
        new StorageReadinessProbe(),
        new EncoderReadinessProbe(),
    ]);
    private AudioCaptureHealth? _microphoneHealth;
    private AudioCaptureHealth? _systemAudioHealth;
    private bool _cameraEnabled = true;
    private bool _disposed;
    private bool _updatingCameraToggle;
    private GlobalHotKeyService? _globalHotKeys;
    private WindowsRecordingSession? _recordingSession;
    private CancellationTokenSource? _recordingStartupCancellation;
    private TaskCompletionSource? _recordingStartupCompletion;
    private CaptureReadinessSnapshot? _lastSnapshot;
    private WindowsCaptureTarget? _selectedScreen;
    private string? _currentPreviewPath;
    private TimelineDocument? _currentTimeline;
    private CaptionEditSession? _captionEditSession;
    private string? _latestPostProcessingProject;
    private Task? _stopCaptureTask;
    private readonly HashSet<string> _disabledAutomation =
        new(StringComparer.Ordinal);

    public MainPage()
    {
        InitializeComponent();
        _recordingUiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _recordingUiTimer.Tick += OnRecordingUiTimerTick;
        _recorderState.StateChanged += OnRecorderStateChanged;
        ReadinessInfoBar.RegisterPropertyChangedCallback(
            InfoBar.TitleProperty,
            OnRecorderStatusPropertyChanged);
        ReadinessInfoBar.RegisterPropertyChangedCallback(
            InfoBar.MessageProperty,
            OnRecorderStatusPropertyChanged);
        ReadinessInfoBar.RegisterPropertyChangedCallback(
            InfoBar.SeverityProperty,
            OnRecorderStatusPropertyChanged);
        UpdateRecorderStatusAccessibility();
        ApplyRecorderVisualState(_recorderState.Snapshot);
    }

    private void OnRecorderStatusPropertyChanged(
        DependencyObject sender,
        DependencyProperty property) =>
        UpdateRecorderStatusAccessibility();

    private void UpdateRecorderStatusAccessibility()
    {
        AutomationProperties.SetName(
            ReadinessInfoBar,
            $"{ReadinessInfoBar.Title}. {ReadinessInfoBar.Message}");
        RecordingHealthIcon.Foreground = ReadinessInfoBar.Severity switch
        {
            InfoBarSeverity.Success => ResourceBrush("SuccessBrush"),
            InfoBarSeverity.Warning => ResourceBrush("WarningBrush"),
            InfoBarSeverity.Error => ResourceBrush("DangerBrush"),
            _ => ResourceBrush("StudioMutedBrush"),
        };
        if (ReadinessInfoBar.Severity is
            InfoBarSeverity.Warning or InfoBarSeverity.Error)
        {
            RecordingHealthExpander.IsExpanded = true;
        }
    }

    private void OnRecorderStateChanged(RecorderSnapshot snapshot)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyRecorderVisualState(snapshot);
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
            ApplyRecorderVisualState(snapshot));
    }

    private void OnRecordingUiTimerTick(
        object? sender,
        object e) =>
        UpdateRecordingElapsedText();

    private void ApplyRecorderVisualState(RecorderSnapshot snapshot)
    {
        Brush recordBrush = ResourceBrush("RecordBrush");
        Brush warningBrush = ResourceBrush("WarningBrush");
        switch (snapshot.State)
        {
            case RecorderState.Starting:
                ProjectsNavigationItem.IsEnabled = false;
                RecordingHealthExpander.IsExpanded = true;
                _recordingUiTimer.Stop();
                RecordingStatusPill.Visibility = Visibility.Visible;
                RecordingStateDot.Fill = recordBrush;
                RecordingStateText.Text = "Starting";
                RecordingElapsedText.Text = "00:00";
                RecordingProgressRing.IsActive = true;
                RecordingProgressRing.Visibility = Visibility.Visible;
                PauseRecordingButton.Visibility = Visibility.Collapsed;
                StartRecordingButton.Content = "Cancel";
                StartRecordingButton.IsEnabled = true;
                AutomationProperties.SetName(
                    StartRecordingButton,
                    "Cancel recording startup");
                PreviewStateText.Text = "Preparing capture";
                PreviewHintText.Text =
                    "Starting the selected screen, camera, and available audio sources.";
                ChooseSourceButton.IsEnabled = false;
                ConfigureCameraButton.IsEnabled = false;
                break;

            case RecorderState.Recording:
                ProjectsNavigationItem.IsEnabled = false;
                RecordingHealthExpander.IsExpanded = true;
                RecordingStatusPill.Visibility = Visibility.Visible;
                RecordingStateDot.Fill = recordBrush;
                RecordingStateText.Text = "Recording";
                RecordingProgressRing.IsActive = false;
                RecordingProgressRing.Visibility = Visibility.Collapsed;
                PauseRecordingButton.Content = "Pause";
                PauseRecordingButton.IsEnabled = true;
                PauseRecordingButton.Visibility = Visibility.Visible;
                AutomationProperties.SetName(
                    PauseRecordingButton,
                    "Pause recording");
                StartRecordingButton.Content = "Stop";
                StartRecordingButton.IsEnabled = true;
                AutomationProperties.SetName(
                    StartRecordingButton,
                    "Stop recording");
                PreviewStateText.Text = "Recording in progress";
                PreviewHintText.Text =
                    "7Record is capturing the selected source into recoverable local segments.";
                ChooseSourceButton.IsEnabled = false;
                ConfigureCameraButton.IsEnabled = false;
                _recordingUiTimer.Start();
                UpdateRecordingElapsedText();
                break;

            case RecorderState.Paused:
                ProjectsNavigationItem.IsEnabled = false;
                RecordingHealthExpander.IsExpanded = true;
                RecordingStatusPill.Visibility = Visibility.Visible;
                RecordingStateDot.Fill = warningBrush;
                RecordingStateText.Text = "Paused";
                RecordingProgressRing.IsActive = false;
                RecordingProgressRing.Visibility = Visibility.Collapsed;
                PauseRecordingButton.Content = "Resume";
                PauseRecordingButton.IsEnabled = true;
                PauseRecordingButton.Visibility = Visibility.Visible;
                AutomationProperties.SetName(
                    PauseRecordingButton,
                    "Resume recording");
                StartRecordingButton.Content = "Stop";
                StartRecordingButton.IsEnabled = true;
                AutomationProperties.SetName(
                    StartRecordingButton,
                    "Stop recording");
                PreviewStateText.Text = "Recording paused";
                PreviewHintText.Text =
                    "Screen, camera, cursor, and audio samples are paused without adding a timeline gap.";
                ChooseSourceButton.IsEnabled = false;
                ConfigureCameraButton.IsEnabled = false;
                UpdateRecordingElapsedText();
                break;

            case RecorderState.Stopping:
                ProjectsNavigationItem.IsEnabled = false;
                RecordingHealthExpander.IsExpanded = true;
                _recordingUiTimer.Stop();
                RecordingStatusPill.Visibility = Visibility.Visible;
                RecordingStateDot.Fill = warningBrush;
                RecordingStateText.Text = "Saving";
                RecordingProgressRing.IsActive = true;
                RecordingProgressRing.Visibility = Visibility.Visible;
                PauseRecordingButton.IsEnabled = false;
                PauseRecordingButton.Visibility = Visibility.Collapsed;
                StartRecordingButton.Content = "Finishing…";
                StartRecordingButton.IsEnabled = false;
                AutomationProperties.SetName(
                    StartRecordingButton,
                    "Finishing recording");
                PreviewStateText.Text = "Saving recording";
                PreviewHintText.Text =
                    "Finalizing recoverable source files and preparing the first edit.";
                ChooseSourceButton.IsEnabled = false;
                ConfigureCameraButton.IsEnabled = false;
                break;

            case RecorderState.Faulted:
                ProjectsNavigationItem.IsEnabled = true;
                _recordingUiTimer.Stop();
                RecordingStatusPill.Visibility = Visibility.Collapsed;
                RecordingProgressRing.IsActive = false;
                RecordingProgressRing.Visibility = Visibility.Collapsed;
                PauseRecordingButton.IsEnabled = false;
                PauseRecordingButton.Visibility = Visibility.Collapsed;
                StartRecordingButton.Content = "Record";
                StartRecordingButton.IsEnabled =
                    _lastSnapshot?.CanRecord is true;
                AutomationProperties.SetName(
                    StartRecordingButton,
                    "Start recording");
                PreviewStateText.Text = "Recording needs attention";
                PreviewHintText.Text =
                    snapshot.Failure ?? "Review the recorder status and try again.";
                ChooseSourceButton.IsEnabled = true;
                ConfigureCameraButton.IsEnabled =
                    CameraOverlayToggle.IsEnabled;
                break;

            default:
                ProjectsNavigationItem.IsEnabled = true;
                _recordingUiTimer.Stop();
                RecordingStatusPill.Visibility = Visibility.Collapsed;
                RecordingProgressRing.IsActive = false;
                RecordingProgressRing.Visibility = Visibility.Collapsed;
                PauseRecordingButton.Content = "Pause";
                PauseRecordingButton.IsEnabled = false;
                PauseRecordingButton.Visibility = Visibility.Collapsed;
                StartRecordingButton.Content = "Record";
                StartRecordingButton.IsEnabled =
                    _lastSnapshot?.CanRecord is true;
                AutomationProperties.SetName(
                    StartRecordingButton,
                    "Start recording");
                PreviewStateText.Text = "Ready to record";
                PreviewHintText.Text = _selectedScreen is null
                    ? "Choose an application or display, or press Record to select one and start."
                    : "Press Record to capture the selected source; available camera and audio are included automatically.";
                ChooseSourceButton.IsEnabled = true;
                ConfigureCameraButton.IsEnabled =
                    CameraOverlayToggle.IsEnabled;
                RecordingElapsedText.Text = "00:00";
                break;
        }
    }

    private void UpdateRecordingElapsedText()
    {
        TimeSpan elapsed = _recordingSession?.ActiveDuration ?? TimeSpan.Zero;
        RecordingElapsedText.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private static Brush ResourceBrush(string key) =>
        (Brush)Application.Current.Resources[key];

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
                await RecoverableCameraRecordingSession.CreateAsync(
                    probeRoot,
                    clock,
                    new RecordingPauseController());
            CameraStatusText.Text =
                $"{camera.DeviceName} ({camera.Width} x {camera.Height}) is ready.";
        }
        catch (Exception exception)
        {
            CameraStatusText.Text = $"Camera unavailable: {exception.Message}";
        }
        finally
        {
            if (Directory.Exists(probeRoot))
            {
                Directory.Delete(probeRoot, recursive: true);
            }

            ConfigureCameraButton.IsEnabled =
                _recordingSession is null &&
                (_recorderState.Snapshot.State is
                    RecorderState.Idle or RecorderState.Faulted) &&
                CameraOverlayToggle.IsEnabled;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyAdaptiveLayout(ActualWidth);
        await TrySelectPrimaryDisplayAsync();
        await RefreshReadinessAsync();
        await RefreshProjectsAsync();
        RegisterGlobalHotKeys();
    }

    private void OnPageSizeChanged(
        object sender,
        SizeChangedEventArgs e) =>
        ApplyAdaptiveLayout(e.NewSize.Width);

    private void ApplyAdaptiveLayout(double width)
    {
        bool narrow = width < 1100;
        if (narrow)
        {
            Grid.SetRow(HeaderCommandPanel, 1);
            Grid.SetColumn(HeaderCommandPanel, 0);
            HeaderCommandPanel.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumnSpan(HeaderTextPanel, 2);
            Grid.SetRow(SetupRail, 1);
            Grid.SetColumn(SetupRail, 0);
            Grid.SetColumnSpan(SetupRail, 2);
            Grid.SetColumnSpan(PreviewStage, 2);
            RecorderRailColumn.Width = new GridLength(0);
            RecorderContentGrid.Padding = new Thickness(20, 16, 20, 24);
            PreviewStage.MinHeight = 420;

            Grid.SetRow(TimelineSection, 1);
            Grid.SetColumn(TimelineSection, 0);
            Grid.SetColumnSpan(TimelineSection, 2);
            Grid.SetRow(ProjectDetailEmptyState, 1);
            Grid.SetColumn(ProjectDetailEmptyState, 0);
            Grid.SetColumnSpan(ProjectDetailEmptyState, 2);
            Grid.SetColumnSpan(ProjectsSection, 2);
            ProjectsListColumn.Width = new GridLength(1, GridUnitType.Star);
            ProjectDetailColumn.Width = new GridLength(0);
        }
        else
        {
            Grid.SetRow(HeaderCommandPanel, 0);
            Grid.SetColumn(HeaderCommandPanel, 1);
            HeaderCommandPanel.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumnSpan(HeaderTextPanel, 1);
            Grid.SetRow(SetupRail, 0);
            Grid.SetColumn(SetupRail, 1);
            Grid.SetColumnSpan(SetupRail, 1);
            Grid.SetColumnSpan(PreviewStage, 1);
            RecorderRailColumn.Width = new GridLength(360);
            RecorderContentGrid.Padding = new Thickness(32, 20, 32, 32);
            PreviewStage.MinHeight = 500;

            Grid.SetRow(TimelineSection, 0);
            Grid.SetColumn(TimelineSection, 1);
            Grid.SetColumnSpan(TimelineSection, 1);
            Grid.SetRow(ProjectDetailEmptyState, 0);
            Grid.SetColumn(ProjectDetailEmptyState, 1);
            Grid.SetColumnSpan(ProjectDetailEmptyState, 1);
            Grid.SetColumnSpan(ProjectsSection, 1);
            ProjectsListColumn.Width = new GridLength(380);
            ProjectDetailColumn.Width = new GridLength(1, GridUnitType.Star);
        }
    }

    private void OnCameraOverlayToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingCameraToggle)
        {
            return;
        }

        _cameraEnabled = CameraOverlayToggle.IsOn;
        CameraStatusText.Text = _cameraEnabled
            ? "The default camera will start automatically with recording."
            : "Camera overlay is off.";
    }

    private async void OnRefreshReadinessClicked(object sender, RoutedEventArgs e)
    {
        await RefreshReadinessAsync();
    }

    private void OnRefreshHotkeysClicked(object sender, RoutedEventArgs e)
    {
        RefreshHotkeysButton.IsEnabled = false;
        HotkeyStatusText.Text = "Registering global shortcuts...";
        DisposeGlobalHotKeys();
        RegisterGlobalHotKeys();
    }

    private void OnPauseRecordingClicked(object sender, RoutedEventArgs e)
    {
        WindowsRecordingSession? session = _recordingSession;
        if (session is null)
        {
            return;
        }

        if (_recorderState.Snapshot.State is RecorderState.Paused)
        {
            session.Resume();
            _recorderState.Resume();
            PauseRecordingButton.Content = "Pause";
            ReadinessInfoBar.Title = "Recording";
            ReadinessInfoBar.Message = "Recording resumed.";
            UpdateAudioWarningState();
        }
        else if (_recorderState.Snapshot.State is RecorderState.Recording)
        {
            session.Pause();
            _recorderState.Pause();
            PauseRecordingButton.Content = "Resume";
            ReadinessInfoBar.Title = "Paused";
            ReadinessInfoBar.Message = "Screen and audio samples are paused.";
        }
    }

    private void OnProjectsClicked(object sender, RoutedEventArgs e)
    {
        ProjectsNavigationItem.IsSelected = true;
        ShowProjectsView();
    }

    private void OnNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not NavigationViewItem item)
        {
            return;
        }

        if (string.Equals(
                item.Tag as string,
                "projects",
                StringComparison.Ordinal))
        {
            ShowProjectsView();
        }
        else
        {
            ShowRecorderView();
        }
    }

    private void ShowRecorderView()
    {
        RecorderView.Visibility = Visibility.Visible;
        ProjectsView.Visibility = Visibility.Collapsed;
    }

    private void ShowProjectsView()
    {
        if (_recorderState.Snapshot.IsActive)
        {
            RecorderNavigationItem.IsSelected = true;
            ShowRecorderView();
            return;
        }

        RecorderView.Visibility = Visibility.Collapsed;
        ProjectsView.Visibility = Visibility.Visible;
        _ = RefreshProjectsAsync();
    }

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

        await OpenProjectAsync(projectPath);
    }

    private async void OnOpenProjectButtonClicked(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Button { Tag: string projectPath })
        {
            await OpenProjectAsync(projectPath);
        }
    }

    private async Task OpenProjectAsync(string projectPath)
    {
        try
        {
            TimelineDocument timeline = await ProjectTimelineLoader.LoadAsync(projectPath);
            _currentTimeline = timeline;
            _disabledAutomation.Clear();
            await LoadCaptionEditorAsync(projectPath);
            LoadProjectPreview(timeline);
            TimelineProjectTitle.Text =
                $"{FormatProjectDisplayName(Path.GetFileName(projectPath))}  ·  " +
                $"{timeline.Duration:hh\\:mm\\:ss}";
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
            ProjectDetailEmptyState.Visibility = Visibility.Collapsed;
            TimelineSection.Visibility = Visibility.Visible;
            TimelineSection.StartBringIntoView();
        }
        catch (Exception exception)
        {
            TimelineProjectTitle.Text = $"Timeline could not be loaded: {exception.Message}";
            ProjectDetailEmptyState.Visibility = Visibility.Collapsed;
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

    private void LoadProjectPreview(TimelineDocument timeline)
    {
        ProjectPreviewPlayer.Source = null;
        _currentPreviewPath = null;
        OpenRecordingExternallyButton.IsEnabled = false;
        OpenProjectFolderButton.IsEnabled = Directory.Exists(timeline.ProjectPath);

        string exportsDirectory = Path.Combine(
            timeline.ProjectPath,
            "exports");
        string? previewPath = Directory.Exists(exportsDirectory)
            ? Directory.EnumerateFiles(
                    exportsDirectory,
                    "*.mp4",
                    SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault()
            : null;
        bool isExport = previewPath is not null;

        if (previewPath is null)
        {
            TimelineClip? screen = timeline.Clips.FirstOrDefault(
                clip => clip.Track is TimelineTrackKind.Screen);
            if (screen is not null)
            {
                string candidate = Path.Combine(
                    timeline.ProjectPath,
                    screen.SourcePath);
                if (File.Exists(candidate))
                {
                    previewPath = candidate;
                }
            }
        }

        if (previewPath is null)
        {
            ProjectPreviewStatusText.Text =
                "No playable screen or exported MP4 source was found for this project.";
            return;
        }

        _currentPreviewPath = previewPath;
        ProjectPreviewPlayer.Source =
            MediaSource.CreateFromUri(new Uri(previewPath));
        OpenRecordingExternallyButton.IsEnabled = true;
        ProjectPreviewStatusText.Text = isExport
            ? $"Playing exported recording: {Path.GetFileName(previewPath)}"
            : "Playing the recorded screen source. Camera and audio remain separate until export.";
    }

    private async void OnOpenRecordingExternallyClicked(
        object sender,
        RoutedEventArgs e)
    {
        if (_currentPreviewPath is null)
        {
            return;
        }

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(
                _currentPreviewPath);
            bool opened = await Launcher.LaunchFileAsync(file);
            if (!opened)
            {
                ProjectPreviewStatusText.Text =
                    "Windows could not open the recording in the default video player.";
            }
        }
        catch (Exception exception)
        {
            ProjectPreviewStatusText.Text =
                $"Recording could not be opened: {exception.Message}";
        }
    }

    private async void OnOpenProjectFolderClicked(
        object sender,
        RoutedEventArgs e)
    {
        if (_currentTimeline is null)
        {
            return;
        }

        try
        {
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(
                _currentTimeline.ProjectPath);
            bool opened = await Launcher.LaunchFolderAsync(folder);
            if (!opened)
            {
                ProjectPreviewStatusText.Text =
                    "Windows could not open the project folder.";
            }
        }
        catch (Exception exception)
        {
            ProjectPreviewStatusText.Text =
                $"Project folder could not be opened: {exception.Message}";
        }
    }

    private void OnRenderPresetChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateRenderPlanSummary();

    private async void OnSaveRenderPlanClicked(object sender, RoutedEventArgs e)
    {
        if (_currentTimeline is null)
        {
            return;
        }

        SaveRenderPlanButton.IsEnabled = false;
        try
        {
            _ = await SaveRenderPlanAsync();
            RenderPlanSummaryText.Text += " Saved to render-plan.json.";
        }
        catch (Exception exception)
        {
            RenderPlanSummaryText.Text =
                $"Render plan could not be saved: {exception.Message}";
        }
        finally
        {
            SaveRenderPlanButton.IsEnabled = true;
        }
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
        catch (Exception exception)
        {
            RenderPlanSummaryText.Text =
                $"Export failed: {exception.Message}";
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
            try
            {
                await PersistCaptionEditsAsync();
            }
            catch (Exception exception)
            {
                RenderPlanSummaryText.Text =
                    $"Caption undo failed: {exception.Message}";
            }
        }
    }

    private async void OnRedoCaptionClicked(object sender, RoutedEventArgs e)
    {
        if (_captionEditSession?.Redo() is true)
        {
            try
            {
                await PersistCaptionEditsAsync();
            }
            catch (Exception exception)
            {
                RenderPlanSummaryText.Text =
                    $"Caption redo failed: {exception.Message}";
            }
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
        _postProcessingCancellation.Cancel();
        _recordingUiTimer.Stop();
        ProjectPreviewPlayer.Source = null;
        DisposeGlobalHotKeys();
        await StopCaptureAsync(RecordingStopReason.ApplicationExit);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _postProcessingCancellation.Cancel();
        _recordingUiTimer.Stop();
        _recordingUiTimer.Tick -= OnRecordingUiTimerTick;
        _recorderState.StateChanged -= OnRecorderStateChanged;
        DisposeGlobalHotKeys();
        _postProcessingCancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private async void OnStartRecordingClicked(object sender, RoutedEventArgs e)
    {
        RecorderState recorderState = _recorderState.Snapshot.State;
        if (recorderState is
            RecorderState.Starting or
            RecorderState.Recording or
            RecorderState.Paused)
        {
            await StopCaptureAsync();
            return;
        }
        if (recorderState is RecorderState.Stopping)
        {
            return;
        }
        if (recorderState is RecorderState.Faulted)
        {
            _recorderState.Reset();
        }

        if (_selectedScreen is null &&
            !await TrySelectPrimaryDisplayAsync() &&
            !await PickCaptureTargetAsync())
        {
            UpdateReadinessSummary();
            return;
        }

        if (_lastSnapshot?.CanRecord is not true)
        {
            await RefreshReadinessAsync();
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
        _recorderState.BeginStart();
        CancellationTokenSource startupCancellation = new();
        TaskCompletionSource startupCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        _recordingStartupCancellation = startupCancellation;
        _recordingStartupCompletion = startupCompletion;

        try
        {
            WindowsRecordingStartResult startResult =
                await WindowsRecordingSession.StartAsync(
                    new WindowsRecordingRequest(
                        CreateProjectRoot(),
                        _selectedScreen,
                        _cameraEnabled),
                    startupCancellation.Token);
            WindowsRecordingSession session = startResult.Session;
            if (_recorderState.Snapshot.State is RecorderState.Stopping)
            {
                await session.StopAsync(RecordingStopReason.User);
                _recorderState.CompleteStop();
                StartRecordingButton.Content = "Record";
                StartRecordingButton.IsEnabled = true;
                PauseRecordingButton.IsEnabled = false;
                await RefreshReadinessAsync();
                return;
            }

            session.ScreenHealthChanged += OnCaptureHealthChanged;
            session.AudioHealthChanged += OnAudioHealthChanged;
            session.CaptureClosed += OnCaptureClosed;
            session.CaptureFailed += OnCaptureFailed;
            _recordingSession = session;
            _microphoneHealth = null;
            _systemAudioHealth = null;
            _recorderState.MarkRecording();

            if (session.CaptureFailure is Exception captureFailure)
            {
                await StopCaptureAfterFailureAsync(captureFailure);
                return;
            }
            if (session.IsCaptureClosed)
            {
                await StopCaptureAsync(RecordingStopReason.CaptureClosed);
                return;
            }

            IReadOnlyList<WindowsRecordingIssue> sourceWarnings =
                startResult.Issues;
            StartRecordingButton.Content = "Stop";
            StartRecordingButton.IsEnabled = true;
            PauseRecordingButton.Content = "Pause";
            PauseRecordingButton.IsEnabled = true;
            ChooseSourceButton.IsEnabled = false;
            CameraOverlayToggle.IsEnabled = false;
            RefreshReadinessButton.IsEnabled = false;
            ReadinessInfoBar.Title = sourceWarnings.Count == 0
                ? "Recording"
                : "Recording with unavailable sources";
            ReadinessInfoBar.Message = sourceWarnings.Count == 0
                ? "Capturing accelerated Direct3D surfaces with camera, microphone, and system audio."
                : $"Screen capture is active. {string.Join(" ", sourceWarnings.Select(issue => issue.Message))}";
            ReadinessInfoBar.Severity = sourceWarnings.Count == 0
                ? InfoBarSeverity.Informational
                : InfoBarSeverity.Warning;
            FrameStatusText.Text = "Waiting for the first frame...";
            if (session.HasAudio)
            {
                AudioStatusText.Text =
                    "Mic: waiting for samples." + Environment.NewLine +
                    "System: waiting for samples.";
            }
            else
            {
                AudioStatusText.Text =
                    IssueMessage(sourceWarnings, "audio", "Audio is unavailable.");
            }

            if (session.CameraDeviceName is not null)
            {
                CameraStatusText.Text =
                    $"{session.CameraDeviceName} is recording with GPU surface encoding.";
            }
            else if (_cameraEnabled)
            {
                CameraStatusText.Text =
                    IssueMessage(sourceWarnings, "camera", "Camera is unavailable.");
            }
        }
        catch (OperationCanceledException)
            when (startupCancellation.IsCancellationRequested)
        {
            if (_recorderState.Snapshot.State is RecorderState.Stopping)
            {
                _recorderState.CompleteStop();
            }

            StartRecordingButton.Content = "Record";
            StartRecordingButton.IsEnabled = true;
            PauseRecordingButton.IsEnabled = false;
            ReadinessInfoBar.Title = "Recording canceled";
            ReadinessInfoBar.Message =
                "Recording was canceled before capture started.";
            ReadinessInfoBar.Severity = InfoBarSeverity.Informational;
            await RefreshReadinessAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            if (_recorderState.Snapshot.State is
                RecorderState.Starting or RecorderState.Stopping)
            {
                _recorderState.MarkFaulted(exception.Message);
            }
            StartRecordingButton.IsEnabled = true;
            FrameStatusText.Text =
                $"Recording start failed: {exception.GetType().Name} " +
                $"0x{exception.HResult:X8}: {exception.Message}";
            ReadinessInfoBar.Title = "Recording could not start";
            ReadinessInfoBar.Message = exception.Message;
            ReadinessInfoBar.Severity = InfoBarSeverity.Error;
        }
        finally
        {
            if (ReferenceEquals(
                    _recordingStartupCancellation,
                    startupCancellation))
            {
                _recordingStartupCancellation = null;
            }
            if (ReferenceEquals(
                    _recordingStartupCompletion,
                    startupCompletion))
            {
                _recordingStartupCompletion = null;
            }

            startupCancellation.Dispose();
            startupCompletion.TrySetResult();
        }
    }

    private async Task<bool> TrySelectPrimaryDisplayAsync()
    {
        try
        {
            WindowsCaptureTarget target =
                await WindowsCaptureSourcePicker.GetPrimaryDisplayAsync();
            _selectedScreen = target;
            ScreenStatusText.Text =
                $"Primary display: {target.DisplayName} ({target.Width} x {target.Height})";
            PreviewSourceText.Text =
                $"{target.DisplayName} · {target.Width} × {target.Height}";
            return true;
        }
        catch (Exception exception) when (
            exception is COMException or
                InvalidOperationException or
                NotSupportedException or
                UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            ScreenStatusText.Text =
                "Primary display could not be selected automatically. Choose an application or display.";
            return false;
        }
    }

    private async void OnChooseSourceClicked(object sender, RoutedEventArgs e)
    {
        ChooseSourceButton.IsEnabled = false;

        try
        {
            await PickCaptureTargetAsync();
        }
        finally
        {
            ChooseSourceButton.IsEnabled = true;
        }
    }

    private async Task<bool> PickCaptureTargetAsync()
    {
        try
        {
            App application = (App)Application.Current;
            nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(
                application.MainWindow);
            WindowsCaptureTarget? target =
                await WindowsCaptureSourcePicker.PickAsync(windowHandle);
            if (target is null)
            {
                return false;
            }

            _selectedScreen = target;
            ScreenStatusText.Text =
                $"{target.DisplayName} ({target.Width} x {target.Height})";
            PreviewSourceText.Text =
                $"{target.DisplayName} · {target.Width} × {target.Height}";
            UpdateReadinessSummary();
            return true;
        }
        catch (COMException exception)
        {
            ReadinessInfoBar.Title = "Screen selection failed";
            ReadinessInfoBar.Message =
                $"Windows capture picker failed (0x{exception.HResult:X8}).";
            ReadinessInfoBar.Severity = InfoBarSeverity.Error;
            return false;
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

            CaptureReadinessItem screen =
                snapshot.Items.Single(item => item.Key == "screen");
            CaptureReadinessItem graphics =
                snapshot.Items.Single(item => item.Key == "graphics-device");
            if (_selectedScreen is null)
            {
                ScreenStatusText.Text = $"{screen.Message} {graphics.Message}";
            }
            SetStatusIcon(
                ScreenSourceIcon,
                WorstState(screen.State, graphics.State));

            CaptureReadinessItem camera =
                snapshot.Items.Single(item => item.Key == "camera");
            bool cameraAvailable = camera.State is
                CaptureSourceState.Ready or CaptureSourceState.Warning;
            _updatingCameraToggle = true;
            try
            {
                CameraOverlayToggle.IsEnabled =
                    cameraAvailable && _recordingSession is null;
                ConfigureCameraButton.IsEnabled =
                    cameraAvailable && _recordingSession is null;
                if (!cameraAvailable)
                {
                    CameraOverlayToggle.IsOn = false;
                    _cameraEnabled = false;
                }
            }
            finally
            {
                _updatingCameraToggle = false;
            }
            ApplyStatus(CameraStatusText, camera, CameraSourceIcon);

            CaptureReadinessItem microphone = snapshot.Items.Single(item => item.Key == "microphone");
            CaptureReadinessItem systemAudio = snapshot.Items.Single(item => item.Key == "system-audio");
            AudioStatusText.Text = $"{microphone.Message} {systemAudio.Message}";
            SetStatusIcon(
                AudioSourceIcon,
                WorstState(microphone.State, systemAudio.State));

            ApplyStatus(
                StorageStatusText,
                snapshot.Items.Single(item => item.Key == "storage"),
                StorageStatusIcon);
            ApplyStatus(
                EncoderStatusText,
                snapshot.Items.Single(item => item.Key == "encoder"),
                EncoderStatusIcon);

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

    private static void ApplyStatus(
        TextBlock textBlock,
        CaptureReadinessItem item,
        FontIcon? icon = null)
    {
        textBlock.Text = item.Message;
        if (icon is not null)
        {
            SetStatusIcon(icon, item.State);
        }
    }

    private static CaptureSourceState WorstState(
        CaptureSourceState first,
        CaptureSourceState second) =>
        StateSeverity(first) >= StateSeverity(second)
            ? first
            : second;

    private static int StateSeverity(CaptureSourceState state) =>
        state switch
        {
            CaptureSourceState.Error => 3,
            CaptureSourceState.Unavailable => 3,
            CaptureSourceState.Warning => 2,
            _ => 1,
        };

    private static void SetStatusIcon(
        FontIcon icon,
        CaptureSourceState state)
    {
        icon.Foreground = state switch
        {
            CaptureSourceState.Ready => ResourceBrush("SuccessBrush"),
            CaptureSourceState.Warning => ResourceBrush("WarningBrush"),
            CaptureSourceState.Error or CaptureSourceState.Unavailable =>
                ResourceBrush("DangerBrush"),
            _ => ResourceBrush("StudioMutedBrush"),
        };
    }

    private void UpdateReadinessSummary()
    {
        if (_recordingSession is not null)
        {
            return;
        }

        if (_lastSnapshot is null)
        {
            StartRecordingButton.IsEnabled = false;
            return;
        }

        bool canRecord = _lastSnapshot.CanRecord;
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
            ReadinessInfoBar.Title = "Ready";
            ReadinessInfoBar.Message =
                "Press Record, choose an application or display, and capture starts immediately.";
            ReadinessInfoBar.Severity = InfoBarSeverity.Success;
            return;
        }

        ReadinessInfoBar.Title = "Ready to record";
        CaptureReadinessItem camera = _lastSnapshot.Items
            .Single(item => item.Key == "camera");
        bool includeCamera =
            _cameraEnabled &&
            camera.State is (
                CaptureSourceState.Ready or CaptureSourceState.Warning);
        ReadinessInfoBar.Message = includeCamera
            ? "Press Record for the selected source with camera and available audio."
            : "Press Record for the selected source. Unavailable optional sources will be skipped.";
        ReadinessInfoBar.Severity = InfoBarSeverity.Success;
    }

    private void OnCaptureHealthChanged(CaptureFrameHealthSnapshot health)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TimeSpan displayedTime = _recordingSession?.MapActiveTime(health.LastProjectTime) ??
                health.LastProjectTime;
            FrameStatusText.Text =
                $"{health.FramesReceived:N0} frames, " +
                $"{health.FramesDropped:N0} dropped, " +
                $"{displayedTime:mm\\:ss\\.fff} elapsed.";
        });
    }

    private void OnCaptureClosed() =>
        DispatcherQueue.TryEnqueue(StopCaptureFromDispatcher);

    private void OnCaptureFailed(Exception exception) =>
        DispatcherQueue.TryEnqueue(() =>
            _ = StopCaptureAfterFailureAsync(exception));

    private async Task StopCaptureAfterFailureAsync(Exception exception)
    {
        FrameStatusText.Text = $"Capture failed: {exception.Message}";
        ReadinessInfoBar.Title = "Screen capture failed";
        ReadinessInfoBar.Message = exception.Message;
        ReadinessInfoBar.Severity = InfoBarSeverity.Error;
        await StopCaptureAsync(RecordingStopReason.CaptureFailed);
    }

    private async void StopCaptureFromDispatcher()
    {
        await StopCaptureAsync(RecordingStopReason.CaptureClosed);
    }

    private Task StopCaptureAsync(
        RecordingStopReason reason = RecordingStopReason.User)
    {
        lock (_stopCaptureGate)
        {
            if (_stopCaptureTask is { IsCompleted: false })
            {
                return _stopCaptureTask;
            }

            if (_recorderState.Snapshot.State is RecorderState.Starting)
            {
                if (_recorderState.TryBeginStop(out _))
                {
                    _recordingStartupCancellation?.Cancel();
                }

                return _recordingStartupCompletion?.Task ??
                    Task.CompletedTask;
            }

            if (_recordingSession is null ||
                !_recorderState.TryBeginStop(out _))
            {
                return Task.CompletedTask;
            }

            Task stopTask = StopCaptureCoreAsync(reason);
            _stopCaptureTask = stopTask;
            return AwaitStopAndClearAsync(stopTask);
        }
    }

    private async Task AwaitStopAndClearAsync(Task stopTask)
    {
        try
        {
            await stopTask;
        }
        finally
        {
            lock (_stopCaptureGate)
            {
                if (ReferenceEquals(_stopCaptureTask, stopTask))
                {
                    _stopCaptureTask = null;
                }
            }
        }
    }

    private async Task StopCaptureCoreAsync(RecordingStopReason reason)
    {
        WindowsRecordingSession? session = _recordingSession;
        if (session is null)
        {
            return;
        }

        _recordingSession = null;
        session.ScreenHealthChanged -= OnCaptureHealthChanged;
        session.AudioHealthChanged -= OnAudioHealthChanged;
        session.CaptureClosed -= OnCaptureClosed;
        session.CaptureFailed -= OnCaptureFailed;
        _microphoneHealth = null;
        _systemAudioHealth = null;

        bool finalizationFailed = false;
        try
        {
            WindowsRecordingFinalizationResult result =
                await session.StopAsync(reason);
            WindowsRecordingIssue[] errors = result.Issues
                .Where(issue =>
                    issue.Severity is RecordingIssueSeverity.Error)
                .ToArray();
            finalizationFailed = errors.Length > 0 || result.Screen is null;

            if (result.Screen is not null)
            {
                FrameStatusText.Text =
                    $"Saved {result.ScreenHealth.FramesReceived:N0} captured frames to " +
                    $"{result.Screen.RelativePath}; " +
                    $"{result.ScreenHealth.FramesDropped:N0} dropped" +
                    (result.Audio is null
                        ? "."
                        : $"; audio saved to {result.Audio.Microphone.RelativePath} and " +
                          $"{result.Audio.SystemAudio.RelativePath}") +
                    (result.Camera is null
                        ? "."
                        : $"; camera saved to {result.Camera.Segment.RelativePath} with " +
                          $"{result.Camera.Layout.Mode} layout.");
            }
            else
            {
                FrameStatusText.Text =
                    $"Stopped after {result.ScreenHealth.FramesReceived:N0} frames; " +
                    "the screen segment could not be published.";
            }

            WindowsRecordingIssue[] warnings = result.Issues
                .Where(issue =>
                    issue.Severity is RecordingIssueSeverity.Warning)
                .ToArray();
            if (!finalizationFailed && warnings.Length > 0)
            {
                ReadinessInfoBar.Title = "Recording saved with warnings";
                ReadinessInfoBar.Message = string.Join(
                    " ",
                    warnings.Select(issue => issue.Message));
                ReadinessInfoBar.Severity = InfoBarSeverity.Warning;
            }
            else if (finalizationFailed)
            {
                string message = errors.Length > 0
                    ? string.Join(" ", errors.Select(issue => issue.Message))
                    : "The screen segment could not be published.";
                if (_recorderState.Snapshot.State is RecorderState.Stopping)
                {
                    _recorderState.MarkFaulted(message);
                }

                ReadinessInfoBar.Title = "Recording could not be finalized";
                ReadinessInfoBar.Message = message;
                ReadinessInfoBar.Severity = InfoBarSeverity.Error;
            }

            if (!finalizationFailed)
            {
                StartProjectPostProcessing(result.ProjectRoot);
            }
        }
        catch (Exception exception)
        {
            finalizationFailed = true;
            System.Diagnostics.Debug.WriteLine(exception);
            if (_recorderState.Snapshot.State is RecorderState.Stopping)
            {
                _recorderState.MarkFaulted(exception.Message);
            }
            FrameStatusText.Text = $"Segment finalization failed: {exception.Message}";
            ReadinessInfoBar.Title = "Recording could not be finalized";
            ReadinessInfoBar.Message = exception.Message;
            ReadinessInfoBar.Severity = InfoBarSeverity.Error;
        }

        if (!finalizationFailed &&
            _recorderState.Snapshot.State is RecorderState.Stopping)
        {
            _recorderState.CompleteStop();
        }

        StartRecordingButton.Content = "Record";
        PauseRecordingButton.Content = "Pause";
        PauseRecordingButton.IsEnabled = false;
        ChooseSourceButton.IsEnabled = true;
        CameraOverlayToggle.IsEnabled = true;
        RefreshReadinessButton.IsEnabled = true;
        if (ReadinessInfoBar.Severity is
            InfoBarSeverity.Informational or InfoBarSeverity.Success)
        {
            await RefreshReadinessAsync();
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

    private static string IssueMessage(
        IEnumerable<WindowsRecordingIssue> issues,
        string component,
        string fallback)
    {
        WindowsRecordingIssue? issue = issues.FirstOrDefault(candidate =>
            candidate.Component.StartsWith(
                component,
                StringComparison.OrdinalIgnoreCase));
        return issue is null
            ? fallback
            : $"{fallback} {issue.Message}";
    }

    private void StartProjectPostProcessing(string projectRoot)
    {
        _latestPostProcessingProject = Path.GetFullPath(projectRoot);
        FrameStatusText.Text += " Smart edits are processing in the background.";
        _ = RunProjectPostProcessingAsync(_latestPostProcessingProject);
    }

    private async Task RunProjectPostProcessingAsync(string projectRoot)
    {
        CancellationToken cancellationToken = _postProcessingCancellation.Token;
        try
        {
            ProjectPostProcessingResult result =
                await _postProcessingPipeline.RunAsync(
                    projectRoot,
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "MediaWorker",
                        "SevenRecord.Media.Worker.exe"),
                    cancellationToken);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed ||
                    !string.Equals(
                        _latestPostProcessingProject,
                        result.ProjectRoot,
                        StringComparison.OrdinalIgnoreCase) ||
                    _recorderState.Snapshot.State is not RecorderState.Idle)
                {
                    return;
                }

                ProjectPostProcessingStageResult[] failures = result.Stages
                    .Where(stage =>
                        stage.State is ProjectPostProcessingStageState.Failed)
                    .ToArray();
                if (failures.Length == 0)
                {
                    FrameStatusText.Text =
                        $"Smart edits ready: {result.SuggestedEdits} suggestion(s).";
                    return;
                }

                FrameStatusText.Text =
                    "Recording saved; some smart edits could not be generated.";
                ReadinessInfoBar.Title = "Smart edits need attention";
                ReadinessInfoBar.Message = string.Join(
                    " ",
                    failures.Select(failure =>
                        $"{failure.Stage}: {failure.Message}"));
                ReadinessInfoBar.Severity = InfoBarSeverity.Warning;
            });
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed ||
                    !string.Equals(
                        _latestPostProcessingProject,
                        projectRoot,
                        StringComparison.OrdinalIgnoreCase) ||
                    _recorderState.Snapshot.State is not RecorderState.Idle)
                {
                    return;
                }

                FrameStatusText.Text =
                    "Recording saved; smart-edit processing stopped unexpectedly.";
                ReadinessInfoBar.Title = "Smart edits need attention";
                ReadinessInfoBar.Message = exception.Message;
                ReadinessInfoBar.Severity = InfoBarSeverity.Warning;
            });
        }
    }

    private void OnAudioHealthChanged(AudioCaptureHealth health)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (health.Source is AudioCaptureSource.Microphone)
            {
                _microphoneHealth = health;
            }
            else
            {
                _systemAudioHealth = health;
            }

            AudioStatusText.Text = BuildAudioHealthText();
            UpdateAudioWarningState();
        });
    }

    private string BuildAudioHealthText() =>
        DescribeAudioHealth("Mic", _microphoneHealth) + Environment.NewLine +
        DescribeAudioHealth("System", _systemAudioHealth);

    private static string DescribeAudioHealth(string source, AudioCaptureHealth? health)
    {
        if (health is null)
        {
            return $"{source}: waiting for samples.";
        }

        string missing = health.TotalMissingDuration > TimeSpan.Zero
            ? $", {health.TotalMissingDuration.TotalMilliseconds:0.#} ms missing"
            : string.Empty;
        return
            $"{source}: {health.Drift.Drift.TotalMilliseconds:+0.0;-0.0;0.0} ms drift " +
            $"({health.Drift.PartsPerMillion:+0;-0;0} ppm), " +
            $"{health.Discontinuities} discontinuities{missing}.";
    }

    private static bool HasAudioSyncRisk(AudioCaptureHealth? health) =>
        health is not null &&
        (health.Drift.Exceeds(AudioDriftWarningThreshold) ||
         health.Discontinuities > 0 ||
         health.TotalMissingDuration >= AudioMissingWarningThreshold);

    private void UpdateAudioWarningState()
    {
        if (_recordingSession is null ||
            _recordingSession.IsPaused ||
            ReadinessInfoBar.Severity is InfoBarSeverity.Error)
        {
            return;
        }

        bool microphoneRisk = HasAudioSyncRisk(_microphoneHealth);
        bool systemRisk = HasAudioSyncRisk(_systemAudioHealth);
        if (!microphoneRisk && !systemRisk)
        {
            if (ReadinessInfoBar.Title == "Audio sync warning")
            {
                ReadinessInfoBar.Title = "Recording";
                ReadinessInfoBar.Message =
                    "Capturing Direct3D surfaces with Media Foundation.";
                ReadinessInfoBar.Severity = InfoBarSeverity.Informational;
            }

            return;
        }

        ReadinessInfoBar.Title = "Audio sync warning";
        ReadinessInfoBar.Message = BuildAudioWarningMessage(
            _microphoneHealth,
            _systemAudioHealth);
        ReadinessInfoBar.Severity = InfoBarSeverity.Warning;
    }

    private static string BuildAudioWarningMessage(
        AudioCaptureHealth? microphoneHealth,
        AudioCaptureHealth? systemAudioHealth)
    {
        List<string> details = [];
        if (HasAudioSyncRisk(microphoneHealth))
        {
            details.Add(
                $"Mic {microphoneHealth!.Drift.Drift.TotalMilliseconds:+0.0;-0.0;0.0} ms drift, " +
                $"{microphoneHealth.Drift.PartsPerMillion:+0;-0;0} ppm, " +
                $"{microphoneHealth.Discontinuities} discontinuities, " +
                $"{microphoneHealth.TotalMissingDuration.TotalMilliseconds:0.#} ms missing");
        }

        if (HasAudioSyncRisk(systemAudioHealth))
        {
            details.Add(
                $"System {systemAudioHealth!.Drift.Drift.TotalMilliseconds:+0.0;-0.0;0.0} ms drift, " +
                $"{systemAudioHealth.Drift.PartsPerMillion:+0;-0;0} ppm, " +
                $"{systemAudioHealth.Discontinuities} discontinuities, " +
                $"{systemAudioHealth.TotalMissingDuration.TotalMilliseconds:0.#} ms missing");
        }

        return details.Count == 0
            ? "Audio sync risk detected. Consider restarting capture."
            : $"Audio sync risk detected: {string.Join("; ", details)}. Consider restarting capture.";
    }

    private void RegisterGlobalHotKeys()
    {
        if (_globalHotKeys is not null)
        {
            HotkeyStatusText.Text =
                "Global shortcuts active: Ctrl+Shift+R (record) and Ctrl+Shift+P (pause).";
            RefreshHotkeysButton.IsEnabled = false;
            UpdateReadinessSummary();
            return;
        }

        try
        {
            App application = (App)Application.Current;
            nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(
                application.MainWindow);
            _globalHotKeys = new GlobalHotKeyService(windowHandle);
            _globalHotKeys.Triggered += OnGlobalHotKey;
            HotkeyStatusText.Text =
                "Global shortcuts active: Ctrl+Shift+R (record) and Ctrl+Shift+P (pause).";
            RefreshHotkeysButton.IsEnabled = false;
            UpdateReadinessSummary();
        }
        catch (Win32Exception exception)
        {
            ReadinessInfoBar.Title = "Global shortcut unavailable";
            ReadinessInfoBar.Message = exception.Message;
            ReadinessInfoBar.Severity = InfoBarSeverity.Warning;
            HotkeyStatusText.Text = $"Global shortcuts unavailable: {exception.Message}";
            RefreshHotkeysButton.IsEnabled = true;
        }
    }

    private void OnGlobalHotKey(GlobalHotKeyAction action)
    {
        if (action is GlobalHotKeyAction.StartStopRecording)
        {
            OnStartRecordingClicked(this, new RoutedEventArgs());
        }
        else
        {
            OnPauseRecordingClicked(this, new RoutedEventArgs());
        }
    }

    private void DisposeGlobalHotKeys()
    {
        if (_globalHotKeys is null)
        {
            return;
        }

        _globalHotKeys.Triggered -= OnGlobalHotKey;
        _globalHotKeys.Dispose();
        _globalHotKeys = null;
        RefreshHotkeysButton.IsEnabled = true;
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
                string displayName =
                    FormatProjectDisplayName(project.Name);
                ListViewItem item = new()
                {
                    Content = CreateProjectListContent(
                        project,
                        displayName),
                    Tag = project.Path,
                };
                AutomationProperties.SetName(
                    item,
                    $"{displayName}, {project.RecoveryState}, " +
                    $"{project.Duration:hh\\:mm\\:ss}, " +
                    $"{project.MediaSegments} sources");
                RecentProjectsList.Items.Add(item);
            }

            ProjectsEmptyText.Visibility = projects.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            RecentProjectsList.Items.Clear();
            ProjectsEmptyText.Text =
                $"Projects could not be loaded. {exception.Message}";
            ProjectsEmptyText.Visibility = Visibility.Visible;
        }
        finally
        {
            RefreshProjectsButton.IsEnabled = true;
        }
    }

    private StackPanel CreateProjectListContent(
        ProjectSummary project,
        string displayName)
    {
        StackPanel content = new()
        {
            Padding = new Thickness(4, 8, 4, 8),
            Spacing = 4,
        };
        content.Children.Add(
            new TextBlock
            {
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ResourceBrush("StudioInkBrush"),
                Text = displayName,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        content.Children.Add(
            new TextBlock
            {
                FontSize = 13,
                Foreground = ResourceBrush("StudioMutedBrush"),
                Text =
                    $"{project.RecoveryState} · {project.Duration:hh\\:mm\\:ss} · " +
                    $"{project.MediaSegments} source(s)",
            });
        content.Children.Add(
            new TextBlock
            {
                FontSize = 13,
                Foreground = ResourceBrush("StudioMutedBrush"),
                MaxLines = 2,
                Text = project.StatusMessage,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap,
            });
        Button openButton = new()
        {
            Content = "Open recording",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
            Tag = project.Path,
        };
        AutomationProperties.SetName(
            openButton,
            $"Open {displayName}");
        openButton.Click += OnOpenProjectButtonClicked;
        content.Children.Add(openButton);
        return content;
    }

    private static string FormatProjectDisplayName(string name)
    {
        const string TimestampFormat = "yyyyMMdd-HHmmss";
        if (name.Length >= TimestampFormat.Length &&
            DateTime.TryParseExact(
                name[..TimestampFormat.Length],
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out DateTime timestamp))
        {
            return $"Recording · {timestamp:MMM d, yyyy} · {timestamp:h:mm tt}";
        }

        return name;
    }

}
