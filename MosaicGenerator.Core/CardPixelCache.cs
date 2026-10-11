using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MosaicGenerator.Core;

/// <summary>
/// Keeps the cards scaled down to the patch-matching resolution in the app's data folder (see
/// <see cref="AppData"/>), so later runs with the same settings don't have to open and scale
/// every card image again. That's most of patch matching's time with big card folders.
/// <para>
/// One cache file per card folder, resolution and background color (the background fills
/// transparent card areas). Each card is stored as 3 bytes (R, G, B) per pixel, with its file
/// size and last-modified time, so cards that were added or changed since are loaded again.
/// </para>
/// <para>
/// At most <see cref="MaxCaches"/> cache files exist across all card folders. When a cache has
/// been saved, the least recently used ones beyond that are deleted. A cancelled run saves and
/// deletes nothing.
/// </para>
/// </summary>
public static class CardPixelCache
{
    private const int MaxCaches = 3;

    // Bump when the file layout, or the way cards are scaled, changes: older caches are then
    // ignored and replaced.
    private const int FormatVersion = 1;
    private static ReadOnlySpan<byte> Magic => "MGPX"u8;

    /// <summary>
    /// Each card's pixels at <paramref name="width"/> × <paramref name="height"/>, flattened onto
    /// <paramref name="background"/>: 3 bytes (R, G, B) per pixel, cards in the order of
    /// <paramref name="cards"/>. Reports this step's progress (0 to 1) to <paramref name="fraction"/>.
    /// </summary>
    public static byte[] Load(
        CardRecord[] cards, string cardFolderPath, int width, int height, Color background,
        ProgressReporter? progress, Action<double>? fraction, CancellationToken ct)
    {
        int  cardBytes = width * height * 3;
        var  pixels    = new byte[(long)cards.Length * cardBytes];
        var  stamps    = cards.Select(c => Stamp(Path.Combine(cardFolderPath, c.FileName))).ToArray();
        string path    = Path.Combine(AppData.CardFolderData(cardFolderPath), FileName(width, height, background));

        // Cards whose cached pixels are still valid
        bool[] cached     = ReadCache(path, cards, stamps, width, height, pixels, progress);
        int    fromCache  = cached.Count(c => c);
        var    toLoad     = Enumerable.Range(0, cards.Length).Where(i => !cached[i]).ToArray();

        if (fromCache > 0)
            progress?.Log($"    {fromCache}/{cards.Length} cards from the cache");
        if (toLoad.Length > 0)
            progress?.Log($"    {toLoad.Length} card image(s) to load and scale...");

        int done = fromCache;
        fraction?.Invoke((double)done / cards.Length);

        Parallel.ForEach(toLoad,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
            i =>
            {
                LoadCard(Path.Combine(cardFolderPath, cards[i].FileName), width, height, background,
                    pixels.AsSpan(i * cardBytes, cardBytes));

                int n = Interlocked.Increment(ref done);
                fraction?.Invoke((double)n / cards.Length);
                if ((n - fromCache) % 1000 == 0 || n == cards.Length)
                    progress?.Log($"    Loaded {n - fromCache}/{toLoad.Length} card images...");
            });

        // Only reached when every card loaded, so a cancelled run never saves a partial cache.
        // The cache is rewritten when cards were loaded, or when it has cards that are gone.
        if (toLoad.Length > 0 || !File.Exists(path) || CachedCardCount(path) != cards.Length)
        {
            if (TrySave(path, cardFolderPath, cards, stamps, width, height, background, pixels, progress))
                RemoveOldCaches(path, progress);
        }
        else
        {
            MarkUsed(path);
        }

        return pixels;
    }

    // Opens one card, flattens and scales it, and copies its pixels into target.
    private static void LoadCard(string path, int width, int height, Color background, Span<byte> target)
    {
        using var img = ImageIO.LoadFlattened(path, background);
        img.Mutate(ctx => ctx.Resize(width, height));
        img.CopyPixelDataTo(target);   // Rgb24: 3 bytes (R, G, B) per pixel, row by row
    }

    private readonly record struct FileStamp(long Size, long ModifiedTicks);

    private static FileStamp Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks) : default;
    }

    private static string FileName(int width, int height, Color background) =>
        $"pixels_{width}x{height}_{background.ToHex()}.bin";

    // File layout:
    //   header: magic "MGPX", format version, width, height, background (RGBA hex), card count
    //   index : per card, its file name, size and last-modified time
    //   pixels: per card, in index order, width * height * 3 bytes

    // Copies every still-valid card from the cache at path into pixels, and returns which cards
    // it found. A missing or unreadable cache counts as empty.
    private static bool[] ReadCache(
        string path, CardRecord[] cards, FileStamp[] stamps, int width, int height, byte[] pixels,
        ProgressReporter? progress)
    {
        var found = new bool[cards.Length];
        if (!File.Exists(path))
            return found;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            var index = ReadIndex(reader, width, height);
            if (index == null)
                return found;

            long dataStart = stream.Position;
            int  cardBytes = width * height * 3;

            var positions = new Dictionary<string, (int Position, FileStamp Stamp)>(StringComparer.OrdinalIgnoreCase);
            for (int k = 0; k < index.Length; k++)
                positions[index[k].Name] = (k, index[k].Stamp);

            for (int i = 0; i < cards.Length; i++)
            {
                if (!positions.TryGetValue(cards[i].FileName, out var entry) || entry.Stamp != stamps[i])
                    continue;

                stream.Position = dataStart + (long)entry.Position * cardBytes;
                stream.ReadExactly(pixels, i * cardBytes, cardBytes);
                found[i] = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or FormatException)
        {
            progress?.Log($"    [WARN] Couldn't read the card cache, so all cards will be loaded: {ex.Message}");
            Array.Clear(found);
        }

        return found;
    }

    // The index after a header that matches this format and size, or null if it doesn't.
    private static (string Name, FileStamp Stamp)[]? ReadIndex(BinaryReader reader, int width, int height)
    {
        Span<byte> magic = stackalloc byte[4];
        if (reader.Read(magic) != 4 || !magic.SequenceEqual(Magic))
            return null;
        if (reader.ReadInt32() != FormatVersion || reader.ReadInt32() != width || reader.ReadInt32() != height)
            return null;

        reader.ReadString();   // background; already part of the file name
        int count = reader.ReadInt32();
        if (count is < 0 or > 10_000_000)
            return null;

        var index = new (string, FileStamp)[count];
        for (int k = 0; k < index.Length; k++)
            index[k] = (reader.ReadString(), new FileStamp(reader.ReadInt64(), reader.ReadInt64()));
        return index;
    }

    private static int CachedCardCount(string path)
    {
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
            reader.ReadBytes(4 + 3 * sizeof(int));
            reader.ReadString();
            return reader.ReadInt32();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return -1;
        }
    }

    // Writes the cache to a temporary file first and then swaps it in, so it's never half
    // written. Not being able to save only means the next run loads the cards again.
    private static bool TrySave(
        string path, string cardFolderPath, CardRecord[] cards, FileStamp[] stamps,
        int width, int height, Color background, byte[] pixels, ProgressReporter? progress)
    {
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            AppData.EnsureCardFolderData(cardFolderPath);

            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(width);
                writer.Write(height);
                writer.Write(background.ToHex());
                writer.Write(cards.Length);
                for (int i = 0; i < cards.Length; i++)
                {
                    writer.Write(cards[i].FileName);
                    writer.Write(stamps[i].Size);
                    writer.Write(stamps[i].ModifiedTicks);
                }
                writer.Write(pixels);
            }

            File.Move(temp, path, overwrite: true);
            progress?.Log($"    Saved the card cache ({pixels.LongLength / 1024 / 1024} MB) for the next run.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            progress?.Log($"    [WARN] Couldn't save the card cache: {ex.Message}");
            return false;
        }
        finally
        {
            File.Delete(temp);   // only still there if writing or moving failed
        }
    }

    // The cache that was just used becomes the most recently used one.
    private static void MarkUsed(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Deletes the least recently used caches, across all card folders, beyond MaxCaches.
    // Never the one just saved.
    private static void RemoveOldCaches(string justSaved, ProgressReporter? progress)
    {
        string cardsRoot = Path.Combine(AppData.Root, "cards");
        if (!Directory.Exists(cardsRoot))
            return;

        var old = Directory.EnumerateFiles(cardsRoot, "pixels_*.bin", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Where(f => !string.Equals(f.FullName, Path.GetFullPath(justSaved), StringComparison.OrdinalIgnoreCase))
            .Skip(MaxCaches - 1);

        foreach (var file in old)
        {
            try
            {
                long megabytes = file.Length / 1024 / 1024;
                file.Delete();
                progress?.Log($"    Removed an older card cache ({megabytes} MB) to keep at most {MaxCaches}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
