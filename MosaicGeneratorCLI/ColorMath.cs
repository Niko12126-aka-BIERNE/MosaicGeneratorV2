namespace MosaicGeneratorCLI;

public static class ColorMath
{
    public static (float L, float A, float B) RgbToLab(byte r, byte g, byte b)
    {
        float rLin = PivotRgb(r / 255f);
        float gLin = PivotRgb(g / 255f);
        float bLin = PivotRgb(b / 255f);

        // sRGB D65 matrix
        float x = (rLin * 0.4124f + gLin * 0.3576f + bLin * 0.1805f) * 100f;
        float y = (rLin * 0.2126f + gLin * 0.7152f + bLin * 0.0722f) * 100f;
        float z = (rLin * 0.0193f + gLin * 0.1192f + bLin * 0.9505f) * 100f;

        // D65 reference white
        float fx = PivotLab(x / 95.047f);
        float fy = PivotLab(y / 100.000f);
        float fz = PivotLab(z / 108.883f);

        return (116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
    }

    private static float PivotRgb(float n) =>
        n > 0.04045f ? MathF.Pow((n + 0.055f) / 1.055f, 2.4f) : n / 12.92f;

    private static float PivotLab(float n) =>
        n > 0.008856f ? MathF.Cbrt(n) : 7.787f * n + 16f / 116f;
}
