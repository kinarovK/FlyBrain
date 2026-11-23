using System.Net;
using System.Text.Json;
using FlyBrain.BrainClient;
using Topo.Core;
using Topo.Training;
using Xunit;
using Xunit.Abstractions;

namespace Topo.Training.Tests;

public sealed class InputStimulationCalibrationIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AllConfiguredChannels_RunTheConservativeAmplitudeSweep()
    {
        if (!FlyBrain.TestSupport.RealBrainGate.IsEnabled("TOPO_RUN_BRAIN_SERVICE_SMOKE"))
        {
            return;
        }

        var configurationPath = Environment.GetEnvironmentVariable("TOPO_BRAIN_CHANNELS_PATH")
            ?? throw new InvalidOperationException("TOPO_BRAIN_CHANNELS_PATH is required for integration calibration.");
        var configuration = BrainChannelConfiguration.LoadFromFile(configurationPath);
        using var httpClient = new HttpClient { BaseAddress = BrainServiceClientOptions.FromEnvironment().BaseAddress };
        var client = new BrainServiceClient(httpClient);
        string? sessionId = null;

        try
        {
            Assert.True((await client.GetReadinessAsync()).IsReady);
            sessionId = (await client.CreateSessionAsync(new CreateBrainSessionRequest
            {
                ExecutionMode = BrainExecutionMode.Reference,
                TransmitterMode = BrainTransmitterMode.Unsigned,
            })).SessionId;

            var experiments = InputStimulationCalibrationRunner.CreateGrid(
                configuration,
                Enum.GetValues<GeoCoordinateToken>(),
                [2, 5, 10, 20, 50, 100],
                [1],
                [1]);
            var results = await new InputStimulationCalibrationRunner(client).ExecuteAsync(sessionId, experiments);

            Assert.Equal(90, results.Count);
            output.WriteLine(JsonSerializer.Serialize(results
                .GroupBy(result => result.Experiment.Parameters)
                .Select(group => new
                {
                    group.Key,
                    active_channels = group.Count(result => result.Measurement?.TotalSpikes > 0),
                    distinct_measurements = group.Select(result => result.Measurement).Distinct().ToArray(),
                })));
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
    public async Task AllConfiguredChannels_RunTheTimingSweepAtTheLowestActiveAmplitude()
    {
        if (!FlyBrain.TestSupport.RealBrainGate.IsEnabled("TOPO_RUN_BRAIN_SERVICE_SMOKE"))
        {
            return;
        }

        var configuration = LoadConfiguration();
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

            var experiments = InputStimulationCalibrationRunner.CreateGrid(
                configuration,
                Enum.GetValues<GeoCoordinateToken>(),
                [100],
                [1, 2, 5, 10],
                [1, 2, 5, 10]);
            var results = await new InputStimulationCalibrationRunner(client).ExecuteAsync(sessionId, experiments);

            Assert.Equal(240, results.Count);
            Assert.All(results, result => Assert.True(result.Succeeded));
            output.WriteLine(JsonSerializer.Serialize(results
                .GroupBy(result => result.Experiment.Parameters)
                .Select(group => new
                {
                    group.Key,
                    active_channels = group.Count(result => result.Measurement?.TotalSpikes > 0),
                    distinct_measurements = group.Select(result => result.Measurement).Distinct().ToArray(),
                })));
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
    public async Task ChosenWeakConfiguration_ReplaysExactlyAfterReset()
    {
        if (!FlyBrain.TestSupport.RealBrainGate.IsEnabled("TOPO_RUN_BRAIN_SERVICE_SMOKE"))
        {
            return;
        }

        var configuration = LoadConfiguration();
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
            var oncePerChannel = InputStimulationCalibrationRunner.CreateGrid(
                configuration,
                Enum.GetValues<GeoCoordinateToken>(),
                [100],
                [1],
                [1]);
            var results = await new InputStimulationCalibrationRunner(client).ExecuteAsync(
                sessionId,
                Enumerable.Range(0, 3).SelectMany(_ => oncePerChannel));

            Assert.Equal(45, results.Count);
            var expected = results.Take(15).Select(result => result.Measurement).ToArray();
            for (var replay = 1; replay < 3; replay++)
            {
                Assert.Equal(expected, results.Skip(replay * 15).Take(15).Select(result => result.Measurement));
            }

            output.WriteLine(JsonSerializer.Serialize(expected));
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
    public async Task RepresentativeChannels_RunTheRepeatGapAndDiagnosticSweep()
    {
        if (!FlyBrain.TestSupport.RealBrainGate.IsEnabled("TOPO_RUN_BRAIN_SERVICE_SMOKE"))
        {
            return;
        }

        var configuration = LoadConfiguration();
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
            var experiments = RepeatedTokenCalibrationRunner.CreateExperiments(
                configuration,
                [GeoCoordinateToken.Digit5, GeoCoordinateToken.North, GeoCoordinateToken.Separator],
                [0, 1, 2, 5, 10, 20, 21, 22, 50]);
            var results = await new RepeatedTokenCalibrationRunner(client).ExecuteAsync(sessionId, configuration, experiments);

            Assert.Equal(27, results.Count);
            Assert.All(results, result => Assert.Equal("completed", result.FinalState.SafetyStatus));
            output.WriteLine(JsonSerializer.Serialize(results.Select(result => new
            {
                token = result.Experiment.Token,
                gap = result.Experiment.EmptyStepGap,
                first_spikes = result.FirstStimulus.TotalSpikes,
                first_events = result.FirstStimulus.Events,
                after_first_refractory = result.AfterFirstStimulus.RefractorySteps,
                before_second_refractory = result.BeforeSecondStimulus.RefractorySteps,
                second_spikes = result.SecondStimulus.TotalSpikes,
                second_events = result.SecondStimulus.Events,
                after_second_refractory = result.AfterSecondStimulus.RefractorySteps,
                cumulative_spikes = result.AfterSecondStimulus.CumulativeSpikeCount,
                safety = result.FinalState.SafetyStatus,
            })));
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
    public async Task MinimumRepeatGap_ReplaysExactlyThreeTimes()
    {
        if (!FlyBrain.TestSupport.RealBrainGate.IsEnabled("TOPO_RUN_BRAIN_SERVICE_SMOKE"))
        {
            return;
        }

        var configuration = LoadConfiguration();
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
            var once = RepeatedTokenCalibrationRunner.CreateExperiments(
                configuration,
                [GeoCoordinateToken.Digit5, GeoCoordinateToken.North, GeoCoordinateToken.Separator],
                [22]);
            var results = await new RepeatedTokenCalibrationRunner(client).ExecuteAsync(
                sessionId,
                configuration,
                Enumerable.Range(0, 3).SelectMany(_ => once));

            Assert.Equal(9, results.Count);
            var expected = results.Take(3).Select(ReplaySignature).ToArray();
            for (var replay = 1; replay < 3; replay++)
            {
                Assert.Equal(expected, results.Skip(replay * 3).Take(3).Select(ReplaySignature));
            }

            output.WriteLine(JsonSerializer.Serialize(expected));
        }
        finally
        {
            if (sessionId is not null)
            {
                await client.DeleteSessionAsync(sessionId);
            }
        }
    }

    private static BrainChannelConfiguration LoadConfiguration()
    {
        var configurationPath = Environment.GetEnvironmentVariable("TOPO_BRAIN_CHANNELS_PATH")
            ?? throw new InvalidOperationException("TOPO_BRAIN_CHANNELS_PATH is required for integration calibration.");
        return BrainChannelConfiguration.LoadFromFile(configurationPath);
    }

    private static object ReplaySignature(RepeatedTokenCalibrationResult result) => new
    {
        token = result.Experiment.Token,
        first_spikes = result.FirstStimulus.TotalSpikes,
        second_spikes = result.SecondStimulus.TotalSpikes,
        after_first_refractory = result.AfterFirstStimulus.RefractorySteps,
        before_second_refractory = result.BeforeSecondStimulus.RefractorySteps,
        after_second_refractory = result.AfterSecondStimulus.RefractorySteps,
        cumulative_spikes = result.AfterSecondStimulus.CumulativeSpikeCount,
        simulation_step = result.FinalState.SimulationStep,
        accumulated_spikes = result.FinalState.TotalAccumulatedSpikes,
        safety = result.FinalState.SafetyStatus,
    };
}
