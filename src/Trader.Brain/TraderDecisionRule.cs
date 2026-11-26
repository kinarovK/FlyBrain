using Trader.Core;

namespace Trader.Brain;

public sealed record PoolResponse(double BuyScore, double SellScore, double Scale);

/// <summary>Turns the two pool scores into BUY / SELL / HOLD.</summary>
public static class TraderDecisionRule
{
    public static TradeAction Decide(PoolResponse response, double deadZoneFraction)
    {
        if (!(response.Scale > 1e-12))
        {
            return TradeAction.Hold; // silent readout
        }

        var difference = response.BuyScore - response.SellScore;
        var threshold = deadZoneFraction * response.Scale;
        return difference > threshold ? TradeAction.Buy
            : difference < -threshold ? TradeAction.Sell
            : TradeAction.Hold;
    }

    /// <summary>
    /// Pool score = mean over the pool of (membrane delta + spikeWeight * spike delta).
    /// Scale = mean absolute per-neuron score over both pools.
    /// </summary>
    public static PoolResponse Score(
        IReadOnlyList<long> buyPool,
        IReadOnlyList<long> sellPool,
        IReadOnlyDictionary<long, (double MembraneDelta, int SpikeDelta)> deltas,
        double spikeWeight)
    {
        double Value(long root) => deltas.TryGetValue(root, out var d) ? d.MembraneDelta + spikeWeight * d.SpikeDelta : 0;

        var buy = buyPool.Count == 0 ? 0 : buyPool.Average(Value);
        var sell = sellPool.Count == 0 ? 0 : sellPool.Average(Value);
        var all = buyPool.Concat(sellPool).ToArray();
        var scale = all.Length == 0 ? 0 : all.Average(root => Math.Abs(Value(root)));
        return new PoolResponse(buy, sell, scale);
    }
}
