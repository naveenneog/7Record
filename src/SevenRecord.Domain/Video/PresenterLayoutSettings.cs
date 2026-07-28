namespace SevenRecord.Domain.Video;

public enum PresenterLayoutMode
{
    ScreenOnly,
    RoundedOverlay,
    SideBySide,
    FullPresenter,
}

public sealed record CameraFramingSettings(
    double Zoom,
    double CenterX,
    double CenterY)
{
    public static CameraFramingSettings Default { get; } =
        new(1, 0.5, 0.5);

    public CameraFramingSettings Constrain() =>
        this with
        {
            Zoom = Math.Clamp(
                double.IsFinite(Zoom) ? Zoom : 1,
                1,
                4),
            CenterX = Math.Clamp(
                double.IsFinite(CenterX) ? CenterX : 0.5,
                0,
                1),
            CenterY = Math.Clamp(
                double.IsFinite(CenterY) ? CenterY : 0.5,
                0,
                1),
        };
}

public enum BackgroundBlurMode
{
    Off,
    Standard,
    Portrait,
}

public sealed record CameraEffectSettings(double Exposure)
{
    public static CameraEffectSettings Default { get; } = new(0);

    public BackgroundBlurMode BackgroundBlur { get; init; } =
        BackgroundBlurMode.Off;

    public CameraEffectSettings Constrain() =>
        this with
        {
            Exposure = Math.Clamp(
                double.IsFinite(Exposure) ? Exposure : 0,
                -1,
                1),
            BackgroundBlur = Enum.IsDefined(BackgroundBlur)
                ? BackgroundBlur
                : BackgroundBlurMode.Off,
        };
}

public readonly record struct NormalizedCameraCrop(
    double X,
    double Y,
    double Width,
    double Height);

public static class CameraCropGeometry
{
    public static NormalizedCameraCrop Calculate(
        int sourceWidth,
        int sourceHeight,
        double viewportAspectRatio,
        CameraFramingSettings framing)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            viewportAspectRatio,
            0);
        CameraFramingSettings constrained = framing.Constrain();
        double sourceAspect = sourceWidth / (double)sourceHeight;
        double baseWidth = sourceAspect > viewportAspectRatio
            ? viewportAspectRatio / sourceAspect
            : 1;
        double baseHeight = sourceAspect > viewportAspectRatio
            ? 1
            : sourceAspect / viewportAspectRatio;
        double width = baseWidth / constrained.Zoom;
        double height = baseHeight / constrained.Zoom;
        double x = Math.Clamp(
            constrained.CenterX - width / 2,
            0,
            1 - width);
        double y = Math.Clamp(
            constrained.CenterY - height / 2,
            0,
            1 - height);
        return new NormalizedCameraCrop(x, y, width, height);
    }
}

public sealed record PresenterLayoutSettings(
    PresenterLayoutMode Mode,
    double X,
    double Y,
    double Width,
    double Height,
    double CornerRadius)
{
    public CameraEffectSettings Effects { get; init; } =
        CameraEffectSettings.Default;

    public CameraFramingSettings Framing { get; init; } =
        CameraFramingSettings.Default;

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
            Effects = (Effects ?? CameraEffectSettings.Default).Constrain(),
            Framing = (Framing ?? CameraFramingSettings.Default).Constrain(),
        };
    }

    private static double ConstrainSize(double value, double fallback) =>
        Math.Clamp(double.IsFinite(value) ? value : fallback, 0.1, 1);
}
