namespace Trader.Core;

public sealed record TradeDecision(TradeAction Action, string? Note = null);

public interface ITradingStrategy
{
    string Name { get; }

    /// <summary>Called once before a backtest or replay starts.</summary>
    void Reset();

    ValueTask<TradeDecision> DecideAsync(MarketSnapshot snapshot, bool hasPosition, CancellationToken cancellationToken);
}

/// <summary>Buys once and never sells.</summary>
public sealed class BuyAndHoldStrategy : ITradingStrategy
{
    public string Name => "Buy & hold";

    public void Reset()
    {
    }

    public ValueTask<TradeDecision> DecideAsync(MarketSnapshot snapshot, bool hasPosition, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new TradeDecision(hasPosition ? TradeAction.Hold : TradeAction.Buy));
}

/// <summary>Seeded random decisions: mostly hold, occasionally buy or sell.</summary>
public sealed class RandomStrategy(int seed, double tradeProbability = 0.1) : ITradingStrategy
{
    private Random _random = new(seed);

    public string Name => $"Random (seed {seed})";

    public void Reset() => _random = new Random(seed);

    public ValueTask<TradeDecision> DecideAsync(MarketSnapshot snapshot, bool hasPosition, CancellationToken cancellationToken)
    {
        var roll = _random.NextDouble();
        var action = roll < tradeProbability / 2 ? TradeAction.Buy
            : roll < tradeProbability ? TradeAction.Sell
            : TradeAction.Hold;
        return ValueTask.FromResult(new TradeDecision(action));
    }
}

/// <summary>Follows the 24 h trend token: in on an up-trend, out on a down-trend.</summary>
public sealed class MomentumStrategy : ITradingStrategy
{
    public string Name => "Momentum (trend token)";

    public void Reset()
    {
    }

    public ValueTask<TradeDecision> DecideAsync(MarketSnapshot snapshot, bool hasPosition, CancellationToken cancellationToken)
    {
        var action = snapshot.Tokens.Contains(MarketToken.TrendUp) ? TradeAction.Buy
            : snapshot.Tokens.Contains(MarketToken.TrendDown) ? TradeAction.Sell
            : TradeAction.Hold;
        return ValueTask.FromResult(new TradeDecision(action));
    }
}
