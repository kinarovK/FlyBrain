using Trader.Core;

namespace Trader.Brain;

/// <summary>The fly: market tokens in, pool readout out. The connectome is fixed; nothing is learned.</summary>
public sealed class BrainTradingStrategy(ITraderBrain session) : ITradingStrategy
{
    public string Name => "Fly brain";

    public event EventHandler<BrainMarketEvaluation>? Evaluated;

    public void Reset()
    {
    }

    public async ValueTask<TradeDecision> DecideAsync(MarketSnapshot snapshot, bool hasPosition, CancellationToken cancellationToken)
    {
        var evaluation = await session.EvaluateAsync(snapshot.Tokens, cancellationToken);
        Evaluated?.Invoke(this, evaluation);
        return new TradeDecision(
            evaluation.Action,
            $"buy {evaluation.Response.BuyScore:F4} / sell {evaluation.Response.SellScore:F4}{(evaluation.FromCache ? " (cached)" : string.Empty)}");
    }
}
