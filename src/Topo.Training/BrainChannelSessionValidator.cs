using FlyBrain.BrainClient;

namespace Topo.Training;

public sealed class BrainChannelSessionValidator(IBrainServiceClient brainServiceClient)
{
    public async Task ValidateAsync(
        string sessionId,
        BrainChannelConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        foreach (var rootId in configuration.Channels.Values.SelectMany(channel => channel))
        {
            await brainServiceClient.GetNeuronDiagnosticAsync(sessionId, rootId, cancellationToken);
        }
    }
}
