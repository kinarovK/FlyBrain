using Topo.Core;
using Topo.Training;

namespace Topo.App.Services;

public interface IInteractiveSearchService
{
    Task<SearchResult> RunAsync(
        GeoCoordinate target,
        SearchSettings settings,
        bool useRandomBaseline,
        IProgress<SearchIteration>? progress = null,
        IProgress<CoordinateDecodeProgress>? neuralProgress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Runs external policy search through the app's existing one-session frozen decoder.</summary>
public sealed class AutonomousSearchService(IInteractiveDecodeService decoder) : IInteractiveSearchService
{
    public Task<SearchResult> RunAsync(GeoCoordinate target, SearchSettings settings, bool useRandomBaseline, IProgress<SearchIteration>? progress = null, IProgress<CoordinateDecodeProgress>? neuralProgress = null, CancellationToken cancellationToken = default)
    {
        if (decoder is not ICoordinateCandidateEvaluator evaluator)
            throw new InvalidOperationException("Interactive decoder does not provide frozen candidate evaluation.");
        ICoordinateSearchPolicy policy = useRandomBaseline
            ? new RandomCoordinateSearchPolicy(settings)
            : new StochasticHillClimbingSearchPolicy(settings);
        return new AutonomousSearchRunner().RunAsync(target, policy, evaluator, settings, progress, neuralProgress, cancellationToken);
    }
}
