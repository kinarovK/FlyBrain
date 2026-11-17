using System.Diagnostics;
using Topo.Core;

namespace Topo.Training;

public sealed record SearchSettings(
    int Seed = 20260924,
    int MaximumIterations = 250,
    IReadOnlyList<int>? MutationRadiiDegrees = null,
    int NonImprovingAttemptsBeforeExplore = 12,
    int HistoryLimit = 100)
{
    public static IReadOnlyList<int> DefaultMutationRadiiDegrees { get; } = [30, 15, 8, 4, 2, 1];

    public IReadOnlyList<int> Radii => MutationRadiiDegrees ?? DefaultMutationRadiiDegrees;

    public void Validate()
    {
        if (MaximumIterations <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumIterations));
        if (HistoryLimit <= 0) throw new ArgumentOutOfRangeException(nameof(HistoryLimit));
        if (NonImprovingAttemptsBeforeExplore <= 0) throw new ArgumentOutOfRangeException(nameof(NonImprovingAttemptsBeforeExplore));
        if (Radii.Count == 0 || Radii.Any(radius => radius <= 0)) throw new ArgumentException("Mutation radii must be non-empty positive integers.", nameof(MutationRadiiDegrees));
    }
}

public enum SearchTerminationReason { ExactSuccess, MaximumIterations, Cancelled, DecodeFailure, EvaluatorFailure }

public sealed record SearchCandidate(GeoCoordinate Coordinate, int RadiusDegrees, bool IsExploration);

/// <summary>Target-free feedback. The runner owns the target and reward computation.</summary>
public sealed record SearchFeedback(
    int Iteration,
    GeoCoordinate ProposedCoordinate,
    GeoCoordinate DecodedCoordinate,
    double AngularErrorDegrees,
    double Reward,
    bool Accepted,
    GeoCoordinate BestDecodedCoordinate,
    double BestReward,
    double BestAngularErrorDegrees,
    int RadiusDegrees,
    int ConsecutiveNonImprovements);

public interface ICoordinateSearchPolicy
{
    SearchCandidate ProposeInitial();
    SearchCandidate ProposeNext(SearchFeedback previous);
}

public interface ICoordinateCandidateEvaluator
{
    Task<CandidateEvaluation> EvaluateAsync(
        GeoCoordinate proposal,
        IProgress<CoordinateDecodeProgress>? neuralProgress,
        CancellationToken cancellationToken);
}

public sealed record CandidateEvaluation(GeoCoordinate ProposedCoordinate, CoordinateNeuralDecode NeuralDecode, TimeSpan Elapsed);

public sealed record SearchIteration(
    int Iteration,
    GeoCoordinate ProposedCoordinate,
    GeoCoordinate? DecodedCoordinate,
    double? AngularErrorDegrees,
    double? Reward,
    bool Accepted,
    GeoCoordinate? BestDecodedCoordinate,
    double? BestReward,
    double? BestAngularErrorDegrees,
    int RadiusDegrees,
    bool IsExploration,
    TimeSpan EvaluationElapsed,
    string? FailureMessage = null);

public sealed record SearchResult(
    GeoCoordinate Target,
    SearchTerminationReason Termination,
    IReadOnlyList<SearchIteration> Trace,
    IReadOnlyList<SearchIteration> History,
    GeoCoordinate? BestDecodedCoordinate,
    double? BestReward,
    double? BestAngularErrorDegrees,
    TimeSpan Elapsed)
{
    public bool IsSuccess => Termination == SearchTerminationReason.ExactSuccess;
    public int IterationsUsed => Trace.Count;
}

public sealed class StochasticHillClimbingSearchPolicy : ICoordinateSearchPolicy
{
    private readonly SearchSettings _settings;
    private readonly Random _random;
    private GeoCoordinate? _anchor;
    private int _radiusIndex;
    private int _nonImprovingAttempts;
    private bool _observedInitial;

    public StochasticHillClimbingSearchPolicy(SearchSettings settings)
    {
        settings.Validate(); _settings = settings; _random = new Random(settings.Seed);
    }

    public SearchCandidate ProposeInitial() => new(DrawGlobal(), _settings.Radii[0], true);

    public SearchCandidate ProposeNext(SearchFeedback previous)
    {
        if (!_observedInitial)
        {
            _observedInitial = true;
            _anchor = previous.ProposedCoordinate;
            _nonImprovingAttempts = 0;
            return DrawLocal();
        }

        if (previous.Accepted)
        {
            _anchor = previous.ProposedCoordinate;
            _radiusIndex = Math.Min(_radiusIndex + 1, _settings.Radii.Count - 1);
            _nonImprovingAttempts = 0;
            return DrawLocal();
        }

        _nonImprovingAttempts++;
        if (_nonImprovingAttempts >= _settings.NonImprovingAttemptsBeforeExplore)
        {
            _nonImprovingAttempts = 0;
            _radiusIndex = 0;
            return new SearchCandidate(DrawGlobal(), _settings.Radii[0], true);
        }

        return DrawLocal();
    }

    private SearchCandidate DrawLocal()
    {
        var anchor = _anchor ?? throw new InvalidOperationException("Initial proposal must be evaluated before local search.");
        var radius = _settings.Radii[_radiusIndex];
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var latitude = Math.Clamp(anchor.LatitudeDegrees + DrawNonZeroOffset(radius), -90, 90);
            var longitude = Math.Clamp(anchor.LongitudeDegrees + DrawNonZeroOffset(radius), -180, 180);
            var candidate = new GeoCoordinate(latitude, longitude);
            if (candidate != anchor) return new SearchCandidate(candidate, radius, false);
        }

        var fallbackLatitude = anchor.LatitudeDegrees == 90 ? 89 : anchor.LatitudeDegrees + 1;
        return new SearchCandidate(new GeoCoordinate(fallbackLatitude, anchor.LongitudeDegrees), radius, false);
    }

    private GeoCoordinate DrawGlobal() => new(_random.Next(-90, 91), _random.Next(-180, 181));
    private int DrawNonZeroOffset(int radius)
    {
        var offset = _random.Next(-radius, radius + 1);
        return offset == 0 ? 1 : offset;
    }
}

public sealed class RandomCoordinateSearchPolicy : ICoordinateSearchPolicy
{
    private readonly Random _random;
    public RandomCoordinateSearchPolicy(SearchSettings settings) { settings.Validate(); _random = new Random(settings.Seed); }
    public SearchCandidate ProposeInitial() => Draw();
    public SearchCandidate ProposeNext(SearchFeedback previous) => Draw();
    private SearchCandidate Draw() => new(new GeoCoordinate(_random.Next(-90, 91), _random.Next(-180, 181)), SearchSettings.DefaultMutationRadiiDegrees[0], true);
}

public sealed class AutonomousSearchRunner
{
    public async Task<SearchResult> RunAsync(
        GeoCoordinate target,
        ICoordinateSearchPolicy policy,
        ICoordinateCandidateEvaluator evaluator,
        SearchSettings settings,
        IProgress<SearchIteration>? progress = null,
        IProgress<CoordinateDecodeProgress>? neuralProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy); ArgumentNullException.ThrowIfNull(evaluator); ArgumentNullException.ThrowIfNull(settings); settings.Validate();
        var stopwatch = Stopwatch.StartNew();
        var trace = new List<SearchIteration>(settings.MaximumIterations);
        var history = new Queue<SearchIteration>(settings.HistoryLimit);
        SearchCandidate candidate = policy.ProposeInitial();
        double? anchorReward = null, anchorError = null, bestReward = null, bestError = null;
        GeoCoordinate? bestDecoded = null;

        try
        {
            for (var iterationNumber = 1; iterationNumber <= settings.MaximumIterations; iterationNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CandidateEvaluation evaluation;
                try { evaluation = await evaluator.EvaluateAsync(candidate.Coordinate, neuralProgress, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    return Complete(target, SearchTerminationReason.EvaluatorFailure, trace, history, settings.HistoryLimit, bestDecoded, bestReward, bestError, stopwatch.Elapsed,
                        new SearchIteration(iterationNumber, candidate.Coordinate, null, null, null, false, bestDecoded, bestReward, bestError, candidate.RadiusDegrees, candidate.IsExploration, TimeSpan.Zero, exception.Message), progress);
                }

                var decoded = evaluation.NeuralDecode.Decoding.Coordinate;
                if (decoded is null)
                {
                    return Complete(target, SearchTerminationReason.DecodeFailure, trace, history, settings.HistoryLimit, bestDecoded, bestReward, bestError, stopwatch.Elapsed,
                        new SearchIteration(iterationNumber, candidate.Coordinate, null, null, null, false, bestDecoded, bestReward, bestError, candidate.RadiusDegrees, candidate.IsExploration, evaluation.Elapsed, evaluation.NeuralDecode.Decoding.ErrorMessage), progress);
                }

                var rewardResult = RewardCalculator.Calculate(target, decoded.Value);
                var accepted = anchorReward is null || IsBetter(rewardResult.Reward, rewardResult.AngularDistanceDegrees, anchorReward.Value, anchorError!.Value);
                if (accepted) { anchorReward = rewardResult.Reward; anchorError = rewardResult.AngularDistanceDegrees; }
                if (bestReward is null || IsBetter(rewardResult.Reward, rewardResult.AngularDistanceDegrees, bestReward.Value, bestError!.Value))
                {
                    bestDecoded = decoded.Value; bestReward = rewardResult.Reward; bestError = rewardResult.AngularDistanceDegrees;
                }

                var feedback = new SearchFeedback(iterationNumber, candidate.Coordinate, decoded.Value, rewardResult.AngularDistanceDegrees, rewardResult.Reward, accepted, bestDecoded!.Value, bestReward!.Value, bestError!.Value, candidate.RadiusDegrees, accepted ? 0 : CountConsecutiveRejections(trace));
                var record = new SearchIteration(iterationNumber, candidate.Coordinate, decoded.Value, rewardResult.AngularDistanceDegrees, rewardResult.Reward, accepted, bestDecoded, bestReward, bestError, candidate.RadiusDegrees, candidate.IsExploration, evaluation.Elapsed);
                Add(trace, history, record, settings.HistoryLimit, progress);

                if (rewardResult.AngularDistanceDegrees == 0d || decoded.Value == target)
                    return new SearchResult(target, SearchTerminationReason.ExactSuccess, trace, history.ToArray(), bestDecoded, bestReward, bestError, stopwatch.Elapsed);
                if (iterationNumber == settings.MaximumIterations)
                    return new SearchResult(target, SearchTerminationReason.MaximumIterations, trace, history.ToArray(), bestDecoded, bestReward, bestError, stopwatch.Elapsed);
                candidate = policy.ProposeNext(feedback);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SearchResult(target, SearchTerminationReason.Cancelled, trace, history.ToArray(), bestDecoded, bestReward, bestError, stopwatch.Elapsed);
        }

        throw new InvalidOperationException("Search loop exited without a terminal result.");
    }

    private static bool IsBetter(double reward, double error, double baselineReward, double baselineError) => reward > baselineReward || (reward == baselineReward && error < baselineError);
    private static int CountConsecutiveRejections(IReadOnlyList<SearchIteration> trace)
    {
        var count = 1;
        for (var index = trace.Count - 1; index >= 0 && !trace[index].Accepted; index--) count++;
        return count;
    }
    private static void Add(List<SearchIteration> trace, Queue<SearchIteration> history, SearchIteration item, int historyLimit, IProgress<SearchIteration>? progress)
    {
        trace.Add(item); history.Enqueue(item); if (history.Count > historyLimit) history.Dequeue(); progress?.Report(item);
    }
    private static SearchResult Complete(GeoCoordinate target, SearchTerminationReason reason, List<SearchIteration> trace, Queue<SearchIteration> history, int historyLimit, GeoCoordinate? bestDecoded, double? bestReward, double? bestError, TimeSpan elapsed, SearchIteration record, IProgress<SearchIteration>? progress)
    {
        Add(trace, history, record, historyLimit, progress);
        return new SearchResult(target, reason, trace, history.ToArray(), bestDecoded, bestReward, bestError, elapsed);
    }
}
