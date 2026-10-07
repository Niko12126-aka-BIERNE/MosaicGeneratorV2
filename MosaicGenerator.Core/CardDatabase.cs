using System.Collections.Concurrent;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MosaicGenerator.Core;

public readonly record struct CardRecord(string FileName, float L, float A, float B);

public static class CardDatabase
{
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

    /// <summary>
    /// Loads card average-colour data from <paramref name="jsonPath"/>.
    /// <list type="bullet">
    ///   <item>If the JSON doesn't exist it is created from scratch.</item>
    ///   <item>If it exists but is missing entries for cards found in
    ///         <paramref name="cardFolderPath"/>, only those new cards are
    ///         processed and the JSON is updated in place.</item>
    ///   <item>If it is already complete, the data is returned as-is.</item>
    /// </list>
    /// If cancelled while processing new cards, the cards finished so far are still
    /// saved to the JSON, so the next run doesn't have to redo them.
    /// </summary>
    public static CardRecord[] LoadOrUpdate(
        string jsonPath, string cardFolderPath, Color background,
        ProgressReporter? progress = null, CancellationToken ct = default)
    {
        // Load whatever is already cached
        var existing = new Dictionary<string, RawCard>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(jsonPath))
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var raw = JsonSerializer.Deserialize<RawCard[]>(
                          File.ReadAllText(jsonPath), opts) ?? [];
            foreach (var card in raw)
                existing[card.FileName] = card;
            progress?.Log($"  Loaded {existing.Count} existing entries from JSON.");
        }
        else
        {
            progress?.Log("  No JSON found — will build it from scratch.");
        }

        // Discover image files on disk
        string[] diskFiles = FindCardImages(cardFolderPath);

        // Work out which cards are new (not yet in the JSON)
        string[] toProcess = diskFiles
            .Where(f => !existing.ContainsKey(Path.GetFileName(f)))
            .ToArray();

        if (toProcess.Length > 0)
        {
            progress?.Log($"  {toProcess.Length} new card(s) to process...");

            var newEntries = new ConcurrentDictionary<string, RawCard>(StringComparer.OrdinalIgnoreCase);
            int done = 0;

            try
            {
                Parallel.ForEach(
                    toProcess,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
                    file =>
                    {
                        string name = Path.GetFileName(file);
                        try
                        {
                            var (l, a, b) = ComputeAverageLab(file, background);
                            newEntries[name] = new RawCard
                            {
                                FileName = name,
                                AverageColor = new RawLabColor { L = l, A = a, B = b }
                            };
                        }
                        catch (Exception ex)
                        {
                            progress?.Log($"  [WARN] Skipping {name}: {ex.Message}");
                        }

                        int n = Interlocked.Increment(ref done);
                        progress?.Fraction(n, toProcess.Length);
                        if (n % 500 == 0 || n == toProcess.Length)
                            progress?.Log($"    Processed {n}/{toProcess.Length}...");
                    });
            }
            finally
            {
                // Runs on cancellation too, so finished cards aren't processed again next time.
                foreach (var (name, card) in newEntries)
                    existing[name] = card;

                // Ensure the target directory exists before writing
                string? dir = Path.GetDirectoryName(jsonPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var serOpts = new JsonSerializerOptions { WriteIndented = false };
                File.WriteAllText(jsonPath,
                    JsonSerializer.Serialize(existing.Values.ToArray(), serOpts));

                progress?.Log($"  JSON saved — {existing.Count} total entries.");
            }
        }
        else
        {
            progress?.Log("  Card database is up to date.");
        }

        // Return only entries whose image file is present on disk
        var onDisk = new HashSet<string>(
            diskFiles.Select(f => Path.GetFileName(f)!),
            StringComparer.OrdinalIgnoreCase);

        return existing
            .Where(kvp => onDisk.Contains(kvp.Key))
            .Select(kvp => new CardRecord(
                kvp.Key,
                (float)kvp.Value.AverageColor.L,
                (float)kvp.Value.AverageColor.A,
                (float)kvp.Value.AverageColor.B))
            .ToArray();
    }

    /// <summary>Paths of all supported image files directly inside <paramref name="cardFolderPath"/>.</summary>
    public static string[] FindCardImages(string cardFolderPath)
    {
        if (!Directory.Exists(cardFolderPath))
            throw new DirectoryNotFoundException($"Card folder not found: {cardFolderPath}");

        return Directory
            .GetFiles(cardFolderPath)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f)))
            .ToArray();
    }

    // Aspect ratio sampling
    public static float SampleAspectRatio(string cardFolderPath, int sampleCount = 20)
    {
        string[] files = FindCardImages(cardFolderPath);

        if (files.Length == 0)
            throw new InvalidOperationException("No image files found in card folder.");

        // Pick evenly-spaced indices so the sample covers the whole folder
        int step = Math.Max(1, files.Length / sampleCount);
        var indices = Enumerable.Range(0, sampleCount)
            .Select(i => i * step)
            .Where(i => i < files.Length);

        double totalRatio = 0;
        int count = 0;

        foreach (int i in indices)
        {
            try
            {
                var info = Image.Identify(files[i]);
                totalRatio += (double)info.Height / info.Width;
                count++;
            }
            catch { /* skip unreadable files */ }
        }

        if (count == 0)
            throw new InvalidOperationException(
                "Could not read any card images to determine aspect ratio.");

        return (float)(totalRatio / count);
    }

    // Image processing
    private static (float L, float A, float B) ComputeAverageLab(string imagePath, Color background)
    {
        using var image = ImageIO.LoadFlattened(imagePath, background);

        long sumR = 0, sumG = 0, sumB = 0;
        int totalPixels = image.Width * image.Height;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    sumR += row[x].R;
                    sumG += row[x].G;
                    sumB += row[x].B;
                }
            }
        });

        byte avgR = (byte)(sumR / totalPixels);
        byte avgG = (byte)(sumG / totalPixels);
        byte avgB = (byte)(sumB / totalPixels);

        return ColorMath.RgbToLab(avgR, avgG, avgB);
    }

    // JSON shape (mirrors the existing file format exactly)
    private class RawCard
    {
        public string FileName { get; set; } = "";
        public RawLabColor AverageColor { get; set; } = new();
    }

    private class RawLabColor
    {
        public double L { get; set; }
        public double A { get; set; }
        public double B { get; set; }
    }
}
