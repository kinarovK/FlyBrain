using FlyBrain.BrainClient;
using Topo.Core;
using Topo.Training;
using Xunit;

namespace Topo.Training.Tests;

public sealed class RepeatedTokenCalibrationRunnerTests
{
    [Fact]
    public void CreateExperiments_IsDeterministicAndRejectsNegativeGaps()
    {
        var configuration = CreateConfiguration();

        var experiments = RepeatedTokenCalibrationRunner.CreateExperiments(
            configuration,
            [GeoCoordinateToken.Separator, GeoCoordinateToken.North, GeoCoordinateToken.North],
            [22, 0, 22]);

        Assert.Equal(4, experiments.Count);
        Assert.Equal(
            [(GeoCoordinateToken.North, 0), (GeoCoordinateToken.North, 22), (GeoCoordinateToken.Separator, 0), (GeoCoordinateToken.Separator, 22)],
            experiments.Select(experiment => (experiment.Token, experiment.EmptyStepGap)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RepeatedTokenCalibrationRunner.CreateExperiments(
            configuration,
            [GeoCoordinateToken.North],
            [-1]));
    }

    [Fact]
    public async Task Execute_UsesOneContinuousSessionPerSampleAndCapturesDiagnostics()
    {
        var client = new RepeatedTokenClient();
        var experiment = new RepeatedTokenCalibrationExperiment(GeoCoordinateToken.North, 1001, 22);

        var result = Assert.Single(await new RepeatedTokenCalibrationRunner(client).ExecuteAsync(
            "session",
            CreateConfiguration(),
            [experiment]));

        Assert.Equal(1, result.FirstStimulus.TotalSpikes);
        Assert.Equal(1, result.SecondStimulus.TotalSpikes);
        Assert.NotNull(result.EmptyAdvance);
        Assert.Equal(["reset", "diagnostic", "stimulus", "diagnostic", "empty", "diagnostic", "stimulus", "diagnostic", "state"], client.Calls);
    }

    [Fact]
    public async Task Execute_ResetsBetweenIndependentRepeatGapSamples()
    {
        var client = new RepeatedTokenClient();
        var experiments = new[]
        {
            new RepeatedTokenCalibrationExperiment(GeoCoordinateToken.North, 1001, 0),
            new RepeatedTokenCalibrationExperiment(GeoCoordinateToken.North, 1001, 22),
        };

        var results = await new RepeatedTokenCalibrationRunner(client).ExecuteAsync("session", CreateConfiguration(), experiments);

        Assert.Equal(2, results.Count);
        Assert.Equal(2, client.Calls.Count(call => call == "reset"));
    }

    [Fact]
    public async Task Execute_StopsWhenCancellationIsRequested()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new RepeatedTokenClient { AfterReset = () => cancellation.Cancel() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RepeatedTokenCalibrationRunner(client).ExecuteAsync(
            "session",
            CreateConfiguration(),
            [new RepeatedTokenCalibrationExperiment(GeoCoordinateToken.North, 1001, 22)],
            cancellation.Token));

        Assert.Equal(["reset"], client.Calls);
    }

    private static BrainChannelConfiguration CreateConfiguration() => new(
        1,
        "reference",
        "unsigned",
        100,
        1,
        22,
        Enum.GetValues<GeoCoordinateToken>()
            .Select((token, index) => new KeyValuePair<GeoCoordinateToken, IReadOnlyList<long>>(token, [1001L + index]))
            .ToDictionary(pair => pair.Key, pair => pair.Value));

    private sealed class RepeatedTokenClient : IBrainServiceClient
    {
        public List<string> Calls { get; } = [];
        public Action? AfterReset { get; init; }

        public Task<BrainResetResult> ResetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("reset");
            AfterReset?.Invoke();
            return Task.FromResult(new BrainResetResult { SessionId = sessionId, Status = "reset", SimulationStep = 0, ResetWallSeconds = 0 });
        }

        public Task<NeuronDiagnostic> GetNeuronDiagnosticAsync(string sessionId, long rootId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("diagnostic");
            return Task.FromResult(new NeuronDiagnostic
            {
                SessionId = sessionId,
                RootId = rootId,
                MembraneMillivolts = -52,
                RefractorySteps = 0,
                CumulativeSpikeCount = 1,
                FirstSpikeStep = 0,
                Note = "test",
            });
        }

        public Task<BrainStepResult> StepAsync(string sessionId, BrainStepRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stimulus = request.Stimulus?.Count > 0;
            Calls.Add(stimulus ? "stimulus" : "empty");
            return Task.FromResult(new BrainStepResult
            {
                ApiVersion = "v1",
                SessionId = sessionId,
                StartingStep = 0,
                EndingStep = request.Steps,
                SimulatedMilliseconds = 0.1,
                StepsExecuted = request.Steps,
                TotalSpikes = stimulus ? 1 : 0,
                UniqueSpikingNeurons = stimulus ? 1 : 0,
                ActiveFraction = stimulus ? 0.001 : 0,
                PeakSimultaneousSpikeCount = stimulus ? 1 : 0,
                PeakSimultaneousSpikeFraction = stimulus ? 0.001 : 0,
                SafetyStatus = "completed",
                Events = stimulus ? [new BrainStepEvent(0, request.Stimulus![0].RootId)] : [],
                EventsTruncated = false,
                ReturnedEventCount = stimulus ? 1 : 0,
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
                SimulationStep = 24,
                SimulatedMilliseconds = 2.4,
                TotalAccumulatedSpikes = 2,
                ActiveNeuronCount = 0,
                SafetyStatus = "completed",
                MembraneMillivolts = new BrainMembraneSummary { Minimum = -70, Mean = -65, Maximum = -52 },
            });
        }

        public Task<BrainReadiness> GetReadinessAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BrainInfo> GetBrainInfoAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BrainSession> CreateSessionAsync(CreateBrainSessionRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SessionRootsPage> GetSessionRootsAsync(string sessionId, int offset = 0, int limit = 64, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BrainReadoutSnapshot> ReadoutAsync(string sessionId, IReadOnlyList<long> rootIds, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
