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

    /// <summary>The color data's file name inside a card folder's data folder (see <see cref="AppData"/>).</summary>
    private const string ColorDataFileName = "cardColors.json";

    /// <summary>Where earlier versions kept the color data: in the card folder itself.</summary>
    private const string LegacyFileName = "allCardLabData.json";

    /// <summary>Where the color data for <paramref name="cardFolderPath"/> is kept.</summary>
    public static string ColorDataPath(string cardFolderPath) =>
        Path.Combine(AppData.CardFolderData(cardFolderPath), ColorDataFileName);

    /// <summary>
    /// Whether <paramref name="cardFolderPath"/> has been used before: it has color data, or an
    /// old <c>allCardLabData.json</c> that will be imported. If not, every card gets analysed once.
    /// </summary>
    public static bool HasColorData(string cardFolderPath) =>
        File.Exists(ColorDataPath(cardFolderPath)) ||
        File.Exists(Path.Combine(cardFolderPath, LegacyFileName));

    /// <summary>
    /// Loads the average color of every card in <paramref name="cardFolderPath"/>, from the
    /// color data kept in the app's data folder (<see cref="ColorDataPath"/>).
    /// <list type="bullet">
    ///   <item>Cards without data yet are analysed, and their data is added and saved.</item>
    ///   <item>If the card folder still has an <c>allCardLabData.json</c> from an earlier
    ///         version, its data is imported once, and the file is deleted from the card
    ///         folder after the data has been saved in the app's data folder.</item>
    /// </list>
    /// If cancelled while analysing new cards, the cards finished so far are still saved,
    /// so the next run doesn't have to redo them.
    /// </summary>
    public static CardRecord[] LoadOrUpdate(
        string cardFolderPath, Color background,
        ProgressReporter? progress = null, CancellationToken ct = default)
    {
        string dataPath   = ColorDataPath(cardFolderPath);
        string legacyPath = Path.Combine(cardFolderPath, LegacyFileName);

        // Load whatever is already known
        var existing = new Dictionary<string, RawCard>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(dataPath))
        {
            foreach (var card in ReadColorData(dataPath))
                existing[card.FileName] = card;
            progress?.Log($"  Loaded {existing.Count} existing entries.");
        }

        // Earlier versions kept the color data in the card folder. Import it once, so the cards
        // don't have to be analysed again; the file is deleted there once the data is saved here.
        bool importedLegacy = false;
        if (File.Exists(legacyPath))
        {
            try
            {
                int imported = 0;
                foreach (var card in ReadColorData(legacyPath))
                    if (existing.TryAdd(card.FileName, card))
                        imported++;
                importedLegacy = true;
                progress?.Log($"  Imported {imported} entries from {LegacyFileName} in the card folder.");
            }
            catch (Exception ex)
            {
                // Left in place: it isn't ours to delete if it isn't what we expect.
                progress?.Log($"  [WARN] Couldn't read {LegacyFileName} in the card folder, so it was left there: {ex.Message}");
            }
        }

        if (existing.Count == 0)
            progress?.Log("  No card data yet — every card will be analysed once.");

        // Discover image files on disk
        string[] diskFiles = FindCardImages(cardFolderPath);

        // Work out which cards are new (no data yet)
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

                SaveColorData(cardFolderPath, dataPath, existing.Values);
                progress?.Log($"  Card data saved — {existing.Count} total entries.");
            }
        }
        else if (importedLegacy)
        {
            SaveColorData(cardFolderPath, dataPath, existing.Values);
            progress?.Log($"  Card data saved — {existing.Count} total entries.");
        }
        else
        {
            progress?.Log("  Card database is up to date.");
        }

        if (importedLegacy)
        {
            try
            {
                File.Delete(legacyPath);
                progress?.Log($"  Removed {LegacyFileName} from the card folder (its data is now kept by the app).");
            }
            catch (Exception ex)
            {
                progress?.Log($"  [WARN] Couldn't remove {LegacyFileName} from the card folder: {ex.Message}");
            }
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

    private static RawCard[] ReadColorData(string path) =>
        JsonSerializer.Deserialize<RawCard[]>(
            File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

    private static void SaveColorData(string cardFolderPath, string dataPath, IEnumerable<RawCard> cards)
    {
        AppData.EnsureCardFolderData(cardFolderPath);
        AppData.WriteAllTextSafely(dataPath, JsonSerializer.Serialize(cards.ToArray()));
    }

    // JSON shape (the same format earlier versions wrote to allCardLabData.json)
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
