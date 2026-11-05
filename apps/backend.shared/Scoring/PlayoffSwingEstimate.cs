namespace backend.Scoring;

public static class PlayoffSwingEstimate
{
    public const double GameSpreadInWins = .55;
    public const double CutLineSpreadInWins = .7;

    public static double Swing(double probability, int remainingIncludingThisGame)
    {
        if (!ProHypePolicy.Probability(probability) || remainingIncludingThisGame < 1)
            throw new ArgumentOutOfRangeException(nameof(probability));
        if (probability is 0 or 1) return 0;
        var z = WichuraAs241InverseStandardNormal(probability);
        var spread = Math.Sqrt(GameSpreadInWins * GameSpreadInWins * (remainingIncludingThisGame - 1) + CutLineSpreadInWins * CutLineSpreadInWins);
        return Math.Exp(-z * z / 2) / Math.Sqrt(2 * Math.PI) / spread;
    }

    public static double WichuraAs241InverseStandardNormal(double p)
    {
        if (!(p > 0 && p < 1)) throw new ArgumentOutOfRangeException(nameof(p));
        var q = p - .5;
        if (Math.Abs(q) <= .425)
        {
            var r = .180625 - q * q;
            return q * Horner(r, CentralNumerator) / Horner(r, CentralDenominator);
        }
        var t = Math.Sqrt(-Math.Log(q <= 0 ? p : 1 - p));
        var x = t <= 5
            ? Horner(t - 1.6, NearNumerator) / Horner(t - 1.6, NearDenominator)
            : Horner(t - 5, FarNumerator) / Horner(t - 5, FarDenominator);
        return q < 0 ? -x : x;
    }

    private static double Horner(double x, double[] coefficientsHighestPowerFirst) => coefficientsHighestPowerFirst.Aggregate(0.0, (sum, c) => sum * x + c);

    private static readonly double[] CentralNumerator = [2.5090809287301226727e+3, 3.3430575583588128105e+4, 6.7265770927008700853e+4,
        4.5921953931549871457e+4, 1.3731693765509461125e+4, 1.9715909503065514427e+3, 1.3314166789178437745e+2, 3.3871328727963666080e+0];
    private static readonly double[] CentralDenominator = [5.2264952788528545610e+3, 2.8729085735721942674e+4, 3.9307895800092710610e+4,
        2.1213794301586595867e+4, 5.3941960214247511077e+3, 6.8718700749205790830e+2, 4.2313330701600911252e+1, 1.0];
    private static readonly double[] NearNumerator = [7.7454501427834140764e-4, 2.2723844989269184583e-2, 2.4178072517745061177e-1,
        1.2704582524523683826e+0, 3.6478483247632045050e+0, 5.7694972214606914055e+0, 4.6303378461565452959e+0, 1.4234371107496835773e+0];
    private static readonly double[] NearDenominator = [1.0507500716444168432e-9, 5.4759380849953449460e-4, 1.5198666563616457197e-2,
        1.4810397642748007459e-1, 6.8976733498510000455e-1, 1.6763848301838038494e+0, 2.0531916266377588219e+0, 1.0];
    private static readonly double[] FarNumerator = [2.0103343992922881326e-7, 2.7115555687434875782e-5, 1.2426609473880784386e-3,
        2.6532189526576123093e-2, 2.9656057182850489123e-1, 1.7848265399172913358e+0, 5.4637849111641143699e+0, 6.6579046435011037772e+0];
    private static readonly double[] FarDenominator = [2.0442631033899397856e-15, 1.4215117583164458887e-7, 1.8463183175100546818e-5,
        7.8686913114561325910e-4, 1.4875361290850614853e-2, 1.3692988092273580531e-1, 5.9983220655588793769e-1, 1.0];
}
