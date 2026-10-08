using MosaicGenerator.Core.Compute;

namespace MosaicGenerator.Core;

public static class ColorMatcher
{
    // For every tile, finds the index of the closest card by CIEDE2000 colour distance.
    // Returns an int[] of length tileCount where each element is an index into the cards array.
    // The distance calculation itself runs on whatever device the backend represents.
    public static int[] Match(
        IMatchBackend backend,
        CardRecord[] cards, float[] tileColors, int tileCount,
        ProgressReporter? progress = null, CancellationToken ct = default)
    {
        float[] cardColors = FlattenCardColors(cards);
        return backend.MatchColors(cardColors, tileColors, tileCount, fraction => progress?.Fraction(fraction), ct);
    }

    private static float[] FlattenCardColors(CardRecord[] cards)
    {
        var colors = new float[cards.Length * 3];
        for (int i = 0; i < cards.Length; i++)
        {
            colors[i * 3]     = cards[i].L;
            colors[i * 3 + 1] = cards[i].A;
            colors[i * 3 + 2] = cards[i].B;
        }
        return colors;
    }
}
