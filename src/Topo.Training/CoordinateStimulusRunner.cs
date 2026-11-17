using FlyBrain.BrainClient;

namespace Topo.Training;

public sealed class CoordinateStimulusRunner(IBrainServiceClient brainServiceClient)
{
    public async Task<CoordinateStimulusRun> ExecuteAsync(
        string sessionId,
        CoordinateStimulusPlan plan,
        CancellationToken cancellationToken = default)
    {
        var executions = new List<CoordinateTokenExecution>();
        foreach (var entry in plan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stimulus = await brainServiceClient.StepAsync(
                sessionId,
                new BrainStepRequest
                {
                    Steps = entry.StimulusSteps,
                    Stimulus = entry.RootIds.Select(rootId => new BrainStimulus(rootId, entry.StimulusAmplitude)).ToArray(),
                },
                cancellationToken);
            EnsureSafe(stimulus);

            BrainStepResult? settle = null;
            if (entry.SettleSteps > 0)
            {
                settle = await brainServiceClient.StepAsync(
                    sessionId,
                    new BrainStepRequest { Steps = entry.SettleSteps, Stimulus = Array.Empty<BrainStimulus>() },
                    cancellationToken);
                EnsureSafe(settle);
            }

            executions.Add(new CoordinateTokenExecution(entry.Token, stimulus, settle));
        }

        return new CoordinateStimulusRun(executions);
    }

    private static void EnsureSafe(BrainStepResult response)
    {
        if (!string.Equals(response.SafetyStatus, "completed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Brain Service reported unsafe status '{response.SafetyStatus}'.");
        }
    }
}

public sealed record CoordinateStimulusRun(IReadOnlyList<CoordinateTokenExecution> Executions);

public sealed record CoordinateTokenExecution(
    Topo.Core.GeoCoordinateToken Token,
    BrainStepResult Stimulus,
    BrainStepResult? Settle);
