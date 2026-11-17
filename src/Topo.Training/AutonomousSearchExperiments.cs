using System.Text.Json;
using Topo.Core;

namespace Topo.Training;

public static class AutonomousSearchEvaluationCorpus
{
    public static IReadOnlyList<GeoCoordinate> Create(int count = 50, int seed = 20260924)
    {
        if (count < 6) throw new ArgumentOutOfRangeException(nameof(count));
        var coordinates = new[] { "N35 W54", "N47 E22", "S23 E117", "N0 E0", "N90 E180", "S33 E111" }.Select(GeoCoordinate.Parse).ToList();
        var known = coordinates.ToHashSet(); var random = new Random(seed);
        while (coordinates.Count < count)
        {
            var coordinate = new GeoCoordinate(random.Next(-90, 91), random.Next(-180, 181));
            if (known.Add(coordinate)) coordinates.Add(coordinate);
        }
        return coordinates;
    }

    public static int DeriveSeed(int baseSeed, int index) => unchecked(baseSeed * 397 + index);
}

public sealed record SearchExperimentTarget(string Target, int Seed, SearchResult Result);
public sealed record SearchExperimentSummary(
    string Policy, int TargetCount, int SuccessCount, double SuccessRate,
    double? MeanIterationsToSuccess, double? MedianIterationsToSuccess,
    double MeanFinalAngularErrorDegrees, double MedianFinalAngularErrorDegrees,
    double MaximumFinalAngularErrorDegrees, double MeanBestAngularErrorDegrees,
    double MeanCandidateEvaluationMilliseconds, double TotalRuntimeMilliseconds);
public sealed record SearchExperimentComparison(
    SearchExperimentSummary HillClimbing, SearchExperimentSummary RandomBaseline,
    IReadOnlyList<SearchExperimentTarget> HillClimbingTargets,
    IReadOnlyList<SearchExperimentTarget> RandomBaselineTargets);
public sealed record SearchExperimentDirectComparison(
    int HillClimbingExactSuccessCount, int RandomBaselineExactSuccessCount,
    double ExactSuccessRateDifference, double MeanFinalAngularErrorDifferenceDegrees,
    double MeanBestAngularErrorDifferenceDegrees, double MeanCandidateEvaluationTimeDifferenceMilliseconds);
public sealed record PersistedSearchExperimentTarget(
    int TargetIndex, string Policy, string Target, int Seed, string InitialCandidate,
    bool IsSuccess, string Termination, int Iterations, int Evaluations,
    string? BestDecodedCoordinate, double? BestAngularErrorDegrees, double? BestReward,
    string? FinalDecodedCoordinate, double? FinalAngularErrorDegrees, double ElapsedMilliseconds,
    IReadOnlyList<PersistedSearchIteration> Trace);
public sealed record PersistedSearchIteration(
    int Iteration, string ProposedCoordinate, string? DecodedCoordinate,
    double? AngularErrorDegrees, double? Reward, bool Accepted,
    string? BestDecodedCoordinate, double? BestReward, double? BestAngularErrorDegrees,
    int RadiusDegrees, bool IsExploration, double EvaluationMilliseconds, string? FailureMessage);

/// <summary>
/// Runs the existing target-owning search runner for the fixed corpus. Persisted runs
/// atomically checkpoint each completed target, so a real-service interruption can
/// resume without changing policy sequences or re-evaluating completed targets.
/// </summary>
public sealed class AutonomousSearchExperimentRunner
{
    private const int ArtifactSchemaVersion = 2;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public async Task<SearchExperimentComparison> RunAsync(
        IReadOnlyList<GeoCoordinate> targets, ICoordinateCandidateEvaluator evaluator,
        SearchSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets); ArgumentNullException.ThrowIfNull(evaluator); settings.Validate();
        var hill = await RunPolicyAsync("hill-climbing", targets, evaluator, settings, false, null, cancellationToken);
        var random = await RunPolicyAsync("random-baseline", targets, evaluator, settings, true, null, cancellationToken);
        return CreateComparison(hill, random);
    }

    public async Task<SearchExperimentComparison> RunPersistedAsync(
        IReadOnlyList<GeoCoordinate> targets, ICoordinateCandidateEvaluator evaluator, SearchSettings settings,
        string rootDirectory, string runName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets); ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory); ArgumentException.ThrowIfNullOrWhiteSpace(runName);
        settings.Validate();
        var directory = Path.Combine(rootDirectory, runName);
        Directory.CreateDirectory(directory);
        await EnsureRunMetadataAsync(directory, targets, settings, cancellationToken);
        var hill = await RunPolicyAsync("hill-climbing", targets, evaluator, settings, false, directory, cancellationToken);
        var random = await RunPolicyAsync("random-baseline", targets, evaluator, settings, true, directory, cancellationToken);
        var comparison = CreateComparison(hill, random);
        await WriteSummaryAsync(directory, comparison, cancellationToken);
        return comparison;
    }

    public async Task<string> PersistAsync(SearchExperimentComparison comparison, IReadOnlyList<GeoCoordinate> targets, SearchSettings settings, string rootDirectory, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(rootDirectory, $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}-seed-{settings.Seed}");
        Directory.CreateDirectory(directory);
        await EnsureRunMetadataAsync(directory, targets, settings, cancellationToken);
        await PersistCompletedTargetsAsync(directory, "hill-climbing", comparison.HillClimbingTargets, cancellationToken);
        await PersistCompletedTargetsAsync(directory, "random-baseline", comparison.RandomBaselineTargets, cancellationToken);
        await WriteSummaryAsync(directory, comparison, cancellationToken);
        return directory;
    }

    public static SearchExperimentDirectComparison Compare(SearchExperimentComparison comparison) => new(
        comparison.HillClimbing.SuccessCount, comparison.RandomBaseline.SuccessCount,
        comparison.HillClimbing.SuccessRate - comparison.RandomBaseline.SuccessRate,
        comparison.HillClimbing.MeanFinalAngularErrorDegrees - comparison.RandomBaseline.MeanFinalAngularErrorDegrees,
        comparison.HillClimbing.MeanBestAngularErrorDegrees - comparison.RandomBaseline.MeanBestAngularErrorDegrees,
        comparison.HillClimbing.MeanCandidateEvaluationMilliseconds - comparison.RandomBaseline.MeanCandidateEvaluationMilliseconds);

    private static async Task<IReadOnlyList<SearchExperimentTarget>> RunPolicyAsync(
        string policy, IReadOnlyList<GeoCoordinate> targets, ICoordinateCandidateEvaluator evaluator,
        SearchSettings settings, bool randomBaseline, string? persistedDirectory, CancellationToken cancellationToken)
    {
        var results = new List<SearchExperimentTarget>(targets.Count);
        for (var index = 0; index < targets.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var perTargetSettings = settings with { Seed = AutonomousSearchEvaluationCorpus.DeriveSeed(settings.Seed, index) };
            var checkpointPath = persistedDirectory is null ? null : TargetPath(persistedDirectory, policy, index);
            var checkpoint = checkpointPath is null ? null : await ReadCheckpointAsync(checkpointPath, policy, index, targets[index], perTargetSettings.Seed, cancellationToken);
            if (checkpoint is not null)
            {
                results.Add(ToExperimentTarget(checkpoint, settings.HistoryLimit));
                continue;
            }

            ICoordinateSearchPolicy searchPolicy = randomBaseline
                ? new RandomCoordinateSearchPolicy(perTargetSettings)
                : new StochasticHillClimbingSearchPolicy(perTargetSettings);
            var result = await new AutonomousSearchRunner().RunAsync(targets[index], searchPolicy, evaluator, perTargetSettings, cancellationToken: cancellationToken);
            var completed = new SearchExperimentTarget(targets[index].ToString(), perTargetSettings.Seed, result);
            if (checkpointPath is not null)
            {
                await WriteAtomicJsonAsync(checkpointPath, ToPersisted(index, policy, completed), cancellationToken);
            }
            results.Add(completed);
        }
        return results;
    }

    private static SearchExperimentComparison CreateComparison(IReadOnlyList<SearchExperimentTarget> hill, IReadOnlyList<SearchExperimentTarget> random) => new(
        SearchExperimentSummaryFor("hill-climbing", hill), SearchExperimentSummaryFor("random-baseline", random), hill, random);

    private static async Task EnsureRunMetadataAsync(string directory, IReadOnlyList<GeoCoordinate> targets, SearchSettings settings, CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(directory, "config.json");
        var configuration = new SearchExperimentConfiguration(ArtifactSchemaVersion, settings.Seed, settings.MaximumIterations, settings.Radii.ToArray(),
            settings.NonImprovingAttemptsBeforeExplore, settings.HistoryLimit, targets.Count, "token-readout-v1.json", "reference", "unsigned");
        if (File.Exists(configPath))
        {
            var existing = JsonSerializer.Deserialize<SearchExperimentConfiguration>(await File.ReadAllTextAsync(configPath, cancellationToken), Json)
                ?? throw new InvalidDataException("Experiment config is unreadable.");
            if (existing.SchemaVersion != configuration.SchemaVersion || existing.Seed != configuration.Seed || existing.MaximumIterations != configuration.MaximumIterations ||
                !existing.MutationRadiiDegrees.SequenceEqual(configuration.MutationRadiiDegrees) || existing.NonImprovingAttemptsBeforeExplore != configuration.NonImprovingAttemptsBeforeExplore ||
                existing.HistoryLimit != configuration.HistoryLimit || existing.TargetCount != configuration.TargetCount || existing.FrozenModel != configuration.FrozenModel ||
                existing.ExecutionMode != configuration.ExecutionMode || existing.TransmitterMode != configuration.TransmitterMode)
            {
                throw new InvalidOperationException("Existing experiment checkpoint configuration does not match this run.");
            }
        }
        else
        {
            await WriteAtomicJsonAsync(configPath, configuration, cancellationToken);
        }

        var targetRecords = targets.Select((target, index) => new SearchExperimentTargetSeed(index, target.ToString(), AutonomousSearchEvaluationCorpus.DeriveSeed(settings.Seed, index))).ToArray();
        var targetsPath = Path.Combine(directory, "targets.json");
        if (File.Exists(targetsPath))
        {
            var existing = JsonSerializer.Deserialize<SearchExperimentTargetSeed[]>(await File.ReadAllTextAsync(targetsPath, cancellationToken), Json)
                ?? throw new InvalidDataException("Experiment target list is unreadable.");
            if (!existing.SequenceEqual(targetRecords)) throw new InvalidOperationException("Existing experiment checkpoint targets do not match this run.");
        }
        else
        {
            await WriteAtomicJsonAsync(targetsPath, targetRecords, cancellationToken);
        }
    }

    private static async Task PersistCompletedTargetsAsync(string directory, string policy, IReadOnlyList<SearchExperimentTarget> targets, CancellationToken cancellationToken)
    {
        for (var index = 0; index < targets.Count; index++)
        {
            await WriteAtomicJsonAsync(TargetPath(directory, policy, index), ToPersisted(index, policy, targets[index]), cancellationToken);
        }
    }

    private static async Task<PersistedSearchExperimentTarget?> ReadCheckpointAsync(string path, string policy, int index, GeoCoordinate target, int seed, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var checkpoint = JsonSerializer.Deserialize<PersistedSearchExperimentTarget>(await File.ReadAllTextAsync(path, cancellationToken), Json)
            ?? throw new InvalidDataException($"Experiment checkpoint '{path}' is unreadable.");
        if (checkpoint.Policy != policy || checkpoint.TargetIndex != index || checkpoint.Target != target.ToString() || checkpoint.Seed != seed)
        {
            throw new InvalidDataException($"Experiment checkpoint '{path}' does not match the requested target.");
        }
        return checkpoint;
    }

    private static string TargetPath(string directory, string policy, int index) => Path.Combine(directory, policy, "targets", $"{index:D3}.json");

    private static PersistedSearchExperimentTarget ToPersisted(int index, string policy, SearchExperimentTarget target)
    {
        var trace = target.Result.Trace;
        var final = trace.LastOrDefault();
        return new(index, policy, target.Target, target.Seed, trace.FirstOrDefault()?.ProposedCoordinate.ToString() ?? target.Target,
            target.Result.IsSuccess, target.Result.Termination.ToString(), target.Result.IterationsUsed, target.Result.IterationsUsed,
            target.Result.BestDecodedCoordinate?.ToString(), target.Result.BestAngularErrorDegrees, target.Result.BestReward,
            final?.DecodedCoordinate?.ToString(), final?.AngularErrorDegrees, target.Result.Elapsed.TotalMilliseconds,
            trace.Select(item => new PersistedSearchIteration(item.Iteration, item.ProposedCoordinate.ToString(), item.DecodedCoordinate?.ToString(), item.AngularErrorDegrees,
                item.Reward, item.Accepted, item.BestDecodedCoordinate?.ToString(), item.BestReward, item.BestAngularErrorDegrees, item.RadiusDegrees,
                item.IsExploration, item.EvaluationElapsed.TotalMilliseconds, item.FailureMessage)).ToArray());
    }

    private static SearchExperimentTarget ToExperimentTarget(PersistedSearchExperimentTarget persisted, int historyLimit)
    {
        var trace = persisted.Trace.Select(item => new SearchIteration(item.Iteration, GeoCoordinate.Parse(item.ProposedCoordinate), ParseCoordinate(item.DecodedCoordinate),
            item.AngularErrorDegrees, item.Reward, item.Accepted, ParseCoordinate(item.BestDecodedCoordinate), item.BestReward, item.BestAngularErrorDegrees,
            item.RadiusDegrees, item.IsExploration, TimeSpan.FromMilliseconds(item.EvaluationMilliseconds), item.FailureMessage)).ToArray();
        var termination = Enum.Parse<SearchTerminationReason>(persisted.Termination, ignoreCase: false);
        var history = trace.Skip(Math.Max(0, trace.Length - historyLimit)).ToArray();
        return new SearchExperimentTarget(persisted.Target, persisted.Seed, new SearchResult(GeoCoordinate.Parse(persisted.Target), termination, trace, history,
            ParseCoordinate(persisted.BestDecodedCoordinate), persisted.BestReward, persisted.BestAngularErrorDegrees, TimeSpan.FromMilliseconds(persisted.ElapsedMilliseconds)));
    }

    private static GeoCoordinate? ParseCoordinate(string? text) => text is null ? null : GeoCoordinate.Parse(text);

    private static SearchExperimentSummary SearchExperimentSummaryFor(string policy, IReadOnlyList<SearchExperimentTarget> targets)
    {
        var successful = targets.Where(target => target.Result.IsSuccess).Select(target => (double)target.Result.IterationsUsed).OrderBy(value => value).ToArray();
        var finalErrors = targets.Select(target => target.Result.Trace.LastOrDefault()?.AngularErrorDegrees ?? 180d).OrderBy(error => error).ToArray();
        var bestErrors = targets.Select(target => target.Result.BestAngularErrorDegrees ?? 180d).ToArray();
        var candidateMilliseconds = targets.SelectMany(target => target.Result.Trace).Select(trace => trace.EvaluationElapsed.TotalMilliseconds).ToArray();
        return new(policy, targets.Count, successful.Length, targets.Count == 0 ? 0 : (double)successful.Length / targets.Count,
            successful.Length == 0 ? null : successful.Average(), successful.Length == 0 ? null : Median(successful),
            finalErrors.Average(), Median(finalErrors), finalErrors.Max(), bestErrors.Average(),
            candidateMilliseconds.Length == 0 ? 0 : candidateMilliseconds.Average(), targets.Sum(target => target.Result.Elapsed.TotalMilliseconds));
    }

    private static async Task WriteSummaryAsync(string directory, SearchExperimentComparison comparison, CancellationToken cancellationToken) =>
        await WriteAtomicJsonAsync(Path.Combine(directory, "summary.json"), new { comparison.HillClimbing, comparison.RandomBaseline, DirectComparison = Compare(comparison) }, cancellationToken);

    private static async Task WriteAtomicJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Artifact path has no directory."));
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(value, Json), cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static double Median(IReadOnlyList<double> values) => values.Count == 0 ? 0 : values.Count % 2 == 0
        ? (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2 : values[values.Count / 2];

    private sealed record SearchExperimentConfiguration(int SchemaVersion, int Seed, int MaximumIterations, int[] MutationRadiiDegrees,
        int NonImprovingAttemptsBeforeExplore, int HistoryLimit, int TargetCount, string FrozenModel, string ExecutionMode, string TransmitterMode);
    private sealed record SearchExperimentTargetSeed(int Index, string Target, int Seed);
}
