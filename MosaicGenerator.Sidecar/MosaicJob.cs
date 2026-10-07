using MosaicGenerator.Core;

namespace MosaicGenerator.Sidecar;

/// <summary>
/// Runs one mosaic generation at a time on a background thread, so the main loop stays
/// free to receive a "cancel" while it runs. Progress is sent as events as it happens,
/// and every job ends with exactly one "done", "cancelled" or "failed" event.
/// </summary>
public static class MosaicJob
{
    private static readonly object Lock = new();
    private static bool                     _running;
    private static CancellationTokenSource? _cts;
    private static Task?                    _task;

    public static void Start(MosaicOptions options)
    {
        lock (Lock)
        {
            if (_running)
                throw new RequestException("A mosaic is already being generated.");

            _running = true;
            _cts     = new CancellationTokenSource();
            var ct   = _cts.Token;
            _task    = Task.Run(() => Run(options, ct));
        }
    }

    /// <summary>Asks the running job to stop. Returns false if nothing was running.</summary>
    public static bool Cancel()
    {
        lock (Lock)
        {
            if (!_running)
                return false;
            _cts!.Cancel();
            return true;
        }
    }

    /// <summary>Cancels any running job and waits for it to clean up. Used when shutting down.</summary>
    public static void CancelAndWait()
    {
        Cancel();
        Task? task;
        lock (Lock)
            task = _task;
        task?.Wait();   // Run() catches everything, so this doesn't throw
    }

    private static void Run(MosaicOptions options, CancellationToken ct)
    {
        Message final;
        try
        {
            var result = MosaicPipeline.Run(options, new EventProgress(), ct);
            final = new DoneEvent(result.OutputPath, result.Layout, result.BlankTiles, result.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            final = new CancelledEvent();
        }
        catch (Exception ex)
        {
            final = new FailedEvent(ex.Message);
        }

        // Mark the job finished *before* telling the app, so a "start" sent straight
        // after "done" isn't refused as "already being generated".
        lock (Lock)
        {
            _running = false;
            _cts!.Dispose();
            _cts = null;
        }

        Output.Send(final);
    }

    /// <summary>Turns engine progress updates into stage / progress / log events.</summary>
    private sealed class EventProgress : IProgress<MosaicProgress>
    {
        public void Report(MosaicProgress p)
        {
            if (p.Message != null)
                Output.Send(new LogEvent(p.Message));
            else if (p.Fraction is double fraction)
                Output.Send(new ProgressEvent(p.Stage, fraction));
            else
                Output.Send(new StageEvent(p.Stage));
        }
    }
}
