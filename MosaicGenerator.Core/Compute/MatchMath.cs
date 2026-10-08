namespace MosaicGenerator.Core.Compute;

/// <summary>
/// Maths shared by every compute backend. Only plain float arithmetic, so ILGPU can compile
/// it into GPU kernels and the CPU backend can call it directly: both use the same formula.
/// </summary>
public static class MatchMath
{
    // CIEDE2000 implemented with float arithmetic, so it also runs inside GPU kernels.
    public static float CieDe2000(float l1, float a1, float b1, float l2, float a2, float b2)
    {
        float avgL = (l1 + l2) * 0.5f;

        float C1 = MathF.Sqrt(a1 * a1 + b1 * b1);
        float C2 = MathF.Sqrt(a2 * a2 + b2 * b2);
        float avgC = (C1 + C2) * 0.5f;

        // avgC^7 without Math.Pow
        float avgC2 = avgC * avgC;
        float avgC4 = avgC2 * avgC2;
        float avgC7 = avgC4 * avgC2 * avgC;
        const float Pow25_7 = 6103515625f; // 25^7

        float G   = 0.5f * (1f - MathF.Sqrt(avgC7 / (avgC7 + Pow25_7)));
        float a1p = (1f + G) * a1;
        float a2p = (1f + G) * a2;

        float C1p   = MathF.Sqrt(a1p * a1p + b1 * b1);
        float C2p   = MathF.Sqrt(a2p * a2p + b2 * b2);
        float avgCp = (C1p + C2p) * 0.5f;

        float h1p = MathF.Atan2(b1, a1p);
        float h2p = MathF.Atan2(b2, a2p);
        if (h1p < 0f) h1p += 2f * MathF.PI;
        if (h2p < 0f) h2p += 2f * MathF.PI;

        float absDiff = MathF.Abs(h1p - h2p);

        float deltahp;
        if (absDiff <= MathF.PI)
            deltahp = h2p - h1p;
        else if (h2p <= h1p)
            deltahp = h2p - h1p + 2f * MathF.PI;
        else
            deltahp = h2p - h1p - 2f * MathF.PI;

        float dLp = l2 - l1;
        float dCp = C2p - C1p;
        float dHp = 2f * MathF.Sqrt(C1p * C2p) * MathF.Sin(deltahp * 0.5f);

        float avgHp = absDiff > MathF.PI
            ? (h1p + h2p + 2f * MathF.PI) * 0.5f
            : (h1p + h2p) * 0.5f;

        float T = 1f
            - 0.17f * MathF.Cos(avgHp - MathF.PI / 6f)
            + 0.24f * MathF.Cos(2f * avgHp)
            + 0.32f * MathF.Cos(3f * avgHp + MathF.PI / 30f)
            - 0.20f * MathF.Cos(4f * avgHp - 63f * MathF.PI / 180f);

        float avgHpDeg  = avgHp * (180f / MathF.PI);
        float dThetaArg = (avgHpDeg - 275f) / 25f;
        float dTheta    = 30f * MathF.Exp(-(dThetaArg * dThetaArg));

        float avgCp2 = avgCp * avgCp;
        float avgCp4 = avgCp2 * avgCp2;
        float avgCp7 = avgCp4 * avgCp2 * avgCp;
        float RC = 2f * MathF.Sqrt(avgCp7 / (avgCp7 + Pow25_7));
        float RT = -RC * MathF.Sin(2f * dTheta * (MathF.PI / 180f));

        float lDiff = avgL - 50f;
        float SL    = 1f + 0.015f * lDiff * lDiff / MathF.Sqrt(20f + lDiff * lDiff);
        float SC    = 1f + 0.045f * avgCp * 4f;   // saturationWeight = 4
        float SH    = 1f + 0.015f * avgCp * T;

        float lTerm = dLp / SL;
        float cTerm = dCp / SC;
        float hTerm = dHp / SH;

        return MathF.Sqrt(lTerm * lTerm + cTerm * cTerm + hTerm * hTerm + RT * cTerm * hTerm);
    }
}
