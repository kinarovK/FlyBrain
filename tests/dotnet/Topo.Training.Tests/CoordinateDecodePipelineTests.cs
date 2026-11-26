using System.Net;
using FlyBrain.BrainClient;
using Topo.Core;
using Topo.Training;
using Xunit;

namespace Topo.Training.Tests;

public sealed class CoordinateDecodePipelineTests
{
    [Fact]
    public async Task SessionRunner_CancellationStopsPipelineAndDeletesSession()
    {
        var client = new CancellationClient();
        var runner = new CoordinateNeuralDecodeSessionRunner(client, new CoordinateNeuralDecodePipeline(client, new LogicalReadoutSnapshotReader(client)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            new GeoCoordinate(0, 0), Channels(), Population(), Classifier(), Preprocessor(), cancellation.Token));

        Assert.True(client.Deleted);
        Assert.Equal(BrainExecutionMode.Reference, client.CreateRequest!.ExecutionMode);
        Assert.Equal(BrainTransmitterMode.Unsigned, client.CreateRequest.TransmitterMode);
    }

    private static BrainChannelConfiguration Channels() => new(1, "reference", "unsigned", 100, 1, 22,
        Enum.GetValues<GeoCoordinateToken>().ToDictionary(token => token, token => (IReadOnlyList<long>)new[] { 100L + (int)token }));
    private static ReadoutPopulationConfiguration Population() => new(1, "reference", 22, [1L], NeuralFeatureExtractor.FeatureFields);
    private static TokenReadoutPreprocessor Preprocessor() => new(3, [0], [0d], [1d]);
    private static TokenSoftmaxClassifier Classifier() => new(Enum.GetValues<GeoCoordinateToken>(), Enum.GetValues<GeoCoordinateToken>().Select((_, index) => (IReadOnlyList<double>)new[] { (double)index }).ToArray(), new double[15]);

    private sealed class CancellationClient : IBrainServiceClient
    {
        public bool Deleted { get; private set; }
        public CreateBrainSessionRequest? CreateRequest { get; private set; }
        public Task<BrainReadiness> GetReadinessAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrainInfo> GetBrainInfoAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrainSession> CreateSessionAsync(CreateBrainSessionRequest request, CancellationToken cancellationToken = default)
        {
            CreateRequest = request;
            return Task.FromResult(new BrainSession { ApiVersion = "v1", SessionId = "fake", Status = "created", ExecutionMode = BrainExecutionMode.Reference, NeuronCount = 1, TransmitterMode = "unsigned", ResolvedConfiguration = new BrainResolvedConfiguration { NeuralFormat = "test", NeuralVersion = 1, TransmitterMode = BrainTransmitterMode.Unsigned, ExecutionMode = BrainExecutionMode.Reference } });
        }
        public Task<BrainStepResult> StepAsync(string sessionId, BrainStepRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrainSessionState> GetSessionStateAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionRootsPage> GetSessionRootsAsync(string sessionId, int offset = 64, int limit = 64, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionLayoutPage> GetSessionLayoutAsync(string sessionId, int offset = 0, int limit = 1024, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NeuronDiagnostic> GetNeuronDiagnosticAsync(string sessionId, long rootId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrainReadoutSnapshot> ReadoutAsync(string sessionId, IReadOnlyList<long> rootIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrainResetResult> ResetSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromCanceled<BrainResetResult>(cancellationToken);
        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) { Deleted = true; return Task.CompletedTask; }
    }
}
