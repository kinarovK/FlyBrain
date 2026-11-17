using System.Diagnostics;
using FlyBrain.BrainClient;
using Topo.Core;

namespace Topo.Training;

/// <summary>
/// Owns at most one reference/unsigned Brain Service session for a caller's lifetime.
/// It is shared by the desktop application and the non-WPF experiment host so both
/// execute the identical frozen coordinate decode path.
/// </summary>
public sealed record OwnedBrainSessionStatus(string State, string Detail, int? NeuronCount, string? ServiceVersion);

public sealed class OwnedBrainSessionDecodeService(IBrainServiceClient client, string? repositoryRoot = null)
    : ICoordinateCandidateEvaluator, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _sessionId;
    private BrainChannelConfiguration? _channels;
    private ReadoutPopulationConfiguration? _population;
    private TokenSoftmaxClassifier? _classifier;
    private TokenReadoutPreprocessor? _preprocessor;

    public async Task<OwnedBrainSessionStatus> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            LoadFrozenArtifacts();
            var readiness = await client.GetReadinessAsync(cancellationToken);
            if (!readiness.IsReady)
            {
                return new OwnedBrainSessionStatus("OFFLINE", "Brain Service is not ready.", null, readiness.Result.ServiceVersion);
            }

            if (_sessionId is null)
            {
                var session = await client.CreateSessionAsync(new CreateBrainSessionRequest
                {
                    ExecutionMode = BrainExecutionMode.Reference,
                    TransmitterMode = BrainTransmitterMode.Unsigned,
                }, cancellationToken);
                _sessionId = session.SessionId;
                return new OwnedBrainSessionStatus("READY", "Reference / unsigned session ready.", session.NeuronCount, readiness.Result.ServiceVersion);
            }

            return new OwnedBrainSessionStatus("READY", "Reference / unsigned session ready.", null, readiness.Result.ServiceVersion);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CoordinateNeuralDecode> DecodeAsync(
        GeoCoordinate source,
        IProgress<CoordinateDecodeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_sessionId is null || _channels is null || _population is null || _classifier is null || _preprocessor is null)
            {
                throw new InvalidOperationException("Brain Service is not connected or the frozen model is unavailable.");
            }

            return await new CoordinateNeuralDecodePipeline(client, new LogicalReadoutSnapshotReader(client)).DecodeAsync(
                _sessionId, source, _channels, _population, _classifier, _preprocessor, progress, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CandidateEvaluation> EvaluateAsync(
        GeoCoordinate proposal,
        IProgress<CoordinateDecodeProgress>? neuralProgress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var decode = await DecodeAsync(proposal, neuralProgress, cancellationToken);
        return new CandidateEvaluation(proposal, decode, stopwatch.Elapsed);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_sessionId is not null)
            {
                await client.DeleteSessionAsync(_sessionId, CancellationToken.None);
            }
        }
        catch
        {
            // The independently managed service may have stopped; disposal still releases local state.
        }
        finally
        {
            _sessionId = null;
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void LoadFrozenArtifacts()
    {
        if (_channels is not null)
        {
            return;
        }

        var root = repositoryRoot
            ?? FlyBrain.Common.RepositoryLocator.FindRoot();
        _channels = BrainChannelConfiguration.LoadFromFile(
            Environment.GetEnvironmentVariable("TOPO_BRAIN_CHANNELS_PATH") ?? Path.Combine(root, "modules", "topo", "config", "brain-channels.json"));
        _population = ReadoutPopulationConfiguration.LoadFromFile(
            Environment.GetEnvironmentVariable("TOPO_READOUT_POPULATION_PATH") ?? Path.Combine(root, "modules", "topo", "config", "readout-population.json"));
        var loaded = TokenReadoutModelStore.Load(
            Environment.GetEnvironmentVariable("TOPO_TOKEN_READOUT_MODEL_PATH") ?? Path.Combine(root, "modules", "topo", "models", "token-readout-v1.json"));
        TokenReadoutCompatibility.Validate(loaded.Artifact, loaded.Classifier, loaded.Preprocessor, _population, _channels);
        _classifier = loaded.Classifier;
        _preprocessor = loaded.Preprocessor;
    }
}
