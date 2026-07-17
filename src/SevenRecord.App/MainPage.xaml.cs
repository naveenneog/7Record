using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SevenRecord.Capture.Abstractions;
using SevenRecord.Capture.Windows;
using SevenRecord.Infrastructure;
using SevenRecord.Media;

namespace SevenRecord.App;

public sealed partial class MainPage : Page
{
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
    private CaptureReadinessSnapshot? _lastSnapshot;
    private WindowsCaptureTarget? _selectedScreen;

    public MainPage()
    {
        InitializeComponent();
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

        ProjectClock projectClock = ProjectClock.StartNew();
        WindowsScreenCaptureSession capture = WindowsScreenCaptureSession.Start(
            _selectedScreen.Item,
            projectClock,
            static (frame, cancellationToken) => ValueTask.CompletedTask);
        capture.HealthChanged += OnCaptureHealthChanged;
        capture.CaptureClosed += OnCaptureClosed;
        capture.CaptureFailed += OnCaptureFailed;
        _captureSession = capture;

        StartRecordingButton.Content = "Stop recording";
        StartRecordingButton.IsEnabled = true;
        ChooseSourceButton.IsEnabled = false;
        RefreshReadinessButton.IsEnabled = false;
        ReadinessInfoBar.Title = "Recording";
        ReadinessInfoBar.Message = "Capturing timestamped Direct3D frames.";
        ReadinessInfoBar.Severity = InfoBarSeverity.Informational;
        FrameStatusText.Text = "Waiting for the first frame...";
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
        FrameStatusText.Text =
            $"Stopped after {health.FramesReceived:N0} frames; " +
            $"{health.FramesDropped:N0} dropped.";
        StartRecordingButton.Content = "New recording";
        ChooseSourceButton.IsEnabled = true;
        RefreshReadinessButton.IsEnabled = true;
        UpdateReadinessSummary();
    }
}
