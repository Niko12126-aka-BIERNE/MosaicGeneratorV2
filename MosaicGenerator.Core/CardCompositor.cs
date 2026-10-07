using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MosaicGenerator.Core;

public static class CardCompositor
{
    // Keep each strip under 2 GB. Well within ImageSharp's hard 4 GB allocation cap.
    private const long MaxStripBytes = 2L * 1024 * 1024 * 1024;

    // Yields one strip image at a time. Caller must dispose each before requesting the next.
    // Strips keep individual ImageSharp allocations under 2 GB (its hard 4 GB cap).
    // A tileCardIndices value of -1 means "leave this tile blank" (fully transparent).
    public static IEnumerable<Image<Rgba32>> CompositeStrips(
        int[] tileCardIndices,
        CardRecord[] cards,
        string cardFolderPath,
        int cols, int rows,
        int tileWidth, int tileHeight,
        int mosaicWidth,
        Color background)
    {
        long bytesPerTileRow     = (long)mosaicWidth * tileHeight * 4;
        int  maxTileRowsPerStrip = Math.Max(1, (int)(MaxStripBytes / bytesPerTileRow));

        var uniqueIndices = tileCardIndices.Where(i => i >= 0).Distinct().ToArray();
        Console.WriteLine($"  Loading {uniqueIndices.Length} unique card images (out of {cards.Length} total)...");

        var cardPixels = new Dictionary<int, Rgba32[]>(uniqueIndices.Length);
        var dictLock   = new object();

        Parallel.ForEach(uniqueIndices, cardIndex =>
        {
            string path = Path.Combine(cardFolderPath, cards[cardIndex].FileName);
            using var original = ImageIO.LoadFlattenedRgba(path, background);
            original.Mutate(ctx => ctx.Resize(tileWidth, tileHeight));

            var flat = new Rgba32[tileWidth * tileHeight];
            original.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < tileHeight; y++)
                    accessor.GetRowSpan(y).CopyTo(flat.AsSpan(y * tileWidth, tileWidth));
            });

            lock (dictLock)
                cardPixels[cardIndex] = flat;
        });

        for (int stripStart = 0; stripStart < rows; stripStart += maxTileRowsPerStrip)
        {
            int stripEnd    = Math.Min(stripStart + maxTileRowsPerStrip, rows);
            int stripRows   = stripEnd - stripStart;
            int stripHeight = stripRows * tileHeight;

            var strip = new Image<Rgba32>(mosaicWidth, stripHeight);

            strip.ProcessPixelRows(mosaicAccessor =>
            {
                for (int row = stripStart; row < stripEnd; row++)
                {
                    int localRow = row - stripStart;
                    for (int col = 0; col < cols; col++)
                    {
                        int cardIndex = tileCardIndices[row * cols + col];
                        if (cardIndex < 0)
                            continue; // leave blank/transparent

                        Rgba32[] src = cardPixels[cardIndex];
                        int destX    = col * tileWidth;
                        int destY    = localRow * tileHeight;

                        for (int cy = 0; cy < tileHeight; cy++)
                        {
                            src.AsSpan(cy * tileWidth, tileWidth)
                               .CopyTo(mosaicAccessor.GetRowSpan(destY + cy).Slice(destX, tileWidth));
                        }
                    }
                }
            });

            yield return strip;
        }
    }
}
