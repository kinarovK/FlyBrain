namespace FlyBrain.Modules;

/// <summary>
/// A brain-driven part of the FlyBrain app (for example Topo or Trader).
/// The shell shows one module at a time. A module owns its own brain sessions
/// and must release them when it is deactivated or when the brain stops.
/// </summary>
public interface IBrainModule : IAsyncDisposable
{
    /// <summary>Stable identifier, used for settings.</summary>
    string Id { get; }

    string DisplayName { get; }

    string Description { get; }

    /// <summary>The module UI (a WPF element). Created once and reused.</summary>
    object View { get; }

    /// <summary>Called when the module becomes the visible module.</summary>
    Task ActivateAsync(CancellationToken cancellationToken = default);

    /// <summary>Called before another module is shown. Cancel work and release brain sessions.</summary>
    Task DeactivateAsync(CancellationToken cancellationToken = default);

    /// <summary>Called for the active module whenever the brain becomes ready or stops.</summary>
    Task OnBrainAvailabilityChangedAsync(bool isReady, CancellationToken cancellationToken = default);
}
