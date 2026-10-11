using System.Diagnostics;
using ILGPU;
using ILGPU.Runtime;

namespace MosaicGenerator.Core.Compute;

/// <summary>
/// Runs matching on a GPU through ILGPU, which compiles the kernels below for CUDA (NVIDIA)
/// or OpenCL (AMD, Intel). The same kernel code serves both.
/// <para>
/// The operating system resets a GPU whose kernel runs too long (about 2 seconds on Windows,
/// see TDR), which slower GPUs hit easily on big mosaics. So the work is split into launches
/// of about <see cref="TargetLaunchSeconds"/> each, see <see cref="RunInSlices"/>. Between
/// launches, progress is reported and cancellation is checked.
/// </para>
/// <para>
/// The color kernel gives each tile one thread. The patch kernel, which compares thousands of
/// values per card, gives each tile a group of threads that share the work, see <see cref="PatchKernel"/>.
/// </para>
/// </summary>
public sealed class IlgpuBackend : IMatchBackend
{
    private readonly Context          _context;
    private readonly Accelerator      _accelerator;
    private readonly ProgressReporter? _log;

    // Each launch aims for this duration, far below the ~2s watchdog, so a slower-than-expected
    // launch still finishes in time.
    private const double TargetLaunchSeconds = 0.25;

    // Threads per group in the patch kernel, which gives each tile a whole group. The largest
    // power of two up to this that the device allows is used (see PatchGroupSize).
    private const int MaxPatchGroupSize = 256;

    // Compiled on first use (well under a second).
    private Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>,
        ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>,
        int, int, int>? _colorKernel;
    private Action<KernelConfig, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>,
        ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>,
        ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>,
        int, int, int, int, int>? _patchKernel;

    /// <summary>Starts <paramref name="device"/>. Takes ownership of <paramref name="context"/>.</summary>
    public IlgpuBackend(Context context, Device device, DeviceInfo info, ProgressReporter? log)
    {
        _context     = context;
        _accelerator = device.CreateAccelerator(context);
        _log         = log;
        Device       = info;
    }

    public DeviceInfo Device { get; }

    public int[] MatchColors(
        float[] cardLab, float[] tileLab, int tileCount,
        Action<double>? progress, CancellationToken ct)
    {
        using var deviceCards     = _accelerator.Allocate1D<float>(cardLab.Length);
        using var deviceTiles     = _accelerator.Allocate1D<float>(tileLab.Length);
        using var deviceBestDists = _accelerator.Allocate1D<float>(tileCount);
        using var deviceBestCards = _accelerator.Allocate1D<int>(tileCount);

        deviceCards.CopyFromCPU(cardLab);
        deviceTiles.CopyFromCPU(tileLab);

        if (_colorKernel == null)
        {
            _log?.Log("  Compiling GPU kernel...");
            _colorKernel = _accelerator.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView1D<float, Stride1D.Dense>,
                ArrayView1D<float, Stride1D.Dense>,
                ArrayView1D<float, Stride1D.Dense>,
                ArrayView1D<int,   Stride1D.Dense>,
                int, int, int>(ColorKernel);
        }

        // One thread per tile: twice the threads the GPU can run at once per launch.
        int tileBatch = (int)Math.Min(2L * _accelerator.MaxNumThreads, int.MaxValue);

        RunInSlices(tileCount, cardLab.Length / 3, tileBatch, progress, ct, (tileOffset, tiles, cardStart, cardEnd) =>
            _colorKernel(tiles, deviceCards.View, deviceTiles.View, deviceBestDists.View, deviceBestCards.View,
                tileOffset, cardStart, cardEnd));

        return deviceBestCards.GetAsArray1D();
    }

    public int[] MatchPatches(
        float[] cardPatches, float[] tilePatches, int[] candidates,
        float[] cardColors, float[] tileColors,
        int tileCount, int candidatesPerTile, int valuesPerPatch,
        Action<double>? progress, CancellationToken ct)
    {
        using var deviceCards      = _accelerator.Allocate1D<float>(cardPatches.Length);
        using var deviceTiles      = _accelerator.Allocate1D<float>(tilePatches.Length);
        using var deviceCandidates = _accelerator.Allocate1D<int>(candidates.Length);
        using var deviceCardColors = _accelerator.Allocate1D<float>(cardColors.Length);
        using var deviceTileColors = _accelerator.Allocate1D<float>(tileColors.Length);
        using var deviceBestDists  = _accelerator.Allocate1D<float>(tileCount);
        using var deviceBestCards  = _accelerator.Allocate1D<int>(tileCount);

        deviceCards.CopyFromCPU(cardPatches);
        deviceTiles.CopyFromCPU(tilePatches);
        deviceCandidates.CopyFromCPU(candidates);
        deviceCardColors.CopyFromCPU(cardColors);
        deviceTileColors.CopyFromCPU(tileColors);

        if (_patchKernel == null)
        {
            _log?.Log("  Compiling GPU kernel...");
            _patchKernel = _accelerator.LoadStreamKernel<
                ArrayView1D<float, Stride1D.Dense>,
                ArrayView1D<float, Stride1D.Dense>,
                ArrayView1D<int,   Stride1D.Dense>,
                ArrayView1D<float, Stride1D.Dense>,
                ArrayView1D<float, Stride1D.Dense>,
                ArrayView1D<float, Stride1D.Dense>,
                ArrayView1D<int,   Stride1D.Dense>,
                int, int, int, int, int>(PatchKernel);
        }

        // One group per tile: enough tiles per launch to fill the GPU eight times over, so a few
        // groups finishing late don't leave it idle at the end of a launch.
        int groupSize = PatchGroupSize();
        int tileBatch = (int)Math.Max(1, 8L * _accelerator.MaxNumThreads / groupSize);

        RunInSlices(tileCount, candidatesPerTile, tileBatch, progress, ct, (tileOffset, tiles, kStart, kEnd) =>
            _patchKernel(new KernelConfig(tiles, groupSize), deviceCards.View, deviceTiles.View, deviceCandidates.View,
                deviceCardColors.View, deviceTileColors.View, deviceBestDists.View, deviceBestCards.View,
                candidatesPerTile, valuesPerPatch, tileOffset, kStart, kEnd));

        return deviceBestCards.GetAsArray1D();
    }

    // The patch kernel's group size: a power of two (its sum is added up in halves), at most
    // MaxPatchGroupSize and what the device allows.
    private int PatchGroupSize()
    {
        int size = MaxPatchGroupSize;
        while (size > 1 && size > _accelerator.MaxNumThreadsPerGroup)
            size /= 2;
        return size;
    }

    // Splits the work (every tile compared with cardsPerTile cards) into launches short enough
    // to stay well inside the watchdog, and calls launch(tileOffset, tiles, cardStart, cardEnd)
    // for each, waiting for it to finish.
    //
    // Every launch runs tileBatch tiles (chosen by the caller to fill the GPU), and the cards
    // are split into slices; the kernels keep each tile's best match so far in GPU memory
    // between slices. Launching fewer tiles wouldn't make a launch shorter, as one tile's
    // cards can take a long time on their own: it would only leave the GPU idle. Launch time
    // grows with the slice size, so the first slice holds a single card and each next one is
    // scaled by how long the last took, growing at most 2x at a time.
    private void RunInSlices(
        int tileCount, int cardsPerTile, int tileBatch, Action<double>? progress, CancellationToken ct,
        Action<int, int, int, int> launch)
    {
        tileBatch = Math.Clamp(tileBatch, 1, tileCount);
        int  slice     = 1;
        long total     = (long)tileCount * cardsPerTile;
        long done      = 0;

        for (int tileOffset = 0; tileOffset < tileCount; tileOffset += tileBatch)
        {
            int tiles = Math.Min(tileBatch, tileCount - tileOffset);

            for (int cardStart = 0; cardStart < cardsPerTile;)
            {
                ct.ThrowIfCancellationRequested();

                int  cardEnd = Math.Min(cardStart + slice, cardsPerTile);
                long start   = Stopwatch.GetTimestamp();
                launch(tileOffset, tiles, cardStart, cardEnd);
                _accelerator.Synchronize();
                double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;

                done += (long)tiles * (cardEnd - cardStart);
                progress?.Invoke((double)done / total);

                double scale = Math.Min(2.0, TargetLaunchSeconds / Math.Max(seconds, 1e-4));
                slice     = (int)Math.Clamp((cardEnd - cardStart) * scale, 1, cardsPerTile);
                cardStart = cardEnd;
            }
        }
    }

    public void Dispose()
    {
        _accelerator.Dispose();
        _context.Dispose();
    }

    // Kernels

    // One GPU thread per tile of the batch starting at tileOffset. Each thread goes through
    // cards [cardStart, cardEnd) and keeps the minimum CIEDE2000 distance, continuing from the
    // best match the previous slice stored for its tile.
    private static void ColorKernel(
        Index1D index,
        ArrayView1D<float, Stride1D.Dense> cards,
        ArrayView1D<float, Stride1D.Dense> tiles,
        ArrayView1D<float, Stride1D.Dense> bestDists,
        ArrayView1D<int,   Stride1D.Dense> bestCards,
        int tileOffset,
        int cardStart,
        int cardEnd)
    {
        int ti = index.X + tileOffset;

        float tL = tiles[ti * 3];
        float tA = tiles[ti * 3 + 1];
        float tB = tiles[ti * 3 + 2];

        int   bestCard = cardStart == 0 ? 0 : bestCards[ti];
        float bestDist = cardStart == 0 ? float.MaxValue : bestDists[ti];

        for (int c = cardStart; c < cardEnd; c++)
        {
            float dist = MatchMath.CieDe2000(tL, tA, tB, cards[c * 3], cards[c * 3 + 1], cards[c * 3 + 2]);

            if (dist < bestDist)
            {
                bestDist = dist;
                bestCard = c;
            }
        }

        bestDists[ti] = bestDist;
        bestCards[ti] = bestCard;
    }

    // One group per tile of the batch starting at tileOffset. For each of this tile's
    // pre-selected candidates [kStart, kEnd), the group's threads each add up the squared
    // differences of every groupSize-th value (thread i takes values i, i + groupSize, ...),
    // so neighbouring threads read neighbouring memory, which the GPU fetches in one go.
    // The partial sums are then added up in shared memory, and the group's first thread adds
    // the Color term and keeps the minimum, continuing from the best match the previous slice
    // stored. The sum's order differs from the CPU backend's, so a near tie between two
    // cards can occasionally go the other way.
    private static void PatchKernel(
        ArrayView1D<float, Stride1D.Dense> cardPixels,
        ArrayView1D<float, Stride1D.Dense> tilePixels,
        ArrayView1D<int,   Stride1D.Dense> candidates,
        ArrayView1D<float, Stride1D.Dense> cardColors,
        ArrayView1D<float, Stride1D.Dense> tileColors,
        ArrayView1D<float, Stride1D.Dense> bestDists,
        ArrayView1D<int,   Stride1D.Dense> bestCards,
        int K,
        int matchPixels3,
        int tileOffset,
        int kStart,
        int kEnd)
    {
        var partialSums = SharedMemory.Allocate<float>(MaxPatchGroupSize);

        int ti            = Grid.IdxX + tileOffset;
        int thread        = Group.IdxX;
        int groupSize     = Group.DimX;
        int tileBase      = ti * matchPixels3;
        int candidateBase = ti * K;

        // Only the first thread's copies are used.
        int   bestCard = kStart == 0 ? candidates[candidateBase] : bestCards[ti];
        float bestDist = kStart == 0 ? float.MaxValue : bestDists[ti];

        for (int k = kStart; k < kEnd; k++)
        {
            int   c        = candidates[candidateBase + k];
            int   cardBase = c * matchPixels3;
            float sum      = 0f;

            for (int p = thread; p < matchPixels3; p += groupSize)
            {
                float diff = tilePixels[tileBase + p] - cardPixels[cardBase + p];
                sum += diff * diff;
            }

            partialSums[thread] = sum;
            Group.Barrier();

            for (int half = groupSize / 2; half > 0; half /= 2)
            {
                if (thread < half)
                    partialSums[thread] += partialSums[thread + half];
                Group.Barrier();
            }

            if (thread == 0)
            {
                float dist = partialSums[0] + MatchMath.ColorTerm(
                    tileColors[ti * 4 + 3], tileColors[ti * 4], tileColors[ti * 4 + 1], tileColors[ti * 4 + 2],
                    cardColors[c * 3], cardColors[c * 3 + 1], cardColors[c * 3 + 2]);

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestCard = c;
                }
            }

            // partialSums[0] must be read before the next candidate overwrites it.
            Group.Barrier();
        }

        if (thread == 0)
        {
            bestDists[ti] = bestDist;
            bestCards[ti] = bestCard;
        }
    }
}
