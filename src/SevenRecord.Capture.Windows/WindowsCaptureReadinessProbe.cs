using System.Runtime.InteropServices;
using SevenRecord.Capture.Abstractions;
using Windows.Devices.Enumeration;
using Windows.Graphics.Capture;
using Windows.Media.Devices;

namespace SevenRecord.Capture.Windows;

public sealed record CaptureReadinessSelection(
    bool RequireCamera,
    bool RequireMicrophone,
    bool RequireSystemAudio);

public sealed class WindowsCaptureReadinessProbe(CaptureReadinessSelection selection) : ICaptureReadinessProbe
{
    public ValueTask<IReadOnlyList<CaptureReadinessItem>> CheckAsync(
        CancellationToken cancellationToken) =>
        new(Task.Run(() => CheckCoreAsync(cancellationToken), cancellationToken));

    private async Task<IReadOnlyList<CaptureReadinessItem>> CheckCoreAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        CaptureReadinessItem screen = ProbeScreenCapture();
        CaptureReadinessItem camera = await ProbeDevicesAsync(
            DeviceClass.VideoCapture,
            "camera",
            "Camera",
            selection.RequireCamera,
            cancellationToken);
        CaptureReadinessItem microphone = await ProbeDevicesAsync(
            DeviceClass.AudioCapture,
            "microphone",
            "Microphone",
            selection.RequireMicrophone,
            cancellationToken);
        CaptureReadinessItem systemAudio = ProbeSystemAudio();

        return [screen, camera, microphone, systemAudio];
    }

    private static CaptureReadinessItem ProbeScreenCapture()
    {
        bool supported = GraphicsCaptureSession.IsSupported();
        return supported
            ? new("screen", "Screen", CaptureSourceState.Ready, true, "Windows screen capture is available.")
            : new("screen", "Screen", CaptureSourceState.Error, true, "Windows screen capture is not supported on this device.");
    }

    private static async Task<CaptureReadinessItem> ProbeDevicesAsync(
        DeviceClass deviceClass,
        string key,
        string displayName,
        bool isRequired,
        CancellationToken cancellationToken)
    {
        try
        {
            DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(deviceClass);
            cancellationToken.ThrowIfCancellationRequested();

            if (devices.Count == 0)
            {
                return new(
                    key,
                    displayName,
                    CaptureSourceState.Unavailable,
                    isRequired,
                    $"No {displayName.ToLowerInvariant()} device was found.");
            }

            return new(
                key,
                displayName,
                CaptureSourceState.Ready,
                isRequired,
                devices.Count == 1
                    ? devices[0].Name
                    : $"{devices.Count} devices available.");
        }
        catch (UnauthorizedAccessException)
        {
            return new(
                key,
                displayName,
                CaptureSourceState.Error,
                isRequired,
                $"{displayName} access is denied in Windows privacy settings.");
        }
        catch (COMException exception)
        {
            return new(
                key,
                displayName,
                CaptureSourceState.Error,
                isRequired,
                $"{displayName} discovery failed (0x{exception.HResult:X8}).");
        }
    }

    private CaptureReadinessItem ProbeSystemAudio()
    {
        try
        {
            string renderDeviceId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            return string.IsNullOrWhiteSpace(renderDeviceId)
                ? new(
                    "system-audio",
                    "System audio",
                    CaptureSourceState.Unavailable,
                    selection.RequireSystemAudio,
                    "No default Windows playback device is available.")
                : new(
                    "system-audio",
                    "System audio",
                    CaptureSourceState.Ready,
                    selection.RequireSystemAudio,
                    "Default playback device is available for loopback capture.");
        }
        catch (UnauthorizedAccessException)
        {
            return new(
                "system-audio",
                "System audio",
                CaptureSourceState.Error,
                selection.RequireSystemAudio,
                "Windows denied access to the default playback device.");
        }
        catch (COMException exception)
        {
            return new(
                "system-audio",
                "System audio",
                CaptureSourceState.Error,
                selection.RequireSystemAudio,
                $"Playback device discovery failed (0x{exception.HResult:X8}).");
        }
    }
}
