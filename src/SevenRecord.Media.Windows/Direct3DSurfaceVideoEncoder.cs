using System.Threading.Channels;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SevenRecord.Media.Windows;

public sealed class Direct3DSurfaceVideoEncoder : IAsyncDisposable
{
    private readonly Channel<MediaStreamSample> _samples;
    private readonly MediaStreamSource _source;
    private readonly IRandomAccessStream _stream;
    private readonly TimeSpan _sampleDuration;
    private Task _transcodeTask = Task.CompletedTask;
    private bool _completed;
    private TimeSpan? _firstTimestamp;
    private bool _streamDisposed;

    private Direct3DSurfaceVideoEncoder(
        MediaStreamSource source,
        IRandomAccessStream stream,
        int framesPerSecond)
    {
        _source = source;
        _stream = stream;
        _sampleDuration = TimeSpan.FromSeconds(1d / framesPerSecond);
        _samples = Channel.CreateBounded<MediaStreamSample>(
            new BoundedChannelOptions(3)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
        _source.SampleRequested += OnSampleRequested;
        _source.Starting += OnStarting;
    }

    public static async Task<Direct3DSurfaceVideoEncoder> CreateAsync(
        string outputPath,
        int width,
        int height,
        int framesPerSecond,
        uint bitrate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitrate);

        string fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, [], cancellationToken);

        VideoEncodingProperties inputProperties = VideoEncodingProperties.CreateUncompressed(
            MediaEncodingSubtypes.Bgra8,
            (uint)width,
            (uint)height);
        inputProperties.FrameRate.Numerator = (uint)framesPerSecond;
        inputProperties.FrameRate.Denominator = 1;
        inputProperties.PixelAspectRatio.Numerator = 1;
        inputProperties.PixelAspectRatio.Denominator = 1;

        VideoStreamDescriptor descriptor = new(inputProperties);
        MediaStreamSource source = new(descriptor)
        {
            BufferTime = TimeSpan.Zero,
        };

        MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Audio = null;
        profile.Video.Width = (uint)width;
        profile.Video.Height = (uint)height;
        profile.Video.Bitrate = bitrate;
        profile.Video.FrameRate.Numerator = (uint)framesPerSecond;
        profile.Video.FrameRate.Denominator = 1;

        StorageFile file = await StorageFile.GetFileFromPathAsync(fullPath);
        IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        MediaTranscoder transcoder = new()
        {
            AlwaysReencode = true,
            HardwareAccelerationEnabled = true,
        };
        PrepareTranscodeResult prepared =
            await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, stream, profile);
        if (!prepared.CanTranscode)
        {
            stream.Dispose();
            throw new InvalidOperationException(
                $"Media Foundation could not prepare the encoder: {prepared.FailureReason}.");
        }

        Direct3DSurfaceVideoEncoder encoder = new(
            source,
            stream,
            framesPerSecond);
        IAsyncActionWithProgress<double> operation = prepared.TranscodeAsync();
        encoder._transcodeTask = operation.AsTask(cancellationToken);
        return encoder;
    }

    public async ValueTask ProcessSurfaceAsync(
        IDirect3DSurface surface,
        TimeSpan projectTime,
        CancellationToken cancellationToken)
    {
        if (_completed)
        {
            throw new InvalidOperationException("The surface encoder is already complete.");
        }

        TaskCompletionSource processed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentOutOfRangeException.ThrowIfLessThan(projectTime, TimeSpan.Zero);
        _firstTimestamp ??= projectTime;
        TimeSpan sampleTime = projectTime - _firstTimestamp.Value;
        MediaStreamSample sample = MediaStreamSample.CreateFromDirect3D11Surface(
            surface,
            sampleTime);
        sample.Duration = _sampleDuration;
        sample.Processed += (_, _) => processed.TrySetResult();

        await _samples.Writer.WriteAsync(sample, cancellationToken);
        Task completion = await Task.WhenAny(processed.Task, _transcodeTask);
        if (completion == _transcodeTask)
        {
            await _transcodeTask;
        }
        else
        {
            await processed.Task.WaitAsync(cancellationToken);
        }
    }

    public async Task CompleteAsync()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The surface encoder is already complete.");
        }

        _completed = true;
        _samples.Writer.TryComplete();
        await _transcodeTask;
        await _stream.FlushAsync();
        _stream.Dispose();
        _streamDisposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        _source.SampleRequested -= OnSampleRequested;
        _source.Starting -= OnStarting;

        if (!_completed)
        {
            _completed = true;
            _samples.Writer.TryComplete();
            try
            {
                await _transcodeTask;
            }
            catch (COMException exception) when (
                (uint)exception.HResult is 0xC00D4A44 or 0xC00D36B6)
            {
            }
        }

        if (!_streamDisposed)
        {
            _stream.Dispose();
            _streamDisposed = true;
        }
    }

    private async void OnSampleRequested(
        MediaStreamSource sender,
        MediaStreamSourceSampleRequestedEventArgs args)
    {
        MediaStreamSourceSampleRequestDeferral deferral = args.Request.GetDeferral();
        try
        {
            if (await _samples.Reader.WaitToReadAsync() &&
                _samples.Reader.TryRead(out MediaStreamSample? sample))
            {
                args.Request.Sample = sample;
            }
            else
            {
                args.Request.Sample = null;
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static void OnStarting(
        MediaStreamSource sender,
        MediaStreamSourceStartingEventArgs args) =>
        args.Request.SetActualStartPosition(TimeSpan.Zero);
}
