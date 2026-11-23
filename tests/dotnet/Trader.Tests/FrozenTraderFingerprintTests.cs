using FlyBrain.BrainClient;
using FlyBrain.Common;
using FlyBrain.TestSupport;
using Trader.Brain;
using Trader.Core;

namespace Trader.Tests;

public sealed record TraderStateFingerprint(string Tokens, TradeAction Action, double BuyScore, double SellScore);

public sealed record TraderFingerprint(Dictionary<string, long> InputRoots, int BuyPool, int SellPool, TraderStateFingerprint[] States);

/// <summary>
/// Pins the frozen Trader brain answer for all 30 possible market states
/// (5 return x 3 trend x 2 volatility tokens) against tests/golden/trader-states-v1.json.
/// Needs a running brain (FLYBRAIN_REAL=1, set by scripts/gate.ps1); no market data required.
/// </summary>
public sealed class FrozenTraderFingerprintTests
{
    [Fact]
    [Trait("Category", "RealBrain")]
    public async Task All_30_market_states_match_golden_fingerprint()
    {
        if (!RealBrainGate.IsEnabled())
        {
            return;
        }

        var root = RepositoryLocator.FindRoot(AppContext.BaseDirectory);
        var configuration = TraderBrainConfiguration.LoadFromFile(TraderBrainConfiguration.DefaultPath(root));
        using var http = new HttpClient { BaseAddress = BrainServiceClientOptions.FromEnvironment().BaseAddress };
        await using var brain = new TraderBrainSession(new BrainServiceClient(http), configuration);
        var status = await brain.ConnectAsync();
        Assert.True(status.IsConnected, status.Detail);

        var states = new List<TraderStateFingerprint>();
        foreach (var tokens in AllStates())
        {
            var evaluation = await brain.EvaluateAsync(tokens);
            states.Add(new TraderStateFingerprint(
                MarketTokenText.Sequence(tokens),
                evaluation.Action,
                Math.Round(evaluation.Response.BuyScore, 10),
                Math.Round(evaluation.Response.SellScore, 10)));
        }

        var layout = brain.Layout!;
        var actual = new TraderFingerprint(
            layout.InputRoots.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value),
            layout.BuyPool.Count,
            layout.SellPool.Count,
            states.ToArray());

        Assert.Equal(30, actual.States.Length);
        GoldenFile.AssertMatches("trader-states-v1.json", actual, Compare);
    }

    internal static IEnumerable<MarketToken[]> AllStates()
    {
        MarketToken[] returns = [MarketToken.ReturnStrongDown, MarketToken.ReturnDown, MarketToken.ReturnFlat, MarketToken.ReturnUp, MarketToken.ReturnStrongUp];
        MarketToken[] trends = [MarketToken.TrendDown, MarketToken.TrendFlat, MarketToken.TrendUp];
        MarketToken[] volatility = [MarketToken.VolatilityLow, MarketToken.VolatilityHigh];
        foreach (var r in returns)
        {
            foreach (var t in trends)
            {
                foreach (var v in volatility)
                {
                    yield return [r, t, v];
                }
            }
        }
    }

    private static string? Compare(TraderFingerprint expected, TraderFingerprint actual)
    {
        if (expected.BuyPool != actual.BuyPool || expected.SellPool != actual.SellPool)
        {
            return $"pools {actual.BuyPool}/{actual.SellPool}, expected {expected.BuyPool}/{expected.SellPool}";
        }

        foreach (var (token, root) in expected.InputRoots)
        {
            if (!actual.InputRoots.TryGetValue(token, out var actualRoot) || actualRoot != root)
            {
                return $"input root for {token} changed";
            }
        }

        if (expected.States.Length != actual.States.Length)
        {
            return $"{actual.States.Length} states, expected {expected.States.Length}";
        }

        for (var i = 0; i < expected.States.Length; i++)
        {
            var e = expected.States[i];
            var a = actual.States[i];
            if (e.Tokens != a.Tokens || e.Action != a.Action || !GoldenFile.Close(e.BuyScore, a.BuyScore) || !GoldenFile.Close(e.SellScore, a.SellScore))
            {
                return $"state {e.Tokens}: {a.Action} {a.BuyScore}/{a.SellScore} (expected {e.Action} {e.BuyScore}/{e.SellScore})";
            }
        }

        return null;
    }
}
