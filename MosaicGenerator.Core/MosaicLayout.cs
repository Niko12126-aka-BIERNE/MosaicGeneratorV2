namespace MosaicGenerator.Core;

/// <summary>
/// The grid and pixel size of a mosaic. Cheap to calculate, so a UI can show the
/// output size live while the user changes settings.
/// </summary>
public readonly record struct MosaicLayout(
    int Cols, int Rows,
    int CardWidth, int CardHeight,
    int Width, int Height)
{
    public int  TileCount  => Cols * Rows;
    public long PixelCount => (long)Width * Height;

    /// <summary>Tile height for a given tile width and card aspect ratio (height / width).</summary>
    public static int CardHeightFor(int cardWidth, float cardAspectRatio) =>
        (int)(cardWidth * cardAspectRatio);

    /// <param name="cardAspectRatio">Card height / width, see <see cref="CardDatabase.SampleAspectRatio"/>.</param>
    public static MosaicLayout Calculate(
        int inputWidth, int inputHeight,
        int cardsPerRow, int cardWidth,
        float cardAspectRatio)
    {
        int cardHeight   = CardHeightFor(cardWidth, cardAspectRatio);
        int mosaicWidth  = cardWidth * cardsPerRow;
        int scaledHeight = (int)((double)mosaicWidth / inputWidth * inputHeight);
        int cols         = mosaicWidth / cardWidth;
        int rows         = cardHeight > 0 ? scaledHeight / cardHeight : 0;

        // Only whole card rows are placed, so the output is exactly rows * cardHeight tall.
        // (Using scaledHeight here wrote a PNG header taller than the pixel data.)
        return new MosaicLayout(cols, rows, cardWidth, cardHeight, mosaicWidth, rows * cardHeight);
    }
}
