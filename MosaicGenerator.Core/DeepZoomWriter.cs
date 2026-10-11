using System.Collections.Concurrent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace MosaicGenerator.Core;

public static class DeepZoomWriter
{
    // Writing a file has a fixed cost (on Windows about 0.5-1 ms, antivirus scanning included),
    // which dominated with 256-pixel tiles: tens of thousands of files for a big mosaic.
    // 512 gives four times fewer files; the viewer reads the size from index.dzi.
    private const int TileSize = 512;
    private static readonly PngEncoder TilePng = new() { CompressionLevel = PngCompressionLevel.Level3 };

    /// <summary>
    /// The folder the Deep Zoom output for <paramref name="outputPath"/> is written into.
    /// All output lives inside this one folder, with no loose files alongside it.
    /// </summary>
    public static string GetOutputFolder(string outputPath)
    {
        // The output path names the folder itself. Only a ".png" is dropped, for when a PNG
        // path is reused for Deep Zoom; other dots are part of the name ("my.photo_Mosaic").
        string fullPath = Path.GetFullPath(outputPath);
        return string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(fullPath, null)
            : fullPath;
    }

    // Writes the tiles, index.dzi and index.html. Returns the path of index.html.
    // On cancellation the folder is left incomplete; the caller decides whether to delete it.
    public static string Write(
        string outputPath, int width, int height, IEnumerable<Image<Rgba32>> strips,
        ProgressReporter? progress = null, CancellationToken ct = default)
    {
        string outDir   = GetOutputFolder(outputPath);
        string filesDir = Path.Combine(outDir, "index_files");
        string dziPath  = Path.Combine(outDir, "index.dzi");
        string htmlPath = Path.Combine(outDir, "index.html");
        Directory.CreateDirectory(outDir);

        int maxLevel = MaxLevel(width, height);
        progress?.Log($"  Max zoom level: {maxLevel}");
        for (int level = maxLevel; level >= 0; level--)
        {
            int levW = LevelDim(width, maxLevel, level), levH = LevelDim(height, maxLevel, level);
            progress?.Log($"    Level {level}: {levW}x{levH} ({TileCols(levW) * TileRows(levH)} tiles)");
        }

        progress?.Log("  Writing all levels' tiles...");
        WriteLevels(filesDir, maxLevel, width, height, strips, progress, ct);

        WriteDzi(dziPath, width, height);
        WriteHtml(htmlPath, "index_files", width, height);
        progress?.Log($"  Open {outDir}{Path.DirectorySeparatorChar}index.html in a browser to view.");

        return htmlPath;
    }

    // Tile generation
    //
    // Every level is written in one pass over the full-size mosaic: each level takes its rows
    // top to bottom, saves every finished row of tiles in the background, and hands a half-size
    // copy of that row of tiles to the next smaller level. Nothing is read back from disk.

    // Buffers for one row of tiles each, per level: one being filled while the others are saved.
    // Saving a row's last few tiles leaves most threads idle, so with more than one row
    // saving at a time those threads move on to the next row's tiles instead of waiting.
    private const int RowBuffers = 3;

    private static void WriteLevels(
        string filesDir, int maxLevel, int width, int height, IEnumerable<Image<Rgba32>> strips,
        ProgressReporter? progress, CancellationToken ct)
    {
        var saving = new List<Task>();
        var writers = new List<LevelWriter>();

        // Built smallest first, so each level can point at the next smaller one.
        LevelWriter? smaller = null;
        for (int level = 0; level <= maxLevel; level++)
        {
            string dir = Path.Combine(filesDir, level.ToString());
            Directory.CreateDirectory(dir);
            smaller = new LevelWriter(dir, LevelDim(width, maxLevel, level), LevelDim(height, maxLevel, level),
                smaller, saving, ct);
            writers.Add(smaller);
        }
        var full = smaller!;

        try
        {
            int yOffset = 0;
            foreach (var strip in strips)
            {
                using (strip)
                {
                    strip.ProcessPixelRows(accessor =>
                    {
                        for (int y = 0; y < strip.Height; y++)
                        {
                            ct.ThrowIfCancellationRequested();
                            full.AddRow(accessor.GetRowSpan(y));
                            progress?.Fraction((double)(yOffset + y + 1) / height);
                        }
                    });
                    yOffset += strip.Height;
                }
            }

            foreach (var task in saving)
                task.GetAwaiter().GetResult();   // rethrows a failed save's own exception
        }
        catch
        {
            // Let the rows still saving finish first, so nothing is written to the folder
            // after this returns (a cancelled run's folder gets deleted).
            try { Task.WaitAll(saving.ToArray()); } catch { /* the first error is rethrown below */ }
            throw;
        }
        finally
        {
            foreach (var writer in writers)
                writer.Dispose();
        }
    }

    // One zoom level. Takes the level's pixel rows top to bottom; see WriteLevels.
    private sealed class LevelWriter : IDisposable
    {
        private readonly string _dir;
        private readonly int _width, _height;
        private readonly LevelWriter? _smaller;
        private readonly List<Task> _saving;   // shared by all levels, only touched by the caller's thread
        private readonly CancellationToken _ct;
        private readonly BlockingCollection<Rgba32[]> _freeBuffers = new();
        private readonly Rgba32[] _half;       // the half-size copy of a row of tiles

        private Rgba32[] _buffer;
        private int _rowsInBuffer, _tileRow, _rowsDone;

        public LevelWriter(string dir, int width, int height, LevelWriter? smaller, List<Task> saving, CancellationToken ct)
        {
            _dir     = dir;
            _width   = width;
            _height  = height;
            _smaller = smaller;
            _saving  = saving;
            _ct      = ct;

            int bufferRows = Math.Min(TileSize, height);
            for (int i = 0; i < RowBuffers; i++)
                _freeBuffers.Add(new Rgba32[width * bufferRows]);
            _buffer = _freeBuffers.Take();
            _half   = smaller == null ? [] : new Rgba32[smaller._width * ((bufferRows + 1) / 2)];
        }

        public void AddRow(ReadOnlySpan<Rgba32> row)
        {
            row[.._width].CopyTo(_buffer.AsSpan(_rowsInBuffer * _width));
            _rowsInBuffer++;
            _rowsDone++;

            if (_rowsInBuffer == TileSize || _rowsDone == _height)
                FinishTileRow();
        }

        // Saves the finished row of tiles in the background, passes its half-size copy on,
        // and continues with a free buffer (waiting for one if all are saving).
        private void FinishTileRow()
        {
            var full     = _buffer;
            int rowCount = _rowsInBuffer;
            int tileRow  = _tileRow++;

            _saving.Add(Task.Run(() =>
            {
                try     { SaveTileColumns(_dir, full, _width, tileRow, rowCount, _ct); }
                finally { _freeBuffers.Add(full); }
            }));

            if (_smaller != null)
            {
                int halfRows = Halve(full, _width, rowCount, _half, _smaller._width, _ct);
                for (int r = 0; r < halfRows; r++)
                    _smaller.AddRow(_half.AsSpan(r * _smaller._width, _smaller._width));
            }

            _rowsInBuffer = 0;
            _buffer = _freeBuffers.Take(_ct);
            ThrowIfAnyFailed(_saving);
        }

        public void Dispose() => _freeBuffers.Dispose();
    }

    // Shrinks rows x width pixels to half size: every 2x2 block becomes the average of its
    // pixels (fewer at an odd right or bottom edge). Colors are averaged weighted by alpha, so
    // transparent pixels (blank tiles) don't darken the edges of the cards next to them.
    // Returns the number of rows written to target.
    private static int Halve(Rgba32[] source, int width, int rows, Rgba32[] target, int halfWidth, CancellationToken ct)
    {
        int halfRows = (rows + 1) / 2;

        Parallel.For(0, halfRows, new ParallelOptions { CancellationToken = ct }, hy =>
        {
            int y0 = hy * 2, y1 = Math.Min(y0 + 1, rows - 1);
            for (int hx = 0; hx < halfWidth; hx++)
            {
                int x0 = hx * 2, x1 = Math.Min(x0 + 1, width - 1);
                int count = (x1 > x0 ? 2 : 1) * (y1 > y0 ? 2 : 1);

                int a = 0, r = 0, g = 0, b = 0;
                Accumulate(source[y0 * width + x0]);
                if (x1 > x0)            Accumulate(source[y0 * width + x1]);
                if (y1 > y0)            Accumulate(source[y1 * width + x0]);
                if (x1 > x0 && y1 > y0) Accumulate(source[y1 * width + x1]);

                target[hy * halfWidth + hx] = a == 0
                    ? default
                    : new Rgba32(
                        (byte)((r + a / 2) / a), (byte)((g + a / 2) / a), (byte)((b + a / 2) / a),
                        (byte)((a + count / 2) / count));

                void Accumulate(Rgba32 p)
                {
                    a += p.A;
                    r += p.R * p.A;
                    g += p.G * p.A;
                    b += p.B * p.A;
                }
            }
        });

        return halfRows;
    }

    private static void ThrowIfAnyFailed(List<Task> tasks)
    {
        foreach (var task in tasks)
            if (task.IsFaulted || task.IsCanceled)
                task.GetAwaiter().GetResult();
    }

    private static void SaveTileColumns(
        string levelDir, Rgba32[] rowBuf, int width, int tileRow, int rowCount, CancellationToken ct)
    {
        int numCols = TileCols(width);
        Parallel.For(0, numCols, new ParallelOptions { CancellationToken = ct }, col =>
        {
            int tileX = col * TileSize;
            int tileW = Math.Min(TileSize, width - tileX);

            using var tile = new Image<Rgba32>(tileW, rowCount);
            tile.ProcessPixelRows(accessor =>
            {
                for (int r = 0; r < rowCount; r++)
                    rowBuf.AsSpan(r * width + tileX, tileW)
                          .CopyTo(accessor.GetRowSpan(r));
            });

            tile.SaveAsPng(Path.Combine(levelDir, $"{col}_{tileRow}.png"), TilePng);
        });
    }

    // DZI + HTML output
    private static void WriteDzi(string path, int width, int height)
    {
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Image TileSize="{TileSize}" Overlap="0" Format="png"
                   xmlns="http://schemas.microsoft.com/deepzoom/2008">
              <Size Width="{width}" Height="{height}"/>
            </Image>
            """);
    }

    private static void WriteHtml(string path, string tilesDirName, int width, int height)
    {
        File.WriteAllText(path, $$"""
            <!DOCTYPE html>
            <html>
            <head>
              <meta charset="utf-8"/>
              <title>Pokemon Card Mosaic</title>
              <style>
                * { margin: 0; padding: 0; box-sizing: border-box; }
                html, body { width: 100%; height: 100%; background: #111; overflow: hidden; }
                #viewer { width: 100%; height: 100%; }
              </style>
            </head>
            <body>
              <div id="viewer"></div>
              <script src="https://cdn.jsdelivr.net/npm/openseadragon@4.1/build/openseadragon/openseadragon.min.js"></script>
              <script>
                OpenSeadragon({
                  id: "viewer",
                  prefixUrl: "https://cdn.jsdelivr.net/npm/openseadragon@4.1/build/openseadragon/images/",
                  tileSources: {
                    Image: {
                      xmlns:    "http://schemas.microsoft.com/deepzoom/2008",
                      Url:      "{{tilesDirName}}/",
                      Format:   "png",
                      TileSize: "{{TileSize}}",
                      Overlap:  "0",
                      Size: { Width: "{{width}}", Height: "{{height}}" }
                    }
                  },
                  defaultZoomLevel: 0,
                  showNavigator:    true,
                  navigatorPosition: "BOTTOM_RIGHT"
                });
              </script>
            </body>
            </html>
            """);
    }

    // Helpers
    private static int MaxLevel(int width, int height) =>
        (int)Math.Ceiling(Math.Log2(Math.Max(width, height)));

    private static int LevelDim(int fullDim, int maxLevel, int level)
    {
        int shift = maxLevel - level;
        if (shift <= 0) return fullDim;
        long div = 1L << shift;
        return (int)Math.Max(1L, (fullDim + div - 1) / div);
    }

    private static int TileCols(int w) => (int)Math.Ceiling((double)w / TileSize);
    private static int TileRows(int h) => (int)Math.Ceiling((double)h / TileSize);
}
