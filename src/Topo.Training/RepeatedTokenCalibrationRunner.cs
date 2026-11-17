using FlyBrain.BrainClient;
using Topo.Core;

namespace Topo.Training;

public sealed record RepeatedTokenCalibrationExperiment(
    GeoCoordinateToken Token,
    long RootId,
    int EmptyStepGap);

public sealed record RepeatedTokenCalibrationResult(
    RepeatedTokenCalibrationExperiment Experiment,
    NeuronDiagnostic BeforeFirstStimulus,
    BrainStepResult FirstStimulus,
    NeuronDiagnostic AfterFirstStimulus,
    BrainStepResult? EmptyAdvance,
    NeuronDiagnostic BeforeSecondStimulus,
    BrainStepResult SecondStimulus,
    NeuronDiagnostic AfterSecondStimulus,
    BrainSessionState FinalState);

public sealed class RepeatedTokenCalibrationRunner(IBrainServiceClient brainServiceClient)
{
    public static IReadOnlyList<RepeatedTokenCalibrationExperiment> CreateExperiments(
        BrainChannelConfiguration configuration,
        IEnumerable<GeoCoordinateToken> tokens,
        IEnumerable<int> emptyStepGaps)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(emptyStepGaps);

        var gaps = emptyStepGaps.Distinct().OrderBy(gap => gap).ToArray();
        if (gaps.Any(gap => gap < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(emptyStepGaps), "Repeat gaps cannot be negative.");
        }

        return tokens
            .Distinct()
            .OrderBy(token => token)
            .SelectMany(token => configuration.GetChannel(token).OrderBy(rootId => rootId)
                .SelectMany(rootId => gaps.Select(gap => new RepeatedTokenCalibrationExperiment(token, rootId, gap))))
            .ToArray();
    }

    public async Task<IReadOnlyList<RepeatedTokenCalibrationResult>> ExecuteAsync(
        string sessionId,
        BrainChannelConfiguration configuration,
        IEnumerable<RepeatedTokenCalibrationExperiment> experiments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(experiments);

        var results = new List<RepeatedTokenCalibrationResult>();
        foreach (var experiment in experiments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!configuration.GetChannel(experiment.Token).Contains(experiment.RootId))
            {
                throw new InvalidOperationException($"Root ID {experiment.RootId} is not configured for token '{experiment.Token}'.");
            }

            if (experiment.EmptyStepGap < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(experiment.EmptyStepGap));
            }

            await brainServiceClient.ResetSessionAsync(sessionId, cancellationToken);
            var beforeFirst = await brainServiceClient.GetNeuronDiagnosticAsync(sessionId, experiment.RootId, cancellationToken);
            var first = await StimulateAsync(sessionId, experiment.RootId, configuration, cancellationToken);
            var afterFirst = await brainServiceClient.GetNeuronDiagnosticAsync(sessionId, experiment.RootId, cancellationToken);

            BrainStepResult? emptyAdvance = null;
            if (experiment.EmptyStepGap > 0)
            {
                emptyAdvance = await brainServiceClient.StepAsync(
                    sessionId,
                    new BrainStepRequest { Steps = experiment.EmptyStepGap, Stimulus = [] },
                    cancellationToken);
                EnsureCompleted(emptyAdvance);
            }

            var beforeSecond = await brainServiceClient.GetNeuronDiagnosticAsync(sessionId, experiment.RootId, cancellationToken);
            var second = await StimulateAsync(sessionId, experiment.RootId, configuration, cancellationToken);
            var afterSecond = await brainServiceClient.GetNeuronDiagnosticAsync(sessionId, experiment.RootId, cancellationToken);
            var finalState = await brainServiceClient.GetSessionStateAsync(sessionId, cancellationToken);
            EnsureCompleted(finalState.SafetyStatus);

            results.Add(new RepeatedTokenCalibrationResult(
                experiment,
                beforeFirst,
                first,
                afterFirst,
                emptyAdvance,
                beforeSecond,
                second,
                afterSecond,
                finalState));
        }

        return results;
    }

    private async Task<BrainStepResult> StimulateAsync(
        string sessionId,
        long rootId,
        BrainChannelConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var response = await brainServiceClient.StepAsync(
            sessionId,
            new BrainStepRequest
            {
                Steps = configuration.StimulusSteps,
                Stimulus = [new BrainStimulus(rootId, configuration.StimulusAmplitude)],
            },
            cancellationToken);
        EnsureCompleted(response);
        return response;
    }

    private static void EnsureCompleted(BrainStepResult response) => EnsureCompleted(response.SafetyStatus);

    private static void EnsureCompleted(string safetyStatus)
    {
        if (!string.Equals(safetyStatus, "completed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Brain Service reported unsafe status '{safetyStatus}'.");
        }
    }
}
