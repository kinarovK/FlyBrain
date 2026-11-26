using System.Diagnostics;
using FlyBrain.BrainClient;
using Trader.Core;

namespace Trader.Brain;

public sealed record BrainMarketEvaluation(
    IReadOnlyList<MarketToken> Tokens,
    TradeAction Action,
    PoolResponse Response,
    int TotalSpikes,
    double MaximumMembraneDelta,
    bool FromCache,
    TimeSpan Elapsed);

public sealed record TraderBrainStatus(bool IsConnected, string Detail, TraderBrainLayout? Layout);

/// <summary>
/// Owns one brain session for the trader. Every market evaluation starts from a
/// reset, so the same token sequence always gives the same answer; answers are
/// therefore cached per token sequence.
/// </summary>
public sealed class TraderBrainSession(IBrainServiceClient client, TraderBrainConfiguration configuration) : ITraderBrain, IAsyncDisposable
{
    private const int MaxBatch = 256;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, BrainMarketEvaluation> _cache = new(StringComparer.Ordinal);
    private string? _sessionId;
    private TraderBrainLayout? _layout;
    private Dictionary<long, (double Membrane, int Spikes)>? _baseline;

    public TraderBrainConfiguration Configuration => configuration;

    public TraderBrainLayout? Layout => _layout;

    public bool IsConnected => _sessionId is not null && _layout is not null;

    public int CacheSize
    {
        get
        {
            lock (_cache)
            {
                return _cache.Count;
            }
        }
    }

    public async Task<TraderBrainStatus> ConnectAsync(IProgress<string>? log = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
            {
                return new TraderBrainStatus(true, "Trader session ready.", _layout);
            }

            var readiness = await client.GetReadinessAsync(cancellationToken);
            if (!readiness.IsReady)
            {
                return new TraderBrainStatus(false, "Brain service is not ready.", null);
            }

            var session = await client.CreateSessionAsync(new CreateBrainSessionRequest
            {
                ExecutionMode = configuration.ExecutionMode == "whole" ? BrainExecutionMode.Whole : BrainExecutionMode.Reference,
                TransmitterMode = configuration.TransmitterMode == "transmitter" ? BrainTransmitterMode.Transmitter : BrainTransmitterMode.Unsigned,
            }, cancellationToken);
            _sessionId = session.SessionId;
            log?.Report($"Trader session created ({session.NeuronCount} neurons, {configuration.ExecutionMode}/{configuration.TransmitterMode}).");

            var roots = await ListRootsAsync(_sessionId, cancellationToken);
            log?.Report($"Selecting {Enum.GetValues<MarketToken>().Length} input roots from rank {configuration.Inputs.FirstRank} of {roots.Count}...");
            _layout = await TraderBrainLayout.BuildAsync(
                roots,
                configuration.Inputs.FirstRank,
                configuration.Inputs.MaxCandidates,
                (root, ct) => configuration.Inputs.RequireSpike ? SpikesOnStimulusAsync(_sessionId, root, ct) : Task.FromResult(true),
                cancellationToken);
            foreach (var (token, root) in _layout.InputRoots)
            {
                log?.Report($"  {MarketTokenText.Short(token),-4} -> {root}");
            }

            await client.ResetSessionAsync(_sessionId, cancellationToken);
            _baseline = await ReadAsync(_sessionId, _layout.ReadoutRoots, cancellationToken);
            log?.Report($"Readout: {_layout.BuyPool.Count} BUY-pool and {_layout.SellPool.Count} SELL-pool neurons.");
            return new TraderBrainStatus(true, "Trader session ready.", _layout);
        }
        catch
        {
            await DeleteSessionAsync();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BrainMarketEvaluation> EvaluateAsync(IReadOnlyList<MarketToken> tokens, CancellationToken cancellationToken = default)
    {
        var key = MarketTokenText.Sequence(tokens);
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached with { FromCache = true, Elapsed = TimeSpan.Zero };
            }
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_sessionId is null || _layout is null || _baseline is null)
            {
                throw new InvalidOperationException("The trader brain session is not connected. Start the brain first.");
            }

            var stopwatch = Stopwatch.StartNew();
            await client.ResetSessionAsync(_sessionId, cancellationToken);
            var spikes = 0;
            foreach (var token in tokens)
            {
                var stimulus = await client.StepAsync(_sessionId, new BrainStepRequest
                {
                    Steps = configuration.StimulusSteps,
                    Stimulus = [new BrainStimulus(_layout.InputRoots[token], configuration.StimulusAmplitude)],
                    MaxReturnedEvents = 1,
                }, cancellationToken);
                EnsureSafe(stimulus);
                spikes += stimulus.TotalSpikes;
                if (configuration.SettleSteps > 0)
                {
                    var settle = await client.StepAsync(_sessionId, new BrainStepRequest
                    {
                        Steps = configuration.SettleSteps,
                        Stimulus = [],
                        MaxReturnedEvents = 1,
                    }, cancellationToken);
                    EnsureSafe(settle);
                    spikes += settle.TotalSpikes;
                }
            }

            var observed = await ReadAsync(_sessionId, _layout.ReadoutRoots, cancellationToken);
            var deltas = new Dictionary<long, (double MembraneDelta, int SpikeDelta)>(observed.Count);
            var maxDelta = 0.0;
            foreach (var (root, value) in observed)
            {
                var baseline = _baseline[root];
                var delta = (value.Membrane - baseline.Membrane, value.Spikes - baseline.Spikes);
                deltas[root] = delta;
                maxDelta = Math.Max(maxDelta, Math.Abs(delta.Item1));
            }

            var response = TraderDecisionRule.Score(_layout.BuyPool, _layout.SellPool, deltas, configuration.Decision.SpikeWeightMillivolts);
            var action = TraderDecisionRule.Decide(response, configuration.Decision.DeadZoneFraction);
            var evaluation = new BrainMarketEvaluation(tokens.ToArray(), action, response, spikes, maxDelta, false, stopwatch.Elapsed);
            lock (_cache)
            {
                _cache[key] = evaluation;
            }

            return evaluation;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes the brain session; the next connect creates a new one. The answer cache is kept.</summary>
    public async Task ReleaseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await DeleteSessionAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ClearCache()
    {
        lock (_cache)
        {
            _cache.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseAsync();
        _gate.Dispose();
    }

    private async Task DeleteSessionAsync()
    {
        var sessionId = _sessionId;
        _sessionId = null;
        _layout = null;
        _baseline = null;
        if (sessionId is null)
        {
            return;
        }

        try
        {
            await client.DeleteSessionAsync(sessionId, CancellationToken.None);
        }
        catch
        {
            // The brain may already be gone; local state is released either way.
        }
    }

    private async Task<bool> SpikesOnStimulusAsync(string sessionId, long root, CancellationToken cancellationToken)
    {
        await client.ResetSessionAsync(sessionId, cancellationToken);
        var result = await client.StepAsync(sessionId, new BrainStepRequest
        {
            Steps = configuration.StimulusSteps,
            Stimulus = [new BrainStimulus(root, configuration.StimulusAmplitude)],
            MaxReturnedEvents = 1,
        }, cancellationToken);
        return string.Equals(result.SafetyStatus, "completed", StringComparison.OrdinalIgnoreCase) && result.TotalSpikes > 0;
    }

    private async Task<IReadOnlyList<long>> ListRootsAsync(string sessionId, CancellationToken cancellationToken)
    {
        var roots = new List<long>();
        var offset = 0;
        while (true)
        {
            var page = await client.GetSessionRootsAsync(sessionId, offset, MaxBatch, cancellationToken);
            roots.AddRange(page.RootIds);
            offset += page.ReturnedCount;
            if (page.ReturnedCount == 0 || offset >= page.TotalRootCount)
            {
                return roots;
            }
        }
    }

    private async Task<Dictionary<long, (double Membrane, int Spikes)>> ReadAsync(
        string sessionId,
        IReadOnlyList<long> roots,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<long, (double, int)>(roots.Count);
        int? step = null;
        for (var offset = 0; offset < roots.Count; offset += MaxBatch)
        {
            var batch = roots.Skip(offset).Take(MaxBatch).ToArray();
            var snapshot = await client.ReadoutAsync(sessionId, batch, cancellationToken);
            if (step is not null && snapshot.SimulationStep != step)
            {
                throw new InvalidOperationException("Readout batches came from different simulation steps.");
            }

            step = snapshot.SimulationStep;
            foreach (var neuron in snapshot.Neurons)
            {
                values[neuron.RootId] = (neuron.MembraneMillivolts, neuron.CumulativeSpikeCount);
            }
        }

        return values;
    }

    private static void EnsureSafe(BrainStepResult result)
    {
        if (!string.Equals(result.SafetyStatus, "completed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Brain reported unsafe status '{result.SafetyStatus}'.");
        }
    }
}
