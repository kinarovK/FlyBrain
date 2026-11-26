using System.Net;
using FlyBrain.BrainClient;
using Topo.Core;
using Topo.Training;
using Xunit;

namespace Topo.Training.Tests;

public sealed class InputStimulationCalibrationRunnerTests
{
    [Fact]
    public void Grid_IsCompleteDeduplicatedAndDeterministicallyOrdered()
    {
        var grid = InputStimulationCalibrationRunner.CreateGrid(
            CreateConfiguration(),
            [GeoCoordinateToken.Digit5, GeoCoordinateToken.North, GeoCoordinateToken.North],
            [20, 2, 20],
            [2, 1],
            [5, 1]);

        Assert.Equal(16, grid.Count);
        Assert.Equal(GeoCoordinateToken.North, grid[0].Token);
        Assert.Equal(2, grid[0].Parameters.StimulusAmplitude);
        Assert.Equal(1, grid[0].Parameters.StimulusSteps);
        Assert.Equal(1, grid[0].Parameters.SettleSteps);
        Assert.Equal(GeoCoordinateToken.Digit5, grid[^1].Token);
        Assert.Equal(20, grid[^1].Parameters.StimulusAmplitude);
        Assert.Equal(2, grid[^1].Parameters.StimulusSteps);
        Assert.Equal(5, grid[^1].Parameters.SettleSteps);
    }

    [Fact]
    public async Task Execute_ResetsBeforeEveryIndependentSample()
    {
        var client = new CalibrationClient();
        var experiments = CreateExperiments(2);

        var results = await new InputStimulationCalibrationRunner(client).ExecuteAsync("session", experiments);

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(["reset", "stimulus", "settle", "state", "reset", "stimulus", "settle", "state"], client.Calls);
    }

    [Fact]
    public async Task Execute_RecordsASingleServiceFailureAndContinuesWithTheNextIndependentSample()
    {
        var client = new CalibrationClient { FailStimulusCall = 1 };

        var results = await new InputStimulationCalibrationRunner(client).ExecuteAsync("session", CreateExperiments(2));

        Assert.False(results[0].Succeeded);
        Assert.Equal("RESOURCE_LIMIT_EXCEEDED", results[0].ErrorCode);
        Assert.True(results[1].Succeeded);
        Assert.Equal(["reset", "stimulus", "reset", "stimulus", "settle", "state"], client.Calls);
    }

    [Fact]
    public async Task Execute_DoesNotEscalateAmplitudeAfterAResourceLimitForTheSameRoot()
    {
        var client = new CalibrationClient { FailStimulusCall = 1 };
        var experiments = new[]
        {
            new InputStimulationCalibrationExperiment(GeoCoordinateToken.North, 1001, new InputStimulationCalibrationParameters(20, 1, 1)),
            new InputStimulationCalibrationExperiment(GeoCoordinateToken.North, 1001, new InputStimulationCalibrationParameters(50, 1, 1)),
            new InputStimulationCalibrationExperiment(GeoCoordinateToken.Digit5, 1002, new InputStimulationCalibrationParameters(50, 1, 1)),
        };

        var results = await new InputStimulationCalibrationRunner(client).ExecuteAsync("session", experiments);

        Assert.Equal("RESOURCE_LIMIT_EXCEEDED", results[0].ErrorCode);
        Assert.Equal("SKIPPED_AFTER_RESOURCE_LIMIT", results[1].ErrorCode);
        Assert.True(results[2].Succeeded);
        Assert.Equal(["reset", "stimulus", "reset", "stimulus", "settle", "state"], client.Calls);
    }

    [Fact]
    public async Task Execute_RecordsUnsafeStimulusWithoutIssuingSettleOrStateCalls()
    {
        var client = new CalibrationClient { UnsafeStimulusCall = 1 };

        var result = Assert.Single(await new InputStimulationCalibrationRunner(client).ExecuteAsync("session", CreateExperiments(1)));

        Assert.False(result.Succeeded);
        Assert.Null(result.ErrorCode);
        Assert.Equal(["reset", "stimulus"], client.Calls);
    }

    [Fact]
    public async Task Execute_StopsImmediatelyWhenCancellationIsRequested()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new CalibrationClient { AfterReset = () => cancellation.Cancel() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new InputStimulationCalibrationRunner(client).ExecuteAsync("session", CreateExperiments(2), cancellation.Token));

        Assert.Equal(["reset"], client.Calls);
    }

    private static IReadOnlyList<InputStimulationCalibrationExperiment> CreateExperiments(int count) =>
        Enumerable.Range(0, count)
            .Select(index => new InputStimulationCalibrationExperiment(
                index == 0 ? GeoCoordinateToken.North : GeoCoordinateToken.Digit5,
                1001 + index,
                new InputStimulationCalibrationParameters(20, 1, 1)))
            .ToArray();

    private static BrainChannelConfiguration CreateConfiguration() => new(
        1,
        "reference",
        "unsigned",
        2,
        1,
        1,
        Enum.GetValues<GeoCoordinateToken>()
            .Select((token, index) => new KeyValuePair<GeoCoordinateToken, IReadOnlyList<long>>(token, [1001L + index]))
            .ToDictionary(pair => pair.Key, pair => pair.Value));

    private sealed class CalibrationClient : IBrainServiceClient
    {
        private int stimulusCalls;

        public List<string> Calls { get; } = [];
        public int? FailStimulusCall { get; init; }
        public int? UnsafeStimulusCall { get; init; }
        public Action? AfterReset { get; init; }

        public Task<BrainResetResult> ResetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("reset");
            AfterReset?.Invoke();
            return Task.FromResult(new BrainResetResult { SessionId = sessionId, Status = "reset", SimulationStep = 0, ResetWallSeconds = 0 });
        }

        public Task<BrainStepResult> StepAsync(string sessionId, BrainStepRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isStimulus = request.Stimulus?.Count > 0;
            Calls.Add(isStimulus ? "stimulus" : "settle");
            if (isStimulus)
            {
                stimulusCalls++;
                if (FailStimulusCall == stimulusCalls)
                {
                    throw new BrainServiceException(HttpStatusCode.TooManyRequests, new BrainServiceError
                    {
                        Code = "RESOURCE_LIMIT_EXCEEDED",
                        Message = "Resource limit exceeded.",
                        Details = null,
                    });
                }
            }

            return Task.FromResult(new BrainStepResult
            {
                ApiVersion = "v1",
                SessionId = sessionId,
                StartingStep = 0,
                EndingStep = request.Steps,
                SimulatedMilliseconds = 0.1,
                StepsExecuted = request.Steps,
                TotalSpikes = 1,
                UniqueSpikingNeurons = 1,
                ActiveFraction = 0.001,
                PeakSimultaneousSpikeCount = 1,
                PeakSimultaneousSpikeFraction = 0.001,
                SafetyStatus = UnsafeStimulusCall == stimulusCalls && isStimulus ? "unsafe" : "completed",
                Events = [new BrainStepEvent(1, 1001)],
                EventsTruncated = false,
                ReturnedEventCount = 1,
                EngineWallSeconds = 0,
            });
        }

        public Task<BrainSessionState> GetSessionStateAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("state");
            return Task.FromResult(new BrainSessionState
            {
                SessionId = sessionId,
                SimulationStep = 2,
                SimulatedMilliseconds = 0.2,
                TotalAccumulatedSpikes = 2,
                ActiveNeuronCount = 0,
                SafetyStatus = "completed",
                MembraneMillivolts = new BrainMembraneSummary { Minimum = -70, Mean = -65, Maximum = -60 },
            });
        }

        public Task<BrainReadiness> GetReadinessAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BrainInfo> GetBrainInfoAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BrainSession> CreateSessionAsync(CreateBrainSessionRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SessionRootsPage> GetSessionRootsAsync(string sessionId, int offset = 0, int limit = 64, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SessionLayoutPage> GetSessionLayoutAsync(string sessionId, int offset = 0, int limit = 1024, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NeuronDiagnostic> GetNeuronDiagnosticAsync(string sessionId, long rootId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BrainReadoutSnapshot> ReadoutAsync(string sessionId, IReadOnlyList<long> rootIds, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
