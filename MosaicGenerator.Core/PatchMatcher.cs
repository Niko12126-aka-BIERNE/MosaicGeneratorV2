using MosaicGenerator.Core.Compute;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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
    /// Patches are stored as 3 bytes per pixel: R, G, B as they are, or L*, a*, b* scaled to
    /// bytes with <paramref name="labSsd"/> (see <see cref="ToLabBytes"/>). That's a quarter of the
    /// memory floats would take, the comparison reads a quarter as much, and its SSD is a whole
    /// number, so every device computes exactly the same sum.
    /// </para>
    /// <para>
    /// SSD penalises every bit of detail a card has that the tile doesn't, so on flat tiles a
    /// plain card of the wrong color tends to beat a textured card of the right color. With
    /// <paramref name="colorWeight"/> above 0, the CIEDE2000 difference between the tile's and
    /// the card's average color (the same measure color matching uses) is added to the score,
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
        int matchPixels  = matchWidth * matchHeight;
        int matchPixels3 = matchPixels * 3;   // bytes per card/tile
        int tileCount    = cols * rows;
        int effectiveK   = Math.Min(topK, cards.Length);

        // Guard: verify both buffers fit in .NET arrays
        long cardBytes = (long)cards.Length * matchPixels3;
        long tileBytes = (long)tileCount    * matchPixels3;

        if (cardBytes > Array.MaxLength)
            throw new InvalidOperationException(
                $"Card buffer too large ({cardBytes / 1024 / 1024} MB). " +
                $"Reduce the number of cards or lower --match-width.");
        if (tileBytes > Array.MaxLength)
            throw new InvalidOperationException(
                $"Tile buffer too large ({tileBytes / 1024 / 1024} MB). " +
                $"Reduce --cards-per-row, --card-width, or lower --match-width.");

        progress?.Log($"  Device           : {backend.Device}");
        progress?.Log($"  Match resolution : {matchWidth}×{matchHeight}px per tile");
        progress?.Log($"  Candidates       : top {effectiveK} of {cards.Length} cards per tile");
        progress?.Log($"  Pixel metric     : {(labSsd ? "LAB SSD (perceptually uniform)" : "RGB SSD")}");
        progress?.Log($"  Color weight     : {(colorWeight > 0 ? $"{colorWeight} (on flat tiles, color counts up to {1 + colorWeight}x)" : "off")}");
        progress?.Log($"  Card buffer      : {cardBytes / 1024 / 1024} MB");
        progress?.Log($"  Tile buffer      : {tileBytes / 1024 / 1024} MB");

        // Load all cards at match resolution
        byte[] cardPatches = LoadCardPatches(
            cards, cardFolderPath, matchWidth, matchHeight, labSsd, background, progress, ct);

        // Extract tile patches from the input image
        byte[] tilePatches = ExtractTilePatches(
            inputImage, cols, rows, matchWidth, matchHeight, matchPixels3, (int)tileBytes, labSsd, ct);

        // CPU pre-filter: pick top-K candidates per tile by average LAB color
        progress?.Log("  Pre-filtering candidates by average color...");
        int[] candidateIndices = SelectTopKCandidates(
            tilePatches, cards, tileCount, matchPixels3, effectiveK, labSsd, progress, ct);
        progress?.Log("  Pre-filter done.");

        float[] cardColors = CardColors(cards);
        float[] tileColors = TileColors(tilePatches, tileCount, matchPixels3, labSsd, colorWeight);

        // Pixel comparison: per tile, SSD only against its K candidates
        progress?.Log($"  Matching {tileCount} tiles × {effectiveK} candidates — this may take a while...");
        int[] result = backend.MatchPatches(
            cardPatches, tilePatches, candidateIndices, cardColors, tileColors,
            tileCount, effectiveK, matchPixels, labSsd ? LabLightnessWeight : 1,
            fraction => progress?.Fraction(0.4 + 0.6 * fraction), ct);
        progress?.Log("  Done.");
        progress?.Fraction(1);

        return result;
    }

    // Patch bytes

    // LAB as bytes: L* (0 to 100) scaled to 0-255; a* and b* (about -110 to 100 for real colors)
    // shifted by 128. A lightness step is then about 0.4, below what the eye can tell apart.
    private const float LabLightnessScale = 2.55f;
    private const int   LabChromaOffset   = 128;

    // In LAB, a lightness difference counts this many times in the SSD, as before: comparing
    // L*/70 against a*/255 weighted a unit of L* 13.3 times a unit of a*; 2 * 2.55^2 = 13.0.
    private const int LabLightnessWeight = 2;

    private static void ToLabBytes(byte r, byte g, byte b, Span<byte> target)
    {
        var (l, a, bb) = ColorMath.RgbToLab(r, g, b);
        target[0] = (byte)Math.Clamp((int)MathF.Round(l * LabLightnessScale), 0, 255);
        target[1] = (byte)Math.Clamp((int)MathF.Round(a) + LabChromaOffset, 0, 255);
        target[2] = (byte)Math.Clamp((int)MathF.Round(bb) + LabChromaOffset, 0, 255);
    }

    // Card loading

    // Every card at match resolution. The scaled pixels come from CardPixelCache (only new or
    // changed cards are opened and scaled) and are used as they are for RGB; for LAB they're
    // converted in place, which is quick and depends on labSsd, so it isn't cached.
    private static byte[] LoadCardPatches(
        CardRecord[] cards, string cardFolderPath,
        int matchWidth, int matchHeight, bool labSsd, Color background,
        ProgressReporter? progress, CancellationToken ct)
    {
        byte[] pixels = CardPixelCache.Load(
            cards, cardFolderPath, matchWidth, matchHeight, background,
            progress, fraction => progress?.Fraction(0.3 * fraction), ct);

        if (labSsd)
        {
            int cardBytes = matchWidth * matchHeight * 3;
            Parallel.For(0, cards.Length, new ParallelOptions { CancellationToken = ct }, i =>
            {
                var card = pixels.AsSpan(i * cardBytes, cardBytes);
                for (int v = 0; v < cardBytes; v += 3)
                    ToLabBytes(card[v], card[v + 1], card[v + 2], card.Slice(v, 3));
            });
        }

        return pixels;
    }

    // Input tile extraction

    private static byte[] ExtractTilePatches(
        Image<Rgb24> image,
        int cols, int rows,
        int matchWidth, int matchHeight, int matchPixels3,
        int totalBytes, bool labSsd, CancellationToken ct)
    {
        // Copy input pixels to a flat array once for lock-free parallel reads.
        var inputPixels = new Rgb24[image.Width * image.Height];
        image.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < image.Height; y++)
                acc.GetRowSpan(y).CopyTo(inputPixels.AsSpan(y * image.Width, image.Width));
        });

        byte[] tilePatches = new byte[totalBytes];

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
                        Rgb24 px  = inputPixels[srcRowBase + tx];
                        int   dst = dstRowBase + tx * 3;
                        if (labSsd)
                        {
                            ToLabBytes(px.R, px.G, px.B, tilePatches.AsSpan(dst, 3));
                        }
                        else
                        {
                            tilePatches[dst]     = px.R;
                            tilePatches[dst + 1] = px.G;
                            tilePatches[dst + 2] = px.B;
                        }
                    }
                }
            }
        });

        return tilePatches;
    }

    // Color weighting

    // How much detail (RMS deviation from the tile's average color, with values scaled to 0-1)
    // halves the color weight's effect. Below it the tile counts as flat, well above it as detailed.
    private const float FlatDetail = 0.05f;

    // Each card's average color in LAB, 3 floats per card: the same values color matching uses.
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

    // Per tile, 4 floats: its average color in LAB, then the factor the backends multiply the
    // color term by (see MatchMath.ColorTerm). The factor is
    //   colorWeight / (1 + (detail / FlatDetail)^2) * pixels per patch
    // so it's colorWeight on a flat tile and fades smoothly as detail grows; the pixel count
    // puts it on the same scale as the SSD, which sums over every pixel. Nearly equal tiles
    // get nearly equal factors, so no visible seam appears where tiles change from flat to detailed.
    private static float[] TileColors(
        byte[] tilePatches, int tileCount, int matchPixels3, bool labSsd, float colorWeight)
    {
        var tileColors  = new float[tileCount * 4];
        int matchPixels = matchPixels3 / 3;
        int weight0     = labSsd ? LabLightnessWeight : 1;   // as in the SSD

        Parallel.For(0, tileCount, ti =>
        {
            int tileBase  = ti * matchPixels3;
            var (m0, m1, m2) = Average(tilePatches, tileBase, matchPixels3);

            double sumSquares = 0;
            for (int p = tileBase; p < tileBase + matchPixels3; p += 3)
            {
                double d0 = tilePatches[p]     - m0;
                double d1 = tilePatches[p + 1] - m1;
                double d2 = tilePatches[p + 2] - m2;
                sumSquares += weight0 * d0 * d0 + d1 * d1 + d2 * d2;
            }
            double detail = Math.Sqrt(sumSquares / matchPixels3) / 255 / FlatDetail;

            var (l, a, labB) = AverageLab(tilePatches, tileBase, matchPixels, labSsd);
            tileColors[ti * 4]     = l;
            tileColors[ti * 4 + 1] = a;
            tileColors[ti * 4 + 2] = labB;
            tileColors[ti * 4 + 3] = (float)(colorWeight / (1 + detail * detail) * matchPixels);
        });

        return tileColors;
    }

    // The average of each of a patch's 3 channels.
    private static (double, double, double) Average(byte[] patches, int start, int matchPixels3)
    {
        long s0 = 0, s1 = 0, s2 = 0;
        for (int p = start; p < start + matchPixels3; p += 3)
        {
            s0 += patches[p];
            s1 += patches[p + 1];
            s2 += patches[p + 2];
        }
        double pixels = matchPixels3 / 3;
        return (s0 / pixels, s1 / pixels, s2 / pixels);
    }

    // A tile's average color in LAB, from its patch bytes.
    private static (float L, float A, float B) AverageLab(
        byte[] tilePatches, int tileBase, int matchPixels, bool labSsd)
    {
        var (m0, m1, m2) = Average(tilePatches, tileBase, matchPixels * 3);

        if (labSsd)
            return ((float)(m0 / LabLightnessScale), (float)(m1 - LabChromaOffset), (float)(m2 - LabChromaOffset));

        // RGB: round the average to bytes and convert to LAB.
        return ColorMath.RgbToLab((byte)(m0 + 0.5), (byte)(m1 + 0.5), (byte)(m2 + 0.5));
    }

    // CPU candidate pre-filter

    // For every tile, the K cards whose average color is closest to the tile's, nearest first.
    private static int[] SelectTopKCandidates(
        byte[] tilePatches, CardRecord[] cards,
        int tileCount, int matchPixels3, int K, bool labSsd,
        ProgressReporter? progress, CancellationToken ct)
    {
        int     cardCount   = cards.Length;
        int     matchPixels = matchPixels3 / 3;   // number of pixels per tile
        float[] cardLab     = CardColors(cards);   // contiguous, so the distance loop reads it quickly

        var candidateIndices = new int[tileCount * K];

        // Each thread gets its own working buffers to avoid allocations inside the loop.
        var distBuf = new ThreadLocal<float[]>(() => new float[cardCount]);
        var idxBuf  = new ThreadLocal<int[]>(() => new int[cardCount]);

        int done = 0;

        Parallel.For(0, tileCount, new ParallelOptions { CancellationToken = ct }, ti =>
        {
            var (tL, tA, tB) = AverageLab(tilePatches, ti * matchPixels3, matchPixels, labSsd);

            // Score every card by squared Euclidean LAB distance (no sqrt needed for ranking).
            float[] dists = distBuf.Value!;
            int[]   idxs  = idxBuf.Value!;

            for (int c = 0; c < cardCount; c++)
            {
                float dL = tL - cardLab[c * 3];
                float dA = tA - cardLab[c * 3 + 1];
                float dB = tB - cardLab[c * 3 + 2];
                dists[c] = dL * dL + dA * dA + dB * dB;
                idxs[c]  = c;
            }

            // Move the K smallest distances to the front without ordering the rest, then
            // order just those K. Much less work than sorting every card.
            SelectSmallest(dists, idxs, cardCount, K);
            Array.Sort(dists, idxs, 0, K);

            // Write the top-K card indices into the output.
            Array.Copy(idxs, 0, candidateIndices, ti * K, K);

            progress?.Fraction(0.3 + 0.1 * Interlocked.Increment(ref done) / tileCount);
        });

        return candidateIndices;
    }

    // Rearranges keys[0..count), and items alongside, so the k smallest keys come first, in no
    // particular order (quickselect: like quicksort, but only the side holding position k - 1
    // is partitioned further).
    private static void SelectSmallest(float[] keys, int[] items, int count, int k)
    {
        if (k >= count)
            return;

        int target = k - 1;
        int lo = 0, hi = count - 1;

        while (lo < hi)
        {
            // Median of the first, middle and last key as the pivot, which avoids the slow
            // worst case on keys that are already (nearly) in order.
            float a = keys[lo], b = keys[lo + (hi - lo) / 2], c = keys[hi];
            float pivot = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

            int i = lo, j = hi;
            while (i <= j)
            {
                while (keys[i] < pivot) i++;
                while (keys[j] > pivot) j--;
                if (i <= j)
                {
                    (keys[i], keys[j])   = (keys[j], keys[i]);
                    (items[i], items[j]) = (items[j], items[i]);
                    i++;
                    j--;
                }
            }

            // Now keys[lo..j] <= pivot <= keys[i..hi], and anything in between equals pivot.
            if (target <= j)      hi = j;
            else if (target >= i) lo = i;
            else                  return;
        }
    }
}
