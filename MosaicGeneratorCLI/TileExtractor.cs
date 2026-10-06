using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MosaicGeneratorCLI;

public static class TileExtractor
{
    // Returns a flat float[] of [L, A, B] triplets, one per tile, row-major order.
    public static float[] Extract(Image<Rgb24> image, int cols, int rows, int tileWidth, int tileHeight)
    {
        int imageWidth = image.Width;
        int imageHeight = image.Height;

        // Copy pixels to a flat array once so parallel tile workers can read without locking.
        var pixels = new Rgb24[imageWidth * imageHeight];
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < imageHeight; y++)
                accessor.GetRowSpan(y).CopyTo(pixels.AsSpan(y * imageWidth, imageWidth));
        });

        var tileColors = new float[cols * rows * 3];

        Parallel.For(0, rows, row =>
        {
            for (int col = 0; col < cols; col++)
            {
                (float L, float A, float B) = GetMedianColor(
                    pixels, imageWidth, imageHeight,
                    col * tileWidth, row * tileHeight, tileWidth, tileHeight);

                int idx = (row * cols + col) * 3;
                tileColors[idx]     = L;
                tileColors[idx + 1] = A;
                tileColors[idx + 2] = B;
            }
        });

        return tileColors;
    }

    private static (float L, float A, float B) GetMedianColor(
        Rgb24[] pixels, int imageWidth, int imageHeight,
        int x, int y, int width, int height)
    {
        // Histogram-based median: O(n + 256) instead of O(n log n) sort.
        int[] histR = new int[256];
        int[] histG = new int[256];
        int[] histB = new int[256];
        int pixelCount = 0;

        int yEnd = Math.Min(y + height, imageHeight);
        int xEnd = Math.Min(x + width, imageWidth);

        for (int py = y; py < yEnd; py++)
        {
            int rowBase = py * imageWidth;
            for (int px = x; px < xEnd; px++)
            {
                ref Rgb24 p = ref pixels[rowBase + px];
                histR[p.R]++;
                histG[p.G]++;
                histB[p.B]++;
                pixelCount++;
            }
        }

        if (pixelCount == 0)
            return (0f, 0f, 0f);

        byte medR = FindMedian(histR, pixelCount);
        byte medG = FindMedian(histG, pixelCount);
        byte medB = FindMedian(histB, pixelCount);

        return ColorMath.RgbToLab(medR, medG, medB);
    }

    private static byte FindMedian(int[] histogram, int totalPixels)
    {
        int target = (totalPixels + 1) / 2;
        int cumulative = 0;
        for (int i = 0; i < 256; i++)
        {
            cumulative += histogram[i];
            if (cumulative >= target)
                return (byte)i;
        }
        return 255;
    }
}
