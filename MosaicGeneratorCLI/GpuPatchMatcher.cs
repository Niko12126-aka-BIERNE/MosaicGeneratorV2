using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MosaicGeneratorCLI;

public static class GpuPatchMatcher
{
    /// <summary>
    /// For every tile in <paramref name="inputImage"/> (which must already be resized to
    /// <c>cols * matchWidth</c> by <c>rows * matchHeight</c>), finds the card whose pixel
    /// patch is the closest match using sum-of-squared-differences (SSD) in RGB space,
    /// computed on the GPU.
    /// <para>
    /// Before running full pixel SSD on the GPU, a cheap CPU pre-filter computes the average
    /// LAB colour of each tile and each card, then selects the closest <paramref name="topK"/>
    /// candidates per tile. Only those candidates are compared on the GPU, cutting GPU work
    /// dramatically while preserving match quality.
    /// </para>
    /// <para>
    /// Pixel values are stored as <c>float</c> in the [0, 1] range rather than raw bytes.
    /// This avoids any ambiguity in how ILGPU handles byte-to-int sign extension in kernels,
    /// and matches the type convention used by <see cref="GpuCardMatcher"/>.
    /// </para>
    /// </summary>
    public static int[] Match(
        CardRecord[] cards,
        Image<Rgb24> inputImage,
        string cardFolderPath,
        int cols, int rows,
        int matchWidth, int matchHeight,
        int topK,
        bool labSsd,
        Color background)
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

        // Initialise GPU early so we can report the device name up front
        using var context     = Context.Create(b => b.Cuda().EnableAlgorithms());
        using var accelerator = context.CreateCudaAccelerator(0);

        Console.WriteLine($"  GPU              : {accelerator.Name}");
        Console.WriteLine($"  Match resolution : {matchWidth}×{matchHeight}px per tile");
        Console.WriteLine($"  Candidates       : top {effectiveK} of {cards.Length} cards per tile");
        Console.WriteLine($"  Pixel metric     : {(labSsd ? "LAB SSD (perceptually uniform)" : "RGB SSD")}");
        Console.WriteLine($"  Card buffer      : {cardElemCount * 4 / 1024 / 1024} MB");
        Console.WriteLine($"  Tile buffer      : {tileElemCount * 4 / 1024 / 1024} MB");

        // Load all cards at match resolution
        float[] cardFloats = LoadCardFloats(
            cards, cardFolderPath, matchWidth, matchHeight, matchPixels3, (int)cardElemCount, labSsd, background);

        // Extract tile patches from the input image
        float[] tileFloats = ExtractTileFloats(
            inputImage, cols, rows, matchWidth, matchHeight, matchPixels3, (int)tileElemCount, labSsd);

        // CPU pre-filter: pick top-K candidates per tile by average LAB colour
        Console.WriteLine("  Pre-filtering candidates by average colour...");
        int[] candidateIndices = SelectTopKCandidates(tileFloats, cards, tileCount, matchPixels3, effectiveK, labSsd);
        Console.WriteLine("  Pre-filter done.");

        // GPU: one thread per tile, SSD only against its K candidates
        return RunGpuMatch(accelerator, cardFloats, tileFloats, candidateIndices, tileCount, matchPixels3, effectiveK);
    }

    // Card loading
    private static float[] LoadCardFloats(
        CardRecord[] cards, string cardFolderPath,
        int matchWidth, int matchHeight, int matchPixels3,
        int totalElements, bool labSsd, Color background)
    {
        float[] cardFloats = new float[totalElements];
        int     done       = 0;

        Parallel.For(0, cards.Length,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
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
                if (n % 1000 == 0 || n == cards.Length)
                    Console.WriteLine($"    Loaded {n}/{cards.Length} card images...");
            });

        return cardFloats;
    }

    // Input tile extraction

    private static float[] ExtractTileFloats(
        Image<Rgb24> image,
        int cols, int rows,
        int matchWidth, int matchHeight, int matchPixels3,
        int totalElements, bool labSsd)
    {
        // Copy input pixels to a flat array once for lock-free parallel reads.
        var inputPixels = new Rgb24[image.Width * image.Height];
        image.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < image.Height; y++)
                acc.GetRowSpan(y).CopyTo(inputPixels.AsSpan(y * image.Width, image.Width));
        });

        float[] tileFloats = new float[totalElements];

        Parallel.For(0, rows, row =>
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

    // CPU candidate pre-filter
    private static int[] SelectTopKCandidates(
        float[] tileFloats, CardRecord[] cards,
        int tileCount, int matchPixels3, int K, bool labSsd)
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

        Parallel.For(0, tileCount, ti =>
        {
            int tileBase = ti * matchPixels3;
            float tL, tA, tB;

            if (labSsd)
            {
                // tileFloats holds (L/100, a/255, b/255). Undo normalisation to get raw LAB.
                // a* and b* can be negative so we must NOT go through byte casting.
                double sumL = 0, sumA = 0, sumB_ = 0;
                for (int p = 0; p < matchPixels; p++)
                {
                    sumL  += tileFloats[tileBase + p * 3];
                    sumA  += tileFloats[tileBase + p * 3 + 1];
                    sumB_ += tileFloats[tileBase + p * 3 + 2];
                }
                tL = (float)(sumL  / matchPixels * 70.0);
                tA = (float)(sumA  / matchPixels * 255.0);
                tB = (float)(sumB_ / matchPixels * 255.0);
            }
            else
            {
                // tileFloats holds (R, G, B) in [0, 1]. average, scale to bytes, convert to LAB.
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
                (tL, tA, tB) = ColorMath.RgbToLab(avgR, avgG, avgB);
            }

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
        });

        return candidateIndices;
    }

    // GPU dispatch
    private static int[] RunGpuMatch(
        CudaAccelerator accelerator,
        float[] cardFloats, float[] tileFloats,
        int[] candidateIndices,
        int tileCount, int matchPixels3, int K)
    {
        using var deviceCards      = accelerator.Allocate1D<float>(cardFloats.Length);
        using var deviceTiles      = accelerator.Allocate1D<float>(tileFloats.Length);
        using var deviceCandidates = accelerator.Allocate1D<int>(candidateIndices.Length);
        using var deviceResults    = accelerator.Allocate1D<int>(tileCount);

        deviceCards.CopyFromCPU(cardFloats);
        deviceTiles.CopyFromCPU(tileFloats);
        deviceCandidates.CopyFromCPU(candidateIndices);

        // JIT-compilation happens here. Can take 15–30 seconds on the first run of the session.
        Console.WriteLine("  Compiling GPU kernel (first run may take ~30s)...");
        var kernel = accelerator.LoadAutoGroupedStreamKernel<
            Index1D,
            ArrayView1D<float, Stride1D.Dense>,
            ArrayView1D<float, Stride1D.Dense>,
            ArrayView1D<int,   Stride1D.Dense>,
            ArrayView1D<int,   Stride1D.Dense>,
            int, int>(MatchKernel);

        Console.WriteLine($"  Matching {tileCount} tiles × {K} candidates — this may take a while...");
        kernel(tileCount, deviceCards.View, deviceTiles.View, deviceCandidates.View, deviceResults.View, K, matchPixels3);
        accelerator.Synchronize();
        Console.WriteLine("  Done.");

        return deviceResults.GetAsArray1D();
    }

    // GPU kernel
    // One thread per tile. Iterates only the K pre-selected candidate cards for this tile,
    // accumulates float RGB SSD, and keeps the minimum.
    private static void MatchKernel(
        Index1D                            index,
        ArrayView1D<float, Stride1D.Dense> cardPixels,
        ArrayView1D<float, Stride1D.Dense> tilePixels,
        ArrayView1D<int,   Stride1D.Dense> candidates,
        ArrayView1D<int,   Stride1D.Dense> results,
        int K,
        int matchPixels3)
    {
        int ti            = index.X;
        int tileBase      = ti * matchPixels3;
        int candidateBase = ti * K;

        int   bestCard = candidates[candidateBase];
        float bestDist = float.MaxValue;

        for (int k = 0; k < K; k++)
        {
            int   c        = candidates[candidateBase + k];
            int   cardBase = c * matchPixels3;
            float dist     = 0f;

            for (int p = 0; p < matchPixels3; p++)
            {
                float diff = tilePixels[tileBase + p] - cardPixels[cardBase + p];
                dist += diff * diff;
            }

            if (dist < bestDist)
            {
                bestDist = dist;
                bestCard = c;
            }
        }

        results[ti] = bestCard;
    }
}
