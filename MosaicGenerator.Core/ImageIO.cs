using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MosaicGenerator.Core;

public static class ImageIO
{
    // Loads an image and flattens any transparency onto a solid background colour
    // before dropping to Rgb24. Without this, transparent pixels (commonly stored
    // as black with zero alpha) would be read as solid black once the alpha
    // channel is discarded, biasing matching towards dark cards.
    public static Image<Rgb24> LoadFlattened(string path, Color background)
    {
        using var src = Image.Load<Rgba32>(path);
        return Flatten(src, background);
    }

    // Same flattening, but keeps an (now fully opaque) alpha channel for card
    // tiles that get composited into an RGBA mosaic output.
    public static Image<Rgba32> LoadFlattenedRgba(string path, Color background)
    {
        var img = Image.Load<Rgba32>(path);
        img.Mutate(ctx => ctx.BackgroundColor(background));
        return img;
    }

    public static Image<Rgb24> Flatten(Image<Rgba32> source, Color background)
    {
        using var flattened = source.Clone(ctx => ctx.BackgroundColor(background));
        return flattened.CloneAs<Rgb24>();
    }

    // Down-samples the source's alpha channel to one value per mosaic tile
    // (a box filter, i.e. each tile's average opacity). Used to decide which
    // tiles are transparent enough to leave blank in the output.
    public static float[] ExtractTileOpacity(Image<Rgba32> source, int cols, int rows)
    {
        using var resized = source.Clone(ctx => ctx.Resize(cols, rows, KnownResamplers.Box));

        var opacity = new float[cols * rows];
        resized.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < rows; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < cols; x++)
                    opacity[y * cols + x] = row[x].A / 255f;
            }
        });
        return opacity;
    }

    public static Color ParseBackgroundColor(string value) =>
        Color.ParseHex(value.StartsWith('#') ? value : "#" + value);
}
