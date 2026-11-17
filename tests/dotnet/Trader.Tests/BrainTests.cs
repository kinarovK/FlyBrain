using System.Net;
using FlyBrain.BrainClient;
using Trader.Brain;
using Trader.Core;

namespace Trader.Tests;

public sealed class BrainTests
{
    [Fact]
    public async Task Layout_skips_silent_candidates_and_splits_readout_into_pools()
    {
        var roots = Enumerable.Range(1, 40).Select(i => (long)i * 10).Reverse();
        var silent = new HashSet<long> { 170, 190 };

        var layout = await TraderBrainLayout.BuildAsync(roots, firstRank: 15, maxCandidates: 30, (root, _) => Task.FromResult(!silent.Contains(root)));

        Assert.Equal(160, layout.InputRoots[MarketToken.ReturnStrongDown]);
        Assert.Equal(180, layout.InputRoots[MarketToken.ReturnDown]);
        Assert.DoesNotContain(layout.InputRoots.Values, silent.Contains);
        Assert.Equal(30, layout.ReadoutRoots.Count);
        Assert.Empty(layout.BuyPool.Intersect(layout.SellPool));
        Assert.Empty(layout.ReadoutRoots.Intersect(layout.InputRoots.Values));
    }

    [Fact]
    public async Task Layout_fails_when_too_few_inputs_respond()
    {
        var roots = Enumerable.Range(1, 40).Select(i => (long)i);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TraderBrainLayout.BuildAsync(roots, 15, 30, (_, _) => Task.FromResult(false)));
    }

    [Theory]
    [InlineData(1.0, 0.5, 1.0, TradeAction.Buy)]
    [InlineData(0.5, 1.0, 1.0, TradeAction.Sell)]
    [InlineData(1.0, 0.95, 1.0, TradeAction.Hold)]
    [InlineData(0.0, 0.0, 0.0, TradeAction.Hold)]
    public void Decision_rule_uses_dead_zone(double buy, double sell, double scale, TradeAction expected)
    {
        Assert.Equal(expected, TraderDecisionRule.Decide(new PoolResponse(buy, sell, scale), 0.1));
    }

    [Fact]
    public void Default_configuration_file_matches_built_in_defaults()
    {
        var root = FlyBrain.Common.RepositoryLocator.TryFindRoot();
        if (root is null)
        {
            return;
        }

        var loaded = TraderBrainConfiguration.LoadFromFile(TraderBrainConfiguration.DefaultPath(root));
        Assert.Equal(new TraderBrainConfiguration().StimulusAmplitude, loaded.StimulusAmplitude);
        Assert.Equal(new TraderBrainConfiguration().Inputs, loaded.Inputs);
        Assert.Equal(new TraderBrainConfiguration().Decision, loaded.Decision);
    }

    [Fact]
    public void Invalid_configuration_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => TraderBrainConfiguration.Load("""{"version": 2}"""));
    }

    [Fact]
    public async Task Session_connects_evaluates_and_caches_by_token_sequence()
    {
        var client = new FakeBrainClient(rootCount: 60);
        await using var session = new TraderBrainSession(client, new TraderBrainConfiguration());

        var status = await session.ConnectAsync();
        var tokens = new[] { MarketToken.ReturnUp, MarketToken.TrendUp, MarketToken.VolatilityLow };
        var first = await session.EvaluateAsync(tokens);
        var second = await session.EvaluateAsync(tokens);

        Assert.True(status.IsConnected);
        Assert.Equal(10, status.Layout!.InputRoots.Count);
        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(first.Action, second.Action);
        Assert.Equal(1, session.CacheSize);
        Assert.Equal(3, client.StimulusStepsSinceLastReset);
    }

    [Fact]
    public async Task Release_deletes_the_session()
    {
        var client = new FakeBrainClient(rootCount: 60);
        var session = new TraderBrainSession(client, new TraderBrainConfiguration());
        await session.ConnectAsync();

        await session.ReleaseAsync();

        Assert.False(session.IsConnected);
        Assert.Equal(1, client.DeletedSessions);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.EvaluateAsync([MarketToken.ReturnFlat, MarketToken.TrendFlat, MarketToken.VolatilityHigh]));
    }

    /// <summary>
    /// A tiny deterministic stand-in for the brain service: every stimulated root nudges
    /// the membrane of a few "downstream" roots chosen from its ID.
    /// </summary>
    private sealed class FakeBrainClient(int rootCount) : IBrainServiceClient
    {
        private readonly long[] _roots = Enumerable.Range(0, rootCount).Select(i => 720_000L + i * 7).ToArray();
        private readonly Dictionary<long, double> _membrane = new();
        private int _step;

        public int StimulusStepsSinceLastReset { get; private set; }

        public int DeletedSessions { get; private set; }

        public Task<BrainReadiness> GetReadinessAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BrainReadiness(HttpStatusCode.OK, new BrainReadyResult { Status = "ready", StartupTimings = new(), ServiceVersion = "test" }));

        public Task<BrainSession> CreateSessionAsync(CreateBrainSessionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BrainSession
            {
                ApiVersion = "v1",
                SessionId = "s1",
                Status = "ready",
                ExecutionMode = request.ExecutionMode,
                NeuronCount = rootCount,
                TransmitterMode = "unsigned",
                ResolvedConfiguration = new BrainResolvedConfiguration
                {
                    NeuralFormat = "test",
                    NeuralVersion = 1,
                    TransmitterMode = request.TransmitterMode,
                    ExecutionMode = request.ExecutionMode,
                },
            });

        public Task<SessionRootsPage> GetSessionRootsAsync(string sessionId, int offset = 0, int limit = 64, CancellationToken cancellationToken = default)
        {
            var page = _roots.Skip(offset).Take(limit).ToList();
            return Task.FromResult(new SessionRootsPage
            {
                SessionId = sessionId,
                Offset = offset,
                Limit = limit,
                ReturnedCount = page.Count,
                TotalRootCount = _roots.Length,
                RootIds = page,
            });
        }

        public Task<BrainResetResult> ResetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            _membrane.Clear();
            _step = 0;
            StimulusStepsSinceLastReset = 0;
            return Task.FromResult(new BrainResetResult { SessionId = sessionId, Status = "reset", SimulationStep = 0, ResetWallSeconds = 0 });
        }

        public Task<BrainStepResult> StepAsync(string sessionId, BrainStepRequest request, CancellationToken cancellationToken = default)
        {
            var spikes = 0;
            foreach (var stimulus in request.Stimulus ?? Array.Empty<BrainStimulus>())
            {
                StimulusStepsSinceLastReset++;
                spikes++;
                for (var k = 1; k <= 3; k++)
                {
                    var target = _roots[(int)((stimulus.RootId * k) % _roots.Length)];
                    _membrane[target] = _membrane.GetValueOrDefault(target) + 0.1 * k;
                }
            }

            _step += request.Steps;
            return Task.FromResult(new BrainStepResult
            {
                ApiVersion = "v1",
                SessionId = sessionId,
                StartingStep = _step - request.Steps,
                EndingStep = _step,
                SimulatedMilliseconds = _step * 0.1,
                StepsExecuted = request.Steps,
                TotalSpikes = spikes,
                UniqueSpikingNeurons = spikes,
                ActiveFraction = 0,
                PeakSimultaneousSpikeCount = spikes,
                PeakSimultaneousSpikeFraction = 0,
                SafetyStatus = "completed",
                Events = [],
                EventsTruncated = false,
                ReturnedEventCount = 0,
                EngineWallSeconds = 0,
            });
        }

        public Task<BrainReadoutSnapshot> ReadoutAsync(string sessionId, IReadOnlyList<long> rootIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BrainReadoutSnapshot
            {
                SessionId = sessionId,
                SimulationStep = _step,
                SimulatedMilliseconds = _step * 0.1,
                ReturnedCount = rootIds.Count,
                Neurons = rootIds.Select(root => new BrainReadoutNeuron
                {
                    RootId = root,
                    MembraneMillivolts = -65 + _membrane.GetValueOrDefault(root),
                    RefractorySteps = 0,
                    CumulativeSpikeCount = 0,
                    Note = "test",
                }).ToList(),
            });

        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            DeletedSessions++;
            return Task.CompletedTask;
        }

        public Task<BrainInfo> GetBrainInfoAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BrainSessionState> GetSessionStateAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NeuronDiagnostic> GetNeuronDiagnosticAsync(string sessionId, long rootId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
