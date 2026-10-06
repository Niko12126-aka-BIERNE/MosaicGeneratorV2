using MosaicGeneratorCLI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Diagnostics;

if (args.Length < 2)
{
    Console.WriteLine("Usage: MosaicGeneratorCLI <input-image> <card-folder> [output] [options]");
    Console.WriteLine();
    Console.WriteLine("Arguments:");
    Console.WriteLine("  <input-image>          Path to the source image");
    Console.WriteLine("  <card-folder>          Path to the folder containing card images");
    Console.WriteLine("  [output]               Output path (optional)");
    Console.WriteLine("                           PNG mode  : path to .png file");
    Console.WriteLine("                           Deep Zoom : path to output folder");
    Console.WriteLine("                           If omitted, defaults to <input-name>_Mosaic.png");
    Console.WriteLine("                           in the current directory");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --cards-per-row <n>    Number of card columns  (default: 100)");
    Console.WriteLine("  --card-width <n>       Card width in pixels    (default: 160)");
    Console.WriteLine("  --patch-match          Match tile patches instead of average colours");
    Console.WriteLine("  --match-width <n>      Patch matching resolution in pixels (default: 64) [high values increase the VRAM consumption and processing time]");
    Console.WriteLine("  --match-candidates <n> Top-K candidate cards per tile for patch matching (default: 500) [high values increase the VRAM consumption and processing time]");
    Console.WriteLine("  --lab-ssd              Use perceptually uniform LAB SSD instead of RGB SSD (slower, better colors and contrast in some scenarios)");
    Console.WriteLine("  --deep-zoom            Generate Deep Zoom tiles + HTML viewer instead of PNG (great for large images, that would be too large for a single PNG)");
    return;
}

string inputPath      = args[0];
string cardFolderPath = args[1];
string? outputPath    = null;
int cardsPerRow       = 100;
int cardWidth         = 160;
bool patchMatch       = false;
int matchWidth        = 64;
int matchCandidates   = 500;
bool labSsd           = false;
bool deepZoom         = false;

// args[2] is the optional output path only if it doesn't look like a flag
int optStart = 2;
if (args.Length > 2 && !args[2].StartsWith("--"))
{
    outputPath = args[2];
    optStart   = 3;
}

for (int i = optStart; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--cards-per-row":     cardsPerRow     = int.Parse(args[++i]); break;
        case "--card-width":        cardWidth       = int.Parse(args[++i]); break;
        case "--patch-match":       patchMatch      = true;                 break;
        case "--match-width":       matchWidth      = int.Parse(args[++i]); break;
        case "--match-candidates":  matchCandidates = int.Parse(args[++i]); break;
        case "--lab-ssd":           labSsd          = true;                 break;
        case "--deep-zoom":         deepZoom        = true;                 break;
    }
}

// Resolve output path
string ext = deepZoom ? "" : ".png";

if (outputPath == null)
{
    // Auto-generate: <input-name>_Mosaic[.png] in the current directory,
    // incrementing a counter if the name is already taken.
    string baseName = Path.GetFileNameWithoutExtension(inputPath) + "_Mosaic";
    outputPath = baseName + ext;

    if (OutputExists(outputPath, deepZoom))
    {
        int n = 1;
        do { outputPath = $"{baseName}{n++}{ext}"; }
        while (OutputExists(outputPath, deepZoom));
    }

    Console.WriteLine($"No output specified — writing to: {Path.GetFullPath(outputPath)}");
    Console.WriteLine();
}
else if (OutputExists(outputPath, deepZoom))
{
    Console.Write($"Output '{outputPath}' already exists. Overwrite? [y/N]: ");
    string? answer = Console.ReadLine()?.Trim();
    if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Cancelled.");
        return;
    }
    Console.WriteLine();
}

static bool OutputExists(string path, bool deepZoom) =>
    deepZoom ? Directory.Exists(path) : File.Exists(path);

// Configuration summary
string cardDataPath = Path.Combine(cardFolderPath, "allCardLabData.json");

var totalTimer = Stopwatch.StartNew();
var sw         = new Stopwatch();

Console.WriteLine("=== Mosaic Generator ===");
Console.WriteLine();
Console.WriteLine("Configuration:");
Console.WriteLine($"  Input image   : {inputPath}");
Console.WriteLine($"  Output        : {outputPath}");
Console.WriteLine($"  Card folder   : {cardFolderPath}");
Console.WriteLine($"  Card data     : {cardDataPath}");
Console.WriteLine($"  Cards per row : {cardsPerRow}");
Console.WriteLine($"  Card width    : {cardWidth}px");
Console.WriteLine($"  Match mode    : {(patchMatch ? $"Patch match ({(labSsd ? "LAB" : "RGB")} SSD, {matchWidth}px wide, top {matchCandidates} candidates)" : "Colour match (CIEDE2000)")}");
Console.WriteLine($"  Output mode   : {(deepZoom ? "Deep Zoom" : "PNG")}");
Console.WriteLine();

// Aspect ratio
Console.WriteLine("Sampling card aspect ratio...");
sw.Restart();
float aspectRatio = CardDatabase.SampleAspectRatio(cardFolderPath);
int cardHeight = (int)(cardWidth * aspectRatio);
Console.WriteLine($"  Ratio: {aspectRatio:F4}  |  card tile size: {cardWidth}x{cardHeight}px  ({sw.ElapsedMilliseconds}ms)");
Console.WriteLine();

// Card database
Console.WriteLine("Loading card database...");
sw.Restart();
var cards = CardDatabase.LoadOrUpdate(cardDataPath, cardFolderPath);
Console.WriteLine($"  {cards.Length} cards ready in {sw.ElapsedMilliseconds}ms");
Console.WriteLine();

// Input image
Console.WriteLine("Loading input image...");
sw.Restart();
using var inputImage = Image.Load<Rgb24>(inputPath);

int mosaicWidth  = cardWidth * cardsPerRow;
int mosaicHeight = (int)((double)mosaicWidth / inputImage.Width * inputImage.Height);
int cols         = mosaicWidth  / cardWidth;
int rows         = mosaicHeight / cardHeight;

Console.WriteLine($"  Source        : {inputImage.Width}x{inputImage.Height}px");
Console.WriteLine($"  Mosaic size   : {mosaicWidth}x{mosaicHeight}px");
Console.WriteLine($"  Grid          : {cols} cols x {rows} rows = {cols * rows} tiles");
Console.WriteLine($"  ({sw.ElapsedMilliseconds}ms)");
Console.WriteLine();

// Matching
int[] tileCardIndices;

if (patchMatch)
{
    // Cap match width to the actual tile width. No point upscaling for matching
    matchWidth = Math.Min(matchWidth, cardWidth);
    int matchHeight = Math.Max(1, (int)Math.Round((double)matchWidth * aspectRatio));

    // Scale input to the match-resolution grid: each tile slot becomes matchWidth × matchHeight
    int matchGridWidth  = cols * matchWidth;
    int matchGridHeight = rows * matchHeight;

    Console.WriteLine("Scaling input image for patch matching...");
    sw.Restart();
    inputImage.Mutate(ctx => ctx.Resize(matchGridWidth, matchGridHeight));
    Console.WriteLine($"  Scaled to {matchGridWidth}x{matchGridHeight}px ({matchWidth}px/tile) in {sw.ElapsedMilliseconds}ms");
    Console.WriteLine();

    Console.WriteLine("Running GPU patch matching...");
    sw.Restart();
    tileCardIndices = GpuPatchMatcher.Match(
        cards, inputImage, cardFolderPath,
        cols, rows, matchWidth, matchHeight, matchCandidates, labSsd);
    Console.WriteLine($"  Done in {sw.ElapsedMilliseconds}ms");
}
else
{
    // Scale input to compact analysis resolution. 8 px per tile is enough for median colour
    const int AnalysisPixelsPerTile = 8;
    int analysisWidth  = cols * AnalysisPixelsPerTile;
    int analysisHeight = rows * AnalysisPixelsPerTile;

    Console.WriteLine("Scaling input image for colour analysis...");
    sw.Restart();
    inputImage.Mutate(ctx => ctx.Resize(analysisWidth, analysisHeight));
    Console.WriteLine($"  Scaled to {analysisWidth}x{analysisHeight}px ({AnalysisPixelsPerTile}px/tile) in {sw.ElapsedMilliseconds}ms");
    Console.WriteLine();

    Console.WriteLine("Extracting tile colours...");
    sw.Restart();
    float[] tileColors = TileExtractor.Extract(inputImage, cols, rows, AnalysisPixelsPerTile, AnalysisPixelsPerTile);
    Console.WriteLine($"  Done in {sw.ElapsedMilliseconds}ms");
    Console.WriteLine();

    Console.WriteLine("Running GPU colour matching...");
    Console.WriteLine("  (First run compiles the CUDA kernel — may take a few seconds)");
    sw.Restart();
    tileCardIndices = GpuCardMatcher.Match(cards, tileColors, cols * rows);
    Console.WriteLine($"  Done in {sw.ElapsedMilliseconds}ms");
}

Console.WriteLine();

// Composite and write output
Console.WriteLine(deepZoom ? "Compositing and building Deep Zoom..." : "Compositing and saving PNG...");
sw.Restart();

var strips = CardCompositor.CompositeStrips(
    tileCardIndices, cards, cardFolderPath,
    cols, rows, cardWidth, cardHeight, mosaicWidth);

if (deepZoom)
    DeepZoomWriter.Write(outputPath, mosaicWidth, mosaicHeight, strips);
else
    PngWriter.Write(outputPath, mosaicWidth, mosaicHeight, strips);

Console.WriteLine($"  Done in {sw.ElapsedMilliseconds}ms");
Console.WriteLine();
Console.WriteLine($"Total time: {totalTimer.Elapsed.TotalSeconds:F1}s");
