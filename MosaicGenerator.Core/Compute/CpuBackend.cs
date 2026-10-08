namespace MosaicGenerator.Core.Compute;

/// <summary>
/// Runs matching on the processor, spread over all its cores. Works on every machine, so it's
/// the fallback when there's no usable GPU. The loops mirror the GPU kernels in
/// <see cref="IlgpuBackend"/>: one iteration per tile, the same maths in the same order.
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
        float[] cardPatches, float[] tilePatches, int[] candidates,
        int tileCount, int candidatesPerTile, int valuesPerPatch,
        Action<double>? progress, CancellationToken ct)
    {
        var results = new int[tileCount];
        int done    = 0;

        Parallel.For(0, tileCount, new ParallelOptions { CancellationToken = ct }, ti =>
        {
            int tileBase      = ti * valuesPerPatch;
            int candidateBase = ti * candidatesPerTile;

            int   bestCard = candidates[candidateBase];
            float bestDist = float.MaxValue;

            for (int k = 0; k < candidatesPerTile; k++)
            {
                int   c        = candidates[candidateBase + k];
                int   cardBase = c * valuesPerPatch;
                float dist     = 0f;

                for (int p = 0; p < valuesPerPatch; p++)
                {
                    float diff = tilePatches[tileBase + p] - cardPatches[cardBase + p];
                    dist += diff * diff;
                }

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

    public void Dispose() { }
}
