using MosaicGenerator.Core;
using SixLabors.ImageSharp;

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
    Console.WriteLine("  --background-color <hex>  Background to flatten transparent pixels onto, e.g. FFFFFF (default: black)");
    Console.WriteLine("  --transparency-threshold <pct>  Tiles more transparent than this are left blank in the output (default: 70)");
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
Color background      = Color.Black;
int transparencyThreshold = 70;

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
        case "--background-color": background      = ImageIO.ParseBackgroundColor(args[++i]); break;
        case "--transparency-threshold": transparencyThreshold = int.Parse(args[++i]); break;
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

var options = new MosaicOptions
{
    InputPath             = inputPath,
    CardFolderPath        = cardFolderPath,
    OutputPath            = outputPath,
    CardsPerRow           = cardsPerRow,
    CardWidth             = cardWidth,
    PatchMatch            = patchMatch,
    MatchWidth            = matchWidth,
    MatchCandidates       = matchCandidates,
    LabSsd                = labSsd,
    DeepZoom              = deepZoom,
    Background            = background,
    TransparencyThreshold = transparencyThreshold,
};

// First Ctrl+C asks the engine to stop cleanly (and remove partial output).
// A second Ctrl+C kills the process as usual.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (cts.IsCancellationRequested)
        return;
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("Cancelling... (press Ctrl+C again to force quit)");
};

Console.WriteLine("=== Mosaic Generator ===");
Console.WriteLine();

try
{
    MosaicPipeline.Run(options, new ConsoleProgress(), cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
    Environment.ExitCode = 1;
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Environment.ExitCode = 1;
}

// Prints the engine's log lines. Written synchronously on purpose: Progress<T> would post
// each line to the thread pool, and lines could then appear out of order.
sealed class ConsoleProgress : IProgress<MosaicProgress>
{
    public void Report(MosaicProgress value)
    {
        if (value.Message != null)
            Console.WriteLine(value.Message);
    }
}
