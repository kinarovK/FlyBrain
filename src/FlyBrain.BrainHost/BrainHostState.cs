namespace FlyBrain.BrainHost;

public enum BrainHostState
{
    Stopped,
    Starting,
    Ready,
    Stopping,
    Failed,
}

public sealed record BrainHostStatus(BrainHostState State, string Message, bool OwnsProcess)
{
    public static BrainHostStatus Stopped { get; } = new(BrainHostState.Stopped, "Brain is off.", false);

    public bool IsReady => State == BrainHostState.Ready;
}
