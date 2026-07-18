namespace SevenRecord.Media;

public static class VideoEncodingDimensions
{
    public static int NormalizeEven(int dimension)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
        return checked(dimension + (dimension & 1));
    }
}
