using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MosaicGenerator.Core;

public static class DeepZoomWriter
{
    private const int TileSize = 256;
    private static readonly PngEncoder TilePng = new() { CompressionLevel = PngCompressionLevel.Level3 };

    // Share of the reported progress fraction spent writing the full-resolution tiles.
    // The rest is the zoom pyramid, which has about a third as many tiles.
    private const double MaxLevelShare = 0.75;

    /// <summary>
    /// The folder the Deep Zoom output for <paramref name="outputPath"/> is written into.
    /// All output lives inside this one folder, with no loose files alongside it.
    /// </summary>
    public static string GetOutputFolder(string outputPath)
    {
        string dir  = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(outputPath);
        return Path.Combine(dir, stem);
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

        string maxLevelDir = Path.Combine(filesDir, maxLevel.ToString());
        Directory.CreateDirectory(maxLevelDir);

        int maxCols = TileCols(width);
        int maxRows = TileRows(height);
        progress?.Log($"  Writing {maxCols * maxRows} tiles at level {maxLevel}...");
        WriteMaxLevelTiles(maxLevelDir, width, height, strips, progress, ct);

        progress?.Log("  Building zoom pyramid...");
        BuildPyramid(filesDir, maxLevel, width, height, progress, ct);

        WriteDzi(dziPath, width, height);
        WriteHtml(htmlPath, "index_files", width, height);
        progress?.Log($"  Open {outDir}{Path.DirectorySeparatorChar}index.html in a browser to view.");

        return htmlPath;
    }

    // Max-level tile generation
    private static void WriteMaxLevelTiles(
        string levelDir, int width, int height, IEnumerable<Image<Rgba32>> strips,
        ProgressReporter? progress, CancellationToken ct)
    {
        var rowBuf  = new Rgba32[width * TileSize];
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
                        int fullY     = yOffset + y;
                        int rowInTile = fullY % TileSize;

                        accessor.GetRowSpan(y).CopyTo(rowBuf.AsSpan(rowInTile * width));

                        if (rowInTile == TileSize - 1 || fullY == height - 1)
                        {
                            int tileRow  = fullY / TileSize;
                            int rowCount = rowInTile + 1;
                            SaveTileColumns(levelDir, rowBuf, width, tileRow, rowCount, ct);
                        }

                        progress?.Fraction(MaxLevelShare * (fullY + 1) / height);
                    }
                });
                yOffset += strip.Height;
            }
        }
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

    // Pyramid builder
    private static void BuildPyramid(
        string filesDir, int maxLevel, int width, int height,
        ProgressReporter? progress, CancellationToken ct)
    {
        long totalTiles = 0;
        for (int lev = maxLevel - 1; lev >= 0; lev--)
            totalTiles += (long)TileCols(LevelDim(width, maxLevel, lev)) * TileRows(LevelDim(height, maxLevel, lev));
        long doneTiles = 0;

        for (int lev = maxLevel - 1; lev >= 0; lev--)
        {
            int levW = LevelDim(width,  maxLevel, lev);
            int levH = LevelDim(height, maxLevel, lev);
            int srcW = LevelDim(width,  maxLevel, lev + 1);
            int srcH = LevelDim(height, maxLevel, lev + 1);

            int levCols = TileCols(levW);
            int levRows = TileRows(levH);

            string levelDir    = Path.Combine(filesDir, lev.ToString());
            string srcLevelDir = Path.Combine(filesDir, (lev + 1).ToString());
            Directory.CreateDirectory(levelDir);

            progress?.Log($"    Level {lev}: {levW}x{levH} ({levCols * levRows} tiles)");

            Parallel.For(0, levRows * levCols, new ParallelOptions { CancellationToken = ct }, idx =>
            {
                int row = idx / levCols;
                int col = idx % levCols;
                BuildTile(levelDir, srcLevelDir, col, row, levW, levH, srcW, srcH);

                long done = Interlocked.Increment(ref doneTiles);
                progress?.Fraction(MaxLevelShare + (1 - MaxLevelShare) * done / totalTiles);
            });
        }
    }

    private static void BuildTile(
        string levelDir, string srcLevelDir,
        int col, int row, int levW, int levH, int srcW, int srcH)
    {
        int srcX0   = col * TileSize * 2;
        int srcY0   = row * TileSize * 2;
        int srcX1   = Math.Min(srcW, srcX0 + TileSize * 2);
        int srcY1   = Math.Min(srcH, srcY0 + TileSize * 2);
        int stitchW = srcX1 - srcX0;
        int stitchH = srcY1 - srcY0;

        using var stitched = new Image<Rgba32>(stitchW, stitchH);

        int tcStart = srcX0 / TileSize;
        int tcEnd   = (srcX1 - 1) / TileSize;
        int trStart = srcY0 / TileSize;
        int trEnd   = (srcY1 - 1) / TileSize;

        for (int tr = trStart; tr <= trEnd; tr++)
        {
            for (int tc = tcStart; tc <= tcEnd; tc++)
            {
                string srcPath = Path.Combine(srcLevelDir, $"{tc}_{tr}.png");
                if (!File.Exists(srcPath)) continue;

                using var srcTile = Image.Load<Rgba32>(srcPath);

                int tileAbsX = tc * TileSize;
                int tileAbsY = tr * TileSize;

                int copyAbsX0 = Math.Max(srcX0, tileAbsX);
                int copyAbsY0 = Math.Max(srcY0, tileAbsY);
                int copyAbsX1 = Math.Min(srcX1, tileAbsX + srcTile.Width);
                int copyAbsY1 = Math.Min(srcY1, tileAbsY + srcTile.Height);

                if (copyAbsX1 <= copyAbsX0 || copyAbsY1 <= copyAbsY0) continue;

                int destX    = copyAbsX0 - srcX0;
                int destY    = copyAbsY0 - srcY0;
                int srcOfsX  = copyAbsX0 - tileAbsX;
                int srcOfsY  = copyAbsY0 - tileAbsY;
                int copyW    = copyAbsX1 - copyAbsX0;
                int copyH    = copyAbsY1 - copyAbsY0;

                // Explicit captures. ProcessPixelRows is synchronous but lambdas
                // capture by reference, so pin the locals before the call.
                int cDestX = destX, cDestY = destY;
                int cSrcX  = srcOfsX, cSrcY  = srcOfsY;
                int cW = copyW, cH = copyH;

                stitched.ProcessPixelRows(srcTile, (destAcc, srcAcc) =>
                {
                    for (int r = 0; r < cH; r++)
                        srcAcc.GetRowSpan(cSrcY + r)
                              .Slice(cSrcX, cW)
                              .CopyTo(destAcc.GetRowSpan(cDestY + r).Slice(cDestX));
                });
            }
        }

        int outW = Math.Min(TileSize, levW - col * TileSize);
        int outH = Math.Min(TileSize, levH - row * TileSize);
        stitched.Mutate(ctx => ctx.Resize(outW, outH, KnownResamplers.Box));

        stitched.SaveAsPng(Path.Combine(levelDir, $"{col}_{row}.png"), TilePng);
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
