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
/// </summary>
public sealed class IlgpuBackend : IMatchBackend
{
    private readonly Context          _context;
    private readonly Accelerator      _accelerator;
    private readonly ProgressReporter? _log;

    // Each launch aims for this duration, far below the ~2s watchdog, so a slower-than-expected
    // launch still finishes in time.
    private const double TargetLaunchSeconds = 0.25;

    // Compiled on first use, which takes a few seconds (up to ~30s for the patch kernel).
    private Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>,
        ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>,
        int, int, int>? _colorKernel;
    private Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>,
        ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>,
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
            _log?.Log("  Compiling GPU kernel (first run may take a few seconds)...");
            _colorKernel = _accelerator.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView1D<float, Stride1D.Dense>,
                ArrayView1D<float, Stride1D.Dense>,
                ArrayView1D<float, Stride1D.Dense>,
                ArrayView1D<int,   Stride1D.Dense>,
                int, int, int>(ColorKernel);
        }

        RunInSlices(tileCount, cardLab.Length / 3, progress, ct, (tileOffset, tiles, cardStart, cardEnd) =>
            _colorKernel(tiles, deviceCards.View, deviceTiles.View, deviceBestDists.View, deviceBestCards.View,
                tileOffset, cardStart, cardEnd));

        return deviceBestCards.GetAsArray1D();
    }

    public int[] MatchPatches(
        float[] cardPatches, float[] tilePatches, int[] candidates,
        int tileCount, int candidatesPerTile, int valuesPerPatch,
        Action<double>? progress, CancellationToken ct)
    {
        using var deviceCards      = _accelerator.Allocate1D<float>(cardPatches.Length);
        using var deviceTiles      = _accelerator.Allocate1D<float>(tilePatches.Length);
        using var deviceCandidates = _accelerator.Allocate1D<int>(candidates.Length);
        using var deviceBestDists  = _accelerator.Allocate1D<float>(tileCount);
        using var deviceBestCards  = _accelerator.Allocate1D<int>(tileCount);

        deviceCards.CopyFromCPU(cardPatches);
        deviceTiles.CopyFromCPU(tilePatches);
        deviceCandidates.CopyFromCPU(candidates);

        if (_patchKernel == null)
        {
            _log?.Log("  Compiling GPU kernel (first run may take ~30s)...");
            _patchKernel = _accelerator.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView1D<float, Stride1D.Dense>,
                ArrayView1D<float, Stride1D.Dense>,
                ArrayView1D<int,   Stride1D.Dense>,
                ArrayView1D<float, Stride1D.Dense>,
                ArrayView1D<int,   Stride1D.Dense>,
                int, int, int, int, int>(PatchKernel);
        }

        RunInSlices(tileCount, candidatesPerTile, progress, ct, (tileOffset, tiles, kStart, kEnd) =>
            _patchKernel(tiles, deviceCards.View, deviceTiles.View, deviceCandidates.View,
                deviceBestDists.View, deviceBestCards.View,
                candidatesPerTile, valuesPerPatch, tileOffset, kStart, kEnd));

        return deviceBestCards.GetAsArray1D();
    }

    // Splits the work (every tile compared with cardsPerTile cards) into launches short enough
    // to stay well inside the watchdog, and calls launch(tileOffset, tiles, cardStart, cardEnd)
    // for each, waiting for it to finish.
    //
    // Each GPU thread handles one tile, and one thread can take ~100ms to go through all its
    // cards, so launching fewer tiles doesn't make a launch shorter: it only leaves the GPU
    // idle. Instead, every launch runs enough tiles to fill the GPU, and the cards are split
    // into slices; the kernels keep each tile's best match so far in GPU memory between slices.
    // Launch time then grows with the slice size, so the first slice holds a single card and
    // each next one is scaled by how long the last took, growing at most 2x at a time.
    private void RunInSlices(
        int tileCount, int cardsPerTile, Action<double>? progress, CancellationToken ct,
        Action<int, int, int, int> launch)
    {
        // Twice the threads the GPU can run at once, so it stays busy to the end of each launch.
        int  tileBatch = (int)Math.Clamp(2L * _accelerator.MaxNumThreads, 1, tileCount);
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

    // One thread per tile of the batch starting at tileOffset. Goes through this tile's
    // pre-selected candidates [kStart, kEnd), accumulates float SSD, and keeps the minimum,
    // continuing from the best match the previous slice stored for its tile.
    private static void PatchKernel(
        Index1D                            index,
        ArrayView1D<float, Stride1D.Dense> cardPixels,
        ArrayView1D<float, Stride1D.Dense> tilePixels,
        ArrayView1D<int,   Stride1D.Dense> candidates,
        ArrayView1D<float, Stride1D.Dense> bestDists,
        ArrayView1D<int,   Stride1D.Dense> bestCards,
        int K,
        int matchPixels3,
        int tileOffset,
        int kStart,
        int kEnd)
    {
        int ti            = index.X + tileOffset;
        int tileBase      = ti * matchPixels3;
        int candidateBase = ti * K;

        int   bestCard = kStart == 0 ? candidates[candidateBase] : bestCards[ti];
        float bestDist = kStart == 0 ? float.MaxValue : bestDists[ti];

        for (int k = kStart; k < kEnd; k++)
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

        bestDists[ti] = bestDist;
        bestCards[ti] = bestCard;
    }
}
