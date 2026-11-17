using FlyBrain.BrainClient;
using Topo.Core;
using Topo.Training;

namespace Topo.App.Services;

public sealed record InteractiveBrainStatus(string State, string Detail, int? NeuronCount, string? ServiceVersion);

public interface IInteractiveDecodeService : IAsyncDisposable
{
    Task<InteractiveBrainStatus> ConnectAsync(CancellationToken cancellationToken = default);
    Task<CoordinateNeuralDecode> DecodeAsync(GeoCoordinate source, IProgress<CoordinateDecodeProgress> progress, CancellationToken cancellationToken = default);
}

/// <summary>Owns at most one interactive reference session for the running desktop application.</summary>
public sealed class InteractiveDecodeService(IBrainServiceClient client) : IInteractiveDecodeService, ICoordinateCandidateEvaluator
{
    private readonly OwnedBrainSessionDecodeService _owned = new(client);

    public async Task<InteractiveBrainStatus> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var status = await _owned.ConnectAsync(cancellationToken);
        return new InteractiveBrainStatus(status.State, status.Detail, status.NeuronCount, status.ServiceVersion);
    }

    public async Task<CoordinateNeuralDecode> DecodeAsync(GeoCoordinate source, IProgress<CoordinateDecodeProgress> progress, CancellationToken cancellationToken = default)
    {
        return await _owned.DecodeAsync(source, progress, cancellationToken);
    }

    public async Task<CandidateEvaluation> EvaluateAsync(GeoCoordinate proposal, IProgress<CoordinateDecodeProgress>? neuralProgress, CancellationToken cancellationToken)
    {
        return await _owned.EvaluateAsync(proposal, neuralProgress, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _owned.DisposeAsync();
    }
}
