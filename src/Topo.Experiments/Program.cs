using FlyBrain.BrainClient;
using Topo.Training;

var repositoryRoot = FindRepositoryRoot();
var runName = ParseRunName(args);
var artifactRoot = Path.Combine(repositoryRoot, "modules", "topo", "experiments", "autonomous-search-v1");
var settings = new SearchSettings();
var targets = AutonomousSearchEvaluationCorpus.Create(50, settings.Seed);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
    Console.WriteLine("Cancellation requested; completed targets have already been checkpointed.");
};

try
{
    var options = BrainServiceClientOptions.FromEnvironment();
    using var httpClient = new HttpClient { BaseAddress = options.BaseAddress };
    await using var decoder = new OwnedBrainSessionDecodeService(new BrainServiceClient(httpClient), repositoryRoot);
    var status = await decoder.ConnectAsync(cancellation.Token);
    if (!string.Equals(status.State, "READY", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"Brain Service is {status.State}: {status.Detail}");
        return 2;
    }

    Console.WriteLine($"Phase 9 experiment: {targets.Count} targets, 250 evaluations/target/policy, run '{runName}'.");
    var comparison = await new AutonomousSearchExperimentRunner().RunPersistedAsync(
        targets, decoder, settings, artifactRoot, runName, cancellation.Token);
    Console.WriteLine($"Completed. Hill exact: {comparison.HillClimbing.SuccessCount}/{comparison.HillClimbing.TargetCount}; random exact: {comparison.RandomBaseline.SuccessCount}/{comparison.RandomBaseline.TargetCount}.");
    Console.WriteLine($"Artifacts: {Path.Combine(artifactRoot, runName)}");
    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Experiment cancelled. Re-run with the same --run-name to resume from checkpoints.");
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Experiment failed: {exception.Message}");
    return 1;
}

static string FindRepositoryRoot() => FlyBrain.Common.RepositoryLocator.FindRoot();

static string ParseRunName(string[] arguments)
{
    if (arguments.Length == 0) return "phase9-50-target-seed-20260924";
    if (arguments.Length == 2 && string.Equals(arguments[0], "--run-name", StringComparison.Ordinal))
    {
        var value = arguments[1];
        if (value.Length > 80 || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("--run-name must be a simple directory name of 80 characters or fewer.");
        }
        return value;
    }
    throw new ArgumentException("Usage: Topo.Experiments [--run-name simple-name]");
}
