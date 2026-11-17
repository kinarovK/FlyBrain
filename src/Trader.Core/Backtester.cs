namespace Trader.Core;

public sealed record BacktestSettings
{
    public decimal InitialCash { get; init; } = 10_000m;

    public decimal FeeRate { get; init; } = 0.001m;
}

public sealed record EquityPoint(DateTimeOffset Time, decimal Close, decimal Equity);

public sealed record BacktestStep(
    int Step,
    int TotalSteps,
    MarketSnapshot Snapshot,
    TradeDecision Decision,
    bool ExecutedPrevious,
    decimal Equity,
    bool HasPosition);

public sealed record BacktestResult(
    string StrategyName,
    IReadOnlyList<EquityPoint> Equity,
    decimal InitialCash,
    decimal FinalEquity,
    double ReturnPercent,
    double MaxDrawdownPercent,
    int TradeCount,
    decimal FeesPaid,
    int BuySignals,
    int SellSignals,
    int HoldSignals,
    bool Completed);

/// <summary>
/// Replays snapshots in time order. A decision taken at the close of candle i is
/// executed at the open of candle i + 1, so no strategy can trade on future prices.
/// </summary>
public sealed class Backtester
{
    public async Task<BacktestResult> RunAsync(
        IReadOnlyList<Candle> candles,
        IReadOnlyList<MarketSnapshot> snapshots,
        ITradingStrategy strategy,
        BacktestSettings? settings = null,
        IProgress<BacktestStep>? progress = null,
        Func<BacktestStep, CancellationToken, Task>? onStep = null,
        CancellationToken cancellationToken = default)
    {
        settings ??= new BacktestSettings();
        var portfolio = new PaperPortfolio(settings.InitialCash, settings.FeeRate);
        var equity = new List<EquityPoint>(snapshots.Count);
        int buys = 0, sells = 0, holds = 0;
        TradeAction? pending = null;
        var completed = true;
        strategy.Reset();

        try
        {
            for (var step = 0; step < snapshots.Count; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = snapshots[step];
                var candle = candles[snapshot.Index];
                var executed = pending is { } action && portfolio.Apply(action, candle.Open);
                var value = portfolio.Equity(candle.Close);
                equity.Add(new EquityPoint(candle.OpenTime, candle.Close, value));

                var decision = await strategy.DecideAsync(snapshot, portfolio.HasPosition, cancellationToken);
                switch (decision.Action)
                {
                    case TradeAction.Buy: buys++; break;
                    case TradeAction.Sell: sells++; break;
                    default: holds++; break;
                }

                pending = decision.Action;
                var report = new BacktestStep(step, snapshots.Count, snapshot, decision, executed, value, portfolio.HasPosition);
                progress?.Report(report);
                if (onStep is not null)
                {
                    await onStep(report, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completed = false;
        }

        var final = equity.Count > 0 ? equity[^1].Equity : settings.InitialCash;
        return new BacktestResult(
            strategy.Name,
            equity,
            settings.InitialCash,
            final,
            (double)((final - settings.InitialCash) / settings.InitialCash) * 100,
            MaxDrawdownPercent(equity),
            portfolio.TradeCount,
            portfolio.FeesPaid,
            buys,
            sells,
            holds,
            completed);
    }

    public static double MaxDrawdownPercent(IReadOnlyList<EquityPoint> equity)
    {
        decimal peak = 0;
        double worst = 0;
        foreach (var point in equity)
        {
            peak = Math.Max(peak, point.Equity);
            if (peak > 0)
            {
                worst = Math.Max(worst, (double)((peak - point.Equity) / peak) * 100);
            }
        }

        return worst;
    }
}
