using Trader.Core;

namespace Trader.Tests;

internal static class SyntheticMarket
{
    /// <summary>A deterministic hourly random walk starting 2024-01-01.</summary>
    public static IReadOnlyList<Candle> Create(int count, int seed = 7, double drift = 0.0)
    {
        var random = new Random(seed);
        var candles = new List<Candle>(count);
        var price = 40_000.0;
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < count; i++)
        {
            var open = price;
            price *= Math.Exp(drift + (random.NextDouble() - 0.5) * 0.01);
            var close = price;
            var high = Math.Max(open, close) * 1.001;
            var low = Math.Min(open, close) * 0.999;
            candles.Add(new Candle(start.AddHours(i), (decimal)open, (decimal)high, (decimal)low, (decimal)close, 10m));
        }

        return candles;
    }
}
