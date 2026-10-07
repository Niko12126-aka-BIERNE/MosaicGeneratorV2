using System.Diagnostics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MosaicGenerator.Core;

/// <summary>What a finished run produced.</summary>
/// <param name="OutputPath">The PNG file, or the Deep Zoom viewer's index.html.</param>
public sealed record MosaicResult(string OutputPath, MosaicLayout Layout, int BlankTiles, TimeSpan Elapsed);

public static class MosaicPipeline
{
    /// <summary>
    /// Builds a mosaic from start to finish: card database, input analysis, GPU matching,
    /// compositing and writing the output.
    /// <para>
    /// Blocks until done, so UI callers should run it on a background thread. Progress goes to
    /// <paramref name="progress"/> as it happens. Use a synchronous <see cref="IProgress{T}"/>
    /// rather than <see cref="Progress{T}"/>, which can deliver log lines out of order.
    /// </para>
    /// <para>
    /// Throws <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// The GPU kernels can't be interrupted, so cancelling during matching takes effect once
    /// the kernel finishes. If writing the output fails or is cancelled, partial output is
    /// removed (a Deep Zoom folder only if this run created it).
    /// </para>
    /// </summary>
    public static MosaicResult Run(
        MosaicOptions options,
        IProgress<MosaicProgress>? progress = null,
        CancellationToken ct = default)
    {
        var errors = options.Validate();
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(Environment.NewLine, errors));

        var report     = new ProgressReporter(progress);
        var totalTimer = Stopwatch.StartNew();
        var sw         = new Stopwatch();

        string cardFolderPath = options.CardFolderPath;
        string cardDataPath   = Path.Combine(cardFolderPath, "allCardLabData.json");
        Color  background     = options.Background;

        // Configuration summary
        report.Log("Configuration:");
        report.Log($"  Input image   : {options.InputPath}");
        report.Log($"  Output        : {options.OutputPath}");
        report.Log($"  Card folder   : {cardFolderPath}");
        report.Log($"  Card data     : {cardDataPath}");
        report.Log($"  Cards per row : {options.CardsPerRow}");
        report.Log($"  Card width    : {options.CardWidth}px");
        report.Log($"  Match mode    : {(options.PatchMatch ? $"Patch match ({(options.LabSsd ? "LAB" : "RGB")} SSD, {options.MatchWidth}px wide, top {options.MatchCandidates} candidates)" : "Colour match (CIEDE2000)")}");
        report.Log($"  Output mode   : {(options.DeepZoom ? "Deep Zoom" : "PNG")}");
        report.Log($"  Background    : #{background.ToHex()[..6]} (used to flatten transparency for matching)");
        report.Log($"  Transparency  : tiles below {100 - options.TransparencyThreshold}% opaque are left blank in the output");
        report.Log("");

        // Fail fast, before the card database spends minutes on a machine that can't match.
        if (GpuInfo.FindCudaDevices().Count == 0)
            throw new InvalidOperationException(
                "No NVIDIA GPU with CUDA support was found. The mosaic generator needs one for matching. " +
                "If you have one, make sure its drivers are up to date.");

        // ── Stage: loading cards ─────────────────────────────────────────────
        ct.ThrowIfCancellationRequested();
        report.BeginStage(MosaicStage.LoadingCards);

        // Aspect ratio
        report.Log("Sampling card aspect ratio...");
        sw.Restart();
        float aspectRatio = CardDatabase.SampleAspectRatio(cardFolderPath);
        int   cardHeight  = MosaicLayout.CardHeightFor(options.CardWidth, aspectRatio);
        report.Log($"  Ratio: {aspectRatio:F4}  |  card tile size: {options.CardWidth}x{cardHeight}px  ({sw.ElapsedMilliseconds}ms)");
        report.Log("");

        // Card database
        report.Log("Loading card database...");
        sw.Restart();
        var cards = CardDatabase.LoadOrUpdate(cardDataPath, cardFolderPath, background, report, ct);
        report.Log($"  {cards.Length} cards ready in {sw.ElapsedMilliseconds}ms");
        report.Log("");

        if (cards.Length == 0)
            throw new InvalidOperationException("None of the images in the card folder could be read.");

        // ── Stage: analysing the input image ─────────────────────────────────
        ct.ThrowIfCancellationRequested();
        report.BeginStage(MosaicStage.AnalysingImage);

        report.Log("Loading input image...");
        sw.Restart();
        using var inputImageRaw = Image.Load<Rgba32>(options.InputPath);

        var layout = MosaicLayout.Calculate(
            inputImageRaw.Width, inputImageRaw.Height, options.CardsPerRow, options.CardWidth, aspectRatio);
        int cols = layout.Cols;
        int rows = layout.Rows;

        report.Log($"  Source        : {inputImageRaw.Width}x{inputImageRaw.Height}px");
        report.Log($"  Mosaic size   : {layout.Width}x{layout.Height}px");
        report.Log($"  Grid          : {cols} cols x {rows} rows = {layout.TileCount} tiles");
        report.Log($"  ({sw.ElapsedMilliseconds}ms)");
        report.Log("");

        if (rows < 1 || layout.CardHeight < 1)
            throw new InvalidOperationException(
                "These settings give a mosaic with no card rows. " +
                "Increase cards per row or card width, or use a less wide input image.");

        // Per-tile opacity. Decides which tiles get left blank in the output.
        report.Log("Measuring tile transparency...");
        sw.Restart();
        float[] tileOpacity    = ImageIO.ExtractTileOpacity(inputImageRaw, cols, rows);
        float   minTileOpacity = 1f - options.TransparencyThreshold / 100f;
        int     blankTiles     = tileOpacity.Count(o => o < minTileOpacity);
        report.Log($"  {blankTiles}/{layout.TileCount} tiles will be left blank ({sw.ElapsedMilliseconds}ms)");
        report.Log("");

        using var inputImage = ImageIO.Flatten(inputImageRaw, background);

        int     matchWidth  = 0, matchHeight = 0;
        float[] tileColors  = [];

        if (options.PatchMatch)
        {
            // Cap match width to the actual tile width. No point upscaling for matching
            matchWidth  = Math.Min(options.MatchWidth, options.CardWidth);
            matchHeight = Math.Max(1, (int)Math.Round((double)matchWidth * aspectRatio));

            // Scale input to the match-resolution grid: each tile slot becomes matchWidth × matchHeight
            int matchGridWidth  = cols * matchWidth;
            int matchGridHeight = rows * matchHeight;

            report.Log("Scaling input image for patch matching...");
            sw.Restart();
            inputImage.Mutate(ctx => ctx.Resize(matchGridWidth, matchGridHeight));
            report.Log($"  Scaled to {matchGridWidth}x{matchGridHeight}px ({matchWidth}px/tile) in {sw.ElapsedMilliseconds}ms");
            report.Log("");
        }
        else
        {
            // Scale input to compact analysis resolution. 8 px per tile is enough for median colour
            const int AnalysisPixelsPerTile = 8;
            int analysisWidth  = cols * AnalysisPixelsPerTile;
            int analysisHeight = rows * AnalysisPixelsPerTile;

            report.Log("Scaling input image for colour analysis...");
            sw.Restart();
            inputImage.Mutate(ctx => ctx.Resize(analysisWidth, analysisHeight));
            report.Log($"  Scaled to {analysisWidth}x{analysisHeight}px ({AnalysisPixelsPerTile}px/tile) in {sw.ElapsedMilliseconds}ms");
            report.Log("");

            report.Log("Extracting tile colours...");
            sw.Restart();
            tileColors = TileExtractor.Extract(inputImage, cols, rows, AnalysisPixelsPerTile, AnalysisPixelsPerTile);
            report.Log($"  Done in {sw.ElapsedMilliseconds}ms");
            report.Log("");
        }

        // ── Stage: matching ──────────────────────────────────────────────────
        ct.ThrowIfCancellationRequested();
        report.BeginStage(MosaicStage.Matching);

        int[] tileCardIndices;

        if (options.PatchMatch)
        {
            report.Log("Running GPU patch matching...");
            sw.Restart();
            tileCardIndices = GpuPatchMatcher.Match(
                cards, inputImage, cardFolderPath,
                cols, rows, matchWidth, matchHeight, options.MatchCandidates, options.LabSsd, background,
                report, ct);
            report.Log($"  Done in {sw.ElapsedMilliseconds}ms");
        }
        else
        {
            report.Log("Running GPU colour matching...");
            report.Log("  (First run compiles the CUDA kernel — may take a few seconds)");
            sw.Restart();
            tileCardIndices = GpuCardMatcher.Match(cards, tileColors, layout.TileCount, report, ct);
            report.Log($"  Done in {sw.ElapsedMilliseconds}ms");
        }

        report.Log("");

        // Blank out tiles that are too transparent to bother placing a card on.
        for (int t = 0; t < tileCardIndices.Length; t++)
            if (tileOpacity[t] < minTileOpacity)
                tileCardIndices[t] = -1;

        // ── Stage: compositing and writing the output ────────────────────────
        ct.ThrowIfCancellationRequested();
        report.BeginStage(MosaicStage.WritingOutput);

        report.Log(options.DeepZoom ? "Compositing and building Deep Zoom..." : "Compositing and saving PNG...");
        sw.Restart();

        var strips = CardCompositor.CompositeStrips(
            tileCardIndices, cards, cardFolderPath,
            cols, rows, layout.CardWidth, layout.CardHeight, layout.Width, background,
            report, ct);

        string deepZoomFolder        = DeepZoomWriter.GetOutputFolder(options.OutputPath);
        bool   deepZoomFolderExisted = Directory.Exists(deepZoomFolder);
        string resultPath;

        try
        {
            resultPath = options.DeepZoom
                ? DeepZoomWriter.Write(options.OutputPath, layout.Width, layout.Height, strips, report, ct)
                : PngWriter.Write(options.OutputPath, layout.Width, layout.Height, strips, report, ct);
        }
        catch
        {
            RemovePartialOutput(options, deepZoomFolder, deepZoomFolderExisted, report);
            throw;
        }

        report.Log($"  Done in {sw.ElapsedMilliseconds}ms");
        report.Log("");
        report.Log($"Total time: {totalTimer.Elapsed.TotalSeconds:F1}s");

        return new MosaicResult(resultPath, layout, blankTiles, totalTimer.Elapsed);
    }

    private static void RemovePartialOutput(
        MosaicOptions options, string deepZoomFolder, bool deepZoomFolderExisted, ProgressReporter report)
    {
        try
        {
            if (!options.DeepZoom)
            {
                File.Delete(options.OutputPath);
            }
            else if (!deepZoomFolderExisted)
            {
                Directory.Delete(deepZoomFolder, recursive: true);
            }
            else
            {
                // The folder was there before this run, so it may hold things we didn't write.
                report.Log($"  Partial Deep Zoom output left in {deepZoomFolder}");
                return;
            }

            report.Log("  Removed partial output.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Log($"  Could not remove partial output: {ex.Message}");
        }
    }
}
