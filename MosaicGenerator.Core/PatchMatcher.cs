using MosaicGenerator.Core.Compute;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MosaicGenerator.Core;

public static class PatchMatcher
{
    /// <summary>
    /// For every tile in <paramref name="inputImage"/> (which must already be resized to
    /// <c>cols * matchWidth</c> by <c>rows * matchHeight</c>), finds the card whose pixel
    /// patch is the closest match using sum-of-squared-differences (SSD), computed on the
    /// device <paramref name="backend"/> represents.
    /// <para>
    /// Before running full pixel SSD, a cheap pre-filter computes the average LAB color of
    /// each tile and each card, then selects the closest <paramref name="topK"/> candidates
    /// per tile. Only those candidates are compared pixel by pixel, cutting the work
    /// dramatically while preserving match quality.
    /// </para>
    /// <para>
    /// Pixel values are stored as <c>float</c> in the [0, 1] range rather than raw bytes.
    /// This avoids any ambiguity in how ILGPU handles byte-to-int sign extension in kernels,
    /// and matches the type convention used by <see cref="ColorMatcher"/>.
    /// </para>
    /// <para>
    /// SSD penalises every bit of detail a card has that the tile doesn't, so on flat tiles a
    /// plain card of the wrong Color tends to beat a textured card of the right Color. With
    /// <paramref name="colorWeight"/> above 0, the CIEDE2000 difference between the tile's and
    /// the card's average Color (the same measure Color matching uses) is added to the score,
    /// weighted by <paramref name="colorWeight"/> on a flat tile and fading out as the tile gets
    /// more detail (see <see cref="TileColors"/>). 0 leaves plain SSD.
    /// </para>
    /// <para>
    /// Progress: loading card images covers the first 30% of the reported fraction, the
    /// pre-filter the next 10%, and the pixel comparison the rest.
    /// </para>
    /// </summary>
    public static int[] Match(
        IMatchBackend backend,
        CardRecord[] cards,
        Image<Rgb24> inputImage,
        string cardFolderPath,
        int cols, int rows,
        int matchWidth, int matchHeight,
        int topK,
        bool labSsd,
        float colorWeight,
        Color background,
        ProgressReporter? progress = null,
        CancellationToken ct = default)
    {
        int matchPixels3 = matchWidth * matchHeight * 3;   // floats per card/tile
        int tileCount    = cols * rows;
        int effectiveK   = Math.Min(topK, cards.Length);

        // Guard: verify both buffers fit in .NET arrays
        long cardElemCount = (long)cards.Length * matchPixels3;
        long tileElemCount = (long)tileCount    * matchPixels3;

        if (cardElemCount > Array.MaxLength)
            throw new InvalidOperationException(
                $"Card buffer too large ({cardElemCount * 4 / 1024 / 1024} MB). " +
                $"Reduce the number of cards or lower --match-width.");
        if (tileElemCount > Array.MaxLength)
            throw new InvalidOperationException(
                $"Tile buffer too large ({tileElemCount * 4 / 1024 / 1024} MB). " +
                $"Reduce --cards-per-row, --card-width, or lower --match-width.");

        progress?.Log($"  Device           : {backend.Device}");
        progress?.Log($"  Match resolution : {matchWidth}×{matchHeight}px per tile");
        progress?.Log($"  Candidates       : top {effectiveK} of {cards.Length} cards per tile");
        progress?.Log($"  Pixel metric     : {(labSsd ? "LAB SSD (perceptually uniform)" : "RGB SSD")}");
        progress?.Log($"  Color weight     : {(colorWeight > 0 ? $"{colorWeight} (on flat tiles, color counts up to {1 + colorWeight}x)" : "off")}");
        progress?.Log($"  Card buffer      : {cardElemCount * 4 / 1024 / 1024} MB");
        progress?.Log($"  Tile buffer      : {tileElemCount * 4 / 1024 / 1024} MB");

        // Load all cards at match resolution
        float[] cardFloats = LoadCardFloats(
            cards, cardFolderPath, matchWidth, matchHeight, matchPixels3, (int)cardElemCount, labSsd, background,
            progress, ct);

        // Extract tile patches from the input image
        float[] tileFloats = ExtractTileFloats(
            inputImage, cols, rows, matchWidth, matchHeight, matchPixels3, (int)tileElemCount, labSsd, ct);

        // CPU pre-filter: pick top-K candidates per tile by average LAB color
        progress?.Log("  Pre-filtering candidates by average color...");
        int[] candidateIndices = SelectTopKCandidates(
            tileFloats, cards, tileCount, matchPixels3, effectiveK, labSsd, progress, ct);
        progress?.Log("  Pre-filter done.");

        float[] cardColors = CardColors(cards);
        float[] tileColors = TileColors(tileFloats, tileCount, matchPixels3, labSsd, colorWeight);

        // Pixel comparison: per tile, SSD only against its K candidates
        progress?.Log($"  Matching {tileCount} tiles × {effectiveK} candidates — this may take a while...");
        int[] result = backend.MatchPatches(
            cardFloats, tileFloats, candidateIndices, cardColors, tileColors, tileCount, effectiveK, matchPixels3,
            fraction => progress?.Fraction(0.4 + 0.6 * fraction), ct);
        progress?.Log("  Done.");
        progress?.Fraction(1);

        return result;
    }

    // Card loading
    private static float[] LoadCardFloats(
        CardRecord[] cards, string cardFolderPath,
        int matchWidth, int matchHeight, int matchPixels3,
        int totalElements, bool labSsd, Color background,
        ProgressReporter? progress, CancellationToken ct)
    {
        float[] cardFloats = new float[totalElements];
        int     done       = 0;

        Parallel.For(0, cards.Length,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
            i =>
            {
                string path = Path.Combine(cardFolderPath, cards[i].FileName);
                using var img = ImageIO.LoadFlattened(path, background);
                img.Mutate(ctx => ctx.Resize(matchWidth, matchHeight));

                int cardBase = i * matchPixels3;
                img.ProcessPixelRows(acc =>
                {
                    for (int y = 0; y < matchHeight; y++)
                    {
                        var row     = acc.GetRowSpan(y);
                        int rowBase = cardBase + y * matchWidth * 3;
                        for (int x = 0; x < matchWidth; x++)
                        {
                            if (labSsd)
                            {
                                var (l, a, b) = ColorMath.RgbToLab(row[x].R, row[x].G, row[x].B);
                                cardFloats[rowBase + x * 3]     = l / 70f;   // L* weighted ~2 times more than chroma
                                cardFloats[rowBase + x * 3 + 1] = a / 255f;
                                cardFloats[rowBase + x * 3 + 2] = b / 255f;
                            }
                            else
                            {
                                cardFloats[rowBase + x * 3]     = row[x].R / 255f;
                                cardFloats[rowBase + x * 3 + 1] = row[x].G / 255f;
                                cardFloats[rowBase + x * 3 + 2] = row[x].B / 255f;
                            }
                        }
                    }
                });

                int n = Interlocked.Increment(ref done);
                progress?.Fraction(0.3 * n / cards.Length);
                if (n % 1000 == 0 || n == cards.Length)
                    progress?.Log($"    Loaded {n}/{cards.Length} card images...");
            });

        return cardFloats;
    }

    // Input tile extraction

    private static float[] ExtractTileFloats(
        Image<Rgb24> image,
        int cols, int rows,
        int matchWidth, int matchHeight, int matchPixels3,
        int totalElements, bool labSsd, CancellationToken ct)
    {
        // Copy input pixels to a flat array once for lock-free parallel reads.
        var inputPixels = new Rgb24[image.Width * image.Height];
        image.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < image.Height; y++)
                acc.GetRowSpan(y).CopyTo(inputPixels.AsSpan(y * image.Width, image.Width));
        });

        float[] tileFloats = new float[totalElements];

        Parallel.For(0, rows, new ParallelOptions { CancellationToken = ct }, row =>
        {
            for (int col = 0; col < cols; col++)
            {
                int tileBase = (row * cols + col) * matchPixels3;
                int srcX     = col * matchWidth;
                int srcY     = row * matchHeight;

                for (int ty = 0; ty < matchHeight; ty++)
                {
                    int srcRowBase = (srcY + ty) * image.Width + srcX;
                    int dstRowBase = tileBase + ty * matchWidth * 3;
                    for (int tx = 0; tx < matchWidth; tx++)
                    {
                        Rgb24 px = inputPixels[srcRowBase + tx];
                        if (labSsd)
                        {
                            var (l, a, b) = ColorMath.RgbToLab(px.R, px.G, px.B);
                            tileFloats[dstRowBase + tx * 3]     = l / 70f;   // L* weighted ~2 times more than chroma
                            tileFloats[dstRowBase + tx * 3 + 1] = a / 255f;
                            tileFloats[dstRowBase + tx * 3 + 2] = b / 255f;
                        }
                        else
                        {
                            tileFloats[dstRowBase + tx * 3]     = px.R / 255f;
                            tileFloats[dstRowBase + tx * 3 + 1] = px.G / 255f;
                            tileFloats[dstRowBase + tx * 3 + 2] = px.B / 255f;
                        }
                    }
                }
            }
        });

        return tileFloats;
    }

    // Color weighting

    // How much detail (RMS deviation from the tile's average Color, in patch units) halves the
    // Color weight's effect. Below it the tile counts as flat, well above it as detailed.
    private const float FlatDetail = 0.05f;

    // Each card's average Color in LAB, 3 floats per card: the same values Color matching uses.
    private static float[] CardColors(CardRecord[] cards)
    {
        var colors = new float[cards.Length * 3];
        for (int i = 0; i < cards.Length; i++)
        {
            colors[i * 3]     = cards[i].L;
            colors[i * 3 + 1] = cards[i].A;
            colors[i * 3 + 2] = cards[i].B;
        }
        return colors;
    }

    // Per tile, 4 floats: its average Color in LAB, then the factor the backends multiply the
    // Color term by (see MatchMath.ColorTerm). The factor is
    //   colorWeight / (1 + (detail / FlatDetail)^2) * pixels per patch
    // so it's colorWeight on a flat tile and fades smoothly as detail grows; the pixel count
    // puts it on the same scale as the SSD, which sums over every pixel. Nearly equal tiles
    // get nearly equal factors, so no visible seam appears where tiles change from flat to detailed.
    private static float[] TileColors(
        float[] tileFloats, int tileCount, int matchPixels3, bool labSsd, float colorWeight)
    {
        var tileColors  = new float[tileCount * 4];
        int matchPixels = matchPixels3 / 3;

        Parallel.For(0, tileCount, ti =>
        {
            int tileBase  = ti * matchPixels3;
            var (r, g, b) = Average(tileFloats, tileBase, matchPixels3);

            double sumSquares = 0;
            for (int p = 0; p < matchPixels3; p += 3)
            {
                double dR = tileFloats[tileBase + p]     - r;
                double dG = tileFloats[tileBase + p + 1] - g;
                double dB = tileFloats[tileBase + p + 2] - b;
                sumSquares += dR * dR + dG * dG + dB * dB;
            }
            double detail = Math.Sqrt(sumSquares / matchPixels3) / FlatDetail;

            var (l, a, labB) = AverageLab(tileFloats, tileBase, matchPixels, labSsd);
            tileColors[ti * 4]     = l;
            tileColors[ti * 4 + 1] = a;
            tileColors[ti * 4 + 2] = labB;
            tileColors[ti * 4 + 3] = (float)(colorWeight / (1 + detail * detail) * matchPixels);
        });

        return tileColors;
    }

    private static (float R, float G, float B) Average(float[] patches, int start, int matchPixels3)
    {
        double r = 0, g = 0, b = 0;
        for (int p = start; p < start + matchPixels3; p += 3)
        {
            r += patches[p];
            g += patches[p + 1];
            b += patches[p + 2];
        }
        int pixels = matchPixels3 / 3;
        return ((float)(r / pixels), (float)(g / pixels), (float)(b / pixels));
    }

    // A tile's average Color in LAB, from its patch floats.
    private static (float L, float A, float B) AverageLab(
        float[] tileFloats, int tileBase, int matchPixels, bool labSsd)
    {
        if (labSsd)
        {
            // tileFloats holds (L/70, a/255, b/255). Undo normalisation to get raw LAB.
            // a* and b* can be negative so we must NOT go through byte casting.
            double sumL = 0, sumA = 0, sumB = 0;
            for (int p = 0; p < matchPixels; p++)
            {
                sumL += tileFloats[tileBase + p * 3];
                sumA += tileFloats[tileBase + p * 3 + 1];
                sumB += tileFloats[tileBase + p * 3 + 2];
            }
            return ((float)(sumL / matchPixels * 70.0),
                    (float)(sumA / matchPixels * 255.0),
                    (float)(sumB / matchPixels * 255.0));
        }
        else
        {
            // tileFloats holds (R, G, B) in [0, 1]. Average, scale to bytes, convert to LAB.
            double sumR = 0, sumG = 0, sumB = 0;
            for (int p = 0; p < matchPixels; p++)
            {
                sumR += tileFloats[tileBase + p * 3];
                sumG += tileFloats[tileBase + p * 3 + 1];
                sumB += tileFloats[tileBase + p * 3 + 2];
            }
            byte avgR = (byte)(sumR / matchPixels * 255.0 + 0.5);
            byte avgG = (byte)(sumG / matchPixels * 255.0 + 0.5);
            byte avgB = (byte)(sumB / matchPixels * 255.0 + 0.5);
            return ColorMath.RgbToLab(avgR, avgG, avgB);
        }
    }

    // CPU candidate pre-filter
    private static int[] SelectTopKCandidates(
        float[] tileFloats, CardRecord[] cards,
        int tileCount, int matchPixels3, int K, bool labSsd,
        ProgressReporter? progress, CancellationToken ct)
    {
        int cardCount   = cards.Length;
        int matchPixels = matchPixels3 / 3;   // number of pixels per tile

        var candidateIndices = new int[tileCount * K];

        // Each thread gets its own working buffers to avoid allocations inside the loop.
        var distBuf = new ThreadLocal<float[]>(() => new float[cardCount]);
        var idxBuf  = new ThreadLocal<int[]>(() =>
        {
            var arr = new int[cardCount];
            for (int i = 0; i < cardCount; i++) arr[i] = i;
            return arr;
        });

        int done = 0;

        Parallel.For(0, tileCount, new ParallelOptions { CancellationToken = ct }, ti =>
        {
            var (tL, tA, tB) = AverageLab(tileFloats, ti * matchPixels3, matchPixels, labSsd);

            // Score every card by squared Euclidean LAB distance (no sqrt needed for ranking).
            float[] dists = distBuf.Value!;
            int[]   idxs  = idxBuf.Value!;

            for (int c = 0; c < cardCount; c++)
            {
                float dL = tL - cards[c].L;
                float dA = tA - cards[c].A;
                float dB = tB - cards[c].B;
                dists[c] = dL * dL + dA * dA + dB * dB;
            }

            // Sort both arrays together so the K smallest distances end up first.
            Array.Sort(dists, idxs, 0, cardCount);

            // Write the top-K card indices into the output.
            int destBase = ti * K;
            for (int k = 0; k < K; k++)
                candidateIndices[destBase + k] = idxs[k];

            // Reset the index buffer for the next tile this thread will process.
            for (int c = 0; c < cardCount; c++) idxs[c] = c;

            progress?.Fraction(0.3 + 0.1 * Interlocked.Increment(ref done) / tileCount);
        });

        return candidateIndices;
    }
}
