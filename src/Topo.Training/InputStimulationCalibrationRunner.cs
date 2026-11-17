using FlyBrain.BrainClient;
using Topo.Core;

namespace Topo.Training;

public sealed record InputStimulationCalibrationParameters(
    double StimulusAmplitude,
    int StimulusSteps,
    int SettleSteps)
{
    public void Validate()
    {
        if (!double.IsFinite(StimulusAmplitude) || StimulusAmplitude < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(StimulusAmplitude), "Stimulus amplitude must be finite and non-negative.");
        }

        if (StimulusSteps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(StimulusSteps), "Stimulus steps must be positive.");
        }

        if (SettleSteps < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SettleSteps), "Settle steps cannot be negative.");
        }
    }
}

public sealed record InputStimulationCalibrationExperiment(
    GeoCoordinateToken Token,
    long RootId,
    InputStimulationCalibrationParameters Parameters);

public sealed record InputStimulationCalibrationMeasurement(
    int TotalSpikes,
    int? UniqueSpikingNeurons,
    double ActiveFraction,
    int PeakSimultaneousSpikeCount,
    double PeakSimultaneousSpikeFraction,
    bool EventsTruncated,
    string SafetyStatus,
    int FinalSimulationStep,
    int FinalAccumulatedSpikes);

public sealed record InputStimulationCalibrationResult(
    InputStimulationCalibrationExperiment Experiment,
    InputStimulationCalibrationMeasurement? Measurement,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Succeeded => Measurement is not null;
}

public sealed class InputStimulationCalibrationRunner(IBrainServiceClient brainServiceClient)
{
    public static IReadOnlyList<InputStimulationCalibrationExperiment> CreateGrid(
        BrainChannelConfiguration configuration,
        IEnumerable<GeoCoordinateToken> tokens,
        IEnumerable<double> amplitudes,
        IEnumerable<int> stimulusSteps,
        IEnumerable<int> settleSteps)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(amplitudes);
        ArgumentNullException.ThrowIfNull(stimulusSteps);
        ArgumentNullException.ThrowIfNull(settleSteps);

        var orderedTokens = tokens.Distinct().OrderBy(token => token).ToArray();
        var orderedAmplitudes = amplitudes.Distinct().OrderBy(value => value).ToArray();
        var orderedStimulusSteps = stimulusSteps.Distinct().OrderBy(value => value).ToArray();
        var orderedSettleSteps = settleSteps.Distinct().OrderBy(value => value).ToArray();
        var experiments = new List<InputStimulationCalibrationExperiment>();

        foreach (var token in orderedTokens)
        {
            foreach (var rootId in configuration.GetChannel(token).OrderBy(rootId => rootId))
            {
                foreach (var amplitude in orderedAmplitudes)
                {
                    foreach (var stimulusDuration in orderedStimulusSteps)
                    {
                        foreach (var settleDuration in orderedSettleSteps)
                        {
                            var parameters = new InputStimulationCalibrationParameters(amplitude, stimulusDuration, settleDuration);
                            parameters.Validate();
                            experiments.Add(new InputStimulationCalibrationExperiment(token, rootId, parameters));
                        }
                    }
                }
            }
        }

        return experiments;
    }

    public async Task<IReadOnlyList<InputStimulationCalibrationResult>> ExecuteAsync(
        string sessionId,
        IEnumerable<InputStimulationCalibrationExperiment> experiments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(experiments);

        var results = new List<InputStimulationCalibrationResult>();
        var resourceLimitedAmplitudes = new Dictionary<long, double>();
        foreach (var experiment in experiments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            experiment.Parameters.Validate();

            if (resourceLimitedAmplitudes.TryGetValue(experiment.RootId, out var limitedAmplitude) &&
                experiment.Parameters.StimulusAmplitude > limitedAmplitude)
            {
                results.Add(new InputStimulationCalibrationResult(
                    experiment,
                    null,
                    "SKIPPED_AFTER_RESOURCE_LIMIT",
                    $"Skipped because amplitude {limitedAmplitude} for root {experiment.RootId} exceeded a Brain Service resource limit."));
                continue;
            }

            try
            {
                await brainServiceClient.ResetSessionAsync(sessionId, cancellationToken);
                var stimulus = await brainServiceClient.StepAsync(
                    sessionId,
                    new BrainStepRequest
                    {
                        Steps = experiment.Parameters.StimulusSteps,
                        Stimulus = [new BrainStimulus(experiment.RootId, experiment.Parameters.StimulusAmplitude)],
                    },
                    cancellationToken);
                EnsureCompleted(stimulus);

                BrainStepResult finalResponse = stimulus;
                if (experiment.Parameters.SettleSteps > 0)
                {
                    finalResponse = await brainServiceClient.StepAsync(
                        sessionId,
                        new BrainStepRequest { Steps = experiment.Parameters.SettleSteps, Stimulus = [] },
                        cancellationToken);
                    EnsureCompleted(finalResponse);
                }

                var state = await brainServiceClient.GetSessionStateAsync(sessionId, cancellationToken);
                EnsureCompleted(state.SafetyStatus);
                results.Add(new InputStimulationCalibrationResult(
                    experiment,
                    Combine(stimulus, finalResponse, state),
                    null,
                    null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (BrainServiceException exception)
            {
                if (string.Equals(exception.Code, "RESOURCE_LIMIT_EXCEEDED", StringComparison.Ordinal))
                {
                    resourceLimitedAmplitudes[experiment.RootId] = resourceLimitedAmplitudes.TryGetValue(experiment.RootId, out var currentLimit)
                        ? Math.Min(currentLimit, experiment.Parameters.StimulusAmplitude)
                        : experiment.Parameters.StimulusAmplitude;
                }

                results.Add(new InputStimulationCalibrationResult(experiment, null, exception.Code, exception.Message));
            }
            catch (Exception exception)
            {
                results.Add(new InputStimulationCalibrationResult(experiment, null, null, exception.Message));
            }
        }

        return results;
    }

    private static InputStimulationCalibrationMeasurement Combine(
        BrainStepResult stimulus,
        BrainStepResult finalResponse,
        BrainSessionState state) => new(
        state.TotalAccumulatedSpikes,
        UniqueSpikingNeurons(stimulus, finalResponse),
        Math.Max(stimulus.ActiveFraction, finalResponse.ActiveFraction),
        Math.Max(stimulus.PeakSimultaneousSpikeCount, finalResponse.PeakSimultaneousSpikeCount),
        Math.Max(stimulus.PeakSimultaneousSpikeFraction, finalResponse.PeakSimultaneousSpikeFraction),
        stimulus.EventsTruncated || finalResponse.EventsTruncated,
        state.SafetyStatus,
        state.SimulationStep,
        state.TotalAccumulatedSpikes);

    private static int? UniqueSpikingNeurons(BrainStepResult stimulus, BrainStepResult finalResponse)
    {
        if (stimulus.EventsTruncated || finalResponse.EventsTruncated)
        {
            return null;
        }

        return stimulus.Events
            .Concat(ReferenceEquals(stimulus, finalResponse) ? [] : finalResponse.Events)
            .Select(@event => @event.RootId)
            .Distinct()
            .Count();
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
