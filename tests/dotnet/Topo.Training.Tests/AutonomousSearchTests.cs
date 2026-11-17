using Topo.Core;
using Xunit;

namespace Topo.Training.Tests;

public sealed class AutonomousSearchTests
{
    [Fact]
    public void Settings_DefaultsAreFrozenPhaseNineValues()
    {
        var settings = new SearchSettings();
        Assert.Equal(250, settings.MaximumIterations); Assert.Equal(12, settings.NonImprovingAttemptsBeforeExplore); Assert.Equal(100, settings.HistoryLimit);
        Assert.Equal(new[] { 30, 15, 8, 4, 2, 1 }, settings.Radii);
    }

    [Fact]
    public void HillClimber_SameSeedAndFeedbackProducesSameProposals()
    {
        var left = new StochasticHillClimbingSearchPolicy(new SearchSettings(17));
        var right = new StochasticHillClimbingSearchPolicy(new SearchSettings(17));
        var firstLeft = left.ProposeInitial(); var firstRight = right.ProposeInitial(); Assert.Equal(firstLeft, firstRight);
        var feedback = Feedback(firstLeft.Coordinate, accepted: true);
        Assert.Equal(left.ProposeNext(feedback), right.ProposeNext(feedback));
    }

    [Fact]
    public void Policies_OnlyExposeTargetFreeInputs()
    {
        Assert.DoesNotContain(typeof(ICoordinateSearchPolicy).GetMethods().SelectMany(method => method.GetParameters()), parameter => parameter.ParameterType == typeof(GeoCoordinate));
        Assert.DoesNotContain(typeof(SearchFeedback).GetProperties(), property => property.Name.Contains("Target", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HillClimber_GeneratesValidCoordinatesAndShrinksRadiusAfterImprovement()
    {
        var policy = new StochasticHillClimbingSearchPolicy(new SearchSettings(19));
        var first = policy.ProposeInitial();
        Assert.InRange(first.Coordinate.LatitudeDegrees, -90, 90); Assert.InRange(first.Coordinate.LongitudeDegrees, -180, 180);
        var afterInitial = policy.ProposeNext(Feedback(first.Coordinate, accepted: true));
        var afterImprovement = policy.ProposeNext(Feedback(afterInitial.Coordinate, accepted: true));
        Assert.Equal(30, afterInitial.RadiusDegrees); Assert.Equal(15, afterImprovement.RadiusDegrees);
    }

    [Fact]
    public void HillClimber_RejectionsPreserveLocalRadiusThenExploreAfterTwelve()
    {
        var policy = new StochasticHillClimbingSearchPolicy(new SearchSettings(23));
        var candidate = policy.ProposeInitial();
        candidate = policy.ProposeNext(Feedback(candidate.Coordinate, accepted: true));
        for (var rejection = 1; rejection <= 12; rejection++)
        {
            candidate = policy.ProposeNext(Feedback(candidate.Coordinate, accepted: false, consecutive: rejection));
        }
        Assert.True(candidate.IsExploration); Assert.Equal(30, candidate.RadiusDegrees);
    }

    [Fact]
    public async Task Runner_EvaluatesProposalThenScoresDecodedCoordinateAndStopsOnSuccess()
    {
        var target = GeoCoordinate.Parse("N35 W54");
        var evaluator = new FakeEvaluator(proposal => Decode(proposal, target));
        var policy = new FixedPolicy(new SearchCandidate(GeoCoordinate.Parse("S20 E30"), 30, true));
        var result = await new AutonomousSearchRunner().RunAsync(target, policy, evaluator, new SearchSettings(MaximumIterations: 10));
        Assert.True(result.IsSuccess); Assert.Equal(1, evaluator.Calls); Assert.Equal(target, result.Trace.Single().DecodedCoordinate); Assert.NotEqual(result.Trace.Single().ProposedCoordinate, result.Trace.Single().DecodedCoordinate);
    }

    [Fact]
    public async Task Runner_StopsAtLimitAndBoundsHistory()
    {
        var target = GeoCoordinate.Parse("N35 W54");
        var evaluator = new FakeEvaluator(proposal => Decode(proposal, GeoCoordinate.Parse("N0 E0")));
        var result = await new AutonomousSearchRunner().RunAsync(target, new RandomCoordinateSearchPolicy(new SearchSettings(29, MaximumIterations: 5, HistoryLimit: 2)), evaluator, new SearchSettings(29, MaximumIterations: 5, HistoryLimit: 2));
        Assert.Equal(SearchTerminationReason.MaximumIterations, result.Termination); Assert.Equal(5, evaluator.Calls); Assert.Equal(2, result.History.Count); Assert.Equal(5, result.Trace.Count);
    }

    [Fact]
    public async Task Runner_CancellationReturnsCompletedHistoryWithoutFurtherEvaluations()
    {
        using var cancellation = new CancellationTokenSource();
        var target = GeoCoordinate.Parse("N35 W54");
        var evaluator = new CancellingEvaluator(cancellation);
        var result = await new AutonomousSearchRunner().RunAsync(target, new RandomCoordinateSearchPolicy(new SearchSettings(31)), evaluator, new SearchSettings(31), cancellationToken: cancellation.Token);
        Assert.Equal(SearchTerminationReason.Cancelled, result.Termination); Assert.Equal(1, evaluator.Calls);
    }

    private static SearchFeedback Feedback(GeoCoordinate coordinate, bool accepted, int consecutive = 0) => new(1, coordinate, coordinate, 10, .5, accepted, coordinate, .5, 10, 30, consecutive);
    private static CoordinateNeuralDecode Decode(GeoCoordinate proposal, GeoCoordinate decoded) => new(proposal, proposal.ToTokens(), [], CoordinateSequenceDecoder.Decode(decoded.ToTokens()));

    private sealed class FakeEvaluator(Func<GeoCoordinate, CoordinateNeuralDecode> decode) : ICoordinateCandidateEvaluator
    {
        public int Calls { get; private set; }
        public Task<CandidateEvaluation> EvaluateAsync(GeoCoordinate proposal, IProgress<CoordinateDecodeProgress>? neuralProgress, CancellationToken cancellationToken)
        {
            Calls++; return Task.FromResult(new CandidateEvaluation(proposal, decode(proposal), TimeSpan.FromMilliseconds(1)));
        }
    }

    private sealed class CancellingEvaluator(CancellationTokenSource cancellation) : ICoordinateCandidateEvaluator
    {
        public int Calls { get; private set; }
        public Task<CandidateEvaluation> EvaluateAsync(GeoCoordinate proposal, IProgress<CoordinateDecodeProgress>? neuralProgress, CancellationToken cancellationToken)
        {
            Calls++; cancellation.Cancel(); return Task.FromCanceled<CandidateEvaluation>(cancellationToken);
        }
    }

    private sealed class FixedPolicy(SearchCandidate candidate) : ICoordinateSearchPolicy
    {
        public SearchCandidate ProposeInitial() => candidate;
        public SearchCandidate ProposeNext(SearchFeedback previous) => candidate;
    }
}
