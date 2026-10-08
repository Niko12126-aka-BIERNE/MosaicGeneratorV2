using ILGPU;
using ILGPU.Runtime;

namespace MosaicGenerator.Core.Compute;

/// <summary>
/// Runs matching on a GPU through ILGPU, which compiles the kernels below for CUDA (NVIDIA)
/// or OpenCL (AMD, Intel). The same kernel code serves both.
/// <para>
/// A GPU kernel can't be interrupted or measured once it runs, so cancellation is checked
/// right before it starts, and progress jumps from 0 to 1.
/// </para>
/// </summary>
public sealed class IlgpuBackend : IMatchBackend
{
    private readonly Context          _context;
    private readonly Accelerator      _accelerator;
    private readonly ProgressReporter? _log;

    // Compiled on first use, which takes a few seconds (up to ~30s for the patch kernel).
    private Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>,
        ArrayView1D<int, Stride1D.Dense>, int>? _colorKernel;
    private Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>,
        ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, int, int>? _patchKernel;

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
        using var deviceCards   = _accelerator.Allocate1D<float>(cardLab.Length);
        using var deviceTiles   = _accelerator.Allocate1D<float>(tileLab.Length);
        using var deviceResults = _accelerator.Allocate1D<int>(tileCount);

        deviceCards.CopyFromCPU(cardLab);
        deviceTiles.CopyFromCPU(tileLab);

        if (_colorKernel == null)
        {
            _log?.Log("  Compiling GPU kernel (first run may take a few seconds)...");
            _colorKernel = _accelerator.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView1D<float, Stride1D.Dense>,
                ArrayView1D<float, Stride1D.Dense>,
                ArrayView1D<int, Stride1D.Dense>,
                int>(ColorKernel);
        }

        ct.ThrowIfCancellationRequested();
        _colorKernel(tileCount, deviceCards.View, deviceTiles.View, deviceResults.View, cardLab.Length / 3);
        _accelerator.Synchronize();
        progress?.Invoke(1);

        return deviceResults.GetAsArray1D();
    }

    public int[] MatchPatches(
        float[] cardPatches, float[] tilePatches, int[] candidates,
        int tileCount, int candidatesPerTile, int valuesPerPatch,
        Action<double>? progress, CancellationToken ct)
    {
        using var deviceCards      = _accelerator.Allocate1D<float>(cardPatches.Length);
        using var deviceTiles      = _accelerator.Allocate1D<float>(tilePatches.Length);
        using var deviceCandidates = _accelerator.Allocate1D<int>(candidates.Length);
        using var deviceResults    = _accelerator.Allocate1D<int>(tileCount);

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
                ArrayView1D<int,   Stride1D.Dense>,
                int, int>(PatchKernel);
        }

        ct.ThrowIfCancellationRequested();
        _patchKernel(tileCount, deviceCards.View, deviceTiles.View, deviceCandidates.View, deviceResults.View,
            candidatesPerTile, valuesPerPatch);
        _accelerator.Synchronize();
        progress?.Invoke(1);

        return deviceResults.GetAsArray1D();
    }

    public void Dispose()
    {
        _accelerator.Dispose();
        _context.Dispose();
    }

    // Kernels

    // One GPU thread per tile. Each thread iterates all cards and tracks the minimum CIEDE2000 distance.
    private static void ColorKernel(
        Index1D index,
        ArrayView1D<float, Stride1D.Dense> cards,
        ArrayView1D<float, Stride1D.Dense> tiles,
        ArrayView1D<int, Stride1D.Dense> results,
        int cardCount)
    {
        int ti = index.X;

        float tL = tiles[ti * 3];
        float tA = tiles[ti * 3 + 1];
        float tB = tiles[ti * 3 + 2];

        int   bestCard = 0;
        float bestDist = float.MaxValue;

        for (int c = 0; c < cardCount; c++)
        {
            float dist = MatchMath.CieDe2000(tL, tA, tB, cards[c * 3], cards[c * 3 + 1], cards[c * 3 + 2]);

            if (dist < bestDist)
            {
                bestDist = dist;
                bestCard = c;
            }
        }

        results[ti] = bestCard;
    }

    // One thread per tile. Iterates only the K pre-selected candidate cards for this tile,
    // accumulates float SSD, and keeps the minimum.
    private static void PatchKernel(
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
