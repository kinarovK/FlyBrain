using FlyBrain.BrainClient;
using FlyBrain.Common;
using FlyBrain.TestSupport;
using Topo.Core;
using Topo.Training;
using Xunit;

namespace Topo.Training.Tests;

public sealed record TopoTokenFingerprint(int Index, string Token, double Confidence, int SimulationStep);

public sealed record TopoDecodeFingerprint(string Source, string? Decoded, TopoTokenFingerprint[] Tokens);

/// <summary>
/// Pins the frozen Topo decode of N35 W54 against tests/golden/topo-n35w54-v1.json.
/// Needs a running brain (FLYBRAIN_REAL=1, set by scripts/gate.ps1).
/// </summary>
public sealed class FrozenBaselineFingerprintTests
{
    [Fact]
    [Trait("Category", "RealBrain")]
    public async Task Topo_N35_W54_decode_matches_golden_fingerprint()
    {
        if (!RealBrainGate.IsEnabled())
        {
            return;
        }

        var root = RepositoryLocator.FindRoot(AppContext.BaseDirectory);
        using var http = new HttpClient { BaseAddress = BrainServiceClientOptions.FromEnvironment().BaseAddress };
        await using var decoder = new OwnedBrainSessionDecodeService(new BrainServiceClient(http), root);
        var status = await decoder.ConnectAsync();
        Assert.Equal("READY", status.State);
        Assert.True(GeoCoordinate.TryParse("N35 W54", out var source));

        var result = await decoder.DecodeAsync(source);
        var actual = new TopoDecodeFingerprint(
            source.ToString(),
            result.Decoding.Coordinate?.ToString(),
            result.Predictions
                .Select(p => new TopoTokenFingerprint(p.SequenceIndex, p.PredictedToken.ToString(), Math.Round(p.ConfidenceScore, 10), p.SimulationStep))
                .ToArray());

        GoldenFile.AssertMatches("topo-n35w54-v1.json", actual, Compare);
    }

    private static string? Compare(TopoDecodeFingerprint expected, TopoDecodeFingerprint actual)
    {
        if (expected.Source != actual.Source || expected.Decoded != actual.Decoded)
        {
            return $"decoded {actual.Decoded}, expected {expected.Decoded}";
        }

        if (expected.Tokens.Length != actual.Tokens.Length)
        {
            return $"{actual.Tokens.Length} tokens, expected {expected.Tokens.Length}";
        }

        for (var i = 0; i < expected.Tokens.Length; i++)
        {
            var e = expected.Tokens[i];
            var a = actual.Tokens[i];
            if (e.Index != a.Index || e.Token != a.Token || e.SimulationStep != a.SimulationStep || !GoldenFile.Close(e.Confidence, a.Confidence))
            {
                return $"token {i}: {a} (expected {e})";
            }
        }

        return null;
    }
}
