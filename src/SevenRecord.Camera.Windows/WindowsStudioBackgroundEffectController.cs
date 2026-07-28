using System.Runtime.InteropServices;
using SevenRecord.Domain.Video;
using Windows.Media.Devices;

namespace SevenRecord.Camera.Windows;

internal sealed class CameraEffectControlRequiredException :
    InvalidOperationException
{
}

public sealed class CameraBackgroundEffectRestoreException :
    InvalidOperationException
{
    public CameraBackgroundEffectRestoreException(string message)
        : base(message)
    {
    }

    public CameraBackgroundEffectRestoreException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record BackgroundEffectSupport(
    bool StandardBlur,
    bool PortraitBlur,
    BackgroundBlurMode ActiveMode,
    string? Message)
{
    public bool IsSupported => StandardBlur || PortraitBlur;

    public ulong RawFlags { get; init; }

    public bool OperationSucceeded { get; init; } = true;

    public bool DefinitelyUnsupported { get; init; }
}

public static class WindowsStudioBackgroundEffectController
{
    private static readonly Guid ExtendedCameraControl =
        Guid.Parse("1CB79112-C0D2-4213-9CA6-CD4FDB927972");
    private const uint BackgroundSegmentationControl = 41;
    private const uint FilterScope = 0xFFFFFFFF;
    private const ulong BlurFlag = 1;
    private const ulong ShallowFocusFlag = 4;

    public static BackgroundEffectSupport Query(
        VideoDeviceController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        try
        {
            byte[] property = ToBytes(
                new KsProperty(
                    ExtendedCameraControl,
                    BackgroundSegmentationControl,
                    (uint)KsPropertyKind.Get));
            VideoDeviceControllerGetDevicePropertyResult result =
                controller.GetDevicePropertyByExtendedId(property, null);
            if (result.Status is not
                VideoDeviceControllerGetDevicePropertyStatus.Success ||
                result.Value is not byte[] payload ||
                payload.Length < Marshal.SizeOf<ExtendedPropertyHeader>())
            {
                return new BackgroundEffectSupport(
                    false,
                    false,
                    BackgroundBlurMode.Off,
                    $"Windows Studio Effects are unavailable ({result.Status}).")
            {
                    OperationSucceeded = false,
                    DefinitelyUnsupported = result.Status is
                        VideoDeviceControllerGetDevicePropertyStatus.NotSupported,
            };
            }
            ExtendedPropertyHeader header =
                FromBytes<ExtendedPropertyHeader>(payload);
            return new BackgroundEffectSupport(
                (header.Capability & BlurFlag) != 0,
                (header.Capability & (BlurFlag | ShallowFocusFlag)) ==
                    (BlurFlag | ShallowFocusFlag),
                ModeFromFlags(header.Flags),
                null)
            {
                RawFlags = header.Flags,
            };
        }
        catch (Exception exception) when (
            exception is COMException or
                InvalidOperationException or
                ArgumentException)
        {
            return new BackgroundEffectSupport(
                false,
                false,
                BackgroundBlurMode.Off,
                exception.Message)
            {
                OperationSucceeded = false,
            };
        }
    }

    public static BackgroundEffectSupport Apply(
        VideoDeviceController controller,
        BackgroundBlurMode requestedMode)
    {
        BackgroundEffectSupport support = Query(controller);
        if (!support.IsSupported)
        {
            return support;
        }
        BackgroundBlurMode applied = requestedMode switch
        {
            BackgroundBlurMode.Portrait when support.PortraitBlur =>
                BackgroundBlurMode.Portrait,
            BackgroundBlurMode.Standard when support.StandardBlur =>
                BackgroundBlurMode.Standard,
            BackgroundBlurMode.Off => BackgroundBlurMode.Off,
            _ => BackgroundBlurMode.Off,
        };
        if (requestedMode is not BackgroundBlurMode.Off &&
            applied is BackgroundBlurMode.Off)
        {
            return support with
            {
                ActiveMode = support.ActiveMode,
                Message =
                    "This camera does not support the selected Windows Studio background effect.",
                OperationSucceeded = false,
            };
        }

        try
        {
            return ApplyRawFlags(
                controller,
                support,
                FlagsFor(applied),
                applied);
        }
        catch (Exception exception) when (
            exception is COMException or
                InvalidOperationException or
                ArgumentException)
        {
            return support with
            {
                Message = exception.Message,
                OperationSucceeded = false,
            };
        }
    }

    public static BackgroundEffectSupport Restore(
        VideoDeviceController controller,
        BackgroundEffectSupport previous) =>
        ApplyRawFlags(
            controller,
            previous,
            previous.RawFlags,
            ModeFromFlags(previous.RawFlags));

    public static BackgroundEffectSupport RestoreIfUnchanged(
        VideoDeviceController controller,
        BackgroundEffectSupport previous,
        BackgroundEffectSupport applied)
    {
        BackgroundEffectSupport current = Query(controller);
        if (!current.OperationSucceeded)
        {
            return current with
            {
                Message =
                    "Windows could not verify the current camera effect before restoration.",
            };
        }
        if (current.RawFlags != applied.RawFlags)
        {
            return current with
            {
                Message =
                    "A newer Windows camera-effect change was preserved.",
                OperationSucceeded = true,
            };
        }
        return Restore(controller, previous);
    }

    public static async Task RestoreWithRetryAsync(
        VideoDeviceController controller,
        BackgroundEffectSupport previous,
        BackgroundEffectSupport applied,
        CancellationToken cancellationToken = default)
    {
        BackgroundEffectSupport? lastResult = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastResult = RestoreIfUnchanged(
                controller,
                previous,
                applied);
            if (lastResult.OperationSucceeded)
            {
                return;
            }
            await Task.Delay(
                TimeSpan.FromMilliseconds(150 * (attempt + 1)),
                cancellationToken);
        }
        throw new CameraBackgroundEffectRestoreException(
            lastResult?.Message ??
            "The previous camera background effect could not be restored.");
    }

    private static BackgroundEffectSupport ApplyRawFlags(
        VideoDeviceController controller,
        BackgroundEffectSupport support,
        ulong flags,
        BackgroundBlurMode activeMode)
    {
        byte[] property = ToBytes(
            new KsProperty(
                ExtendedCameraControl,
                BackgroundSegmentationControl,
                (uint)KsPropertyKind.Set));
        byte[] value = ToBytes(
            new BasicExtendedPropertyPayload
            {
                Header = new ExtendedPropertyHeader
                {
                    Version = 1,
                    PinId = FilterScope,
                    Size = (uint)Marshal.SizeOf<
                        BasicExtendedPropertyPayload>(),
                    Flags = flags,
                },
            });
        VideoDeviceControllerSetDevicePropertyStatus status =
            controller.SetDevicePropertyByExtendedId(
                property,
                value);
        if (status is not
            VideoDeviceControllerSetDevicePropertyStatus.Success)
        {
            return support with
            {
                ActiveMode = support.ActiveMode,
                Message =
                    $"Windows rejected the background effect ({status}).",
                OperationSucceeded = false,
            };
        }

        BackgroundEffectSupport verified = Query(controller);
        if (!verified.OperationSucceeded ||
            verified.RawFlags != flags)
        {
            return support with
            {
                ActiveMode = activeMode,
                RawFlags = flags,
                Message =
                    "Windows did not confirm the requested background effect.",
                OperationSucceeded = false,
            };
        }
        return verified with
        {
            ActiveMode = activeMode,
            OperationSucceeded = true,
        };
    }

    public static ulong FlagsFor(BackgroundBlurMode mode) =>
        mode switch
        {
            BackgroundBlurMode.Standard => BlurFlag,
            BackgroundBlurMode.Portrait => BlurFlag | ShallowFocusFlag,
            _ => 0,
        };

    public static BackgroundBlurMode ModeFromFlags(ulong flags) =>
        (flags & (BlurFlag | ShallowFocusFlag)) ==
            (BlurFlag | ShallowFocusFlag)
            ? BackgroundBlurMode.Portrait
            : (flags & BlurFlag) != 0
                ? BackgroundBlurMode.Standard
                : BackgroundBlurMode.Off;

    private static byte[] ToBytes<T>(T value)
        where T : struct
    {
        int size = Marshal.SizeOf<T>();
        nint pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, pointer, false);
            byte[] bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static T FromBytes<T>(byte[] bytes)
        where T : struct
    {
        int size = Marshal.SizeOf<T>();
        if (bytes.Length < size)
        {
            throw new ArgumentException(
                "The camera property payload is incomplete.",
                nameof(bytes));
        }
        nint pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(bytes, 0, pointer, size);
            return Marshal.PtrToStructure<T>(pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private enum KsPropertyKind : uint
    {
        Get = 1,
        Set = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KsProperty
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Set;
        public uint Id;
        public uint Flags;

        public KsProperty(Guid set, uint id, uint flags)
        {
            Set = set.ToByteArray();
            Id = id;
            Flags = flags;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedPropertyHeader
    {
        public uint Version;
        public uint PinId;
        public uint Size;
        public uint Result;
        public ulong Flags;
        public ulong Capability;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedPropertyValue
    {
        public ulong Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicExtendedPropertyPayload
    {
        public ExtendedPropertyHeader Header;
        public ExtendedPropertyValue Value;
    }
}
