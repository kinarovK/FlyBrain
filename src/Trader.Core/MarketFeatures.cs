namespace Trader.Core;

public sealed record MarketFeatureSettings
{
    /// <summary>Hours used for the trend return and the volatility estimate.</summary>
    public int TrendWindow { get; init; } = 24;

    /// <summary>Hours used for the median-volatility regime threshold.</summary>
    public int RegimeWindow { get; init; } = 24 * 30;

    /// <summary>|z| above this is a strong move; below <see cref="FlatZ"/> is flat.</summary>
    public double StrongZ { get; init; } = 1.5;

    public double FlatZ { get; init; } = 0.25;

    /// <summary>Trend return (in units of hourly volatility * sqrt(window)) treated as flat.</summary>
    public double TrendFlatZ { get; init; } = 0.5;

    public int WarmupCandles => RegimeWindow + TrendWindow + 1;
}

/// <summary>The market state at the close of candle <see cref="Index"/>.</summary>
public sealed record MarketSnapshot(
    int Index,
    Candle Candle,
    double LastReturn,
    double TrendReturn,
    double Volatility,
    double RegimeVolatility,
    IReadOnlyList<MarketToken> Tokens);

/// <summary>Turns a candle series into discrete market tokens using only past data.</summary>
public sealed class MarketFeatureExtractor(MarketFeatureSettings? settings = null)
{
    public MarketFeatureSettings Settings { get; } = settings ?? new MarketFeatureSettings();

    public IReadOnlyList<MarketSnapshot> Extract(IReadOnlyList<Candle> candles)
    {
        var s = Settings;
        if (candles.Count <= s.WarmupCandles)
        {
            return [];
        }

        var logReturns = new double[candles.Count];
        for (var i = 1; i < candles.Count; i++)
        {
            logReturns[i] = Math.Log((double)candles[i].Close / (double)candles[i - 1].Close);
        }

        var volatility = new double[candles.Count];
        for (var i = s.TrendWindow; i < candles.Count; i++)
        {
            volatility[i] = StandardDeviation(logReturns, i - s.TrendWindow + 1, s.TrendWindow);
        }

        var snapshots = new List<MarketSnapshot>(candles.Count - s.WarmupCandles);
        var window = new double[s.RegimeWindow];
        for (var i = s.WarmupCandles; i < candles.Count; i++)
        {
            var vol = volatility[i];
            Array.Copy(volatility, i - s.RegimeWindow + 1, window, 0, s.RegimeWindow);
            var regime = Median(window);
            var lastReturn = logReturns[i];
            var trend = Math.Log((double)candles[i].Close / (double)candles[i - s.TrendWindow].Close);
            var tokens = new[]
            {
                ReturnToken(lastReturn, vol),
                TrendToken(trend, vol),
                vol > regime ? MarketToken.VolatilityHigh : MarketToken.VolatilityLow,
            };
            snapshots.Add(new MarketSnapshot(i, candles[i], lastReturn, trend, vol, regime, tokens));
        }

        return snapshots;
    }

    internal MarketToken ReturnToken(double logReturn, double volatility)
    {
        if (volatility <= 0)
        {
            return MarketToken.ReturnFlat;
        }

        var z = logReturn / volatility;
        return z switch
        {
            _ when z <= -Settings.StrongZ => MarketToken.ReturnStrongDown,
            _ when z <= -Settings.FlatZ => MarketToken.ReturnDown,
            _ when z < Settings.FlatZ => MarketToken.ReturnFlat,
            _ when z < Settings.StrongZ => MarketToken.ReturnUp,
            _ => MarketToken.ReturnStrongUp,
        };
    }

    internal MarketToken TrendToken(double trendLogReturn, double volatility)
    {
        if (volatility <= 0)
        {
            return MarketToken.TrendFlat;
        }

        var z = trendLogReturn / (volatility * Math.Sqrt(Settings.TrendWindow));
        return z <= -Settings.TrendFlatZ ? MarketToken.TrendDown
            : z >= Settings.TrendFlatZ ? MarketToken.TrendUp
            : MarketToken.TrendFlat;
    }

    private static double StandardDeviation(double[] values, int start, int count)
    {
        double sum = 0, sumSquares = 0;
        for (var i = start; i < start + count; i++)
        {
            sum += values[i];
            sumSquares += values[i] * values[i];
        }

        var mean = sum / count;
        return Math.Sqrt(Math.Max(0, sumSquares / count - mean * mean));
    }

    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }
}
