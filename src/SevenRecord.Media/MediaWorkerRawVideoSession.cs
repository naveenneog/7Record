using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace SevenRecord.Media;

public sealed class MediaWorkerRawVideoSession : IAsyncDisposable
{
    private readonly Task<string> _standardError;
    private readonly Process _worker;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _completed;

    private MediaWorkerRawVideoSession(Process worker)
    {
        _worker = worker;
        _standardError = worker.StandardError.ReadToEndAsync();
    }

    public static MediaWorkerRawVideoSession Start(
        string workerPath,
        RawVideoEncoderSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        ArgumentNullException.ThrowIfNull(settings);

        ProcessStartInfo startInfo = new()
        {
            FileName = workerPath,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("encode-bgra");
        startInfo.ArgumentList.Add(settings.Width.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(settings.Height.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(settings.FramesPerSecond.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(settings.EncoderName);
        startInfo.ArgumentList.Add(settings.OutputPath);

        try
        {
            Process worker = new() { StartInfo = startInfo };
            if (!worker.Start())
            {
                worker.Dispose();
                throw new InvalidOperationException("The media worker could not be started.");
            }

            return new MediaWorkerRawVideoSession(worker);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("The media worker could not be executed.", exception);
        }
    }

    public async ValueTask WriteFrameAsync(
        ReadOnlyMemory<byte> bgraFrame,
        CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            throw new InvalidOperationException("The media worker input is already complete.");
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _worker.StandardInput.BaseStream.WriteAsync(bgraFrame, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<RawVideoEncoderResult> CompleteAsync(
        CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            throw new InvalidOperationException("The media worker input is already complete.");
        }

        _completed = true;
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            _worker.StandardInput.Close();
        }
        finally
        {
            _writeLock.Release();
        }

        await _worker.WaitForExitAsync(cancellationToken);
        string error = await _standardError;
        return _worker.ExitCode == 0
            ? new RawVideoEncoderResult(true, 0, null)
            : new RawVideoEncoderResult(
                false,
                _worker.ExitCode,
                string.IsNullOrWhiteSpace(error)
                    ? $"Media worker exited with code {_worker.ExitCode}."
                    : error.Trim());
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed && !_worker.HasExited)
        {
            _worker.StandardInput.Close();
            await _worker.WaitForExitAsync();
        }

        _writeLock.Dispose();
        _worker.Dispose();
    }
}
