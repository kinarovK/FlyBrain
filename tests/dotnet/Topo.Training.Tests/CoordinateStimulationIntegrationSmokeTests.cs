using System.Net;
using System.Text.Json;
using FlyBrain.BrainClient;
using Topo.Core;
using Topo.Training;
using Xunit;
using Xunit.Abstractions;

namespace Topo.Training.Tests;

public sealed class CoordinateStimulationIntegrationSmokeTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task N35W54_ExecutesACompleteReferenceSessionSequence()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TOPO_RUN_BRAIN_SERVICE_SMOKE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var configurationPath = Environment.GetEnvironmentVariable("TOPO_BRAIN_CHANNELS_PATH")
            ?? throw new InvalidOperationException("TOPO_BRAIN_CHANNELS_PATH is required for the integration smoke test.");
        var configuration = BrainChannelConfiguration.LoadFromFile(configurationPath);
        var options = BrainServiceClientOptions.FromEnvironment();
        using var httpClient = new HttpClient { BaseAddress = options.BaseAddress };
        var client = new BrainServiceClient(httpClient);
        string? sessionId = null;

        try
        {
            var readiness = await client.GetReadinessAsync();
            Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
            Assert.True(readiness.IsReady);

            var session = await client.CreateSessionAsync(new CreateBrainSessionRequest
            {
                ExecutionMode = BrainExecutionMode.Reference,
                TransmitterMode = BrainTransmitterMode.Unsigned,
            });
            sessionId = session.SessionId;
            Assert.Equal("reference", session.ExecutionMode.ToString().ToLowerInvariant());

            await new BrainChannelSessionValidator(client).ValidateAsync(sessionId, configuration);
            await client.ResetSessionAsync(sessionId);

            var plan = new CoordinateNeuralEncoder().Encode(GeoCoordinate.Parse("N35 W54"), configuration);
            var run = await new CoordinateStimulusRunner(client).ExecuteAsync(sessionId, plan);
            var finalState = await client.GetSessionStateAsync(sessionId);

            Assert.Equal(7, run.Executions.Count);
            Assert.All(run.Executions, execution => Assert.NotNull(execution.Settle));
            Assert.Equal(plan.Entries.Sum(entry => entry.StimulusSteps + entry.SettleSteps), finalState.SimulationStep);
            Assert.Equal("completed", finalState.SafetyStatus);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                tokens = plan.Entries.Select(entry => entry.Token.ToString()).ToArray(),
                simulation_steps = finalState.SimulationStep,
                total_spikes = run.Executions.Sum(execution => execution.Stimulus.TotalSpikes + (execution.Settle?.TotalSpikes ?? 0)),
                unique_event_roots = run.Executions
                    .SelectMany(execution => execution.Stimulus.Events.Concat(execution.Settle?.Events ?? []))
                    .Select(@event => @event.RootId)
                    .Distinct()
                    .Count(),
                maximum_active_fraction = run.Executions.Max(execution => Math.Max(execution.Stimulus.ActiveFraction, execution.Settle?.ActiveFraction ?? 0)),
                events_truncated = run.Executions.Any(execution => execution.Stimulus.EventsTruncated || execution.Settle?.EventsTruncated == true),
                final_accumulated_spikes = finalState.TotalAccumulatedSpikes,
                safety_status = finalState.SafetyStatus,
            }));
        }
        finally
        {
            if (sessionId is not null)
            {
                await client.DeleteSessionAsync(sessionId);
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RepeatedDigitCoordinates_ProduceAnInputSpikeForEveryTokenPresentation()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TOPO_RUN_BRAIN_SERVICE_SMOKE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var configurationPath = Environment.GetEnvironmentVariable("TOPO_BRAIN_CHANNELS_PATH")
            ?? throw new InvalidOperationException("TOPO_BRAIN_CHANNELS_PATH is required for the integration smoke test.");
        var configuration = BrainChannelConfiguration.LoadFromFile(configurationPath);
        using var httpClient = new HttpClient { BaseAddress = BrainServiceClientOptions.FromEnvironment().BaseAddress };
        var client = new BrainServiceClient(httpClient);
        string? sessionId = null;

        try
        {
            sessionId = (await client.CreateSessionAsync(new CreateBrainSessionRequest
            {
                ExecutionMode = BrainExecutionMode.Reference,
                TransmitterMode = BrainTransmitterMode.Unsigned,
            })).SessionId;
            var records = new List<object>();
            foreach (var coordinateText in new[] { "N55 W11", "S33 E111", "N11 E11" })
            {
                await client.ResetSessionAsync(sessionId);
                var plan = new CoordinateNeuralEncoder().Encode(GeoCoordinate.Parse(coordinateText), configuration);
                var run = await new CoordinateStimulusRunner(client).ExecuteAsync(sessionId, plan);
                var spikes = run.Executions.Sum(execution => execution.Stimulus.TotalSpikes + (execution.Settle?.TotalSpikes ?? 0));
                Assert.Equal(plan.Entries.Count, spikes);
                records.Add(new
                {
                    coordinate = coordinateText,
                    expected_token_count = plan.Entries.Count,
                    observed_input_spike_count = spikes,
                    simulation_steps = plan.Entries.Sum(entry => entry.StimulusSteps + entry.SettleSteps),
                });
            }

            foreach (var pattern in new[]
            {
                new[] { GeoCoordinateToken.Digit5, GeoCoordinateToken.Digit5 },
                new[] { GeoCoordinateToken.Digit1, GeoCoordinateToken.Digit1 },
                new[] { GeoCoordinateToken.Digit1, GeoCoordinateToken.Digit1, GeoCoordinateToken.Digit1 },
            })
            {
                await client.ResetSessionAsync(sessionId);
                var plan = new CoordinateStimulusPlan(pattern
                    .Select(token => new CoordinateTokenStimulus(
                        token,
                        configuration.GetChannel(token),
                        configuration.StimulusAmplitude,
                        configuration.StimulusSteps,
                        configuration.SettleSteps))
                    .ToArray());
                var run = await new CoordinateStimulusRunner(client).ExecuteAsync(sessionId, plan);
                var spikes = run.Executions.Sum(execution => execution.Stimulus.TotalSpikes + (execution.Settle?.TotalSpikes ?? 0));
                Assert.Equal(plan.Entries.Count, spikes);
                records.Add(new
                {
                    token_pattern = string.Concat(pattern.Select(token => token == GeoCoordinateToken.Digit5 ? "5" : "1")),
                    expected_token_count = plan.Entries.Count,
                    observed_input_spike_count = spikes,
                });
            }

            output.WriteLine(JsonSerializer.Serialize(records));
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
