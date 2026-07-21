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

    public PresenterLayoutSettings ConstrainToFrame()
    {
        double width = ConstrainSize(Width, DefaultOverlay.Width);
        double height = ConstrainSize(Height, DefaultOverlay.Height);
        double x = Math.Clamp(
            double.IsFinite(X) ? X : DefaultOverlay.X,
            0,
            1 - width);
        double y = Math.Clamp(
            double.IsFinite(Y) ? Y : DefaultOverlay.Y,
            0,
            1 - height);
        double cornerRadius = Math.Clamp(
            double.IsFinite(CornerRadius)
                ? CornerRadius
                : DefaultOverlay.CornerRadius,
            0,
            0.5);
        return this with
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            CornerRadius = cornerRadius,
        };
    }

    private static double ConstrainSize(double value, double fallback) =>
        Math.Clamp(double.IsFinite(value) ? value : fallback, 0.1, 1);
}
