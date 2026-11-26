using System.Globalization;
using FlyBrain.BrainClient;
using FlyBrain.Common;
using Trader.Brain;
using Trader.Core;
using Trader.Market;
using Xunit.Abstractions;

namespace Trader.Tests;

/// <summary>
/// Real-brain smoke test: full 2024 replay of the brain and the three baselines.
/// Needs a running brain service and data/market/binance; enable with FLYBRAIN_RUN_TRADER_SMOKE=1.
/// </summary>
public sealed class TraderBrainSmokeTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Brain_and_baselines_complete_the_2024_backtest()
    {
        if (!FlyBrain.TestSupport.RealBrainGate.IsEnabled("FLYBRAIN_RUN_TRADER_SMOKE"))
        {
            return;
        }

        var root = RepositoryLocator.FindRoot(AppContext.BaseDirectory);
        var data = new BinanceKlineLoader().LoadDirectory(BinanceKlineLoader.DefaultDirectory(root));
        var snapshots = new MarketFeatureExtractor().Extract(data.Candles);
        var states = snapshots.Select(s => MarketTokenText.Sequence(s.Tokens)).Distinct(StringComparer.Ordinal).ToArray();
        output.WriteLine($"Candles: {data.Candles.Count} ({data.Start:u} .. {data.End:u}), snapshots: {snapshots.Count}, distinct market states: {states.Length}");

        var configuration = TraderBrainConfiguration.LoadFromFile(TraderBrainConfiguration.DefaultPath(root));
        using var http = new HttpClient { BaseAddress = BrainServiceClientOptions.FromEnvironment().BaseAddress };
        await using var brain = new TraderBrainSession(new BrainServiceClient(http), configuration);
        var status = await brain.ConnectAsync(new SyncProgress(output.WriteLine));
        Assert.True(status.IsConnected, status.Detail);
        var layout = brain.Layout!;
        Assert.Equal(Enum.GetValues<MarketToken>().Length, layout.InputRoots.Count);
        output.WriteLine($"Pools: BUY {layout.BuyPool.Count}, SELL {layout.SellPool.Count}");

        output.WriteLine("Per-state brain response (buy / sell / scale -> action):");
        foreach (var snapshot in snapshots.DistinctBy(s => MarketTokenText.Sequence(s.Tokens)))
        {
            var evaluation = await brain.EvaluateAsync(snapshot.Tokens);
            var r = evaluation.Response;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {MarketTokenText.Sequence(snapshot.Tokens),-16} {r.BuyScore,10:F4} {r.SellScore,10:F4} {r.Scale,10:F4} -> {evaluation.Action} (spikes {evaluation.TotalSpikes})"));
        }

        var strategies = new ITradingStrategy[]
        {
            new BrainTradingStrategy(brain),
            new BuyAndHoldStrategy(),
            new MomentumStrategy(),
            new RandomStrategy(20260924), // TraderViewModel.RandomSeed
        };
        var backtester = new Backtester();
        foreach (var strategy in strategies)
        {
            var result = await backtester.RunAsync(data.Candles, snapshots, strategy);
            Assert.True(result.Completed);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{result.StrategyName,-24} final {result.FinalEquity,12:F2}  return {result.ReturnPercent,8:F2} %  maxDD {result.MaxDrawdownPercent,6:F2} %  trades {result.TradeCount,4}  B/S/H {result.BuySignals}/{result.SellSignals}/{result.HoldSignals}"));
        }

        Assert.Equal(states.Length, brain.CacheSize);
    }

    private sealed class SyncProgress(Action<string> write) : IProgress<string>
    {
        public void Report(string value) => write(value);
    }
}
