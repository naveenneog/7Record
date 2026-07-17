namespace SevenRecord.Domain.Video;

public enum PresenterLayoutMode
{
    ScreenOnly,
    RoundedOverlay,
    SideBySide,
    FullPresenter,
}

public sealed record PresenterLayoutSettings(
    PresenterLayoutMode Mode,
    double X,
    double Y,
    double Width,
    double Height,
    double CornerRadius)
{
    public static PresenterLayoutSettings DefaultOverlay { get; } =
        new(
            PresenterLayoutMode.RoundedOverlay,
            X: 0.72,
            Y: 0.68,
            Width: 0.24,
            Height: 0.24,
            CornerRadius: 0.5);
}
