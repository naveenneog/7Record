namespace SevenRecord.Capture.Abstractions;

public sealed record CaptureReadinessItem(
    string Key,
    string DisplayName,
    CaptureSourceState State,
    bool IsRequired,
    string Message);

public sealed class CaptureReadinessSnapshot
{
    public CaptureReadinessSnapshot(IReadOnlyList<CaptureReadinessItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items;
    }

    public IReadOnlyList<CaptureReadinessItem> Items { get; }

    public bool CanRecord => Items
        .Where(item => item.IsRequired)
        .All(item => item.State is CaptureSourceState.Ready or CaptureSourceState.Warning);

    public IReadOnlyList<CaptureReadinessItem> BlockingItems => Items
        .Where(item => item.IsRequired && item.State is CaptureSourceState.Unavailable or CaptureSourceState.Error)
        .ToArray();
}

public interface ICaptureReadinessProbe
{
    ValueTask<IReadOnlyList<CaptureReadinessItem>> CheckAsync(CancellationToken cancellationToken);
}

public sealed class CaptureReadinessService
{
    private readonly IReadOnlyList<ICaptureReadinessProbe> _probes;

    public CaptureReadinessService(IEnumerable<ICaptureReadinessProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        _probes = probes.ToArray();
    }

    public async Task<CaptureReadinessSnapshot> CheckAsync(CancellationToken cancellationToken = default)
    {
        Task<IReadOnlyList<CaptureReadinessItem>>[] checks = _probes
            .Select(probe => probe.CheckAsync(cancellationToken).AsTask())
            .ToArray();

        IReadOnlyList<CaptureReadinessItem>[] results = await Task.WhenAll(checks);
        return new CaptureReadinessSnapshot(results.SelectMany(items => items).ToArray());
    }
}
