namespace MosaicGenerator.Core.Compute;

/// <summary>
/// Runs matching on a GPU, and if that fails (out of memory, a driver problem...), logs why
/// and runs the same step on the processor instead. Used when the device is chosen
/// automatically. A device the user picked is used as-is, so its errors are reported.
/// </summary>
public sealed class FallbackBackend(IMatchBackend primary, ProgressReporter? log) : IMatchBackend
{
    private CpuBackend? _cpu;   // created on the first failure; the GPU isn't used again after that

    public DeviceInfo Device => _cpu?.Device ?? primary.Device;

    public int[] MatchColors(
        float[] cardLab, float[] tileLab, int tileCount,
        Action<double>? progress, CancellationToken ct) =>
        Run(backend => backend.MatchColors(cardLab, tileLab, tileCount, progress, ct));

    public int[] MatchPatches(
        float[] cardPatches, float[] tilePatches, int[] candidates,
        int tileCount, int candidatesPerTile, int valuesPerPatch,
        Action<double>? progress, CancellationToken ct) =>
        Run(backend => backend.MatchPatches(
            cardPatches, tilePatches, candidates, tileCount, candidatesPerTile, valuesPerPatch, progress, ct));

    private int[] Run(Func<IMatchBackend, int[]> step)
    {
        if (_cpu == null)
        {
            try
            {
                return step(primary);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Log($"  {primary.Device} failed: {ex.Message}");
                log?.Log("  Continuing on the processor instead (slower).");
                _cpu = new CpuBackend();
            }
        }

        return step(_cpu);
    }

    public void Dispose()
    {
        primary.Dispose();
        _cpu?.Dispose();
    }
}
