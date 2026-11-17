using Topo.Core;

namespace Topo.Training;

public sealed class CoordinateNeuralEncoder
{
    public CoordinateStimulusPlan Encode(GeoCoordinate coordinate, BrainChannelConfiguration configuration)
    {
        var entries = coordinate.ToTokens()
            .Select(token => new CoordinateTokenStimulus(
                token,
                configuration.GetChannel(token),
                configuration.StimulusAmplitude,
                configuration.StimulusSteps,
                configuration.SettleSteps))
            .ToArray();

        return new CoordinateStimulusPlan(entries);
    }
}

public sealed record CoordinateStimulusPlan(IReadOnlyList<CoordinateTokenStimulus> Entries);

public sealed record CoordinateTokenStimulus(
    GeoCoordinateToken Token,
    IReadOnlyList<long> RootIds,
    double StimulusAmplitude,
    int StimulusSteps,
    int SettleSteps);
