namespace FlyBrain.BrainClient;

public interface IBrainServiceClient
{
    Task<BrainReadiness> GetReadinessAsync(CancellationToken cancellationToken = default);

    Task<BrainInfo> GetBrainInfoAsync(CancellationToken cancellationToken = default);

    Task<BrainSession> CreateSessionAsync(
        CreateBrainSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<BrainStepResult> StepAsync(
        string sessionId,
        BrainStepRequest request,
        CancellationToken cancellationToken = default);

    Task<BrainSessionState> GetSessionStateAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<SessionRootsPage> GetSessionRootsAsync(
        string sessionId,
        int offset = 0,
        int limit = 64,
        CancellationToken cancellationToken = default);

    Task<NeuronDiagnostic> GetNeuronDiagnosticAsync(
        string sessionId,
        long rootId,
        CancellationToken cancellationToken = default);

    Task<BrainReadoutSnapshot> ReadoutAsync(
        string sessionId,
        IReadOnlyList<long> rootIds,
        CancellationToken cancellationToken = default);

    Task<BrainResetResult> ResetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}
