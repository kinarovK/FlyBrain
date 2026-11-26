using FlyBrain.BrainClient;
using Topo.Core;
using Topo.Training;
using Xunit;

namespace Topo.Training.Tests;

public sealed class NeuralReadoutTests
{
    [Fact]
    public async Task LogicalSnapshotReader_BatchesInOrderAndCombinesOneStep()
    {
        var client = new ReadoutClient((roots, _) => Snapshot(roots, 7));
        var roots = Enumerable.Range(1, 257).Select(value => (long)value).ToArray();

        var snapshot = await new LogicalReadoutSnapshotReader(client).ReadAsync("session", roots);

        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(256, client.Requests[0].Count);
        Assert.Equal([257L], client.Requests[1]);
        Assert.Equal(7, snapshot.SimulationStep);
        Assert.Equal(roots, snapshot.Neurons.Select(neuron => neuron.RootId));
    }

    [Fact]
    public async Task LogicalSnapshotReader_RejectsMismatchedBatchSteps()
    {
        var client = new ReadoutClient((roots, call) => Snapshot(roots, call == 1 ? 7 : 8));
        var roots = Enumerable.Range(1, 257).Select(value => (long)value).ToArray();

        await Assert.ThrowsAsync<InvalidOperationException>(() => new LogicalReadoutSnapshotReader(client).ReadAsync("session", roots));
    }

    [Fact]
    public void FeatureExtractor_PreservesRootAndFieldOrderWithDeltas()
    {
        var roots = new long[] { 20, 10 };
        var baseline = LogicalSnapshot(roots, step: 0, membrane: [-60, -65], spikes: [0, 2], refractory: [0, 1]);
        var after = LogicalSnapshot(roots, step: 3, membrane: [-58.5, -66], spikes: [3, 2], refractory: [22, 0]);

        var vector = NeuralFeatureExtractor.Extract(baseline, after, roots, observationOffsetSteps: 2);

        Assert.Equal(2, vector.ReadoutRootCount);
        Assert.Equal(6, vector.FeatureCount);
        Assert.Equal([1.5, 3, 22, -1, 0, 0], vector.Features);
        Assert.Equal(vector.Features, NeuralFeatureExtractor.Extract(baseline, after, roots, observationOffsetSteps: 2).Features);
    }

    [Fact]
    public void PopulationSelector_ExcludesDirectInputsAndSelectsOnlyResponsiveRoots()
    {
        var roots = new long[] { 1001, 2001, 2002 };
        var baseline = LogicalSnapshot(roots, 0, [-65, -65, -65], [0, 0, 0], [0, 0, 0]);
        var north = LogicalSnapshot(roots, 1, [-52, -64, -65], [1, 0, 0], [22, 0, 0]);
        var south = LogicalSnapshot(roots, 1, [-52, -65, -63], [1, 0, 0], [22, 0, 0]);
        var trials = new[]
        {
            new TokenReadoutTrial(GeoCoordinateToken.North, baseline, new Dictionary<int, LogicalReadoutSnapshot> { [0] = north }),
            new TokenReadoutTrial(GeoCoordinateToken.South, baseline, new Dictionary<int, LogicalReadoutSnapshot> { [0] = south }),
        };

        var selection = ReadoutPopulationSelector.Select(trials, 0, [1001]);

        Assert.Equal([2002L, 2001L], selection);
        Assert.DoesNotContain(1001, selection);
    }

    [Fact]
    public void TokenFeatureSurveyAnalysis_ReportsCollisionsAndReplayDistance()
    {
        var survey = new Dictionary<GeoCoordinateToken, IReadOnlyList<NeuralFeatureVector>>
        {
            [GeoCoordinateToken.North] = [Vector([1, 0, 0]), Vector([1, 0, 0])],
            [GeoCoordinateToken.South] = [Vector([2, 0, 0]), Vector([2, 0, 0])],
            [GeoCoordinateToken.East] = [Vector([1, 0, 0]), Vector([1, 0, 0])],
        };

        var report = TokenFeatureSurveyAnalysis.Analyze(survey);

        Assert.Equal(2, report.UniqueTokenVectors);
        Assert.Equal(0, report.MaximumWithinTokenReplayDistance);
        Assert.Equal(1, report.MinimumNonZeroBetweenTokenDistance);
        Assert.Equal([GeoCoordinateToken.North, GeoCoordinateToken.East], Assert.Single(report.Collisions));
    }

    private static BrainReadoutSnapshot Snapshot(IReadOnlyList<long> roots, int step) => new()
    {
        SessionId = "session",
        SimulationStep = step,
        SimulatedMilliseconds = step * 0.1,
        ReturnedCount = roots.Count,
        Neurons = roots.Select(root => Neuron(root, -65, 0, 0)).ToList(),
    };

    private static NeuralFeatureVector Vector(double[] features) => new(1, 1, features.Length, 0, features);

    private static LogicalReadoutSnapshot LogicalSnapshot(long[] roots, int step, double[] membrane, int[] spikes, int[] refractory) => new(
        "session",
        step,
        step * 0.1,
        roots.Select((root, index) => Neuron(root, membrane[index], spikes[index], refractory[index])).ToArray());

    private static BrainReadoutNeuron Neuron(long root, double membrane, int spikes, int refractory) => new()
    {
        RootId = root,
        MembraneMillivolts = membrane,
        RefractorySteps = refractory,
        CumulativeSpikeCount = spikes,
        FirstSpikeStep = spikes > 0 ? 0 : null,
        Note = "test",
    };

    private sealed class ReadoutClient(Func<IReadOnlyList<long>, int, BrainReadoutSnapshot> responseFactory) : IBrainServiceClient
    {
        public List<IReadOnlyList<long>> Requests { get; } = [];

        public Task<BrainReadoutSnapshot> ReadoutAsync(string sessionId, IReadOnlyList<long> rootIds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(rootIds.ToArray());
            return Task.FromResult(responseFactory(rootIds, Requests.Count));
        }

        public Task<BrainReadiness> GetReadinessAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BrainInfo> GetBrainInfoAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BrainSession> CreateSessionAsync(CreateBrainSessionRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BrainStepResult> StepAsync(string sessionId, BrainStepRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BrainSessionState> GetSessionStateAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SessionRootsPage> GetSessionRootsAsync(string sessionId, int offset = 0, int limit = 64, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SessionLayoutPage> GetSessionLayoutAsync(string sessionId, int offset = 0, int limit = 1024, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NeuronDiagnostic> GetNeuronDiagnosticAsync(string sessionId, long rootId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BrainResetResult> ResetSessionAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
