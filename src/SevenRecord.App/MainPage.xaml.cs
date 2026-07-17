using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SevenRecord.Analysis;
using SevenRecord.Audio.Windows;
using SevenRecord.Capture.Abstractions;
using SevenRecord.Capture.Windows;
using SevenRecord.Camera.Windows;
using SevenRecord.Infrastructure;
using SevenRecord.Media;
using SevenRecord.Recording;

namespace SevenRecord.App;

public sealed partial class MainPage : Page
{
    private static readonly JsonSerializerOptions AudioRepairSerializerOptions =
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
    private SurfaceScreenSegmentRecorder? _segmentRecorder;
    private CaptureReadinessSnapshot? _lastSnapshot;
    private WindowsCaptureTarget? _selectedScreen;

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
    }

    private async void OnRefreshReadinessClicked(object sender, RoutedEventArgs e)
    {
        await RefreshReadinessAsync();
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
        try
        {
            string projectRoot = CreateProjectRoot();
            ProjectClock projectClock = ProjectClock.StartNew();
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
            _activeProjectRoot = projectRoot;
            pendingSegmentRecorder = null;
            pendingAudioRecorder = null;
            pendingCameraRecorder = null;
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
        string? projectRoot = _activeProjectRoot;
        _segmentRecorder = null;
        _audioRecorder = null;
        _cameraRecorder = null;
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
        }

        StartRecordingButton.Content = "New recording";
        ChooseSourceButton.IsEnabled = true;
        RefreshReadinessButton.IsEnabled = true;
        if (ReadinessInfoBar.Severity is not InfoBarSeverity.Error)
        {
            UpdateReadinessSummary();
        }
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
