using System.Numerics;

namespace MosaicGenerator.Core.Compute;

/// <summary>
/// Runs matching on the processor, spread over all its cores. Works on every machine, so it's
/// the fallback when there's no usable GPU. The loops mirror the GPU kernels in
/// <see cref="IlgpuBackend"/>: one iteration per tile, the same maths in the same order.
/// Patch SSD uses the processor's SIMD instructions where it has them (see <see cref="PatchSsd"/>).
/// </summary>
public sealed class CpuBackend : IMatchBackend
{
    public static DeviceInfo Info { get; } = new(
        "cpu",
        $"Processor ({Environment.ProcessorCount} threads)",
        DeviceKind.Cpu,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    public DeviceInfo Device => Info;

    public int[] MatchColors(
        float[] cardLab, float[] tileLab, int tileCount,
        Action<double>? progress, CancellationToken ct)
    {
        int cardCount = cardLab.Length / 3;
        var results   = new int[tileCount];
        int done      = 0;

        Parallel.For(0, tileCount, new ParallelOptions { CancellationToken = ct }, ti =>
        {
            float tL = tileLab[ti * 3];
            float tA = tileLab[ti * 3 + 1];
            float tB = tileLab[ti * 3 + 2];

            int   bestCard = 0;
            float bestDist = float.MaxValue;

            for (int c = 0; c < cardCount; c++)
            {
                float dist = MatchMath.CieDe2000(tL, tA, tB, cardLab[c * 3], cardLab[c * 3 + 1], cardLab[c * 3 + 2]);

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestCard = c;
                }
            }

            results[ti] = bestCard;
            progress?.Invoke((double)Interlocked.Increment(ref done) / tileCount);
        });

        return results;
    }

    public int[] MatchPatches(
        byte[] cardPatches, byte[] tilePatches, int[] candidates,
        float[] cardColors, float[] tileColors,
        int tileCount, int candidatesPerTile, int pixelsPerPatch, int firstChannelWeight,
        Action<double>? progress, CancellationToken ct)
    {
        var results        = new int[tileCount];
        int done           = 0;
        int valuesPerPatch = pixelsPerPatch * 3;
        var weights        = ChannelWeights(firstChannelWeight);

        Parallel.For(0, tileCount, new ParallelOptions { CancellationToken = ct }, ti =>
        {
            int tileBase      = ti * valuesPerPatch;
            int candidateBase = ti * candidatesPerTile;

            float tileL       = tileColors[ti * 4];
            float tileA       = tileColors[ti * 4 + 1];
            float tileB       = tileColors[ti * 4 + 2];
            float colorFactor = tileColors[ti * 4 + 3];

            int   bestCard = candidates[candidateBase];
            float bestDist = float.MaxValue;

            for (int k = 0; k < candidatesPerTile; k++)
            {
                int  c   = candidates[candidateBase + k];
                long ssd = PatchSsd(
                    tilePatches.AsSpan(tileBase, valuesPerPatch),
                    cardPatches.AsSpan(c * valuesPerPatch, valuesPerPatch),
                    firstChannelWeight, weights);

                float dist = MatchMath.PatchScore(ssd, MatchMath.ColorTerm(colorFactor, tileL, tileA, tileB,
                    cardColors[c * 3], cardColors[c * 3 + 1], cardColors[c * 3 + 2]));

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestCard = c;
                }
            }

            results[ti] = bestCard;
            progress?.Invoke((double)Interlocked.Increment(ref done) / tileCount);
        });

        return results;
    }

    // Patch SSD with SIMD
    //
    // Vector<T> is as wide as the processor's SIMD registers: 32 bytes with AVX2 (most PCs),
    // 16 with NEON (Apple Silicon). Each step takes that many bytes of both patches, widens
    // them to 16 and then 32 bits (squares of differences up to 255 need 17 bits), and adds
    // the weighted squares into four running sums. The sums are whole numbers, so the result
    // is exactly the plain loop's, whatever order the values are added in.

    // The running sums move into the 64-bit total every this many steps, before they could
    // overflow: each step adds at most 2 * 255^2 per lane, and 8192 of those fit an int.
    private const int StepsPerFlush = 8192;

    // A pixel's first channel counts firstChannelWeight times. Pixels are 3 bytes, so where the
    // first channel falls in a step depends on where the step starts (its position mod 3).
    // For each of those 3 starting phases, the weights of the step's bytes, split the same way
    // the bytes are widened: 4 vectors of ints, together covering one Vector<byte>.
    private static Vector<int>[] ChannelWeights(int firstChannelWeight)
    {
        int ints    = Vector<int>.Count;
        var weights = new Vector<int>[3 * 4];
        var lanes   = new int[ints];

        for (int phase = 0; phase < 3; phase++)
            for (int part = 0; part < 4; part++)
            {
                for (int lane = 0; lane < ints; lane++)
                    lanes[lane] = (phase + part * ints + lane) % 3 == 0 ? firstChannelWeight : 1;
                weights[phase * 4 + part] = new Vector<int>(lanes);
            }

        return weights;
    }

    // Sum of firstChannelWeight * d0² + d1² + d2² over the pixels of two equally long patches.
    private static long PatchSsd(ReadOnlySpan<byte> tile, ReadOnlySpan<byte> card, int firstChannelWeight, Vector<int>[] weights)
    {
        long total = 0;
        int  p     = 0;

        if (Vector.IsHardwareAccelerated)
        {
            int step      = Vector<byte>.Count;
            int phaseStep = step % 3;
            int phase     = 0;

            var sum0 = Vector<int>.Zero;
            var sum1 = Vector<int>.Zero;
            var sum2 = Vector<int>.Zero;
            var sum3 = Vector<int>.Zero;
            int steps = 0;

            for (; p <= tile.Length - step; p += step)
            {
                Vector.Widen(new Vector<byte>(tile[p..]), out var tileLow, out var tileHigh);
                Vector.Widen(new Vector<byte>(card[p..]), out var cardLow, out var cardHigh);

                var diffLow  = Vector.AsVectorInt16(tileLow)  - Vector.AsVectorInt16(cardLow);
                var diffHigh = Vector.AsVectorInt16(tileHigh) - Vector.AsVectorInt16(cardHigh);
                Vector.Widen(diffLow,  out var d0, out var d1);
                Vector.Widen(diffHigh, out var d2, out var d3);

                int w = phase * 4;
                sum0 += d0 * d0 * weights[w];
                sum1 += d1 * d1 * weights[w + 1];
                sum2 += d2 * d2 * weights[w + 2];
                sum3 += d3 * d3 * weights[w + 3];

                phase += phaseStep;
                if (phase >= 3) phase -= 3;

                if (++steps == StepsPerFlush)
                {
                    total += Sum(sum0) + Sum(sum1) + Sum(sum2) + Sum(sum3);
                    sum0 = sum1 = sum2 = sum3 = Vector<int>.Zero;
                    steps = 0;
                }
            }

            total += Sum(sum0) + Sum(sum1) + Sum(sum2) + Sum(sum3);
        }

        // The rest (or everything, without SIMD), one byte at a time.
        for (; p < tile.Length; p++)
        {
            int d = tile[p] - card[p];
            total += (p % 3 == 0 ? firstChannelWeight : 1) * d * d;
        }

        return total;
    }

    // Adds up a vector's lanes as a 64-bit number, so the lanes together can't overflow.
    private static long Sum(Vector<int> v)
    {
        Vector.Widen(v, out var low, out var high);
        return Vector.Sum(low + high);
    }

    public void Dispose() { }
}
