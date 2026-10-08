namespace MosaicGenerator.Core.Compute;

public enum DeviceKind
{
    Cuda,
    OpenCL,
    Cpu,
}

/// <summary>A device that can run the matching.</summary>
/// <param name="Id">Stable name used to choose this device, e.g. "cuda:0", "opencl:1" or "cpu".</param>
/// <param name="MemoryBytes">Device memory (system memory for the CPU).</param>
public sealed record DeviceInfo(string Id, string Name, DeviceKind Kind, long MemoryBytes)
{
    public string KindName => Kind switch
    {
        DeviceKind.Cuda   => "CUDA",
        DeviceKind.OpenCL => "OpenCL",
        _                 => "CPU",
    };

    public override string ToString() => Kind == DeviceKind.Cpu ? Name : $"{Name} ({KindName})";
}

/// <summary>
/// The number-crunching part of matching, the only part that differs between devices.
/// Everything before it (loading card images, the colour pre-filter, flattening data into
/// arrays) is the same for every device and lives in <see cref="ColorMatcher"/> and
/// <see cref="PatchMatcher"/>.
/// <para>
/// <c>progress</c> in each method receives this step's own progress from 0 to 1,
/// where the device can measure it. Implementations that can't, report nothing.
/// </para>
/// </summary>
public interface IMatchBackend : IDisposable
{
    DeviceInfo Device { get; }

    /// <summary>
    /// For each tile, the index of the card with the smallest CIEDE2000 distance.
    /// Both arrays hold 3 floats (L, a, b) per tile or card.
    /// </summary>
    int[] MatchColors(
        float[] cardLab, float[] tileLab, int tileCount,
        Action<double>? progress, CancellationToken ct);

    /// <summary>
    /// For each tile, the candidate card whose pixels differ least (sum of squared differences).
    /// Patches are <paramref name="valuesPerPatch"/> floats each; tile t's candidates are
    /// <c>candidates[t * candidatesPerTile ..]</c>, indices into the card patches.
    /// </summary>
    int[] MatchPatches(
        float[] cardPatches, float[] tilePatches, int[] candidates,
        int tileCount, int candidatesPerTile, int valuesPerPatch,
        Action<double>? progress, CancellationToken ct);
}
