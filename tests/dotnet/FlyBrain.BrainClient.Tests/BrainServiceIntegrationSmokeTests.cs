using System.Net;
using FlyBrain.BrainClient;
using Xunit;

namespace FlyBrain.BrainClient.Tests;

public sealed class BrainServiceIntegrationSmokeTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReferenceSessionLifecycleSucceeds()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("TOPO_RUN_BRAIN_SERVICE_SMOKE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var options = BrainServiceClientOptions.FromEnvironment();
        using var httpClient = new HttpClient { BaseAddress = options.BaseAddress };
        var client = new BrainServiceClient(httpClient);
        string? sessionId = null;

        try
        {
            var readiness = await client.GetReadinessAsync();
            Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
            Assert.True(readiness.IsReady);

            var brainInfo = await client.GetBrainInfoAsync();
            Assert.Equal("v1", brainInfo.ApiVersion);

            var session = await client.CreateSessionAsync(new CreateBrainSessionRequest
            {
                ExecutionMode = BrainExecutionMode.Reference,
                TransmitterMode = BrainTransmitterMode.Unsigned,
            });
            sessionId = session.SessionId;

            var step = await client.StepAsync(sessionId, new BrainStepRequest
            {
                Steps = 1,
                Stimulus = [],
            });
            Assert.Equal(1, step.StepsExecuted);

            var state = await client.GetSessionStateAsync(sessionId);
            Assert.Equal(1, state.SimulationStep);

            var reset = await client.ResetSessionAsync(sessionId);
            Assert.Equal(0, reset.SimulationStep);
        }
        finally
        {
            if (sessionId is not null)
            {
                await client.DeleteSessionAsync(sessionId);
            }
        }
    }
}
