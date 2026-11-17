namespace FlyBrain.BrainHost;

/// <summary>
/// Owns the lifetime of the local Python brain service: starts it on demand,
/// reports readiness and stops it again. If a service is already running at the
/// configured address, the host attaches to it instead of starting a second one.
/// </summary>
public interface IBrainHost : IAsyncDisposable
{
    BrainHostStatus Status { get; }

    Uri BaseAddress { get; }

    IReadOnlyList<string> RecentLog { get; }

    event EventHandler<BrainHostStatus>? StatusChanged;

    event EventHandler<string>? LogReceived;

    Task<BrainHostStatus> StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
