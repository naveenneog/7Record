using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Media.Core;
using Windows.Media.Playback;
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
using SevenRecord.Domain.Video;
using SevenRecord.Editor;
using SevenRecord.Export;
using SevenRecord.Infrastructure;
using SevenRecord.Input.Windows;
using SevenRecord.Media;
using SevenRecord.Media.Windows;
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
    private PresenterLayoutSettings _cameraLayout =
        PresenterLayoutSettings.DefaultOverlay;
    private CameraPreviewSession? _cameraPreviewSession;
    private CancellationTokenSource? _cameraPreviewStartupCancellation;
    private CancellationTokenSource? _cameraSettingsSaveCancellation;
    private bool _cameraOverlayDragging;
    private bool _cameraEnabled = true;
    private bool _disposed;
    private bool _loadingProject;
    private bool _updatingCameraStudioControls;
    private readonly SemaphoreSlim _cameraEffectTransitionGate =
        new(1, 1);
    private bool _updatingCameraToggle;
    private GlobalHotKeyService? _globalHotKeys;
    private WindowsRecordingSession? _recordingSession;
    private CancellationTokenSource? _recordingStartupCancellation;
    private TaskCompletionSource? _recordingStartupCompletion;
    private CaptureReadinessSnapshot? _lastSnapshot;
    private WindowsCaptureTarget? _selectedScreen;
    private SoftwareBitmapSource? _screenPreviewSource;
    private SoftwareBitmapSource? _cameraPreviewSource;
    private int _screenPreviewPixelHeight;
    private int _screenPreviewPixelWidth;
    private int _cameraPreviewPixelHeight;
    private int _cameraPreviewPixelWidth;
    private SoftwareBitmapPreviewFrame? _pendingScreenPreview;
    private SoftwareBitmapPreviewFrame? _pendingCameraPreview;
    private int _screenPreviewDispatchPending;
    private int _cameraPreviewDispatchPending;
    private uint _cameraOverlayPointerId;
    private Windows.Foundation.Point _cameraOverlayDragStart;
    private double _cameraOverlayStartX;
    private double _cameraOverlayStartY;
    private string? _currentPreviewPath;
    private MediaPlaybackList? _projectPlaybackList;
    private TimelineDocument? _currentTimeline;
    private CaptionEditSession? _captionEditSession;
    private string? _latestPostProcessingProject;
    private Task? _stopCaptureTask;
    private Task<bool>? _shutdownTask;
    private readonly HashSet<string> _disabledAutomation =
        new(StringComparer.Ordinal);

    public MainPage()
    {
        InitializeComponent();
        CameraOverlayCanvas.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnCameraOverlayCanvasPointerPressed),
            handledEventsToo: true);
        CameraOverlayCanvas.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(OnCameraOverlayCanvasPointerMoved),
            handledEventsToo: true);
        CameraOverlayCanvas.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnCameraOverlayCanvasPointerReleased),
            handledEventsToo: true);
        CameraOverlayCanvas.AddHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler(OnCameraOverlayCanvasPointerReleased),
            handledEventsToo: true);
        CameraOverlayCanvas.AddHandler(
            UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnCameraOverlayCanvasPointerReleased),
            handledEventsToo: true);
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
        SetProjectActionsEnabled(false);
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
                ShowLivePreviewShell();
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
                ShowLivePreviewShell();
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
                ShowLivePreviewShell();
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
                ResetLivePreview();
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
                ResetLivePreview();
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
        if (_cameraPreviewSession is not null)
        {
            await StopCameraStudioAsync();
            return;
        }
        await StartCameraStudioAsync();
    }

    private async Task<bool> StartCameraStudioAsync()
    {
        await _cameraEffectTransitionGate.WaitAsync();
        try
        {
            return await StartCameraStudioCoreAsync();
        }
        finally
        {
            _cameraEffectTransitionGate.Release();
        }
    }

    private async Task<bool> StartCameraStudioCoreAsync()
    {
        if (_cameraPreviewSession is not null ||
            !_cameraEnabled ||
            _recorderState.Snapshot.IsActive)
        {
            return _cameraPreviewSession is not null;
        }

        ConfigureCameraButton.IsEnabled = false;
        CameraStudioPanel.Visibility = Visibility.Visible;
        CameraStudioStatusText.Text = "Starting camera preview...";
        _cameraPreviewStartupCancellation?.Cancel();
        _cameraPreviewStartupCancellation?.Dispose();
        CancellationTokenSource startupCancellation = new();
        _cameraPreviewStartupCancellation = startupCancellation;
        try
        {
            CameraPreviewSession session =
                await CameraPreviewSession.CreateAsync(
                    _cameraLayout,
                    startupCancellation.Token);
            if (startupCancellation.IsCancellationRequested ||
                !_cameraEnabled ||
                _recorderState.Snapshot.IsActive ||
                CameraStudioPanel.Visibility is not Visibility.Visible)
            {
                await session.DisposeAsync();
                return false;
            }
            session.FrameReady += OnCameraPreviewFrameReady;
            session.Failed += OnCameraStudioFailed;
            _cameraPreviewSession = session;
            _cameraLayout = _cameraLayout with
            {
                Effects = _cameraLayout.Effects with
                {
                    BackgroundBlur =
                        session.BackgroundEffects.ActiveMode,
                },
            };
            ConfigureBackgroundEffectControls(
                session.BackgroundEffects);
            session.UpdateLayout(_cameraLayout);
            CameraStatusText.Text =
                $"{session.DeviceName} ({session.Width} x {session.Height}) is ready.";
            CameraStudioStatusText.Text =
                session.BackgroundEffects.Message ??
                "Drag the camera bubble to place it. Adjust framing and effects before recording.";
            ConfigureCameraButton.Content = "Close camera studio";
            ShowLivePreviewShell();
            return true;
        }
        catch (OperationCanceledException)
            when (startupCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            CameraStudioStatusText.Text =
                $"Camera preview could not start: {exception.Message}";
            CameraStatusText.Text =
                "Camera preview is unavailable. Recording can continue without it.";
            ConfigureBackgroundEffectControls(
                new BackgroundEffectSupport(
                    false,
                    false,
                    BackgroundBlurMode.Off,
                    "Windows Studio Effects are unavailable."));
            return false;
        }
        finally
        {
            ConfigureCameraButton.IsEnabled =
                _recordingSession is null &&
                (_recorderState.Snapshot.State is
                    RecorderState.Idle or RecorderState.Faulted) &&
                CameraOverlayToggle.IsEnabled;
        }
    }

    private async Task<bool> StopCameraStudioAsync(bool resetPreview = true)
    {
        await _cameraEffectTransitionGate.WaitAsync();
        try
        {
            return await StopCameraStudioCoreAsync(resetPreview);
        }
        finally
        {
            _cameraEffectTransitionGate.Release();
        }
    }

    private async Task<bool> StopCameraStudioCoreAsync(
        bool resetPreview = true)
    {
        CameraPreviewSession? session = _cameraPreviewSession;
        _cameraPreviewSession = null;
        string? closeWarning = null;
        _cameraPreviewStartupCancellation?.Cancel();
        _cameraPreviewStartupCancellation?.Dispose();
        _cameraPreviewStartupCancellation = null;
        if (session is not null)
        {
            session.FrameReady -= OnCameraPreviewFrameReady;
            session.Failed -= OnCameraStudioFailed;
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception exception)
            {
                closeWarning =
                    $"Camera closed with a settings warning: {exception.Message}";
                if (exception is CameraBackgroundEffectRestoreException)
                {
                    _cameraPreviewSession = session;
                }
            }
        }
        CameraStudioPanel.Visibility = Visibility.Collapsed;
        CameraStudioStatusText.Text =
            closeWarning ?? "Camera studio is closed.";
        ConfigureCameraButton.Content = "Open camera studio";
        if (closeWarning is not null)
        {
            CameraStatusText.Text = closeWarning;
            ReadinessInfoBar.Title = "Camera settings need attention";
            ReadinessInfoBar.Message = closeWarning;
            ReadinessInfoBar.Severity = InfoBarSeverity.Warning;
        }
        if (resetPreview && _recordingSession is null)
        {
            ResetLivePreview();
        }
        return closeWarning is null;
    }

    private void OnCameraStudioFailed(Exception exception) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            CameraStudioStatusText.Text =
                $"Camera preview stopped: {exception.Message}";
        });

    private void OnCameraStudioValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs e)
    {
        if (_updatingCameraStudioControls ||
            CameraZoomSlider is null ||
            CameraCenterXSlider is null ||
            CameraCenterYSlider is null ||
            CameraSizeSlider is null ||
            CameraExposureSlider is null)
        {
            return;
        }

        PresenterLayoutSettings previousLayout = _cameraLayout;
        _cameraLayout = (_cameraLayout with
        {
            Width = CameraSizeSlider.Value,
            Height = CameraSizeSlider.Value,
            Framing = new CameraFramingSettings(
                CameraZoomSlider.Value,
                CameraCenterXSlider.Value,
                CameraCenterYSlider.Value),
            Effects = new CameraEffectSettings(
                CameraExposureSlider.Value)
            {
                BackgroundBlur =
                    _cameraLayout.Effects.BackgroundBlur,
            },
        }).ConstrainToFrame();
        _cameraPreviewSession?.UpdateLayout(_cameraLayout);
        _recordingSession?.UpdateCameraLayout(_cameraLayout);
        ApplyCameraOverlayLayout();
        ApplyCameraFramingTransform();
        UpdateCameraStudioStatus();
        ScheduleCameraStudioSave();
    }

    private async void OnResetCameraStudioClicked(
        object sender,
        RoutedEventArgs e)
    {
        await _cameraEffectTransitionGate.WaitAsync();
        try
        {
            CameraBackgroundBlurComboBox.IsEnabled = false;
            PresenterLayoutSettings previousLayout = _cameraLayout;
            _cameraLayout = PresenterLayoutSettings.DefaultOverlay;
            if (_cameraPreviewSession is not null)
            {
                if (!await StopCameraStudioCoreAsync(resetPreview: false))
                {
                    _cameraLayout = previousLayout;
                    UpdateCameraStudioControls();
                    return;
                }
                if (!await StartCameraStudioCoreAsync())
                {
                    _cameraLayout = previousLayout;
                    UpdateCameraStudioControls();
                    return;
                }
            }
            UpdateCameraStudioControls();
            _cameraPreviewSession?.UpdateLayout(_cameraLayout);
            _recordingSession?.UpdateCameraLayout(_cameraLayout);
            ApplyCameraOverlayLayout();
            ApplyCameraFramingTransform();
            UpdateCameraStudioStatus();
            ScheduleCameraStudioSave();
        }
        finally
        {
            CameraBackgroundBlurComboBox.IsEnabled =
                _cameraPreviewSession?.BackgroundEffects.IsSupported is true;
            _cameraEffectTransitionGate.Release();
        }
    }

    private async void OnCloseCameraStudioClicked(
        object sender,
        RoutedEventArgs e) =>
        await StopCameraStudioAsync();

    private async void OnCameraBackgroundBlurChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_updatingCameraStudioControls ||
            CameraBackgroundBlurComboBox.SelectedItem is not
                ComboBoxItem { Tag: string modeName } ||
            !Enum.TryParse(modeName, out BackgroundBlurMode mode))
        {
            return;
        }

        await _cameraEffectTransitionGate.WaitAsync();
        try
        {
            CameraBackgroundBlurComboBox.IsEnabled = false;
            PresenterLayoutSettings previousLayout = _cameraLayout;
            _cameraLayout = (_cameraLayout with
            {
                Effects = _cameraLayout.Effects with
                {
                    BackgroundBlur = mode,
                },
            }).ConstrainToFrame();
            PresenterLayoutSettings requestedLayout = _cameraLayout;
            if (_cameraPreviewSession is not null)
            {
                if (!await StopCameraStudioCoreAsync(resetPreview: false))
                {
                    _cameraLayout = previousLayout;
                    UpdateCameraStudioControls();
                    ReadinessInfoBar.Title = "Camera settings need attention";
                    ReadinessInfoBar.Message =
                        "The background effect was not changed because the prior camera setting could not be restored.";
                    ReadinessInfoBar.Severity = InfoBarSeverity.Error;
                    return;
                }
                _cameraLayout = requestedLayout;
                if (!await StartCameraStudioCoreAsync())
                {
                    _cameraLayout = previousLayout;
                    UpdateCameraStudioControls();
                    return;
                }
            }
            ScheduleCameraStudioSave();
            UpdateCameraStudioStatus();
        }
        finally
        {
            CameraBackgroundBlurComboBox.IsEnabled =
                _cameraPreviewSession?.BackgroundEffects.IsSupported is true;
            _cameraEffectTransitionGate.Release();
        }
    }

    private void UpdateCameraStudioControls()
    {
        _updatingCameraStudioControls = true;
        try
        {
            CameraZoomSlider.Value = _cameraLayout.Framing.Zoom;
            CameraCenterXSlider.Value = _cameraLayout.Framing.CenterX;
            CameraCenterYSlider.Value = _cameraLayout.Framing.CenterY;
            CameraSizeSlider.Value = _cameraLayout.Width;
            CameraExposureSlider.Value = _cameraLayout.Effects.Exposure;
            SelectBackgroundBlurMode(
                _cameraLayout.Effects.BackgroundBlur);
        }
        finally
        {
            _updatingCameraStudioControls = false;
        }
        UpdateCameraStudioStatus();
    }

    private void UpdateCameraStudioStatus()
    {
        string status =
            $"Overlay {_cameraLayout.X:P0} left, {_cameraLayout.Y:P0} top, " +
            $"size {_cameraLayout.Width:P0}; zoom {_cameraLayout.Framing.Zoom:F1}x; " +
            $"brightness {_cameraLayout.Effects.Exposure:+0.00;-0.00;0.00}; " +
            $"background {_cameraLayout.Effects.BackgroundBlur}.";
        CameraStudioStatusText.Text = status;
        AutomationProperties.SetItemStatus(
            CameraPreviewBubble,
            status);
    }

    private void ApplyCameraFramingTransform()
    {
        double zoom = _cameraLayout.Framing.Zoom;
        CameraPreviewImage.RenderTransformOrigin =
            new Windows.Foundation.Point(
                _cameraLayout.Framing.CenterX,
                _cameraLayout.Framing.CenterY);
        CameraPreviewImage.RenderTransform = new CompositeTransform
        {
            ScaleX = zoom,
            ScaleY = zoom,
        };
    }

    private void ScheduleCameraStudioSave()
    {
        _cameraSettingsSaveCancellation?.Cancel();
        _cameraSettingsSaveCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _cameraSettingsSaveCancellation = cancellation;
        _ = SaveCameraStudioAfterDelayAsync(cancellation);
    }

    private void ConfigureBackgroundEffectControls(
        BackgroundEffectSupport support)
    {
        _updatingCameraStudioControls = true;
        try
        {
            CameraBackgroundBlurComboBox.Items.Clear();
            CameraBackgroundBlurComboBox.Items.Add(
                new ComboBoxItem
                {
                    Content = "Off",
                    Tag = BackgroundBlurMode.Off.ToString(),
                });
            if (support.StandardBlur)
            {
                CameraBackgroundBlurComboBox.Items.Add(
                    new ComboBoxItem
                    {
                        Content = "Standard blur",
                        Tag = BackgroundBlurMode.Standard.ToString(),
                    });
            }
            if (support.PortraitBlur)
            {
                CameraBackgroundBlurComboBox.Items.Add(
                    new ComboBoxItem
                    {
                        Content = "Portrait blur",
                        Tag = BackgroundBlurMode.Portrait.ToString(),
                    });
            }
            CameraBackgroundBlurComboBox.IsEnabled =
                support.IsSupported;
            SelectBackgroundBlurMode(support.ActiveMode);
        }
        finally
        {
            _updatingCameraStudioControls = false;
        }
    }

    private void SelectBackgroundBlurMode(BackgroundBlurMode mode)
    {
        for (int index = 0;
             index < CameraBackgroundBlurComboBox.Items.Count;
             index++)
        {
            if (CameraBackgroundBlurComboBox.Items[index] is
                ComboBoxItem { Tag: string tag } &&
                string.Equals(
                    tag,
                    mode.ToString(),
                    StringComparison.Ordinal))
            {
                CameraBackgroundBlurComboBox.SelectedIndex = index;
                return;
            }
        }
        CameraBackgroundBlurComboBox.SelectedIndex = 0;
    }

    private async Task SaveCameraStudioAfterDelayAsync(
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(300),
                cancellation.Token);
            await CameraStudioSettingsStore.SaveAsync(
                _cameraLayout,
                cancellationToken: cancellation.Token);
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Camera studio settings could not be saved: {exception}");
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyAdaptiveLayout(ActualWidth);
        CameraStudioSettingsLoadResult cameraSettings =
            await CameraStudioSettingsStore.LoadAsync();
        _cameraLayout = cameraSettings.Layout;
        UpdateCameraStudioControls();
        if (!string.IsNullOrWhiteSpace(cameraSettings.Warning))
        {
            CameraStatusText.Text = cameraSettings.Warning;
        }
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

    private async void OnCameraOverlayToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingCameraToggle)
        {
            return;
        }

        _cameraEnabled = CameraOverlayToggle.IsOn;
        CameraStatusText.Text = _cameraEnabled
            ? "The default camera will start automatically with recording."
            : "Camera overlay is off.";
        if (!_cameraEnabled)
        {
            await StopCameraStudioAsync();
        }
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
        ClearProjectEditorState();
        _loadingProject = true;
        TimelineProjectTitle.Text = "Opening recording...";
        ProjectDetailEmptyState.Visibility = Visibility.Collapsed;
        TimelineSection.Visibility = Visibility.Visible;
        try
        {
            TimelineDocument timeline = await ProjectTimelineLoader.LoadAsync(projectPath);
            CaptionEditSession? captionSession =
                await LoadCaptionEditSessionAsync(projectPath, timeline.Duration);
            EditorProjectStateLoadResult editorState =
                await EditorProjectStateStore.LoadAsync(projectPath);

            _currentTimeline = timeline;
            _captionEditSession = captionSession;
            HashSet<string> validAutomationIds = timeline.Automation
                .Select(item => item.Id)
                .ToHashSet(StringComparer.Ordinal);
            foreach (string automationId in editorState.State.DisabledAutomationIds)
            {
                if (validAutomationIds.Contains(automationId))
                {
                    _disabledAutomation.Add(automationId);
                }
            }
            RenderPresetComboBox.SelectedIndex =
                editorState.State.RenderPresetIndex;
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
                    IsChecked = !_disabledAutomation.Contains(automation.Id),
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

            PopulateCaptionEditor();
            UpdateRenderPlanSummary();
            if (!string.IsNullOrWhiteSpace(editorState.Warning))
            {
                RenderPlanSummaryText.Text += $" {editorState.Warning}";
            }
            ProjectDetailEmptyState.Visibility = Visibility.Collapsed;
            TimelineSection.Visibility = Visibility.Visible;
            TimelineSection.StartBringIntoView();
            ProjectPreviewPlayer.Focus(FocusState.Programmatic);
        }
        catch (Exception exception)
        {
            ClearProjectEditorState();
            TimelineProjectTitle.Text = $"Timeline could not be loaded: {exception.Message}";
            ProjectDetailEmptyState.Visibility = Visibility.Collapsed;
            TimelineSection.Visibility = Visibility.Visible;
            ProjectPreviewStatusText.Text =
                "This recording was not opened. Review its recovery status and try again.";
        }
        finally
        {
            _loadingProject = false;
            SetProjectActionsEnabled(_currentTimeline is not null);
        }
    }

    private void ClearProjectEditorState()
    {
        _currentTimeline = null;
        _captionEditSession = null;
        _disabledAutomation.Clear();
        _currentPreviewPath = null;
        _projectPlaybackList = null;
        ProjectPreviewPlayer.Source = null;
        _projectPlaybackList = null;
        ProjectPreviewStatusText.Text = "No recording is loaded.";
        TimelineItemsList.Items.Clear();
        CaptionSelectorComboBox.Items.Clear();
        CaptionEditorPanel.Visibility = Visibility.Collapsed;
        OpenRecordingExternallyButton.IsEnabled = false;
        OpenProjectFolderButton.IsEnabled = false;
        SetProjectActionsEnabled(false);
    }

    private void SetProjectActionsEnabled(bool enabled)
    {
        RenderPresetComboBox.IsEnabled = enabled;
        SaveRenderPlanButton.IsEnabled = enabled;
        ExportMp4Button.IsEnabled = enabled;
        GenerateCaptionsButton.IsEnabled = enabled;
    }

    private async Task PersistEditorStateAsync()
    {
        if (_loadingProject || _currentTimeline is null)
        {
            return;
        }

        try
        {
            await EditorProjectStateStore.SaveAsync(
                _currentTimeline.ProjectPath,
                new EditorProjectState(
                    1,
                    Math.Clamp(RenderPresetComboBox.SelectedIndex, 0, 2),
                    _disabledAutomation
                        .Order(StringComparer.Ordinal)
                        .ToArray()));
        }
        catch (IOException exception)
        {
            RenderPlanSummaryText.Text =
                $"Editor choices could not be saved: {exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            RenderPlanSummaryText.Text =
                $"Editor choices could not be saved: {exception.Message}";
        }
    }

    private async void OnAutomationToggled(object sender, RoutedEventArgs e)
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
        await PersistEditorStateAsync();
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
            TimelineClip[] screens = timeline.Clips
                .Where(clip => clip.Track is TimelineTrackKind.Screen)
                .OrderBy(clip => clip.Range.Start)
                .ToArray();
            if (screens.Length > 1)
            {
                MediaPlaybackList playbackList = new();
                foreach (TimelineClip screen in screens)
                {
                    string candidate = Path.Combine(
                        timeline.ProjectPath,
                        screen.SourcePath);
                    if (!File.Exists(candidate))
                    {
                        ProjectPreviewStatusText.Text =
                            $"Screen segment is missing: {screen.SourcePath}";
                        return;
                    }
                    playbackList.Items.Add(
                        new MediaPlaybackItem(
                            MediaSource.CreateFromUri(new Uri(candidate))));
                }
                _projectPlaybackList = playbackList;
                ProjectPreviewPlayer.Source = playbackList;
                ProjectPreviewStatusText.Text =
                    $"Playing {screens.Length} recorded screen segments. " +
                    "Camera and audio remain separate until export.";
                return;
            }
            if (screens.Length == 1)
            {
                string candidate = Path.Combine(
                    timeline.ProjectPath,
                    screens[0].SourcePath);
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

    private async void OnRenderPresetChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        UpdateRenderPlanSummary();
        await PersistEditorStateAsync();
    }

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
            string workerPath = MediaWorkerLocator.FindExecutable() ??
                throw new InvalidOperationException(
                    "The 7Record media worker is missing. Repair or reinstall 7Record.");
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
        TimelineDocument timeline = _currentTimeline;

        TimelineClip[] microphones = timeline.Clips
            .Where(clip => clip.Track is TimelineTrackKind.Microphone)
            .OrderBy(clip => clip.Range.Start)
            .ToArray();
        if (microphones.Length == 0)
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
            CaptionDocument captions = await Task.Run(async () =>
            {
                List<CaptionSegment> segments = [];
                string language = "auto";
                foreach (TimelineClip microphone in microphones)
                {
                    string audioPath = Path.Combine(
                        timeline.ProjectPath,
                        microphone.SourcePath);
                    CaptionDocument segmentDocument =
                        await LocalWhisperTranscriber.TranscribeAsync(
                            audioPath,
                            modelPath,
                            "auto");
                    language = segmentDocument.Language;
                    segments.AddRange(
                        segmentDocument.Segments.Select(segment =>
                            segment with
                            {
                                Id = $"{microphone.Id}-{segment.Id}",
                                Start = segment.Start + microphone.Range.Start,
                                End = segment.End + microphone.Range.Start,
                            }));
                }
                return CaptionDocumentValidator.ValidateAndNormalize(
                    new CaptionDocument(1, language, segments),
                    timeline.Duration);
            });
            string captionPath = Path.Combine(
                timeline.ProjectPath,
                "captions.json");
            string temporaryPath = captionPath + ".tmp";
            string json = JsonSerializer.Serialize(
                captions,
                RenderPlanSerializerOptions);
            await File.WriteAllTextAsync(temporaryPath, json);
            File.Move(temporaryPath, captionPath, overwrite: true);
            await File.WriteAllTextAsync(
                Path.Combine(timeline.ProjectPath, "captions.srt"),
                CaptionFormatter.ToSrt(captions));
            await File.WriteAllTextAsync(
                Path.Combine(timeline.ProjectPath, "captions.vtt"),
                CaptionFormatter.ToVtt(captions));

            TimelineDocument refreshed =
                await ProjectTimelineLoader.LoadAsync(timeline.ProjectPath);
            if (!string.Equals(
                    _currentTimeline?.ProjectPath,
                    timeline.ProjectPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _currentTimeline = refreshed;
            _captionEditSession = new CaptionEditSession(
                captions,
                refreshed.Duration);
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

    private static async Task<CaptionEditSession?> LoadCaptionEditSessionAsync(
        string projectPath,
        TimeSpan timelineDuration)
    {
        string path = Path.Combine(projectPath, "captions.json");
        if (!File.Exists(path))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(path);
        CaptionDocument? document = JsonSerializer.Deserialize<CaptionDocument>(
            json,
            RenderPlanSerializerOptions);
        return document is null
            ? null
            : new CaptionEditSession(document, timelineDuration);
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
        await PersistEditorStateAsync();
        return path;
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        await ShutdownAsync();
    }

    public async Task<bool> ShutdownAsync()
    {
        Task<bool> task = _shutdownTask ??= ShutdownCoreAsync();
        bool result = await task;
        if (!result && ReferenceEquals(_shutdownTask, task))
        {
            _shutdownTask = null;
        }
        return result;
    }

    private async Task<bool> ShutdownCoreAsync()
    {
        _postProcessingCancellation.Cancel();
        _cameraSettingsSaveCancellation?.Cancel();
        try
        {
            await CameraStudioSettingsStore.SaveAsync(_cameraLayout);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Camera studio settings could not be saved: {exception}");
        }
        bool cameraRestored = await StopCameraStudioAsync();
        if (!cameraRestored)
        {
            await Launcher.LaunchUriAsync(
                new Uri("ms-settings:camera"));
            return false;
        }
        _recordingUiTimer.Stop();
        ProjectPreviewPlayer.Source = null;
        DisposeGlobalHotKeys();
        await StopCaptureAsync(RecordingStopReason.ApplicationExit);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _postProcessingCancellation.Cancel();
        _cameraSettingsSaveCancellation?.Cancel();
        _cameraSettingsSaveCancellation?.Dispose();
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

        if (!await StopCameraStudioAsync(resetPreview: false))
        {
            ReadinessInfoBar.Title = "Camera settings need attention";
            ReadinessInfoBar.Message =
                "Recording did not start because the prior camera background effect could not be restored.";
            ReadinessInfoBar.Severity = InfoBarSeverity.Error;
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
                        _cameraEnabled,
                        _cameraLayout),
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
            session.ScreenPreviewFrameReady += OnScreenPreviewFrameReady;
            session.CameraPreviewFrameReady += OnCameraPreviewFrameReady;
            session.PreviewFailed += OnPreviewFailed;
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
                CameraPreviewBubble.Visibility = Visibility.Collapsed;
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
                ReadinessInfoBar.Title = "No screen selected";
                ReadinessInfoBar.Message =
                    "Windows did not return a capture source. Try again, or use the installed 7Record package for automatic primary-display capture.";
                ReadinessInfoBar.Severity = InfoBarSeverity.Warning;
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
        session.ScreenPreviewFrameReady -= OnScreenPreviewFrameReady;
        session.CameraPreviewFrameReady -= OnCameraPreviewFrameReady;
        session.PreviewFailed -= OnPreviewFailed;
        session.CaptureClosed -= OnCaptureClosed;
        session.CaptureFailed -= OnCaptureFailed;
        _microphoneHealth = null;
        _systemAudioHealth = null;

        bool finalizationFailed = false;
        string? completedProjectRoot = null;
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
                completedProjectRoot = result.ProjectRoot;
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
        if (completedProjectRoot is not null &&
            reason is not RecordingStopReason.ApplicationExit)
        {
            WorkspaceNavigation.SelectedItem = ProjectsNavigationItem;
            ProjectsNavigationItem.IsSelected = true;
            RecorderView.Visibility = Visibility.Collapsed;
            ProjectsView.Visibility = Visibility.Visible;
            await OpenProjectAsync(completedProjectRoot);
        }
    }

    private static string CreateProjectRoot()
    {
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        string projectName =
            $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        return Path.Combine(videos, "7Record", "Projects", projectName);
    }

    private void OnScreenPreviewFrameReady(SoftwareBitmapPreviewFrame frame)
    {
        Interlocked.Exchange(ref _pendingScreenPreview, frame)?.Dispose();
        QueueScreenPreviewDispatch();
    }

    private void OnCameraPreviewFrameReady(SoftwareBitmapPreviewFrame frame)
    {
        Interlocked.Exchange(ref _pendingCameraPreview, frame)?.Dispose();
        QueueCameraPreviewDispatch();
    }

    private void QueueScreenPreviewDispatch()
    {
        if (Interlocked.CompareExchange(
                ref _screenPreviewDispatchPending,
                1,
                0) != 0)
        {
            return;
        }

        if (!DispatcherQueue.TryEnqueue(ProcessPendingScreenPreview))
        {
            Interlocked.Exchange(ref _screenPreviewDispatchPending, 0);
        }
    }

    private void QueueCameraPreviewDispatch()
    {
        if (Interlocked.CompareExchange(
                ref _cameraPreviewDispatchPending,
                1,
                0) != 0)
        {
            return;
        }

        if (!DispatcherQueue.TryEnqueue(ProcessPendingCameraPreview))
        {
            Interlocked.Exchange(ref _cameraPreviewDispatchPending, 0);
        }
    }

    private async void ProcessPendingScreenPreview()
    {
        SoftwareBitmapPreviewFrame? frame =
            Interlocked.Exchange(ref _pendingScreenPreview, null);
        try
        {
            if ((_recordingSession is not null ||
                 _cameraPreviewSession is not null) &&
                frame is not null)
            {
                _screenPreviewPixelWidth = frame.Bitmap.PixelWidth;
                _screenPreviewPixelHeight = frame.Bitmap.PixelHeight;
                _screenPreviewSource = await UpdatePreviewSourceAsync(
                    _screenPreviewSource,
                    ScreenPreviewImage,
                    frame);
                LivePreviewSurface.Visibility = Visibility.Visible;
                IdlePreviewContent.Visibility = Visibility.Collapsed;
                AutomationProperties.SetName(
                    ScreenPreviewImage,
                    "Live screen preview, video active");
                ApplyCameraOverlayLayout();
                ApplyCameraFramingTransform();
            }
        }
        catch (Exception exception)
        {
            OnPreviewFailed("screen", exception);
        }
        finally
        {
            frame?.Dispose();
            Interlocked.Exchange(ref _screenPreviewDispatchPending, 0);
        }
        if (Volatile.Read(ref _pendingScreenPreview) is not null)
        {
            QueueScreenPreviewDispatch();
        }
    }

    private async void ProcessPendingCameraPreview()
    {
        SoftwareBitmapPreviewFrame? frame =
            Interlocked.Exchange(ref _pendingCameraPreview, null);
        try
        {
            if ((_recordingSession is not null ||
                 _cameraPreviewSession is not null) &&
                frame is not null)
            {
                _cameraPreviewPixelWidth = frame.Bitmap.PixelWidth;
                _cameraPreviewPixelHeight = frame.Bitmap.PixelHeight;
                _cameraPreviewSource = await UpdatePreviewSourceAsync(
                    _cameraPreviewSource,
                    CameraPreviewImage,
                    frame);
                CameraPreviewPlaceholder.Visibility = Visibility.Collapsed;
                AutomationProperties.SetName(
                    CameraPreviewBubble,
                    "Live camera overlay, video active");
                ApplyCameraFramingTransform();
                CameraPreviewBubble.Visibility = _cameraEnabled
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
        catch (Exception exception)
        {
            OnPreviewFailed("camera", exception);
        }
        finally
        {
            frame?.Dispose();
            Interlocked.Exchange(ref _cameraPreviewDispatchPending, 0);
        }
        if (Volatile.Read(ref _pendingCameraPreview) is not null)
        {
            QueueCameraPreviewDispatch();
        }
    }

    private static async Task<SoftwareBitmapSource> UpdatePreviewSourceAsync(
        SoftwareBitmapSource? source,
        Image target,
        SoftwareBitmapPreviewFrame frame)
    {
        if (source is null)
        {
            source = new SoftwareBitmapSource();
            target.Source = source;
        }

        await source.SetBitmapAsync(frame.Bitmap);
        return source;
    }

    private void ShowLivePreviewShell()
    {
        LivePreviewSurface.Visibility = Visibility.Visible;
        IdlePreviewContent.Visibility = Visibility.Collapsed;
        PreviewStage.StartBringIntoView(
            new BringIntoViewOptions
            {
                AnimationDesired = false,
                VerticalAlignmentRatio = 0,
            });
        CameraPreviewBubble.Visibility =
            _cameraEnabled ? Visibility.Visible : Visibility.Collapsed;
        CameraPreviewPlaceholder.Visibility =
            CameraPreviewImage.Source is null
                ? Visibility.Visible
                : Visibility.Collapsed;
        ApplyCameraOverlayLayout();
    }

    private void ResetLivePreview()
    {
        Interlocked.Exchange(ref _pendingScreenPreview, null)?.Dispose();
        Interlocked.Exchange(ref _pendingCameraPreview, null)?.Dispose();
        ScreenPreviewImage.Source = null;
        CameraPreviewImage.Source = null;
        _screenPreviewSource = null;
        _cameraPreviewSource = null;
        _screenPreviewPixelWidth = 0;
        _screenPreviewPixelHeight = 0;
        _cameraPreviewPixelWidth = 0;
        _cameraPreviewPixelHeight = 0;
        LivePreviewSurface.Visibility = Visibility.Collapsed;
        IdlePreviewContent.Visibility = Visibility.Visible;
        CameraPreviewBubble.Visibility = Visibility.Collapsed;
        CameraPreviewPlaceholder.Visibility = Visibility.Visible;
        AutomationProperties.SetName(
            ScreenPreviewImage,
            "Live screen preview, waiting");
        AutomationProperties.SetName(
            CameraPreviewBubble,
            "Live camera overlay, waiting");
    }

    private void OnPreviewFailed(string source, Exception exception)
    {
        System.Diagnostics.Debug.WriteLine(
            $"{source} preview failed: {exception}");
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_recordingSession is null ||
                ReadinessInfoBar.Severity is InfoBarSeverity.Error)
            {
                return;
            }

            ReadinessInfoBar.Title = $"{source} preview unavailable";
            ReadinessInfoBar.Message =
                "Recording continues normally; only the live preview is affected. " +
                exception.Message;
            ReadinessInfoBar.Severity = InfoBarSeverity.Warning;
        });
    }

    private void OnCameraOverlayCanvasSizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        ApplyCameraOverlayLayout();
        ApplyCameraFramingTransform();
    }

    private void ApplyCameraOverlayLayout()
    {
        Windows.Foundation.Rect frameBounds = GetScreenPreviewBounds();
        if (frameBounds.Width <= 0 || frameBounds.Height <= 0)
        {
            return;
        }

        _cameraLayout = _cameraLayout.ConstrainToFrame();
        double width = frameBounds.Width * _cameraLayout.Width;
        double height = frameBounds.Height * _cameraLayout.Height;
        CameraPreviewBubble.Width = width;
        CameraPreviewBubble.Height = height;
        Canvas.SetLeft(
            CameraPreviewBubble,
            frameBounds.X +
            Math.Clamp(
                _cameraLayout.X * frameBounds.Width,
                0,
                frameBounds.Width - width));
        Canvas.SetTop(
            CameraPreviewBubble,
            frameBounds.Y +
            Math.Clamp(
                _cameraLayout.Y * frameBounds.Height,
                0,
                frameBounds.Height - height));
    }

    private Windows.Foundation.Rect GetScreenPreviewBounds()
    {
        double canvasWidth = CameraOverlayCanvas.ActualWidth;
        double canvasHeight = CameraOverlayCanvas.ActualHeight;
        if (canvasWidth <= 0 ||
            canvasHeight <= 0 ||
            _screenPreviewPixelWidth <= 0 ||
            _screenPreviewPixelHeight <= 0)
        {
            return new Windows.Foundation.Rect(
                0,
                0,
                Math.Max(0, canvasWidth),
                Math.Max(0, canvasHeight));
        }

        double scale = Math.Min(
            canvasWidth / _screenPreviewPixelWidth,
            canvasHeight / _screenPreviewPixelHeight);
        double width = _screenPreviewPixelWidth * scale;
        double height = _screenPreviewPixelHeight * scale;
        return new Windows.Foundation.Rect(
            (canvasWidth - width) / 2,
            (canvasHeight - height) / 2,
            width,
            height);
    }

    private void OnCameraOverlayCanvasPointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (_recordingSession is null &&
            _cameraPreviewSession is null)
        {
            return;
        }

        Windows.Foundation.Point position =
            e.GetCurrentPoint(CameraOverlayCanvas).Position;
        double left = Canvas.GetLeft(CameraPreviewBubble);
        double top = Canvas.GetTop(CameraPreviewBubble);
        if (position.X < left ||
            position.X > left + CameraPreviewBubble.ActualWidth ||
            position.Y < top ||
            position.Y > top + CameraPreviewBubble.ActualHeight)
        {
            return;
        }

        _cameraOverlayDragging = true;
        _cameraOverlayPointerId = e.Pointer.PointerId;
        _cameraOverlayDragStart = position;
        _cameraOverlayStartX = _cameraLayout.X;
        _cameraOverlayStartY = _cameraLayout.Y;
        CameraOverlayCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnCameraOverlayCanvasPointerMoved(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (!_cameraOverlayDragging ||
            e.Pointer.PointerId != _cameraOverlayPointerId)
        {
            return;
        }

        Windows.Foundation.Point current =
            e.GetCurrentPoint(CameraOverlayCanvas).Position;
        Windows.Foundation.Rect frameBounds = GetScreenPreviewBounds();
        MoveCameraOverlay(
            _cameraOverlayStartX +
                (current.X - _cameraOverlayDragStart.X) /
                Math.Max(1, frameBounds.Width),
            _cameraOverlayStartY +
                (current.Y - _cameraOverlayDragStart.Y) /
                Math.Max(1, frameBounds.Height));
        e.Handled = true;
    }

    private void OnCameraOverlayCanvasPointerReleased(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (!_cameraOverlayDragging ||
            e.Pointer.PointerId != _cameraOverlayPointerId)
        {
            return;
        }

        _cameraOverlayDragging = false;
        CameraOverlayCanvas.ReleasePointerCapture(e.Pointer);
        UpdateCameraStudioStatus();
        e.Handled = true;
    }

    private void OnCameraPreviewBubbleKeyDown(
        object sender,
        KeyRoutedEventArgs e)
    {
        const double keyboardStep = 0.01;
        (double deltaX, double deltaY) = e.Key switch
        {
            VirtualKey.Left => (-keyboardStep, 0d),
            VirtualKey.Right => (keyboardStep, 0d),
            VirtualKey.Up => (0d, -keyboardStep),
            VirtualKey.Down => (0d, keyboardStep),
            _ => (0d, 0d),
        };
        if (deltaX == 0 && deltaY == 0)
        {
            return;
        }

        MoveCameraOverlay(
            _cameraLayout.X + deltaX,
            _cameraLayout.Y + deltaY);
        UpdateCameraStudioStatus();
        e.Handled = true;
    }

    private void MoveCameraOverlay(double x, double y)
    {
        _cameraLayout = (_cameraLayout with { X = x, Y = y })
            .ConstrainToFrame();
        _recordingSession?.UpdateCameraLayout(_cameraLayout);
        _cameraPreviewSession?.UpdateLayout(_cameraLayout);
        ApplyCameraOverlayLayout();
        ScheduleCameraStudioSave();
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
            string? workerPath = MediaWorkerLocator.FindExecutable();
            ProjectPostProcessingResult result =
                await _postProcessingPipeline.RunAsync(
                    projectRoot,
                    workerPath,
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
        string queueOverflows = health.QueueOverflows > 0
            ? $", {health.QueueOverflows} queue overflows"
            : string.Empty;
        return
            $"{source}: {health.Drift.Drift.TotalMilliseconds:+0.0;-0.0;0.0} ms drift " +
            $"({health.Drift.PartsPerMillion:+0;-0;0} ppm), " +
            $"{health.Discontinuities} discontinuities{missing}{queueOverflows}.";
    }

    private static bool HasAudioSyncRisk(AudioCaptureHealth? health) =>
        health is not null &&
        (health.Drift.Exceeds(AudioDriftWarningThreshold) ||
         health.Discontinuities > 0 ||
         health.QueueOverflows > 0 ||
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
                $"{microphoneHealth.TotalMissingDuration.TotalMilliseconds:0.#} ms missing, " +
                $"{microphoneHealth.QueueOverflows} queue overflows");
        }

        if (HasAudioSyncRisk(systemAudioHealth))
        {
            details.Add(
                $"System {systemAudioHealth!.Drift.Drift.TotalMilliseconds:+0.0;-0.0;0.0} ms drift, " +
                $"{systemAudioHealth.Drift.PartsPerMillion:+0;-0;0} ppm, " +
                $"{systemAudioHealth.Discontinuities} discontinuities, " +
                $"{systemAudioHealth.TotalMissingDuration.TotalMilliseconds:0.#} ms missing, " +
                $"{systemAudioHealth.QueueOverflows} queue overflows");
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
                    IsEnabled = project.RecoveryState is
                        not ProjectRecoveryState.NeedsAttention and
                        not ProjectRecoveryState.Corrupt,
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
                TextWrapping = TextWrapping.Wrap,
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
        bool canOpen = project.RecoveryState is
            not ProjectRecoveryState.NeedsAttention and
            not ProjectRecoveryState.Corrupt;
        Button openButton = new()
        {
            Content = canOpen ? "Open recording" : "Recovery required",
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = canOpen,
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
