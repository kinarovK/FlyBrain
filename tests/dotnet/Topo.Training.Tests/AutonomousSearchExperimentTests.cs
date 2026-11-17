using Topo.Core;
using Xunit;

namespace Topo.Training.Tests;

public sealed class AutonomousSearchExperimentTests
{
    [Fact]
    public void Corpus_IsDeterministicAndContainsRequiredTargets()
    {
        var first = AutonomousSearchEvaluationCorpus.Create(); var second = AutonomousSearchEvaluationCorpus.Create();
        Assert.Equal(50, first.Count); Assert.Equal(first, second);
        foreach (var target in new[] { "N35 W54", "N47 E22", "S23 E117", "N0 E0", "N90 E180", "S33 E111" }) Assert.Contains(GeoCoordinate.Parse(target), first);
    }

    [Fact]
    public async Task Experiment_UsesEqualBudgetForHillClimbingAndRandomBaseline()
    {
        var evaluator = new ConstantEvaluator(); var settings = new SearchSettings(37, MaximumIterations: 3);
        var comparison = await new AutonomousSearchExperimentRunner().RunAsync(AutonomousSearchEvaluationCorpus.Create(6), evaluator, settings);
        Assert.Equal(comparison.HillClimbingTargets.Select(item => item.Result.IterationsUsed), comparison.RandomBaselineTargets.Select(item => item.Result.IterationsUsed));
        Assert.Equal(32, evaluator.Calls);
    }

    [Fact]
    public async Task PersistedExperiment_CheckpointsEachTarget_AndResumesWithoutReevaluation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "topo-phase9-" + Guid.NewGuid().ToString("N"));
        try
        {
            var targets = AutonomousSearchEvaluationCorpus.Create(6);
            var settings = new SearchSettings(20260924, MaximumIterations: 2);
            var firstEvaluator = new ConstantEvaluator();
            var runner = new AutonomousSearchExperimentRunner();
            var first = await runner.RunPersistedAsync(targets, firstEvaluator, settings, directory, "resume-test");

            Assert.Equal(
                first.HillClimbingTargets.Sum(item => item.Result.IterationsUsed) + first.RandomBaselineTargets.Sum(item => item.Result.IterationsUsed),
                firstEvaluator.Calls);
            Assert.Equal(6, Directory.GetFiles(Path.Combine(directory, "resume-test", "hill-climbing", "targets"), "*.json").Length);
            Assert.Equal(6, Directory.GetFiles(Path.Combine(directory, "resume-test", "random-baseline", "targets"), "*.json").Length);
            Assert.True(File.Exists(Path.Combine(directory, "resume-test", "summary.json")));

            var resumedEvaluator = new ThrowingEvaluator();
            var resumed = await runner.RunPersistedAsync(targets, resumedEvaluator, settings, directory, "resume-test");

            Assert.Equal(0, resumedEvaluator.Calls);
            Assert.Equal(first.HillClimbingTargets.Select(item => item.Result.Trace.Count), resumed.HillClimbingTargets.Select(item => item.Result.Trace.Count));
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunPersistedAsync(targets, resumedEvaluator, settings with { MaximumIterations = 3 }, directory, "resume-test"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ConstantEvaluator : ICoordinateCandidateEvaluator
    {
        public int Calls { get; private set; }
        public Task<CandidateEvaluation> EvaluateAsync(GeoCoordinate proposal, IProgress<CoordinateDecodeProgress>? neuralProgress, CancellationToken cancellationToken)
        {
            Calls++; var decoded = GeoCoordinate.Parse("N0 E0");
            return Task.FromResult(new CandidateEvaluation(proposal, new CoordinateNeuralDecode(proposal, proposal.ToTokens(), [], CoordinateSequenceDecoder.Decode(decoded.ToTokens())), TimeSpan.FromMilliseconds(1)));
        }
    }

    private sealed class ThrowingEvaluator : ICoordinateCandidateEvaluator
    {
        public int Calls { get; private set; }
        public Task<CandidateEvaluation> EvaluateAsync(GeoCoordinate proposal, IProgress<CoordinateDecodeProgress>? neuralProgress, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("A resumed checkpoint should avoid evaluator calls.");
        }
    }
}
