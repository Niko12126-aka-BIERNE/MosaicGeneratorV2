namespace MosaicGenerator.Core;

/// <summary>The phases of a mosaic run, in the order they happen.</summary>
public enum MosaicStage
{
    LoadingCards,
    AnalysingImage,
    Matching,
    WritingOutput,
}

/// <summary>
/// One progress update from the engine:
/// <list type="bullet">
///   <item><see cref="Message"/> set: a log line (the CLI prints these).</item>
///   <item><see cref="Fraction"/> set: how far the current stage is, from 0 to 1.</item>
///   <item>Neither set: the stage has just started, and its progress is not yet known.</item>
/// </list>
/// </summary>
public readonly record struct MosaicProgress(MosaicStage Stage, string? Message, double? Fraction);

/// <summary>
/// Tracks the current stage and forwards log lines and fractions to an
/// <see cref="IProgress{T}"/> sink, so engine components can report progress
/// without knowing which stage they run in.
/// <para>
/// Fractions are only forwarded when they reach a new whole percent, which keeps the
/// update rate low however often components call <see cref="Fraction(double)"/>.
/// </para>
/// <para>
/// Thread-safe: parallel loops report from many threads. The sink is called under a
/// lock so updates arrive in order, so it must not call back into this reporter.
/// </para>
/// </summary>
public sealed class ProgressReporter(IProgress<MosaicProgress>? sink)
{
    private readonly object _lock = new();
    private MosaicStage _stage;
    private int _lastPercent = -1;

    public void BeginStage(MosaicStage stage)
    {
        lock (_lock)
        {
            _stage       = stage;
            _lastPercent = -1;
            sink?.Report(new MosaicProgress(stage, null, null));
        }
    }

    public void Log(string message)
    {
        lock (_lock)
            sink?.Report(new MosaicProgress(_stage, message, null));
    }

    public void Fraction(long done, long total) =>
        Fraction(total <= 0 ? 1 : (double)done / total);

    public void Fraction(double fraction)
    {
        int percent = (int)(Math.Clamp(fraction, 0, 1) * 100);

        lock (_lock)
        {
            if (percent <= _lastPercent)
                return;
            _lastPercent = percent;
            sink?.Report(new MosaicProgress(_stage, null, percent / 100.0));
        }
    }
}
