using Trader.Core;

namespace Trader.Tests;

public sealed class CoreTests
{
    [Fact]
    public void Features_start_after_warmup_and_have_one_token_per_group()
    {
        var candles = SyntheticMarket.Create(1_000);
        var extractor = new MarketFeatureExtractor();

        var snapshots = extractor.Extract(candles);

        Assert.Equal(candles.Count - extractor.Settings.WarmupCandles, snapshots.Count);
        Assert.Equal(extractor.Settings.WarmupCandles, snapshots[0].Index);
        Assert.All(snapshots, s =>
        {
            Assert.Equal(3, s.Tokens.Count);
            Assert.InRange((int)s.Tokens[0], (int)MarketToken.ReturnStrongDown, (int)MarketToken.ReturnStrongUp);
            Assert.InRange((int)s.Tokens[1], (int)MarketToken.TrendDown, (int)MarketToken.TrendUp);
            Assert.InRange((int)s.Tokens[2], (int)MarketToken.VolatilityLow, (int)MarketToken.VolatilityHigh);
        });
    }

    [Fact]
    public void Features_do_not_look_ahead()
    {
        var candles = SyntheticMarket.Create(1_000).ToList();
        var extractor = new MarketFeatureExtractor();
        var before = extractor.Extract(candles);

        candles[^1] = candles[^1] with { Close = candles[^1].Close * 2, High = candles[^1].Close * 2 };
        var after = extractor.Extract(candles);

        for (var i = 0; i < before.Count - 1; i++)
        {
            Assert.Equal(before[i].Tokens, after[i].Tokens);
        }
    }

    [Theory]
    [InlineData(-0.02, MarketToken.ReturnStrongDown)]
    [InlineData(-0.005, MarketToken.ReturnDown)]
    [InlineData(0.0, MarketToken.ReturnFlat)]
    [InlineData(0.005, MarketToken.ReturnUp)]
    [InlineData(0.02, MarketToken.ReturnStrongUp)]
    public void Return_token_uses_volatility_scaled_bins(double logReturn, MarketToken expected)
    {
        Assert.Equal(expected, new MarketFeatureExtractor().ReturnToken(logReturn, 0.01));
    }

    [Fact]
    public void Portfolio_buys_all_in_and_sells_all_out_with_fees()
    {
        var portfolio = new PaperPortfolio(10_000m, 0.001m);

        Assert.True(portfolio.Apply(TradeAction.Buy, 50_000m));
        Assert.False(portfolio.Apply(TradeAction.Buy, 50_000m));
        Assert.Equal(0m, portfolio.Cash);
        Assert.Equal(9_990m / 50_000m, portfolio.Btc);

        Assert.True(portfolio.Apply(TradeAction.Sell, 60_000m));
        Assert.Equal(0m, portfolio.Btc);
        var proceeds = 9_990m / 50_000m * 60_000m;
        Assert.Equal(proceeds - proceeds * 0.001m, portfolio.Cash);
        Assert.Equal(2, portfolio.TradeCount);
    }

    [Fact]
    public async Task Decisions_execute_at_next_open()
    {
        var candles = SyntheticMarket.Create(900);
        var snapshots = new MarketFeatureExtractor().Extract(candles);
        var result = await new Backtester().RunAsync(candles, snapshots, new BuyAndHoldStrategy());

        var firstExecution = candles[snapshots[1].Index].Open;
        var expectedBtc = (10_000m - 10_000m * 0.001m) / firstExecution;
        Assert.Equal(10_000m, result.Equity[0].Equity);
        Assert.Equal(expectedBtc * candles[snapshots[1].Index].Close, result.Equity[1].Equity);
        Assert.Equal(1, result.TradeCount);
        Assert.True(result.Completed);
    }

    [Fact]
    public async Task Random_strategy_is_reproducible()
    {
        var candles = SyntheticMarket.Create(1_200);
        var snapshots = new MarketFeatureExtractor().Extract(candles);
        var backtester = new Backtester();

        var first = await backtester.RunAsync(candles, snapshots, new RandomStrategy(5));
        var second = await backtester.RunAsync(candles, snapshots, new RandomStrategy(5));

        Assert.Equal(first.FinalEquity, second.FinalEquity);
        Assert.Equal(first.TradeCount, second.TradeCount);
    }

    [Fact]
    public void Max_drawdown_is_measured_from_the_running_peak()
    {
        var t = DateTimeOffset.UnixEpoch;
        var equity = new[] { 100m, 120m, 90m, 130m, 117m }.Select(v => new EquityPoint(t, 1m, v)).ToArray();

        Assert.Equal(25.0, Backtester.MaxDrawdownPercent(equity), 6);
    }

    [Fact]
    public async Task Cancelled_backtest_returns_partial_result()
    {
        var candles = SyntheticMarket.Create(1_200);
        var snapshots = new MarketFeatureExtractor().Extract(candles);
        using var cancellation = new CancellationTokenSource();

        var result = await new Backtester().RunAsync(
            candles, snapshots, new MomentumStrategy(),
            onStep: (step, _) => { if (step.Step == 10) cancellation.Cancel(); return Task.CompletedTask; },
            cancellationToken: cancellation.Token);

        Assert.False(result.Completed);
        Assert.Equal(11, result.Equity.Count);
    }
}
