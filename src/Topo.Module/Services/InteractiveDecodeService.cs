using FlyBrain.BrainClient;
using Topo.Core;
using Topo.Training;

namespace Topo.Module.Services;

public sealed record InteractiveBrainStatus(string State, string Detail, int? NeuronCount, string? ServiceVersion);

public interface IInteractiveDecodeService : IAsyncDisposable
{
    Task<InteractiveBrainStatus> ConnectAsync(CancellationToken cancellationToken = default);
    Task<CoordinateNeuralDecode> DecodeAsync(GeoCoordinate source, IProgress<CoordinateDecodeProgress> progress, CancellationToken cancellationToken = default);
}

/// <summary>Owns at most one interactive reference session for the running desktop application.</summary>
public sealed class InteractiveDecodeService(IBrainServiceClient client) : IInteractiveDecodeService, ICoordinateCandidateEvaluator
{
    private readonly object _sync = new();
    private OwnedBrainSessionDecodeService? _owned;

    private OwnedBrainSessionDecodeService Owned
    {
        get
        {
            lock (_sync)
            {
                return _owned ??= new OwnedBrainSessionDecodeService(client);
            }
        }
    }

    /// <summary>Deletes the brain session (if any). The next connect creates a fresh one.</summary>
    public async Task ReleaseSessionAsync()
    {
        OwnedBrainSessionDecodeService? owned;
        lock (_sync)
        {
            owned = _owned;
            _owned = null;
        }

        if (owned is not null)
        {
            await owned.DisposeAsync();
        }
    }

    public async Task<InteractiveBrainStatus> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var status = await Owned.ConnectAsync(cancellationToken);
        return new InteractiveBrainStatus(status.State, status.Detail, status.NeuronCount, status.ServiceVersion);
    }

    public async Task<CoordinateNeuralDecode> DecodeAsync(GeoCoordinate source, IProgress<CoordinateDecodeProgress> progress, CancellationToken cancellationToken = default)
    {
        return await Owned.DecodeAsync(source, progress, cancellationToken);
    }

    public async Task<CandidateEvaluation> EvaluateAsync(GeoCoordinate proposal, IProgress<CoordinateDecodeProgress>? neuralProgress, CancellationToken cancellationToken)
    {
        return await Owned.EvaluateAsync(proposal, neuralProgress, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseSessionAsync();
    }
}
