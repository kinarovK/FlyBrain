using FlyBrain.BrainClient;
using Topo.Core;
using Topo.Training;
using Xunit;

namespace Topo.Training.Tests;

public sealed class NeuralChannelTests
{
    [Fact]
    public void Configuration_AcceptsACompleteUniqueMapping()
    {
        var configuration = CreateValidConfiguration();

        Assert.Equal(15, configuration.Channels.Count);
        Assert.Equal(1001, Assert.Single(configuration.GetChannel(GeoCoordinateToken.North)));
    }

    [Fact]
    public void Configuration_RejectsInvalidMappingsAndParameters()
    {
        var missing = CreateChannels();
        missing.Remove(GeoCoordinateToken.Separator);
        Assert.Throws<InvalidOperationException>(() => CreateConfiguration(missing));

        var empty = CreateChannels();
        empty[GeoCoordinateToken.North] = [];
        Assert.Throws<InvalidOperationException>(() => CreateConfiguration(empty));

        var duplicateInChannel = CreateChannels();
        duplicateInChannel[GeoCoordinateToken.North] = [1001, 1001];
        Assert.Throws<InvalidOperationException>(() => CreateConfiguration(duplicateInChannel));

        var sharedBetweenChannels = CreateChannels();
        sharedBetweenChannels[GeoCoordinateToken.South] = [1001];
        Assert.Throws<InvalidOperationException>(() => CreateConfiguration(sharedBetweenChannels));

        Assert.Throws<InvalidOperationException>(() => CreateConfiguration(CreateChannels(), amplitude: double.NaN));
        Assert.Throws<InvalidOperationException>(() => CreateConfiguration(CreateChannels(), amplitude: double.PositiveInfinity));
        Assert.Throws<InvalidOperationException>(() => CreateConfiguration(CreateChannels(), stimulusSteps: 0));
        Assert.Throws<InvalidOperationException>(() => CreateConfiguration(CreateChannels(), stimulusSteps: -1));
        Assert.Throws<InvalidOperationException>(() => CreateConfiguration(CreateChannels(), settleSteps: -1));
    }

    [Fact]
    public void Encoder_PreservesTheN35W54TokenAndChannelOrder()
    {
        var plan = new CoordinateNeuralEncoder().Encode(GeoCoordinate.Parse("N35 W54"), CreateValidConfiguration());

        Assert.Equal(
            [GeoCoordinateToken.North, GeoCoordinateToken.Digit3, GeoCoordinateToken.Digit5, GeoCoordinateToken.Separator, GeoCoordinateToken.West, GeoCoordinateToken.Digit5, GeoCoordinateToken.Digit4],
            plan.Entries.Select(entry => entry.Token));
        Assert.Equal([1001L, 1008L, 1010L, 1015L, 1004L, 1010L, 1009L], plan.Entries.Select(entry => Assert.Single(entry.RootIds)));
    }

    [Theory]
    [InlineData("N5 E7", new[] { GeoCoordinateToken.North, GeoCoordinateToken.Digit5, GeoCoordinateToken.Separator, GeoCoordinateToken.East, GeoCoordinateToken.Digit7 })]
    [InlineData("S23 E117", new[] { GeoCoordinateToken.South, GeoCoordinateToken.Digit2, GeoCoordinateToken.Digit3, GeoCoordinateToken.Separator, GeoCoordinateToken.East, GeoCoordinateToken.Digit1, GeoCoordinateToken.Digit1, GeoCoordinateToken.Digit7 })]
    public void Encoder_HandlesShortAndThreeDigitCoordinates(string coordinateText, GeoCoordinateToken[] expected)
    {
        var plan = new CoordinateNeuralEncoder().Encode(GeoCoordinate.Parse(coordinateText), CreateValidConfiguration());

        Assert.Equal(expected, plan.Entries.Select(entry => entry.Token));
    }

    [Fact]
    public void Encoder_PreservesConsecutiveRepeatedTokensAndConfiguredSettleDuration()
    {
        var configuration = CreateConfiguration(CreateChannels(), settleSteps: 22);
        var plan = new CoordinateNeuralEncoder().Encode(GeoCoordinate.Parse("N55 W11"), configuration);

        Assert.Equal(
            [GeoCoordinateToken.North, GeoCoordinateToken.Digit5, GeoCoordinateToken.Digit5, GeoCoordinateToken.Separator, GeoCoordinateToken.West, GeoCoordinateToken.Digit1, GeoCoordinateToken.Digit1],
            plan.Entries.Select(entry => entry.Token));
        Assert.Equal(7, plan.Entries.Count);
        Assert.All(plan.Entries, entry => Assert.Equal(22, entry.SettleSteps));
        Assert.Equal(1010, Assert.Single(plan.Entries[1].RootIds));
        Assert.Equal(1010, Assert.Single(plan.Entries[2].RootIds));
    }

    [Fact]
    public async Task Runner_ExecutesStimulusAndSettleWindowsInOrder()
    {
        var fakeClient = new RecordingBrainServiceClient();
        var plan = new CoordinateNeuralEncoder().Encode(GeoCoordinate.Parse("N5 E7"), CreateValidConfiguration());

        var run = await new CoordinateStimulusRunner(fakeClient).ExecuteAsync("session-1", plan);

        Assert.Equal(5, run.Executions.Count);
        Assert.Equal(10, fakeClient.StepRequests.Count);
        Assert.Equal([true, false, true, false, true, false, true, false, true, false], fakeClient.StepRequests.Select(request => request.Stimulus!.Count > 0));
        Assert.Equal([1001L, 1010L, 1015L, 1003L, 1012L], fakeClient.StepRequests.Where(request => request.Stimulus!.Count > 0).Select(request => Assert.Single(request.Stimulus!).RootId));
    }

    [Fact]
    public async Task Runner_StopsAfterABrainServiceFailure()
    {
        var fakeClient = new RecordingBrainServiceClient { FailAtCall = 2 };
        var plan = new CoordinateNeuralEncoder().Encode(GeoCoordinate.Parse("N5 E7"), CreateValidConfiguration());

        await Assert.ThrowsAsync<InvalidOperationException>(() => new CoordinateStimulusRunner(fakeClient).ExecuteAsync("session-1", plan));

        Assert.Equal(2, fakeClient.StepRequests.Count);
    }

    [Fact]
    public async Task Runner_ExecutesConsecutiveDuplicateTokensAsDistinctStimulusOperations()
    {
        var fakeClient = new RecordingBrainServiceClient();
        var plan = new CoordinateNeuralEncoder().Encode(
            GeoCoordinate.Parse("N55 W11"),
            CreateConfiguration(CreateChannels(), settleSteps: 22));

        var run = await new CoordinateStimulusRunner(fakeClient).ExecuteAsync("session-1", plan);

        Assert.Equal(7, run.Executions.Count);
        var stimulusRoots = fakeClient.StepRequests
            .Where(request => request.Stimulus!.Count > 0)
            .Select(request => Assert.Single(request.Stimulus!).RootId)
            .ToArray();
        Assert.Equal([1001L, 1010L, 1010L, 1015L, 1004L, 1006L, 1006L], stimulusRoots);
        Assert.Equal(14, fakeClient.StepRequests.Count);
    }

    [Fact]
    public async Task Runner_StopsWhenCancellationIsRequested()
    {
        using var cancellation = new CancellationTokenSource();
        var fakeClient = new RecordingBrainServiceClient { AfterCall = call => { if (call == 1) cancellation.Cancel(); } };
        var plan = new CoordinateNeuralEncoder().Encode(GeoCoordinate.Parse("N5 E7"), CreateValidConfiguration());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new CoordinateStimulusRunner(fakeClient).ExecuteAsync("session-1", plan, cancellation.Token));

        Assert.Single(fakeClient.StepRequests);
    }

    private static BrainChannelConfiguration CreateValidConfiguration() => CreateConfiguration(CreateChannels());

    private static BrainChannelConfiguration CreateConfiguration(
        Dictionary<GeoCoordinateToken, IReadOnlyList<long>> channels,
        double amplitude = 2.0,
        int stimulusSteps = 1,
        int settleSteps = 1) =>
        new(1, "reference", "unsigned", amplitude, stimulusSteps, settleSteps, channels);

    private static Dictionary<GeoCoordinateToken, IReadOnlyList<long>> CreateChannels() =>
        Enum.GetValues<GeoCoordinateToken>()
            .Select((token, index) => new KeyValuePair<GeoCoordinateToken, IReadOnlyList<long>>(token, [1001L + index]))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

    private sealed class RecordingBrainServiceClient : IBrainServiceClient
    {
        public List<BrainStepRequest> StepRequests { get; } = [];
        public int? FailAtCall { get; init; }
        public Action<int>? AfterCall { get; init; }

        public Task<BrainReadiness> GetReadinessAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BrainInfo> GetBrainInfoAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BrainSession> CreateSessionAsync(CreateBrainSessionRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BrainSessionState> GetSessionStateAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SessionRootsPage> GetSessionRootsAsync(string sessionId, int offset = 0, int limit = 64, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SessionLayoutPage> GetSessionLayoutAsync(string sessionId, int offset = 0, int limit = 1024, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NeuronDiagnostic> GetNeuronDiagnosticAsync(string sessionId, long rootId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BrainReadoutSnapshot> ReadoutAsync(string sessionId, IReadOnlyList<long> rootIds, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BrainResetResult> ResetSessionAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<BrainStepResult> StepAsync(string sessionId, BrainStepRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StepRequests.Add(request);
            var call = StepRequests.Count;
            if (FailAtCall == call)
            {
                throw new InvalidOperationException("Brain Service failure.");
            }

            AfterCall?.Invoke(call);
            return Task.FromResult(new BrainStepResult
            {
                ApiVersion = "v1",
                SessionId = sessionId,
                StartingStep = call - 1,
                EndingStep = call,
                SimulatedMilliseconds = 0.1,
                StepsExecuted = request.Steps,
                TotalSpikes = 0,
                UniqueSpikingNeurons = 0,
                ActiveFraction = 0,
                PeakSimultaneousSpikeCount = 0,
                PeakSimultaneousSpikeFraction = 0,
                SafetyStatus = "completed",
                Events = [],
                EventsTruncated = false,
                ReturnedEventCount = 0,
                EngineWallSeconds = 0,
            });
        }
    }
}
